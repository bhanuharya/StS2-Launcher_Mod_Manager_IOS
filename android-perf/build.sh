#!/usr/bin/env bash
# Builds the perf APK from upstream's published release APK (see perf.env):
#
#   1. STS2Perf.dll, the add-on (Sts2Perf/), compiled against the launcher
#      DLLs inside the upstream APK. No game files or Steam login needed.
#   2. STS2Mobile.dll patched to load it, to check this fork's releases for
#      launcher updates, and with its Korean-only text in English
#      (translations.json) (tools/LauncherPatcher).
#   3. classes2.dex: java/, a provider that sets the .NET runtime's GC and
#      log options before it starts (see java/com/sts2perf/StartupEnv.java).
#   4. AndroidManifest.xml with its own package id, name and version, so it
#      installs next to the upstream app, and the provider (tools/axml.py).
#   5. Repacked, zipaligned and signed with your key.
#
# Usage: build.sh <out_dir>
# Env:   KEYSTORE           signing keystore (PKCS#12 or JKS)
#        KEYSTORE_PASSWORD  its password
#        KEY_ALIAS          optional; needed only if the keystore has several keys
#        UPDATE_REPO        owner/name whose releases the in-app updater checks
#                           (default: $GITHUB_REPOSITORY, else the origin remote)
#        APK_CACHE          optional download cache for the upstream APK
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck disable=SC1091
source "$HERE/perf.env"
mkdir -p "$1"
OUT="$(cd "$1" && pwd)"
WORK="$OUT/work"
log() { echo "==> $*"; }
fail() { echo "ERROR: $*" >&2; exit 1; }

: "${KEYSTORE:?set KEYSTORE to your signing keystore (see android-perf/README.md)}"
: "${KEYSTORE_PASSWORD:?set KEYSTORE_PASSWORD}"
[ -f "$KEYSTORE" ] || fail "keystore not found: $KEYSTORE"

if [ -z "${UPDATE_REPO:-}" ]; then
    UPDATE_REPO="${GITHUB_REPOSITORY:-$(git -C "$HERE" remote get-url origin 2>/dev/null \
        | sed -E 's#^.*github\.com[:/]([^/]+/[^/.]+)(\.git)?$#\1#')}"
