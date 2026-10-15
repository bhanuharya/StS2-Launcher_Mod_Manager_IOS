#!/usr/bin/env bash
# Platform-independent half of the iOS game build. Used by CI (real game) and
# rehearse-linux.sh (fake game) so both run the same steps.
#
# Usage: prepare-game.sh <game_dir> <work_dir>
#   game_dir: a Steam install of the game (Windows depot layout):
#             SlayTheSpire2.pck, data_*/ (assemblies), release_info.json
#
# Produces in work_dir:
#   assemblies/  the game's own managed DLLs, unmodified
#   prebuilt/    NativeAOT input: patched sts2.dll, its dependencies, Sts2IosSupport.dll
#   bootstrap/   files for the app's main pack: edited project.binary and the
#                game's .godot metadata (uid cache, script class cache)
#   app-extra/   files copied next to the app executable (release_info.json)
#   game.env     PCK_SIZE and GAME_VERSION, for ios_boot's version check,
#                and PCK_REPACKED (1 if the textures were converted)
#
# Env: REPACK_ASTC=1 also converts the pck's BC textures to ASTC first
# (Apple GPUs can't sample BC, so Godot would expand them to raw RGBA in
# memory — about 4x the VRAM and slower loads). Needs astcenc on PATH and
# `pip install texture2ddecoder`; skipped with a note when they're absent.
# game.env's PCK_REPACKED says whether it ran.
# REPACK_QUALITY overrides the astcenc effort (fast|medium|thorough|...).
set -euo pipefail

IOS="$(cd "$(dirname "$0")/.." && pwd)"
GAME_DIR="$(cd "$1" && pwd)"
mkdir -p "$2"
WORK="$(cd "$2" && pwd)"
log() { echo "==> $*"; }
fail() { echo "ERROR: $*" >&2; exit 1; }

PCK="$GAME_DIR/SlayTheSpire2.pck"
[ -f "$PCK" ] || fail "missing $PCK"
DATA_DIR="$(find "$GAME_DIR" -maxdepth 1 -type d -name 'data_*' | head -1)"
[ -n "$DATA_DIR" ] || fail "no data_* assemblies folder in $GAME_DIR"

# BC -> ASTC texture conversion, before anything records the pck's size
# (ios_boot checks PCK_SIZE, so the app and the repacked pck must ship
# together — the workflow puts this pck inside the encrypted artifact).
PCK_REPACKED=0
if [ "${REPACK_ASTC:-0}" = "1" ]; then
    if command -v astcenc >/dev/null 2>&1 || command -v astcenc-avx2 >/dev/null 2>&1 \
        || command -v astcenc-sse4.1 >/dev/null 2>&1 || command -v astcenc-sse2 >/dev/null 2>&1 \
        || command -v astcenc-neon >/dev/null 2>&1; then
        ASTCENC_OK=1
    else
        ASTCENC_OK=0
    fi
    if python3 -c "import texture2ddecoder" >/dev/null 2>&1 && [ "$ASTCENC_OK" = "1" ]; then
        log "BC -> ASTC texture conversion"
        python3 "$IOS/tools/repack_astc.py" selftest
        if [ -n "${REPACK_QUALITY:-}" ]; then
            python3 "$IOS/tools/repack_astc.py" repack "$PCK" "$GAME_DIR/.repacked.pck" --quality "$REPACK_QUALITY"
        else
            python3 "$IOS/tools/repack_astc.py" repack "$PCK" "$GAME_DIR/.repacked.pck"
        fi
        mv "$GAME_DIR/.repacked.pck" "$PCK"
        PCK_REPACKED=1
    else
        log "NOTE: REPACK_ASTC=1 but astcenc/texture2ddecoder not found; keeping BC textures"
    fi
fi

