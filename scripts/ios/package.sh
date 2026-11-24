#!/bin/bash
# Run after prepare.sh and xcodegen. Produce a device IPA for personal re-signing.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
output="$root/artifacts/ios"
mkdir -p "$output"
xcodebuild -project src/ClipDock.iOS/ClipDock.xcodeproj -scheme ClipDock \
    -configuration Release -destination 'generic/platform=iOS' \
    -derivedDataPath "$output/build" CODE_SIGNING_ALLOWED=NO build -quiet
app="$output/build/Build/Products/Release-iphoneos/ClipDock.app"
test "$(plutil -extract CFBundleSupportedPlatforms.0 raw "$app/Info.plist")" = iPhoneOS
test "$(plutil -extract MinimumOSVersion raw "$app/Info.plist")" = 26.0
test "$(plutil -extract UIFileSharingEnabled raw "$app/Info.plist")" = true
test "$(plutil -extract LSSupportsOpeningDocumentsInPlace raw "$app/Info.plist")" = true
test -s "$app/packages/media.py"
test -s "$app/packages/ios_media.py"
test -d "$app/packages/yt_dlp_ejs"
test -s "$app/Frameworks/ClipDockRemux.framework/ClipDockRemux"
test -s "$app/Frameworks/Python.framework/Python"
test -s "$app/Frameworks/_ssl.framework/_ssl"
lipo "$app/ClipDock" -verify_arch arm64
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/Payload"
ditto "$app" "$stage/Payload/ClipDock.app"
(cd "$stage" && ditto -c -k --keepParent --norsrc Payload "$stage/ClipDock-iOS.ipa")
mv "$stage/ClipDock-iOS.ipa" "$output/"
(cd "$output" && shasum -a 256 ClipDock-iOS.ipa > ClipDock-iOS.ipa.sha256)
