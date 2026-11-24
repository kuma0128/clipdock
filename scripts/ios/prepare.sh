#!/bin/bash
# Prepare the exact iOS runtime and pure-Python packages used by the app.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
deps="$root/artifacts/ios-deps"
mkdir -p "$deps"
cd "$deps"

fetch() {
    local name="$1" digest="$2" url="$3"
    if ! echo "$digest  $name" | shasum -a 256 --status -c 2>/dev/null; then
        curl --fail --location --silent --show-error --retry 3 "$url" -o "$name.tmp"
        echo "$digest  $name.tmp" | shasum -a 256 -c -
        mv "$name.tmp" "$name"
    fi
}

fetch python.tgz 703ff822529f46edcbcd8b88096b2544f6990f3109ad38b4b4b61bb489a7c1ee \
    https://github.com/beeware/Python-Apple-support/releases/download/3.14-b14/Python-3.14-iOS-support.b14.tar.gz
fetch yt-dlp.whl e9188e0c559905f7ee05acc1ba0b6fb3f310e7565bf6dea5f62b5300e0775e35 \
    https://files.pythonhosted.org/packages/30/1e/f4bf64ca733135e609696c6df77a6c22c6d4909b6e293e4563ae51ea335c/yt_dlp-2026.11.23-py3-none-any.whl
fetch certifi.whl 7422df25243767b9877031a2248cb14d09ddac9beee846f4cef3d5c9191d488f \
    https://files.pythonhosted.org/packages/6d/39/5eec3296860b94ac20bb8ed807725b88f6adfab3f343df592afd975f5f86/certifi-2026.11.20-py3-none-any.whl
fetch ejs.whl 79300e5fca7f937a1eeede11f0456862c1b41107ce1d726871e0207424f4bdb4 \
    https://files.pythonhosted.org/packages/e3/bd/520769863744b669440a924271a6159ddd82ad5ae26b4ac4d4b69e9f8d44/yt_dlp_ejs-0.8.0-py3-none-any.whl
fetch ffmpeg.tar.xz 7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3 \
    https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz

rm -rf Python.xcframework packages
tar -xzf python.tgz Python.xcframework VERSIONS
mkdir packages
for wheel in yt-dlp.whl certifi.whl ejs.whl; do unzip -q "$wheel" -d packages; done

tar -xf ffmpeg.tar.xz
for sdk in iphoneos iphonesimulator; do
    framework="$deps/$sdk/ClipDockRemux.framework"
    mkdir -p "$framework"
    slices=()
    archs=(arm64)
    platform=iPhoneOS
    if [[ "$sdk" == iphonesimulator ]]; then archs+=(x86_64); platform=iPhoneSimulator; fi
    for arch in "${archs[@]}"; do
        slice="$deps/$sdk/$arch/ClipDockRemux"
        bash "$root/scripts/ios/build-remux.sh" "$deps/ffmpeg-8.1.3" "$sdk" "$arch" "$slice"
        slices+=("$slice")
    done
    lipo -create "${slices[@]}" -output "$framework/ClipDockRemux"
    cat > "$framework/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>app.clipdock.remux</string>
<key>CFBundleName</key><string>ClipDockRemux</string>
<key>CFBundleExecutable</key><string>ClipDockRemux</string>
<key>CFBundlePackageType</key><string>FMWK</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleShortVersionString</key><string>1.0</string>
<key>CFBundleSupportedPlatforms</key><array><string>$platform</string></array>
<key>MinimumOSVersion</key><string>26.0</string>
</dict></plist>
PLIST
done
rm -rf "$deps/ClipDockRemux.xcframework"
xcodebuild -create-xcframework -framework "$deps/iphoneos/ClipDockRemux.framework" \
    -framework "$deps/iphonesimulator/ClipDockRemux.framework" -output "$deps/ClipDockRemux.xcframework"

# Use the same artwork as the desktop app.
icons="$deps/Assets.xcassets/AppIcon.appiconset"
mkdir -p "$icons"
cp "$root/src/ClipDock.Mac/icon.png" "$icons/ClipDock.png"
sips -s format png "$root/src/ClipDock.iOS/Artwork/ClipDockDark.svg" --out "$icons/ClipDockDark.png" >/dev/null
cat > "$icons/Contents.json" <<'JSON'
{
  "images": [
    {"filename":"ClipDock.png","idiom":"universal","platform":"ios","size":"1024x1024"},
    {"appearances":[{"appearance":"luminosity","value":"dark"}],"filename":"ClipDockDark.png","idiom":"universal","platform":"ios","size":"1024x1024"}
  ],
  "info":{"author":"xcode","version":1}
}
JSON
echo '{"info":{"author":"xcode","version":1}}' > "$deps/Assets.xcassets/Contents.json"
