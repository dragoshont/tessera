#!/bin/sh
set -eu

root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
scratch="${TMPDIR:-/tmp}/tessera-mac-host-release"
dist="$root/dist"
rm -rf "$dist"
mkdir -p "$dist/TesseraMacHost.app/Contents/MacOS"

swift build --package-path "$root" --scratch-path "$scratch" -c release --product TesseraHostLoginItem
swift build --package-path "$root" --scratch-path "$scratch" -c release --product TesseraHostControl
cp "$scratch/release/TesseraHostLoginItem" "$dist/TesseraMacHost.app/Contents/MacOS/TesseraMacHost"
cp "$scratch/release/TesseraHostControl" "$dist/TesseraHostControl"
cp "$root/LoginItem/Info.plist" "$dist/TesseraMacHost.app/Contents/Info.plist"
if [ -n "${TESSERA_TEAM_IDENTIFIER:-}" ]; then
	printf '%s' "$TESSERA_TEAM_IDENTIFIER" | grep -Eq '^[A-Z0-9]{10}$' || {
		echo "TESSERA_TEAM_IDENTIFIER must be a 10-character Apple Team ID" >&2
		exit 1
	}
	sed "s/__TEAM_IDENTIFIER__/$TESSERA_TEAM_IDENTIFIER/g" \
		"$root/LoginItem/TesseraMacHost.entitlements.template" > "$dist/TesseraMacHost.entitlements"
else
	cp "$root/LoginItem/TesseraMacHost.entitlements.empty" "$dist/TesseraMacHost.entitlements"
fi
chmod 0755 "$dist/TesseraMacHost.app/Contents/MacOS/TesseraMacHost" "$dist/TesseraHostControl"
/usr/bin/plutil -lint "$dist/TesseraMacHost.app/Contents/Info.plist"
/usr/bin/plutil -lint "$dist/TesseraMacHost.entitlements"

test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$dist/TesseraMacHost.app/Contents/Info.plist")" = "ro.hont.tessera.host"
test "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$dist/TesseraMacHost.app/Contents/Info.plist")" = "true"
echo "MAC_HOST_BUILD: PASS"