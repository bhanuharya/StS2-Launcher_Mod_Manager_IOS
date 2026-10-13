#!/usr/bin/env bash
# Lays out the fake game (ios/test) like a Windows Steam install of
# Slay the Spire 2, so the iOS pipeline can run without game files.
#
# Usage: make-fake-game.sh <out_dir>
# Env:   GODOT  any Godot 4.5.1 editor binary (used to write project.binary)
set -euo pipefail

IOS="$(cd "$(dirname "$0")/.." && pwd)"
: "${GODOT:?set GODOT to a Godot 4.5.1 editor binary}"
mkdir -p "$1"
OUT="$(cd "$1" && pwd)"
SRC="$(mktemp -d)"
trap 'rm -rf "$SRC"' EXIT

cp -r "$IOS/test/fakegame" "$IOS/test/fakesteam" "$SRC/"
dotnet build "$SRC/fakegame/sts2.csproj" -c ExportRelease -nologo -v q
BIN="$SRC/fakegame/.godot/mono/temp/bin/ExportRelease"

# Managed DLLs mixed with runtime and native files, like the real depot, so
# select_assemblies.py has something to skip.
DATA="$OUT/data_sts2_windows_x86_64"
rm -rf "$DATA" && mkdir -p "$DATA"
cp "$BIN"/*.dll "$DATA/"
RUNTIME="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" { gsub(/[\[\]]/, "", $3); print $3 "/" $2 }' | tail -1)"
cp "$RUNTIME/System.Runtime.dll" "$DATA/"
head -c 4096 /dev/urandom > "$DATA/steam_api64.dll"
echo '{"version": "v0.0.0-fake"}' > "$OUT/release_info.json"

PACK="$SRC/pack"
mkdir -p "$PACK/scenes" "$PACK/src" "$PACK/addons/sentry"
printf 'extends SceneTree\nfunc _init():\n\tProjectSettings.save_custom(OS.get_environment("SETTINGS_OUT"))\n\tquit()\n' \
    > "$SRC/fakegame/save_settings.gd"
SETTINGS_OUT="$PACK/project.binary" HOME="$SRC/home" "$GODOT" --headless --path "$SRC/fakegame" \
    --script res://save_settings.gd >/dev/null 2>&1 || true
[ -f "$PACK/project.binary" ] || { echo "ERROR: Godot did not write project.binary" >&2; exit 1; }
python3 "$IOS/tools/projbin.py" edit "$PACK/project.binary" "$PACK/project.binary" --set-string _custom_features=dotnet
cp "$SRC/fakegame/scenes/game.tscn" "$PACK/scenes/"
cp "$SRC/fakegame/src/"*.cs "$PACK/src/"
cp "$SRC/fakegame/addons/sentry/SentryInit.gd" "$PACK/addons/sentry/"
python3 "$IOS/tools/pck.py" build "$OUT/SlayTheSpire2.pck" "$PACK"
echo "fake game ready in $OUT"
