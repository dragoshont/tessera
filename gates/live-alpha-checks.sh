#!/usr/bin/env bash
# Opt-in checks against a running loopback Tessera instance. Provider credentials
# remain in Tessera custody; this process handles no API keys or access tokens.
set -uo pipefail

BASE_URL="${TESSERA_LIVE_BASE_URL:-http://localhost:8080}"
PRINCIPAL="${TESSERA_LIVE_PRINCIPAL:-alice@example.com}"
REPOSITORY="${TESSERA_LIVE_GITHUB_REPOSITORY:-}"
WRITE_ENABLED="${TESSERA_ENABLE_LIVE_WRITE_TESTS:-false}"
WRITE_TARGET="${TESSERA_LIVE_WRITE_CONFIRM_TARGET:-}"
WRITE_TITLE="${TESSERA_LIVE_GITHUB_ISSUE_TITLE:-Tessera R2.1 live verification}"

case "$BASE_URL" in
  http://localhost:*|http://127.0.0.1:*) ;;
  *) echo "live-alpha: only a loopback dev instance is supported; no bearer token is accepted" >&2; exit 2 ;;
esac
command -v curl >/dev/null 2>&1 || { echo "live-alpha: curl is required" >&2; exit 2; }
command -v jq >/dev/null 2>&1 || { echo "live-alpha: jq is required" >&2; exit 2; }

fail=0
blocked=0
api_get() { curl -fsS --max-time 20 -H "X-Tessera-Dev-Principal: $PRINCIPAL" "$BASE_URL/api/v1$1"; }
api_post() {
  local path="$1" key="$2" body="$3"
  curl -fsS --max-time 30 -X POST -H "X-Tessera-Dev-Principal: $PRINCIPAL" \
    -H "Content-Type: application/json" -H "Idempotency-Key: $key" --data-binary "$body" "$BASE_URL/api/v1$path"
}
line() { printf '%-34s %s\n' "$1" "$2"; }
new_key() { printf 'live-%s-%s' "$1" "$(date +%s)-$RANDOM"; }
wait_for_execution() {
  local conversation="$1" execution="$2"
  curl -fsS --max-time 120 -H "X-Tessera-Dev-Principal: $PRINCIPAL" \
    "$BASE_URL/api/v1/conversations/$conversation/events?executionId=$execution" >/dev/null
}

if ! curl -fsS --max-time 10 "$BASE_URL/readyz" | jq -e '.ready == true' >/dev/null; then
  line "Tessera runtime" "FAIL"
  exit 1
fi
line "Tessera runtime" "PASS"

profiles="$(api_get '/settings/model-profiles' 2>/dev/null || true)"
settings="$(api_get '/settings' 2>/dev/null || true)"
profile_id="$(jq -r --arg preferred "$(jq -r '.defaultChatModelProfileId // empty' <<<"$settings" 2>/dev/null)" \
  '[.items[] | select(.enabled == true)] | (map(select(.profileId == $preferred))[0] // .[0] // {}) | .profileId // empty' <<<"$profiles" 2>/dev/null)"

if [[ -z "$profile_id" ]]; then
  line "OpenAI-compatible chat" "BLOCKED_EXTERNAL"
  line "OpenAI tool call" "BLOCKED_EXTERNAL"
  blocked=1
else
  conversation="$(api_post '/conversations' "$(new_key conversation)" "$(jq -cn --arg profile "$profile_id" '{title:"R2.1 live verification",modelProfileId:$profile}')" 2>/dev/null || true)"
  conversation_id="$(jq -r '.id // empty' <<<"$conversation" 2>/dev/null)"
  if [[ -z "$conversation_id" ]]; then
    line "OpenAI-compatible chat" "FAIL"
    line "OpenAI tool call" "FAIL"
    fail=1
  else
    sent="$(api_post "/conversations/$conversation_id/messages" "$(new_key chat)" "$(jq -cn --arg profile "$profile_id" '{text:"Reply with exactly: TESSERA LIVE MODEL OK",modelProfileId:$profile}')" 2>/dev/null || true)"
    execution_id="$(jq -r '.executionId // empty' <<<"$sent" 2>/dev/null)"
    if [[ -n "$execution_id" ]] && wait_for_execution "$conversation_id" "$execution_id" 2>/dev/null && \
      api_get "/conversations/$conversation_id/messages" | jq -e '.items | any(.role == "ASSISTANT" and .status == "COMPLETED")' >/dev/null; then
      line "OpenAI-compatible chat" "PASS"
    else
      line "OpenAI-compatible chat" "FAIL"
      fail=1
    fi

    tool_sent="$(api_post "/conversations/$conversation_id/messages" "$(new_key tool)" "$(jq -cn --arg profile "$profile_id" '{text:"Use the current_time tool for UTC, then report the result.",modelProfileId:$profile}')" 2>/dev/null || true)"
    tool_execution="$(jq -r '.executionId // empty' <<<"$tool_sent" 2>/dev/null)"
    if [[ -n "$tool_execution" ]] && wait_for_execution "$conversation_id" "$tool_execution" 2>/dev/null && \
      api_get "/conversations/$conversation_id/messages" | jq -e '.items | any(.parts | any(.kind == "CAPABILITY_RESULT"))' >/dev/null; then
      line "OpenAI tool call" "PASS"
    else
      line "OpenAI tool call" "FAIL"
      fail=1
    fi
  fi
