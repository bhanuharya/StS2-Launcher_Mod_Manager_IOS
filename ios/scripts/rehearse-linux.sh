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

rm -rf "$WORK"/{steam,prepare,aot,run,home}
mkdir -p "$WORK/run" "$WORK/home"

log "Building the fake game"
GODOT="$GODOT_EDITOR" "$IOS/scripts/make-fake-game.sh" "$WORK/steam"

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