# Shared framework folder, used to tell the game's DLLs apart from the .NET
# runtime it ships. Prefer 9.x (the game's target).
FRAMEWORK_DIR="$(dotnet --list-runtimes | python3 -c '
import re, sys
found = []
for line in sys.stdin:
    m = re.match(r"Microsoft\.NETCore\.App (\S+) \[(.+)\]", line.strip())
    if m:
        found.append((tuple(int(x) for x in re.findall(r"\d+", m.group(1))[:3]), m.group(2) + "/" + m.group(1)))
found.sort()
nine = [p for v, p in found if v[0] == 9]
print((nine or [p for _, p in found] or [""])[-1])
')"
[ -d "$FRAMEWORK_DIR" ] || fail "no Microsoft.NETCore.App runtime found"

rm -rf "$WORK/assemblies" "$WORK/prebuilt" "$WORK/bootstrap" "$WORK/app-extra" "$WORK/pck-meta"

log "Selecting game assemblies from $(basename "$DATA_DIR")"
python3 "$IOS/tools/select_assemblies.py" "$DATA_DIR" "$FRAMEWORK_DIR" "$WORK/assemblies"

log "Building Sts2IosSupport and IlPatcher"
dotnet build "$IOS/support/Sts2IosSupport.csproj" -c Release -nologo -v q \
    -p:GameDir="$WORK/assemblies" -o "$WORK/support"
dotnet build "$IOS/tools/IlPatcher/IlPatcher.csproj" -c Release -nologo -v q -o "$WORK/patcher"

log "Patching sts2.dll"
mkdir -p "$WORK/prebuilt"
find "$WORK/assemblies" -maxdepth 1 -name '*.dll' ! -name 'sts2.dll' -exec cp {} "$WORK/prebuilt/" \;
cp "$WORK/support/Sts2IosSupport.dll" "$WORK/prebuilt/"
# The game's data folder lets the patcher resolve GodotSharp.dll and the .NET
# assemblies sts2.dll's attributes refer to; nothing is copied from it.
dotnet "$WORK/patcher/IlPatcher.dll" "$WORK/assemblies/sts2.dll" "$WORK/support/Sts2IosSupport.dll" \
    "$WORK/prebuilt/sts2.dll" "$DATA_DIR"

log "Extracting project settings from the game pck"
python3 "$IOS/tools/pck.py" extract "$PCK" "$WORK/pck-meta" \
    project.binary .godot/uid_cache.bin .godot/global_script_class_cache.cfg
[ -f "$WORK/pck-meta/project.binary" ] || fail "project.binary not found in $PCK"
mkdir -p "$WORK/bootstrap/.godot"
python3 "$IOS/tools/projbin.py" edit "$WORK/pck-meta/project.binary" "$WORK/bootstrap/project.binary" \
    --drop-autoload sentry \
    --set-bool debug/file_logging/enable_file_logging=true \
    --set-int display/window/handheld/orientation=4
for f in uid_cache.bin global_script_class_cache.cfg; do
    [ -f "$WORK/pck-meta/.godot/$f" ] && cp "$WORK/pck-meta/.godot/$f" "$WORK/bootstrap/.godot/"
done
python3 "$IOS/tools/projbin.py" list "$WORK/bootstrap/project.binary" \
    'application/run/*' 'autoload/*' 'rendering/renderer/*' 'dotnet/*' '_custom_features'

mkdir -p "$WORK/app-extra"
GAME_VERSION="unknown"
if [ -f "$GAME_DIR/release_info.json" ]; then
    cp "$GAME_DIR/release_info.json" "$WORK/app-extra/"
    GAME_VERSION="$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(next((str(d[k]) for k in ("version","Version") if k in d), "unknown"))' "$GAME_DIR/release_info.json")"
fi
PCK_SIZE="$(python3 -c 'import os,sys; print(os.path.getsize(sys.argv[1]))' "$PCK")"
printf 'PCK_SIZE=%s\nGAME_VERSION=%s\nPCK_REPACKED=%s\n' "$PCK_SIZE" "$GAME_VERSION" "$PCK_REPACKED" > "$WORK/game.env"
log "Game version $GAME_VERSION, pck $PCK_SIZE bytes"
