#!/usr/bin/env bash
# Builds the unsigned iOS app for Slay the Spire 2 from a Windows-depot game
# install (real, or ios/scripts/make-fake-game.sh). macOS + Xcode only; CI
# runs it on a GitHub macOS runner.
#
# The .ipa contains the engine, the NativeAOT-compiled (patched) game code,
# and a small bootstrap pack. It does not contain SlayTheSpire2.pck: the
# player copies that into the app's folder, and ios_boot mounts it at startup.
#
# Usage: build-game-ios.sh <game_dir> <out_dir>
# Env:   GODOT      Godot 4.5.1 mono editor binary
#        EXT_CACHE  optional download cache for the FMOD/Spine releases
set -euo pipefail

IOS="$(cd "$(dirname "$0")/.." && pwd)"
GAME_DIR="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
WORK="$OUT/work"
: "${GODOT:?set GODOT to the Godot 4.5.1 mono editor binary}"
log() { echo "==> $*"; }

"$IOS/scripts/prepare-game.sh" "$GAME_DIR" "$WORK"
# shellcheck disable=SC1091
source "$WORK/game.env"

# The status screen tells the player where the app's folder is in Files.
python3 "$IOS/tools/projbin.py" edit "$WORK/bootstrap/project.binary" "$WORK/bootstrap/project.binary" \
    --set-string "ios_boot/app_name=$(sed -n 's/^config\/name="\(.*\)"/\1/p' "$IOS/game/project.godot")"

log "Assembling the iOS export project"
PROJ="$WORK/godot-project"
rm -rf "$PROJ"
cp -R "$IOS/game" "$PROJ"
cp "$IOS/aot/PrebuiltAot.csproj" "$PROJ/sts2.csproj"
# MSBuild reads environment variables as properties, so this also reaches the
# `dotnet publish` that Godot's exporter runs for NativeAOT.
export PrebuiltDir="$WORK/prebuilt"

mkdir -p "$PROJ/addons/ios_boot"
cp "$IOS/boot/addon/"* "$PROJ/addons/ios_boot/"
"$IOS/boot/build.sh" ios "$PROJ/addons/ios_boot" "$PCK_SIZE" "$GAME_VERSION"
"$IOS/scripts/fetch-gdextensions.sh" "$PROJ/addons" "${EXT_CACHE:-$WORK/ext-cache}"

log "Building the app"
BOOTSTRAP_OVERLAY="$WORK/bootstrap" APP_EXTRA_DIR="$WORK/app-extra" \
    "$IOS/scripts/build-godot-ios.sh" "$PROJ" StS2 "$OUT"

log "Checking the bundle"
APP_LIST="$(unzip -l "$OUT/StS2.ipa")"
for required in \
    "Frameworks/sts2.framework/sts2" \
    "Frameworks/libios_boot.ios.framework/libios_boot.ios" \
    "StS2.pck"; do
    if ! grep -q "Payload/[^/]*\.app/$required\$" <<<"$APP_LIST"; then
        echo "ERROR: $required missing from StS2.ipa" >&2
        grep "Frameworks/" <<<"$APP_LIST" | awk '{print $4}' | sed 's|.*\.app/||' | cut -d/ -f1-2 | sort -u >&2
        exit 1
    fi
done
grep "Frameworks/" <<<"$APP_LIST" | awk '{print $4}' | sed 's|.*\.app/||' | cut -d/ -f1-2 | sort -u

# libGodotFmod is linked at launch and resolves load_all_fmod_plugins from the
# main executable (FMOD's export plugin generates it). Without it the app dies
# at launch with "Symbol not found", so check before shipping.
CHECK="$(mktemp -d)"
unzip -q -o "$OUT/StS2.ipa" 'Payload/*.app/StS2' -d "$CHECK"
# Capture first: grep -q would close the pipe early and pipefail would report nm's SIGPIPE.
EXPORTS="$(xcrun nm -gU "$CHECK"/Payload/*.app/StS2)"
if ! grep -q ' _load_all_fmod_plugins$' <<<"$EXPORTS"; then
    echo "ERROR: app executable doesn't export load_all_fmod_plugins (FMOD export plugin didn't run?)" >&2
    exit 1
fi
rm -rf "$CHECK"
log "load_all_fmod_plugins is exported by the app executable"
log "Built StS2.ipa for game $GAME_VERSION (expects SlayTheSpire2.pck of $PCK_SIZE bytes)"