fi
[[ "$UPDATE_REPO" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "set UPDATE_REPO=owner/name (got '$UPDATE_REPO')"

PERF_VERSION="${UPSTREAM_TAG#v}.$PERF_BUILD"
VERSION_CODE=$((UPSTREAM_VERSION_CODE * 100 + PERF_BUILD))
APK_NAME="StS2LauncherPerf-v$PERF_VERSION.apk"

# Android build-tools: on PATH, or the newest under $ANDROID_HOME (GitHub runners).
tool() {
    if command -v "$1" >/dev/null 2>&1; then
        command -v "$1"
        return
    fi
    local sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}" found=""
    [ -n "$sdk" ] && found="$(ls -d "$sdk"/build-tools/*/ 2>/dev/null | sort -V | tail -1)"
    [ -n "$found" ] && [ -x "$found$1" ] || fail "$1 not found (install Android build-tools)"
    echo "$found$1"
}
ZIPALIGN="$(tool zipalign)"
APKSIGNER="$(tool apksigner)"
# d8 turns the provider into dex (dex OUT_DIR CLASSES_DIR); Debian's
# packaged dx works as well.
if DEX="$(tool d8 2>/dev/null)"; then
    dex() { "$DEX" --min-api 24 --release --lib "$ANDROID_JAR" --output "$1" $(find "$2" -name '*.class'); }
elif DEX="$(command -v dalvik-exchange || command -v dx)"; then
    dex() { "$DEX" --dex --min-sdk-version=24 --output="$1/classes.dex" "$2"; }
else
    fail "d8 not found (install Android build-tools)"
fi
# android.jar to compile against: the newest platform under $ANDROID_HOME.
SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
ANDROID_JAR="$(ls "$SDK"/platforms/android-*/android.jar 2>/dev/null | sort -V | tail -1)"
[ -f "$ANDROID_JAR" ] || fail "android.jar not found (install an Android platform under \$ANDROID_HOME)"

rm -rf "$WORK"
mkdir -p "$WORK/base" "$WORK/patched"

CACHE="${APK_CACHE:-$OUT/cache}"
BASE_APK="$CACHE/$UPSTREAM_APK"
mkdir -p "$CACHE"
if [ ! -f "$BASE_APK" ]; then
    log "Downloading $UPSTREAM_REPO $UPSTREAM_TAG"
    curl -fsSL --retry 3 -o "$BASE_APK.part" \
        "https://github.com/$UPSTREAM_REPO/releases/download/$UPSTREAM_TAG/$UPSTREAM_APK"
    mv "$BASE_APK.part" "$BASE_APK"
fi
echo "$UPSTREAM_APK_SHA256  $BASE_APK" | sha256sum -c --quiet - \
    || fail "$UPSTREAM_APK doesn't match UPSTREAM_APK_SHA256 in perf.env"

log "Extracting the launcher from $UPSTREAM_APK"
unzip -q -o "$BASE_APK" AndroidManifest.xml \
    assets/dotnet_bcl/STS2Mobile.dll assets/dotnet_bcl/GodotSharp.dll assets/dotnet_bcl/0Harmony.dll \
    -d "$WORK/base"
REFS="$WORK/base/assets/dotnet_bcl"

log "Building STS2Perf $PERF_VERSION"
dotnet build "$HERE/Sts2Perf/Sts2Perf.csproj" -c Release -nologo -v q \
    -p:RefDir="$REFS" -p:PerfVersion="$PERF_VERSION" -o "$WORK/addon"

log "Patching STS2Mobile.dll"
dotnet build "$HERE/tools/LauncherPatcher/LauncherPatcher.csproj" -c Release -nologo -v q -o "$WORK/patcher"
# GodotApp.setupAssemblies() copies assets/dotnet_bcl to <files dir>/.godot/mono/publish/arm64.
dotnet "$WORK/patcher/LauncherPatcher.dll" "$REFS/STS2Mobile.dll" "$WORK/patched/STS2Mobile.dll" \
    --string "https://api.github.com/repos/$UPSTREAM_REPO/releases/latest" \
             "https://api.github.com/repos/$UPDATE_REPO/releases/latest" \
    --string "https://github.com/$UPSTREAM_REPO/releases/latest" \
             "https://github.com/$UPDATE_REPO/releases/latest" \
    --string "/data/user/0/$UPSTREAM_PACKAGE/cache/MonoMod" \
             "/data/user/0/$PERF_PACKAGE/cache/MonoMod" \
    --translations "$HERE/translations.json" \
    --addon STS2Perf.dll STS2Perf.PerfEntry \
    --addon-dir "/data/user/0/$PERF_PACKAGE/files/.godot/mono/publish/arm64"

log "Building the startup provider"
unzip -Z1 "$BASE_APK" | grep -qx classes2.dex && fail "$UPSTREAM_APK already has a classes2.dex"
mkdir -p "$WORK/java" "$WORK/dex"
javac --release 8 -nowarn -cp "$ANDROID_JAR" -d "$WORK/java" $(find "$HERE/java" -name '*.java')
dex "$WORK/dex" "$WORK/java"

log "Editing AndroidManifest.xml"
python3 "$HERE/tools/axml.py" edit "$WORK/base/AndroidManifest.xml" "$WORK/patched/AndroidManifest.xml" \
    --package "$PERF_PACKAGE" --label "$PERF_LABEL" \
    --version-name "$PERF_VERSION" --version-code "$VERSION_CODE" \
    --add-provider "com.sts2perf.StartupEnv=$PERF_PACKAGE.sts2perf-env"

log "Repacking"
python3 "$HERE/tools/repack.py" "$BASE_APK" "$WORK/unsigned.apk" \
    --put AndroidManifest.xml="$WORK/patched/AndroidManifest.xml" \
    --put assets/dotnet_bcl/STS2Mobile.dll="$WORK/patched/STS2Mobile.dll" \
    --put assets/dotnet_bcl/STS2Perf.dll="$WORK/addon/STS2Perf.dll" \
    --put classes2.dex="$WORK/dex/classes.dex"
"$ZIPALIGN" -f -p 4 "$WORK/unsigned.apk" "$WORK/aligned.apk"

log "Signing"
SIGN_ARGS=(--ks "$KEYSTORE" --ks-pass env:KEYSTORE_PASSWORD)
[ -n "${KEY_ALIAS:-}" ] && SIGN_ARGS+=(--ks-key-alias "$KEY_ALIAS")
export KEYSTORE_PASSWORD
rm -f "$OUT/$APK_NAME"
"$APKSIGNER" sign "${SIGN_ARGS[@]}" --out "$OUT/$APK_NAME" "$WORK/aligned.apk"
"$APKSIGNER" verify "$OUT/$APK_NAME"
"$ZIPALIGN" -c -p 4 "$OUT/$APK_NAME"

# The manifest must read back as the new identity.
python3 "$HERE/tools/axml.py" dump "$WORK/patched/AndroidManifest.xml" \
    | grep -q "^<manifest> package = '$PERF_PACKAGE'$" || fail "manifest check failed"
python3 "$HERE/tools/axml.py" dump "$WORK/patched/AndroidManifest.xml" \
    | grep -q "^<provider> name = 'com.sts2perf.StartupEnv'$" || fail "manifest check failed (provider)"

cat > "$OUT/build.env" <<EOF
PERF_VERSION=$PERF_VERSION
VERSION_CODE=$VERSION_CODE
APK_NAME=$APK_NAME
PERF_PACKAGE=$PERF_PACKAGE
UPSTREAM_TAG=$UPSTREAM_TAG
EOF
log "Built $OUT/$APK_NAME ($PERF_PACKAGE $PERF_VERSION, code $VERSION_CODE; updates from $UPDATE_REPO)"
