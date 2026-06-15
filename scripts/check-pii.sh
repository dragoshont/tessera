#!/usr/bin/env bash
#
# Structural PII / secret gate for this PUBLIC, generic-broker repo. BLOCKING in
# the pre-commit hook and the CI `hygiene` job (exit 1 on the first match).
#
# Tessera is a GENERIC credential broker. This public repo must never contain a
# real secret, a real (non-example) identity, a private domain, an internal
# resource name, or a live tenant/app GUID. Generic example data (example.com,
# alice/bob/carol, health-portal, placeholder GUIDs) is fine.
#
# Blocks (structural, always): private keys, GitHub/Slack/AWS tokens, Azure
# connection-string keys, and any GUID that is not an obvious placeholder / the
# public Microsoft tenant.
#
# Maintainer's private list (optional): real names / domains / resource ids are
# kept in `scripts/.pii-local` (gitignored — never in this public repo). If
# present, each non-comment line is added to the block set.
set -eu

cd "$(git rev-parse --show-toplevel)"

structural='-----BEGIN [A-Z ]*PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AccountKey=[A-Za-z0-9+/=]{20,}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'

pattern="$structural"
if [ -f scripts/.pii-local ]; then
  extra="$(grep -vE '^[[:space:]]*#|^[[:space:]]*$' scripts/.pii-local | paste -sd'|' - || true)"
  [ -n "$extra" ] && pattern="$structural|$extra"
fi

# Allowlist: placeholder GUIDs (first 8 hex a single repeated char) + the public
# Microsoft consumer-tenant GUID.
if git grep -nIE -e "$pattern" -- . ':!SECURITY.md' ':!scripts/check-pii.sh' \
     | grep -viE '\b([0-9a-f])\1{7}-' \
     | grep -vE '9188040d-6c67-4c5b-b112-36a304b66dad'; then
  echo "::error::check-pii: structural PII / secret pattern found above. Genericize before committing." >&2
  exit 1
fi
echo "check-pii: clean — no structural PII/secrets found."
