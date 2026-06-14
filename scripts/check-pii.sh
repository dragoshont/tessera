#!/usr/bin/env bash
#
# Blocking PII / secret gate for this PUBLIC, generic-broker repo.
#
# Runs in two places, both BLOCKING (non-zero exit stops the action):
#   - the pre-commit hook (.pre-commit-config.yaml)  -> stops the commit
#   - the `hygiene` job in .github/workflows/ci.yml   -> fails the build
#
# Lesson learned the hard way: a scan chained with `&&` after `echo` is NOT a
# gate (echo always succeeds, so the chain continues). This script `exit 1`s on
# the first surviving match so it can never be a silent pass.
#
# Tessera is a GENERIC credential broker. It must not name any specific homelab
# user, the specific medical provider it happened to be tested against, internal
# secret names, the private domain, or live Entra tenant/app GUIDs. Generic
# example data (example.com, alice/bob, portal-mcp) is fine.
#
# ALLOWED (intentional public constants / placeholders):
#   - SECURITY.md maintainer contact
#   - the documented PUBLIC Microsoft consumer-tenant GUID
#     9188040d-6c67-4c5b-b112-36a304b66dad
#   - obvious placeholder GUIDs (00000000-…, 11111111-…, 1111-1111-1111, …)
set -eu

cd "$(git rev-parse --show-toplevel)"

patterns='alice79|bob84|health-portal|account-[abc]-session|examplekv|hont\.ro|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'

# Exclude this script + SECURITY.md (maintainer contact) + the CI file (which
# documents the same patterns). Then drop the allowlisted public/placeholder
# GUIDs so they don't trip the gate.
if git grep -nIE "$patterns" -- . ':!SECURITY.md' ':!scripts/check-pii.sh' ':!.github/workflows/ci.yml' \
     | grep -vE '9188040d-6c67-4c5b-b112-36a304b66dad' \
     | grep -viE '\b([0-9a-f])\1{7}-' \
     | grep -vE '1111-1111-1111|2222-3333-4444'; then
  echo "::error::check-pii: private identity / secret pattern found above. Genericize before committing." >&2
  exit 1
fi
echo "check-pii: clean — no private identities/secrets found."
