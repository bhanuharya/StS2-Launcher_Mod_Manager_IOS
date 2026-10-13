#!/usr/bin/env bash
# Builds an unsigned .ipa from a Godot 4.5.1 .NET project. macOS only, since
# Godot's iOS export and xcodebuild need Xcode. CI runs this on a GitHub macOS
# runner. Sign and install the result from Windows/Linux with Sideloadly.
#
# Usage: build-godot-ios.sh <project_dir> <binary_name> <out_dir>
# Env:   GODOT  path to the Godot 4.5.1 mono editor binary (required)
set -euo pipefail

PROJECT_DIR="$(cd "$1" && pwd)"
BINARY_NAME="$2"
OUT_DIR="$3"
: "${GODOT:?set GODOT to the Godot 4.5.1 mono editor binary}"

mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
LOG_DIR="$OUT_DIR/logs"
mkdir -p "$LOG_DIR"

log() { echo "==> $*"; }

log "Godot: $("$GODOT" --version)"
log "dotnet: $(dotnet --version)"
xcodebuild -version

# 1. Build the C# project once so the editor has script assemblies to load
#    while it imports and exports. The iOS publish (NativeAOT) happens later
#    inside the export itself.
CSPROJ="$(ls "$PROJECT_DIR"/*.csproj | head -1)"
log "Building $CSPROJ"
dotnet build "$CSPROJ" -c Debug 2>&1 | tee "$LOG_DIR/dotnet-build.log"

# 2. Import resources (creates .godot/).
log "Importing project"
"$GODOT" --headless --path "$PROJECT_DIR" --import 2>&1 | tee "$LOG_DIR/godot-import.log" || true

# 3. Export the Xcode project (export_project_only=true in the preset). This
#    also runs `dotnet publish` with NativeAOT for ios-arm64 and the simulator,
#    then wraps the result in <assembly>_aot.xcframework.
log "Exporting Xcode project"
"$GODOT" --headless --path "$PROJECT_DIR" --export-release "iOS" "$OUT_DIR/$BINARY_NAME.ipa" \
    2>&1 | tee "$LOG_DIR/godot-export.log" || true
if [ ! -d "$OUT_DIR/$BINARY_NAME.xcodeproj" ]; then
    echo "ERROR: Godot did not produce $OUT_DIR/$BINARY_NAME.xcodeproj (see godot-export.log)" >&2
    exit 1
fi

# 4. Compile the app with signing disabled. Sideloadly signs it later.
log "Building app with xcodebuild (unsigned)"
DERIVED="$OUT_DIR/DerivedData"
xcodebuild \
    -project "$OUT_DIR/$BINARY_NAME.xcodeproj" \
    -scheme "$BINARY_NAME" \
    -configuration Release \
    -sdk iphoneos \
    -destination "generic/platform=iOS" \
    -derivedDataPath "$DERIVED" \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY="" \
    DEVELOPMENT_TEAM="" \
    build 2>&1 | tee "$LOG_DIR/xcodebuild.log"

APP="$(find "$DERIVED/Build/Products/Release-iphoneos" -maxdepth 1 -name '*.app' | head -1)"
if [ -z "$APP" ]; then
    echo "ERROR: no .app produced (see xcodebuild.log)" >&2
    exit 1
fi

# Godot skips the C# export step without complaint when it doesn't detect a
# .NET project (it looks for <assembly>.sln). The app would then crash at
# launch, so require the NativeAOT library to be in the bundle.
ASSEMBLY="$(basename "$CSPROJ" .csproj)"
if [ ! -f "$APP/Frameworks/$ASSEMBLY.dylib" ]; then
    echo "ERROR: $ASSEMBLY.dylib (NativeAOT C#) missing from $APP/Frameworks" >&2
    ls -la "$APP" "$APP/Frameworks" 2>&1 || true
    exit 1
fi

# 5. Package as .ipa (a zip with Payload/<App>.app).
log "Packaging $APP"
STAGE="$OUT_DIR/ipa-stage"
rm -rf "$STAGE" && mkdir -p "$STAGE/Payload"
cp -R "$APP" "$STAGE/Payload/"
rm -f "$OUT_DIR/$BINARY_NAME.ipa"
(cd "$STAGE" && zip -qry "$OUT_DIR/$BINARY_NAME.ipa" Payload)
rm -rf "$STAGE"

log "App bundle contents:"
find "$APP" -maxdepth 2 \( -name '*.framework' -o -name '*.pck' -o -name '*.dylib' -o -name 'Info.plist' \) -exec du -sh {} + | sed "s|$APP/||"
log "Done: $OUT_DIR/$BINARY_NAME.ipa ($(du -h "$OUT_DIR/$BINARY_NAME.ipa" | cut -f1))"
