#!/usr/bin/env bash
# Sign an update-compatible release with the owner's existing key.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
project="$root/src/ClipDock.Android"
: "${ANDROID_HOME:?Set ANDROID_HOME to the Android SDK directory}"
: "${CLIPDOCK_ANDROID_KEYSTORE:?Set CLIPDOCK_ANDROID_KEYSTORE to the release keystore}"
: "${CLIPDOCK_ANDROID_KEY_PASSWORD:?Provide the keystore password in the environment}"
"$project/gradlew" -p "$project" assembleRelease
apk=$(find "$project/build/outputs/apk/release" -maxdepth 1 -name '*.apk')
output="$root/artifacts/android"
mkdir -p "$output"
"$ANDROID_HOME/build-tools/36.0.0/apksigner" verify --verbose --print-certs "$apk"
"$ANDROID_HOME/build-tools/36.0.0/zipalign" -c -P 16 4 "$apk"
cp "$apk" "$output/ClipDock-Android.apk"
(cd "$output" && shasum -a 256 ClipDock-Android.apk > ClipDock-Android.apk.sha256)
