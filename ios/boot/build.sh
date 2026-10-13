#!/usr/bin/env bash
# Builds the ios_boot GDExtension (pck mounter + Steam API/FMOD plugin stubs).
#
# Usage: build.sh <ios|linux> <out_dir> [expected_pck_size] [game_version]
#   ios   -> <out_dir>/libios_boot.ios.dylib        (needs Xcode, macOS only)
#   linux -> <out_dir>/libios_boot.linux.x86_64.so  (for local smoke tests)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
STUBS="$HERE/../../src/stubs"
PLATFORM="$1"
OUT_DIR="$2"
EXPECTED_SIZE="${3:-0}"
GAME_VERSION="${4:-unknown}"
mkdir -p "$OUT_DIR"

SOURCES=("$HERE/ios_boot.c" "$STUBS/steam_stub.c" "$STUBS/steam_stub_auto.c")
DEFINES=(
    "-DIOS_BOOT_EXPECTED_PCK_SIZE=${EXPECTED_SIZE}LL"
    "-DIOS_BOOT_GAME_VERSION=\"${GAME_VERSION}\""
)
CFLAGS=(-std=c11 -O2 -Wno-cast-function-type -I"$HERE/include")

case "$PLATFORM" in
    ios)
        SDK="$(xcrun --sdk iphoneos --show-sdk-path)"
        xcrun --sdk iphoneos clang -arch arm64 -isysroot "$SDK" -miphoneos-version-min=16.0 \
            -dynamiclib "${CFLAGS[@]}" "${DEFINES[@]}" \
            -install_name @rpath/libios_boot.ios.dylib \
            -o "$OUT_DIR/libios_boot.ios.dylib" "${SOURCES[@]}"
        ;;
    linux)
        gcc -shared -fPIC "${CFLAGS[@]}" "${DEFINES[@]}" \
            -o "$OUT_DIR/libios_boot.linux.x86_64.so" "${SOURCES[@]}"
        ;;
    *)
        echo "unknown platform: $PLATFORM" >&2
        exit 1
        ;;
esac
echo "built ios_boot ($PLATFORM) in $OUT_DIR (expected pck size: $EXPECTED_SIZE, game: $GAME_VERSION)"
