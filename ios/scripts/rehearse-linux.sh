#!/usr/bin/env bash
# End-to-end rehearsal of the iOS game pipeline on Linux with the fake game
# (ios/test). It runs the same prepare-game.sh and bootstrap/ios_boot code as
# the iOS build. Only the last step differs: NativeAOT targets linux-x64, and
# the stock Godot mono Linux template stands in for the iOS app.
#
# Usage: rehearse-linux.sh <work_dir>
# Env:   GODOT_EDITOR    Godot 4.5.1 Linux editor (any flavour; writes project.binary)
#        GODOT_TEMPLATE  Godot 4.5.1 mono linux_release.x86_64 export template
set -euo pipefail

IOS="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$1"
WORK="$(cd "$1" && pwd)"
: "${GODOT_EDITOR:?}" "${GODOT_TEMPLATE:?}"
log() { echo "==> $*"; }

rm -rf "$WORK"/{src,steam,prepare,aot,run,home}
mkdir -p "$WORK/src" "$WORK/steam/data_sts2_windows_x86_64" "$WORK/run" "$WORK/home"

log "Building the fake game"
cp -r "$IOS/test/fakegame" "$IOS/test/fakesteam" "$WORK/src/"
dotnet build "$WORK/src/fakegame/sts2.csproj" -c ExportRelease -nologo -v q
BIN="$WORK/src/fakegame/.godot/mono/temp/bin/ExportRelease"

# Lay it out like the Windows depot: managed DLLs mixed with runtime and
# native files that select_assemblies.py has to skip.
DATA="$WORK/steam/data_sts2_windows_x86_64"
cp "$BIN"/*.dll "$DATA/"
FRAMEWORK="$(dirname "$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" { gsub(/[\[\]]/, "", $3); print $3 "/" $2 "/x" }' | tail -1)")"
cp "$FRAMEWORK/System.Runtime.dll" "$DATA/"
head -c 4096 /dev/urandom > "$DATA/steam_api64.dll"
echo '{"version": "v0.0.0-fake"}' > "$WORK/steam/release_info.json"

log "Packing the fake game's SlayTheSpire2.pck"
PACK="$WORK/src/pack"
mkdir -p "$PACK/scenes" "$PACK/src" "$PACK/addons/sentry"
printf 'extends SceneTree\nfunc _init():\n\tProjectSettings.save_custom(OS.get_environment("OUT"))\n\tquit()\n' > "$WORK/src/fakegame/save_settings.gd"
OUT="$PACK/project.binary" HOME="$WORK/home" "$GODOT_EDITOR" --headless --path "$WORK/src/fakegame" --script res://save_settings.gd >/dev/null 2>&1 || true
python3 "$IOS/tools/projbin.py" edit "$PACK/project.binary" "$PACK/project.binary" --set-string _custom_features=dotnet
cp "$WORK/src/fakegame/scenes/game.tscn" "$PACK/scenes/"
cp "$WORK/src/fakegame/src/"*.cs "$PACK/src/"
cp "$WORK/src/fakegame/addons/sentry/SentryInit.gd" "$PACK/addons/sentry/"
python3 "$IOS/tools/pck.py" build "$WORK/steam/SlayTheSpire2.pck" "$PACK"

log "prepare-game.sh"
"$IOS/scripts/prepare-game.sh" "$WORK/steam" "$WORK/prepare"
# shellcheck disable=SC1091
source "$WORK/prepare/game.env"

log "NativeAOT (linux-x64) of the patched assembly"
mkdir -p "$WORK/aot"
cp "$IOS/aot/PrebuiltAot.csproj" "$WORK/aot/sts2.csproj"
dotnet publish "$WORK/aot/sts2.csproj" -c ExportRelease -r linux-x64 --self-contained true -nologo -v q \
    -p:PrebuiltDir="$WORK/prepare/prebuilt" -o "$WORK/aot/out"

log "ios_boot (linux) + bootstrap pack"
"$IOS/boot/build.sh" linux "$WORK/run" "$PCK_SIZE" "$GAME_VERSION"
python3 "$IOS/tools/make_bootstrap.py" --overlay "$WORK/prepare/bootstrap" --addon "$IOS/boot/addon" \
    --out "$WORK/run/boot.pck"

cp "$GODOT_TEMPLATE" "$WORK/run/game.x86_64"
chmod +x "$WORK/run/game.x86_64"
mkdir -p "$WORK/run/data_sts2_linuxbsd_x86_64"
cp "$WORK/aot/out/sts2.so" "$WORK/run/data_sts2_linuxbsd_x86_64/"
USER_DIR="$WORK/home/.local/share/godot/app_userdata/Slay the Spire 2"
mkdir -p "$USER_DIR"
cp "$WORK/steam/SlayTheSpire2.pck" "$USER_DIR/"

log "Running"
cd "$WORK/run"
set +e
HOME="$WORK/home" timeout 120 ./game.x86_64 --headless --main-pack boot.pck 2>&1 | tee "$WORK/run.log" \
    | grep -E 'FAKEGAME|\[iOS\]|ios_boot|ERROR|Unhandled'
set -e
if grep -q "FAKEGAME RESULT PASS" "$WORK/run.log" && ! grep -q "sentryAutoload" "$WORK/run.log"; then
    log "REHEARSAL PASSED"
else
    echo "REHEARSAL FAILED (full log: $WORK/run.log)" >&2
    exit 1
fi
