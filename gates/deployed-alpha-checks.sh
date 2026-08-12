#!/usr/bin/env bash
set -euo pipefail

: "${TESSERA_BASE_URL:?Set TESSERA_BASE_URL to the deployed HTTPS origin}"
base="${TESSERA_BASE_URL%/}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

request() {
  local path="$1" output="$2"
  curl --silent --show-error --fail-with-body --max-time 30 "$base$path" -o "$output"
}

auth_request() {
  local path="$1" output="$2"
  : "${TESSERA_BEARER_TOKEN:?Set TESSERA_BEARER_TOKEN for authenticated product checks}"
  local config="$work/curl.conf"
  umask 077
  printf 'header = "Authorization: Bearer %s"\n' "$TESSERA_BEARER_TOKEN" > "$config"
  curl --silent --show-error --fail-with-body --max-time 30 --config "$config" "$base$path" -o "$output"
}

request /healthz "$work/health.json"
jq -e '.status == "ok"' "$work/health.json" >/dev/null
echo 'DEPLOY_HEALTH: PASS'

request /readyz "$work/ready.json"
jq -e '.ready == true and .database.state == "ready" and .scheduler.state == "ready"' "$work/ready.json" >/dev/null
echo 'DEPLOY_READY: PASS'

auth_request /api/v1/accounts "$work/accounts.json"
auth_request /api/v1/plugins "$work/plugins.json"
auth_request /api/v1/jobs "$work/jobs.json"
jq -e '.items | type == "array"' "$work/accounts.json" >/dev/null
jq -e '.items | type == "array"' "$work/plugins.json" >/dev/null
jq -e '.items | type == "array"' "$work/jobs.json" >/dev/null
echo 'PRODUCT_API: PASS'

status_for() {
  local provider="$1" label="$2"
  local connected auth_required
  connected="$(jq --arg provider "$provider" '[.items[] | select(.providerId==$provider and .lifecycle=="CONNECTED")] | length' "$work/accounts.json")"
  auth_required="$(jq --arg provider "$provider" '[.items[] | select(.providerId==$provider and .lifecycle=="AUTH_REQUIRED")] | length' "$work/accounts.json")"
  if (( connected > 0 )); then echo "$label: PASS ($connected connected)"
  elif (( auth_required > 0 )); then echo "$label: AUTH_REQUIRED ($auth_required)"
  else echo "$label: BLOCKED (not connected)"
  fi
}

status_for openai-compatible LiteLLM
status_for gmail 'Gmail OAuth'
status_for regina-maria 'Regina Maria'

if [[ -n "${TESSERA_GMAIL_ACCOUNT_ID:-}" ]]; then
  payload="$(jq -cn --arg account "$TESSERA_GMAIL_ACCOUNT_ID" '{capabilityId:"gmail.messages.search",capabilityVersion:"1",pluginId:"gmail",pluginVersion:"1.0.0",accountId:$account,target:"mailbox:search",input:{query:"is:unread newer_than:1d",maxResults:1}}')"
  printf '%s' "$payload" > "$work/request.json"
  config="$work/curl.conf"
  curl --silent --show-error --fail-with-body --max-time 30 --config "$config" -H 'Content-Type: application/json' -H "Idempotency-Key: deployed-gmail-$(date +%s)" --data-binary @"$work/request.json" "$base/api/v1/capabilities/gmail.messages.search/invoke" -o "$work/gmail.json"
  jq -e '.result.messages | type == "array"' "$work/gmail.json" >/dev/null
  echo 'Gmail real read: PASS'
else
  echo 'Gmail real read: BLOCKED (set TESSERA_GMAIL_ACCOUNT_ID after OAuth)'
fi

check_rm() {
  local variable="$1" label="$2" account="${!variable:-}"
  if [[ -z "$account" ]]; then echo "$label appointments read: BLOCKED (set $variable)"; return; fi
  jq -cn --arg account "$account" '{capabilityId:"reginamaria.appointments.list",capabilityVersion:"1",pluginId:"regina-maria",pluginVersion:"1.0.0",accountId:$account,target:"appointments:list",input:{upcoming:true,maxResults:20}}' > "$work/rm-request.json"
  curl --silent --show-error --fail-with-body --max-time 30 --config "$work/curl.conf" -H 'Content-Type: application/json' -H "Idempotency-Key: deployed-rm-$(date +%s)-$label" --data-binary @"$work/rm-request.json" "$base/api/v1/capabilities/reginamaria.appointments.list/invoke" -o "$work/rm.json"
  jq -e '.result.appointments | type == "array"' "$work/rm.json" >/dev/null
  echo "$label appointments read: PASS"
}

check_rm TESSERA_RM_USER_ACCOUNT_ID 'Regina Maria - User'
check_rm TESSERA_RM_WIFE_ACCOUNT_ID 'Regina Maria - Wife'

echo 'SIDE_EFFECTS: NOT_RUN_SAFE_TARGET'
