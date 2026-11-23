#!/bin/bash
# Checks a bundle built by scripts/build-mac.sh. Runs on macOS in a desktop session.
#
#   scripts/test-mac.sh [arm64|x64]
#
# Always: signature, version, architecture, disk image contents and checksum.
# When the bundle matches this Mac's architecture: the real engine (install, then save synthetic MP4 and MP3),
# the login item (written to a scratch folder, never to ~/Library/LaunchAgents) and the windows (main window,
# sign-in window loading a page, closing to the menu bar, Quit). Logs and a screenshot go to artifacts/mac-checks.
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

# The login item, written to a scratch folder: a valid LaunchAgent that starts this very executable hidden.
"$binary" --login-item-check "$work/LaunchAgents" >/dev/null
agent="$work/LaunchAgents/app.clipdock.ClipDock.plist"
plutil -lint "$agent" >/dev/null
[ "$(/usr/libexec/PlistBuddy -c 'Print :Label' "$agent")" = "app.clipdock.ClipDock" ]
[ "$(/usr/libexec/PlistBuddy -c 'Print :ProgramArguments:0' "$agent")" = "$(cd "$(dirname "$binary")" && pwd -P)/ClipDock" ]
[ "$(/usr/libexec/PlistBuddy -c 'Print :ProgramArguments:1' "$agent")" = "--background" ]
[ "$(/usr/libexec/PlistBuddy -c 'Print :RunAtLoad' "$agent")" = "true" ]
[ "$(stat -f %Lp "$agent")" = "644" ]
pass "login item: a valid LaunchAgent that opens this bundle in the menu bar"

# Runs with --data-dir must leave the user's own login item as it was.
real_agent="$HOME/Library/LaunchAgents/app.clipdock.ClipDock.plist"
real_before="$(cat "$real_agent" 2>/dev/null || true)"

# Each language chosen in the app, then a first launch with nothing saved, where macOS's preferred languages decide
# (given for this run only with -AppleLanguages): Traditional Chinese is not one of ClipDock's, so the next, Spanish, is.
for language in ja en zh-CN es first-launch; do
result="$logs/window-check-$language.json"
mkdir -p "$work/data-$language"
rm -f "$result"
# English starts as the login item does: hidden, with only the menu bar icon, until the check opens the window.
case "$language" in en) background="--background" ;; *) background="" ;; esac
if [ "$language" = first-launch ]; then
  expected=es
  "$binary" --data-dir "$work/data-$language" --smoke "$result" -AppleLanguages '("zh-Hant-TW", "es-ES")' > "$logs/window-$language.log" 2>&1 &
else
  expected="$language"
  printf '{"language":"%s"}\n' "$language" > "$work/data-$language/settings.json"
  "$binary" --data-dir "$work/data-$language" $background --smoke "$result" > "$logs/window-$language.log" 2>&1 &
fi
pid=$!
for _ in $(seq 1 240); do
  [ -f "$result" ] && break
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.5
done
sleep 3
screencapture -x "$logs/window-check-$language.png" 2>/dev/null || echo "No screenshot: screen capture is unavailable in this session."
# The check ends with the menu bar icon's Quit, which must end the process (closing the window alone does not).
for _ in $(seq 1 120); do
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.5
done
if kill -0 "$pid" 2>/dev/null; then
  kill "$pid"; echo "FAIL ClipDock was still running after Quit" >&2; cat "$logs/window-$language.log" >&2; exit 1
fi
wait "$pid" || true
[ -f "$result" ] || { echo "FAIL the app did not report a window check result" >&2; cat "$logs/window-$language.log" >&2; exit 1; }
cat "$result"; echo
grep -q '"passed": true' "$result" || { echo "FAIL window check" >&2; cat "$logs/window-$language.log" >&2; exit 1; }
grep -q "\"language\": \"$expected\"" "$result" || { echo "FAIL the window was not in $expected ($language)" >&2; exit 1; }
pass "main window, sign-in session and sign-in window; closing keeps the menu bar icon, Quit ends ClipDock${background:+ (started with $background)}"
if [ "$language" = first-launch ]; then
  grep -q "\"language\": \"$expected\"" "$work/data-$language/settings.json"
  pass "with no language saved, the first of macOS's preferred languages ClipDock has is shown and saved"
fi

[ "$(stat -f %Lp "$work/data-$language")" = "700" ] && [ "$(stat -f %Lp "$work/data-$language/history.dat")" = "600" ] && [ "$(stat -f %Lp "$work/data-$language/settings.json")" = "600" ]
pass "settings and history are readable by the current user only ($language)"
done
[ "$(cat "$real_agent" 2>/dev/null || true)" = "$real_before" ]
pass "runs with --data-dir left ~/Library/LaunchAgents unchanged"
# Where WebKit keeps the app's sign-in session (recorded for the privacy documentation).
ls -d "$HOME/Library/WebKit/app.clipdock.ClipDock" "$HOME/Library/HTTPStorages/app.clipdock.ClipDock"* "$HOME/Library/Caches/app.clipdock.ClipDock" 2>/dev/null | sed 's/^/session data: /' || true
echo "macOS checks passed."
