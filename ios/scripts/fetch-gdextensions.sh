#!/usr/bin/env bash
# Installs the official iOS builds of the game's GDExtensions into a Godot
# project's addons folder:
#   FMOD   fmod-gdextension 6.1.0-4.5.0 (FMOD 2.03, bundled with the release)
#   Spine  spine-godot extension 4.2 for Godot 4.5.1 (Esoteric's build)
# Only the iOS libraries and the macOS editor libraries (so the Godot editor
# running the export can load them) are kept.
#
# Usage: fetch-gdextensions.sh <addons_dir> <download_cache_dir>
set -euo pipefail

FMOD_URL="https://github.com/utopia-rise/fmod-gdextension/releases/download/6.1.0-4.5.0/addons.zip"
SPINE_URL="https://spine-godot.s3.eu-central-1.amazonaws.com/4.2/4.5.1-stable/spine-godot-extension-4.2-4.5.1-stable.zip"

ADDONS="$1"
CACHE="$2"
mkdir -p "$ADDONS" "$CACHE"

fetch() {
    local url="$1" dest="$2"
    if [ ! -s "$dest" ]; then
        echo "downloading $url"
        curl -fsSL --retry 3 -o "$dest.tmp" "$url"
        mv "$dest.tmp" "$dest"
    fi
}

fetch "$FMOD_URL" "$CACHE/fmod-6.1.0-4.5.0.zip"
fetch "$SPINE_URL" "$CACHE/spine-4.2-4.5.1.zip"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

unzip -q "$CACHE/fmod-6.1.0-4.5.0.zip" -d "$TMP/fmod"
rm -rf "$ADDONS/fmod"
mv "$TMP/fmod/fmod" "$ADDONS/fmod"
# The release ships libs/iOS but fmod.gdextension points at libs/ios, which
# only works on case-insensitive file systems.
if [ -d "$ADDONS/fmod/libs/iOS" ]; then
    mv "$ADDONS/fmod/libs/iOS" "$ADDONS/fmod/libs/ios.tmp"
    mv "$ADDONS/fmod/libs/ios.tmp" "$ADDONS/fmod/libs/ios"
fi
rm -rf "$ADDONS/fmod/libs/"{windows,linux,android,web}
find "$ADDONS/fmod/libs/ios" -name '*simulator*' -prune -exec rm -rf {} + 2>/dev/null || true
find "$ADDONS/fmod/libs/ios" \( -name '*appletv*' -o -name '*xr*' \) -prune -exec rm -rf {} + 2>/dev/null || true

unzip -q "$CACHE/spine-4.2-4.5.1.zip" -d "$TMP/spine"
rm -rf "$ADDONS/spine"
mv "$TMP/spine/bin" "$ADDONS/spine"
rm -rf "$ADDONS/spine/"{windows,linux,android,web}

for f in "$ADDONS/fmod/libs/ios/libGodotFmod.ios.template_release.xcframework" \
         "$ADDONS/spine/ios/libspine_godot.ios.template_release.framework"; do
    [ -e "$f" ] || { echo "ERROR: expected $f" >&2; exit 1; }
done
echo "installed FMOD and Spine iOS GDExtensions into $ADDONS"
