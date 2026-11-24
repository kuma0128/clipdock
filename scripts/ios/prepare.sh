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

rm -rf Python.xcframework packages
tar -xzf python.tgz Python.xcframework VERSIONS
mkdir packages
for wheel in yt-dlp.whl certifi.whl; do unzip -q "$wheel" -d packages; done

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
