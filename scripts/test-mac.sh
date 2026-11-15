#!/bin/bash
# Checks a bundle built by scripts/build-mac.sh. Runs on macOS in a desktop session.
#
#   scripts/test-mac.sh [arm64|x64]
#
# Always: signature, version, architecture, disk image contents and checksum.
# When the bundle matches this Mac's architecture: the real engine (install, then save synthetic MP4 and MP3)
# and the windows (main window, sign-in window loading a page). Logs and a screenshot go to artifacts/mac-checks.
set -euo pipefail

arch="${1:-arm64}"
case "$arch" in arm64|x64) ;; *) echo "Architecture must be arm64 or x64." >&2; exit 2 ;; esac
root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's/.*<Version>\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\)<\/Version>.*/\1/p' "$root/Directory.Build.props")"
app="$root/artifacts/mac-$arch/ClipDock.app"
binary="$app/Contents/MacOS/ClipDock"
release="$root/artifacts/mac"
image="ClipDock-$version-mac-$arch-unsigned.dmg"
logs="$root/artifacts/mac-checks"
work="$(mktemp -d)"
mkdir -p "$logs"
pass() { echo "PASS $1"; }

codesign --verify --deep --strict "$app"
pass "bundle signature is intact (ad-hoc)"
[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")" = "$version" ]
[ -f "$app/Contents/Resources/ClipDock.icns" ] && [ -f "$app/Contents/Resources/licenses/dotnet/LICENSE.TXT" ] && [ -f "$app/Contents/Resources/THIRD-PARTY.md" ]
machine="$([ "$arch" = arm64 ] && echo arm64 || echo x86_64)"
file "$binary" | grep -q "$machine"
pass "bundle version $version, icon, licenses and $machine executable"

(cd "$release" && shasum -a 256 -c "$image.sha256" >/dev/null)
hdiutil attach -nobrowse -readonly -mountpoint "$work/volume" "$release/$image" >/dev/null
[ -x "$work/volume/ClipDock.app/Contents/MacOS/ClipDock" ] && [ -L "$work/volume/Applications" ]
codesign --verify --deep --strict "$work/volume/ClipDock.app"
hdiutil detach "$work/volume" >/dev/null
pass "disk image checksum, contents and signature"

if [ "$(uname -m)" != "$machine" ]; then
  echo "SKIP engine and window checks: this Mac is $(uname -m)."
  exit 0
fi

"$binary" --engine-check "$work/engine"

result="$logs/window-check.json"
rm -f "$result"
"$binary" --data-dir "$work/data" --smoke "$result" > "$logs/window.log" 2>&1 &
pid=$!
for _ in $(seq 1 240); do
  [ -f "$result" ] && break
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.5
done
sleep 3
screencapture -x "$logs/window-check.png" 2>/dev/null || echo "No screenshot: screen capture is unavailable in this session."
wait "$pid" || true
[ -f "$result" ] || { echo "FAIL the app did not report a window check result" >&2; cat "$logs/window.log" >&2; exit 1; }
cat "$result"; echo
grep -q '"passed": true' "$result" || { echo "FAIL window check" >&2; cat "$logs/window.log" >&2; exit 1; }
pass "main window, sign-in session and sign-in window"

[ "$(stat -f %Lp "$work/data")" = "700" ] && [ "$(stat -f %Lp "$work/data/history.dat")" = "600" ] && [ "$(stat -f %Lp "$work/data/settings.json")" = "600" ]
pass "settings and history are readable by the current user only"
# Where WebKit keeps the app's sign-in session (recorded for the privacy documentation).
ls -d "$HOME/Library/WebKit/app.clipdock.ClipDock" "$HOME/Library/HTTPStorages/app.clipdock.ClipDock"* "$HOME/Library/Caches/app.clipdock.ClipDock" 2>/dev/null | sed 's/^/session data: /' || true
echo "macOS checks passed."
