#!/bin/sh
set -eu

repo="$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)"
scratch="${TMPDIR:-/tmp}/tessera-mac-host-checks"

swift test --package-path "$repo/mac-host" --scratch-path "$scratch"
sh "$repo/mac-host/scripts/build-for-electron.sh"
TESSERA_TEAM_IDENTIFIER=ABCDEFGHIJ sh "$repo/mac-host/scripts/build-for-electron.sh"
grep -q 'ABCDEFGHIJ.ro.hont.tessera.host.shared' "$repo/mac-host/dist/TesseraMacHost.entitlements"
sh "$repo/mac-host/scripts/build-for-electron.sh"
npm --prefix "$repo/desktop" run lint
npm --prefix "$repo/desktop" test
npm --prefix "$repo/desktop" run build

if grep -R -nE 'Process\(|NSTask|posix_spawn|system\(|/bin/(sh|bash)' \
  "$repo/mac-host/Sources/TesseraHostCore" "$repo/mac-host/Sources/TesseraHostMac" \
  "$repo/mac-host/Sources/TesseraHostLoginItem"; then
  echo "MAC_HOST_CHECKS: forbidden Host authority found" >&2
  exit 1
fi
if grep -nEi 'executeHost|readPath|sendEnvelope|privateKey|pairHost|signHost' "$repo/desktop/src/preload.ts"; then
  echo "MAC_HOST_CHECKS: privileged renderer IPC found" >&2
  exit 1
fi

git -C "$repo" diff --check
"$repo/scripts/check-pii.sh"
echo "MAC_HOST_CHECKS: PASS"