fi

accounts="$(api_get '/accounts' 2>/dev/null || true)"
github_id="$(jq -r '[.items[] | select(.providerId == "github" and .lifecycle == "CONNECTED" and .health == "HEALTHY")][0].accountId // empty' <<<"$accounts" 2>/dev/null)"
github_identity="$(jq -r --arg id "$github_id" '.items[] | select(.accountId == $id) | .identityHint // empty' <<<"$accounts" 2>/dev/null)"
github_provider_id="$(jq -r --arg id "$github_id" '.items[] | select(.accountId == $id) | .providerAccountId // empty' <<<"$accounts" 2>/dev/null)"
if [[ -z "$github_id" ]]; then
  line "GitHub identity" "BLOCKED_EXTERNAL"
  line "GitHub read capability" "BLOCKED_EXTERNAL"
  blocked=1
elif [[ -z "$github_identity" || -z "$github_provider_id" ]]; then
  line "GitHub identity" "FAIL"
  fail=1
else
  line "GitHub identity" "PASS"
fi

if [[ -n "$github_id" && -n "$REPOSITORY" ]]; then
  read_body="$(jq -cn --arg account "$github_id" --arg repository "$REPOSITORY" '{capabilityId:"github.issues.list",capabilityVersion:"1",pluginId:"github",pluginVersion:"1.0.0",accountId:$account,target:$repository,input:{repository:$repository}}')"
  if api_post '/capabilities/github.issues.list/invoke' "$(new_key github-read)" "$read_body" >/dev/null 2>&1; then
    line "GitHub read capability" "PASS"
  else
    line "GitHub read capability" "FAIL"
    fail=1
  fi
elif [[ -n "$github_id" ]]; then
  line "GitHub read capability" "BLOCKED_EXTERNAL (set TESSERA_LIVE_GITHUB_REPOSITORY)"
  blocked=1
fi

if [[ "$WRITE_ENABLED" != "true" ]]; then
  line "GitHub write capability" "NOT_RUN_SAFE_MODE"
elif [[ -z "$github_id" || -z "$REPOSITORY" || "$WRITE_TARGET" != "$REPOSITORY" ]]; then
  line "GitHub write capability" "BLOCKED_EXTERNAL (explicit account/repository confirmation required)"
  blocked=1
else
  line "GitHub write target" "$REPOSITORY / $WRITE_TITLE"
  action_body="$(jq -cn --arg account "$github_id" --arg repository "$REPOSITORY" --arg title "$WRITE_TITLE" '{capabilityId:"github.issues.create",capabilityVersion:"1",pluginId:"github",pluginVersion:"1.0.0",accountId:$account,target:$repository,input:{repository:$repository,title:$title,body:"Created by the explicit Tessera R2.1 live verification harness."}}')"
  action="$(api_post '/actions' "$(new_key github-write-proposal)" "$action_body" 2>/dev/null || true)"
  action_id="$(jq -r '.id // empty' <<<"$action" 2>/dev/null)"
  action_version="$(jq -r '.version // empty' <<<"$action" 2>/dev/null)"
  approved="$(api_post "/actions/$action_id/approve" "$(new_key github-write-approval)" "$(jq -cn --argjson version "${action_version:-0}" '{expectedVersion:$version}')" 2>/dev/null || true)"
  if jq -e '.state == "PROVIDER_VERIFIED" and .verificationState == "provider_verified"' <<<"$approved" >/dev/null 2>&1; then
    line "GitHub write capability" "PASS"
  else
    line "GitHub write capability" "FAIL"
    fail=1
  fi
fi

[[ "$fail" -eq 0 ]] || exit 1
[[ "$blocked" -eq 0 ]] || exit 3
exit 0