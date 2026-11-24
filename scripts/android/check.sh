#!/usr/bin/env bash
# Verify normal paste actions on a dedicated, running Android emulator.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
project="$root/src/ClipDock.Android"
variant="${1:-debug}"
case "$variant" in
    debug) task=Debug ;;
    release) task=Release ;;
    *) echo "Expected debug or release." >&2; exit 2 ;;
esac
: "${ANDROID_SERIAL:?Set ANDROID_SERIAL to the test emulator}"
test "$(adb shell getprop ro.kernel.qemu | tr -d '\r')" = 1
output="$root/artifacts/android"
mkdir -p "$output"
"$project/gradlew" -p "$project" "-PandroidTestVariant=$variant" "assemble$task" "assemble${task}AndroidTest"
app=$(find "$project/build/outputs/apk/$variant" -maxdepth 1 -name '*.apk')
tests=$(find "$project/build/outputs/apk/androidTest/$variant" -maxdepth 1 -name '*.apk')
adb install -r "$app"
adb install -r -t "$tests"
uv run --no-project python "$root/tests/iOS/fixture.py" > "$output/fixture.log" 2>&1 &
fixture=$!
trap 'kill "$fixture" 2>/dev/null || true; adb reverse --remove tcp:18741 >/dev/null' EXIT
curl -fsS --retry 8 --retry-connrefused --retry-delay 1 http://127.0.0.1:18741/watch >/dev/null
kill -0 "$fixture"
adb reverse tcp:18741 tcp:18741
adb shell input keyevent KEYCODE_WAKEUP
adb shell wm dismiss-keyguard
video_hash=$(shasum -a 256 "$root/tests/iOS/sample.mp4" | cut -d' ' -f1)
audio_hash=$(curl -fsS http://127.0.0.1:18741/tone.wav | shasum -a 256 | cut -d' ' -f1)
adb shell am instrument -e videoHash "$video_hash" -e audioHash "$audio_hash" -w app.clipdock.android.test/androidx.test.runner.AndroidJUnitRunner | tee "$output/test.log"
grep -q 'OK (1 test)' "$output/test.log"
adb shell am force-stop app.clipdock.android
adb shell am start -W -n app.clipdock.android/.MainActivity
# Wait for the saved files to reappear after a cold start before capturing the screen.
for _ in {1..15}; do
    adb shell uiautomator dump /sdcard/clipdock-window.xml >/dev/null
    if adb shell cat /sdcard/clipdock-window.xml | grep -F 'split.mp4' >/dev/null; then
        adb exec-out screencap -p > "$output/android.png"
        exit 0
    fi
    sleep 1
done
echo "Saved files did not reappear after restart." >&2
exit 1
