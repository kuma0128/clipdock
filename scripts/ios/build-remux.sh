#!/bin/bash
# Build the local-file MP4 remuxer for one SDK/architecture.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
source_dir="$1"
sdk="$2"
arch="$3"
output="$4"
case "$sdk" in
    iphoneos) target="$arch-apple-ios26.0" ;;
    iphonesimulator) target="$arch-apple-ios26.0-simulator" ;;
    macosx) target="$arch-apple-macos26.0" ;;
    *) exit 1 ;;
esac
build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT
cd "$build"
sdk_path="$(xcrun --sdk "$sdk" --show-sdk-path)"
cc="$(xcrun --sdk "$sdk" --find clang) -target $target -isysroot $sdk_path"
"$source_dir/configure" --cc="$cc" --arch="$arch" --target-os=darwin --enable-cross-compile \
    --disable-autodetect --disable-everything --disable-network --disable-programs --disable-doc \
    --disable-asm --disable-avdevice --disable-avfilter --disable-swscale --disable-swresample \
    --enable-protocol=file --enable-demuxer=mov,mpegts,aac,matroska --enable-muxer=mp4 \
    --enable-parser=aac,h264,hevc,av1,vp9,opus --enable-decoder=aac,h264,hevc,av1,vp9,opus \
    --enable-bsf=aac_adtstoasc,extract_extradata > configure.log || { tail -60 ffbuild/config.log; exit 1; }
make -s -j"$(sysctl -n hw.ncpu)"
mkdir -p "$(dirname "$output")"
xcrun --sdk "$sdk" clang -target "$target" -isysroot "$sdk_path" -dynamiclib -fblocks -O2 \
    -install_name @rpath/ClipDockRemux.framework/ClipDockRemux \
    -I"$source_dir" -I"$build" "$root/src/ClipDock.iOS/Remux/ClipDockRemux.c" \
    libavformat/libavformat.a libavcodec/libavcodec.a libavutil/libavutil.a \
    -Wl,-dead_strip -Wl,-exported_symbol,_CDRemux -o "$output"
