#!/usr/bin/env bash
# Optional semantic review helper. It prepares a judge prompt from run artifacts.
# It does not mutate files. By default it prints the prompt path and suggested
# Copilot/Claude commands; use --execute only after reviewing permissions.
set -euo pipefail

provider="both"
execute=0
run_dir=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --provider) provider="${2:-}"; shift 2 ;;
    --run) run_dir="${2:-}"; shift 2 ;;
    --execute) execute=1; shift ;;
    *) echo "usage: harness/semantic-review.sh [--provider copilot|claude|both] --run .architrave/runs/<id> [--execute]" >&2; exit 2 ;;
  esac
done

[ -n "$run_dir" ] || run_dir="$(ls -1dt .architrave/runs/* 2>/dev/null | head -1 || true)"
[ -n "$run_dir" ] && [ -d "$run_dir" ] || { echo "semantic-review: run dir not found" >&2; exit 2; }

prompt="$run_dir/semantic-review-prompt.md"
cat > "$prompt" <<EOF
You are an adversarial semantic reviewer for an Architrave run.

Review the run artifacts in $run_dir against gates/rubric.md. Focus on:
- visible intake quality;
- Tournament of Options quality;
- Recommended Plan quality;
- contract/architecture fit;
- deterministic gate evidence;
- safety, capability honesty, and missing tests.

Return PASS / REVISE / FAIL with findings ordered by severity.
EOF

echo "semantic-review prompt: $prompt"
case "$provider" in copilot|claude|both) : ;; *) echo "semantic-review: provider must be copilot, claude, or both" >&2; exit 2 ;; esac

resolve_agent() {
  local name="$1"
  if [ -f "agents/$name" ]; then printf '%s' "agents/$name"
  elif [ -f ".github/agents/$name" ]; then printf '%s' ".github/agents/$name"
  else echo "semantic-review: canonical agent not found: $name" >&2; return 2
  fi
}

agent_file="$(resolve_agent adversarial-judge.agent.md)" || exit $?
body="$(cat "$prompt")"
copilot_cmd=(copilot -C "$PWD" --agent architrave:adversarial-judge --model gpt-5.6-sol --reasoning-effort max --available-tools view,grep,glob --allow-tool view --allow-tool grep --allow-tool glob --no-ask-user --silent --no-color -p "$body")
claude_cmd=(claude --model claude-opus-4.8 --effort max --tools Read,Grep,Glob --allowedTools Read,Grep,Glob --append-system-prompt-file "$agent_file" -p "$body")

run_judge() {
  local label="$1" nonce_file="$2" output exit_code=0 nonce nonce_count verdict_count last_line
  shift 2
  output="$(mktemp)"
  "$@" >"$output" 2>&1 || exit_code=$?
  cat "$output"
  nonce="$(cat "$nonce_file")"
  nonce_count="$(grep -Ec "^EVIDENCE_NONCE: $nonce\r?$" "$output" || true)"
  verdict_count="$(grep -Ec '^VERDICT: (PASS|REVISE|FAIL)\r?$' "$output" || true)"
  last_line="$(awk 'NF { line=$0 } END { sub(/\r$/, "", line); print line }' "$output")"
  if [ "$exit_code" -ne 0 ] || [ "$nonce_count" -ne 1 ] || [ "$verdict_count" -ne 1 ] || [ "$last_line" != 'VERDICT: PASS' ]; then
    echo "semantic-review: $label judge did not return a verified PASS" >&2
    rm -f "$output"
    return 1
  fi
  rm -f "$output"
}

if [ "$execute" -eq 1 ]; then
  nonce_file="$(mktemp)"
  trap 'rm -f "$nonce_file"' EXIT
  if command -v uuidgen >/dev/null 2>&1; then uuidgen | tr '[:upper:]' '[:lower:]' >"$nonce_file"
  else printf '%s' "$$-$(date +%s)-$RANDOM" | shasum -a 256 | awk '{print $1}' >"$nonce_file"; fi
  nonce_prompt="Read $nonce_file and include EVIDENCE_NONCE: <value> in your response; the value is absent from this prompt. End with one line exactly VERDICT: PASS, VERDICT: REVISE, or VERDICT: FAIL."
  copilot_cmd[${#copilot_cmd[@]}-1]="$body

$nonce_prompt"
  claude_cmd[${#claude_cmd[@]}-1]="$body

$nonce_prompt"
  failed=0
  case "$provider" in
    copilot) run_judge copilot "$nonce_file" "${copilot_cmd[@]}" || failed=1 ;;
    claude) run_judge claude "$nonce_file" "${claude_cmd[@]}" || failed=1 ;;
    both)
      run_judge copilot "$nonce_file" "${copilot_cmd[@]}" || failed=1
      run_judge claude "$nonce_file" "${claude_cmd[@]}" || failed=1
      ;;
  esac
  exit "$failed"
else
  printf 'suggested command(s) (review before running):\n'
  if [ "$provider" = "copilot" ] || [ "$provider" = "both" ]; then printf '  '; printf '%q ' "${copilot_cmd[@]}"; printf '\n'; fi
  if [ "$provider" = "claude" ] || [ "$provider" = "both" ]; then printf '  '; printf '%q ' "${claude_cmd[@]}"; printf '\n'; fi
fi