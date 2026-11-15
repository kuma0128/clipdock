#!/bin/bash
# Builds the macOS app bundle and disk image. Runs on macOS only (uses sips, iconutil, codesign, hdiutil).
#
#   scripts/build-mac.sh [arm64|x64]
#
# Output: artifacts/mac-<arch>/ClipDock.app
#         artifacts/mac/ClipDock-<version>-mac-<arch>-unsigned.dmg (+ .sha256)
#
# The bundle is ad-hoc signed: enough for Apple silicon to run it, but macOS cannot confirm the publisher
# and warns on first launch. A Developer ID signature and notarization are not applied here.
set -euo pipefail

arch="${1:-arm64}"
case "$arch" in arm64|x64) ;; *) echo "Architecture must be arm64 or x64." >&2; exit 2 ;; esac
[ "$(uname -s)" = "Darwin" ] || { echo "The macOS package can only be built on macOS." >&2; exit 2; }

root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's/.*<Version>\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\)<\/Version>.*/\1/p' "$root/Directory.Build.props")"
[ -n "$version" ] || { echo "Directory.Build.props must define a three-component version." >&2; exit 1; }

output="$root/artifacts/mac-$arch"
app="$output/ClipDock.app"
contents="$app/Contents"
release="$root/artifacts/mac"
image="$release/ClipDock-$version-mac-$arch-unsigned.dmg"

rm -rf "$output"
mkdir -p "$contents/MacOS" "$contents/Resources" "$release"

dotnet publish "$root/src/ClipDock.Mac" -c Release -r "osx-$arch" --self-contained true -o "$contents/MacOS" --nologo
# The Windows development manifest and debug symbols have no use inside the bundle.
rm -f "$contents/MacOS"/*.pdb

sed "s/@VERSION@/$version/g" "$root/src/ClipDock.Mac/Info.plist" > "$contents/Info.plist"
plutil -lint "$contents/Info.plist"

# App icon: every size macOS asks for, from the 1024 px source.
iconset="$output/ClipDock.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/src/ClipDock.Mac/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/src/ClipDock.Mac/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$contents/Resources/ClipDock.icns"
rm -rf "$iconset"

# Documents and the licenses of what the bundle contains.
for name in README.md PRIVACY.md THIRD-PARTY.md LICENSE; do cp "$root/$name" "$contents/Resources/"; done
# The self-contained runtime comes from this runtime pack; its version is recorded in the published deps file.
runtime_version="$(sed -n 's/.*"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.osx-'"$arch"'\/\([0-9][^"]*\)".*/\1/p' "$contents/MacOS/ClipDock.deps.json" | head -1)"
packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
runtime_pack="$packages/microsoft.netcore.app.runtime.osx-$arch/$runtime_version"
[ -n "$runtime_version" ] && [ -f "$runtime_pack/LICENSE.TXT" ] && [ -f "$runtime_pack/THIRD-PARTY-NOTICES.TXT" ] || { echo "The .NET runtime license was not found: $runtime_pack" >&2; exit 1; }
mkdir -p "$contents/Resources/licenses/dotnet"
cp "$runtime_pack/LICENSE.TXT" "$runtime_pack/THIRD-PARTY-NOTICES.TXT" "$contents/Resources/licenses/dotnet/"

# Sign inside-out: every file beside the executable first, then the bundle, so the seal covers all that is shipped.
# codesign treats everything in Contents/MacOS as code, including the managed assemblies and JSON files.
find "$contents/MacOS" -type f ! -name "ClipDock" -exec codesign --force --sign - {} \; 2>/dev/null
codesign --force --sign - "$contents/MacOS/ClipDock"
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"

# Disk image: the app next to a shortcut to the Applications folder.
staging="$output/dmg"
rm -rf "$staging"; mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
rm -f "$image"
hdiutil create -volname "ClipDock" -srcfolder "$staging" -ov -format UDZO "$image" >/dev/null
rm -rf "$staging"
(cd "$release" && shasum -a 256 "$(basename "$image")" > "$(basename "$image").sha256")

echo "Ready: $app"
echo "Ready: $image"
cat "$image.sha256"
