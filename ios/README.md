# StS2 on iOS (experimental, no Mac required)

Goal: run your own copy of Slay the Spire 2 natively on an iPhone or iPad,
built in the cloud on GitHub's macOS runners and sideloaded from Windows or
Linux with a free Apple ID. Nothing in this repo contains game files.

**Status: phase 0, proving the toolchain.** The game itself is not running on iOS yet.

## Why this isn't just the Android launcher recompiled

The Android build loads the game's `sts2.dll` at runtime on Mono's JIT and
patches it live with Harmony. iOS doesn't allow JIT for sideloaded apps, so
none of that works there:

| Android launcher | iOS port |
|---|---|
| Mono JIT loads `sts2.dll` downloaded on the phone | C# must be compiled ahead of time (NativeAOT) **at build time**, so the build machine needs `sts2.dll` |
| Harmony patches the game at runtime | Patches have to be applied to `sts2.dll` before compiling (Mono.Cecil IL rewriting) |
| Workshop / DLL mods loaded at runtime | **Not possible.** Mods are compiled code, and iOS can't load new code. Unmodded only. |
| FMOD / Spine `.so` from the Android APK | Have to be rebuilt as iOS frameworks |

Godot 4.5.1 officially supports C# on iOS through NativeAOT. Our tools
replicate what the Godot editor's iOS export does, using the game's own
compiled assemblies in place of source code.

## Pipeline

```
GitHub Actions (macos runner)                Your PC (Windows/Linux)       iPhone
──────────────────────────────               ───────────────────────       ──────
Godot 4.5.1 mono + iOS template                                            
dotnet publish -r ios-arm64 (NativeAOT)  →   download .ipa artifact   →    Sideloadly
xcodebuild (signing disabled)                Sideloadly signs it            installs
zip → unsigned .ipa                          with your Apple ID             the app
```

A free Apple ID signature lasts **7 days** and allows 3 sideloaded apps at
once. Re-run Sideloadly weekly to refresh it. Your saves survive a refresh.

## Phase 0: probe app (do this first)

`ios/probe` is a tiny Godot C# app. It shows and saves the facts the real port
depends on. The most important is whether your GPU can read the desktop game's
BC-compressed textures (`Texture BC7: YES/NO`).

1. Open **Actions → "iOS probe (phase 0)"** in this repo and select the latest green run.
   Download the **StS2Probe-ipa** artifact and unzip it to get `StS2Probe.ipa`.
2. On Windows, install **iTunes and iCloud (the apple.com versions, not the Microsoft Store ones)**
   and **[Sideloadly](https://sideloadly.io)**. Plug in the iPhone and tap *Trust*.
3. Drag `StS2Probe.ipa` into Sideloadly, enter your Apple ID, and click **Start**.
   (Use a secondary Apple ID if you prefer. Sideloadly sends the login to Apple only.)
4. On the iPhone, open **Settings → Privacy & Security → Developer Mode**, turn it on,
   and restart when asked.
5. Go to **Settings → General → VPN & Device Management**, tap your Apple ID,
   and tap **Trust**.
6. Open **StS2 iOS Probe**. Touch the screen, or hover with an Apple Pencil, to check input.
7. Send back the report. Either take a screenshot, or open
   **Files → On My iPhone → StS2 iOS Probe → probe_report.txt**.

## Phase 1: the game (next)

Planned CI workflow `ios-game.yml`, manually triggered:

1. **Fetch your game files** from Steam with your account. The credentials are
   stored as repo secrets, and the Steam Guard code is entered when you start a
   run. Only the assemblies and the `.pck` metadata are needed in CI.
2. **Patch `sts2.dll`** with a Cecil tool (`ios/tools/IlPatcher`). It skips
   Steam init, Sentry, and telemetry, and fixes desktop-only paths. These are
   the same fixes as `src/STS2Mobile/Patches/PlatformPatches.cs`, applied
   statically.
3. **NativeAOT-compile** the patched `sts2.dll` and its dependencies into
   `sts2.dylib`, which exports `godotsharp_game_main_init`.
4. **Build iOS GDExtensions**:
   - fmod-gdextension 6.1.0-4.5.0. This needs the FMOD iOS SDK, so you'd need
     a free fmod.com account added as secrets.
   - spine-godot, from source.
   - Sentry gets dropped.
5. **Bootstrap pack.** A small main `.pck` that holds the game's project
   settings, with iOS library paths in the `.gdextension` files and the Sentry
   autoload removed. The big game `.pck` loads from the app's Documents folder.
6. **Encrypt the artifact.** This repo is **public** and the `.ipa` contains
   compiled game code. The workflow will put the `.ipa` in a password-protected
   zip, using a password secret only you know, with 1-day retention.

You then copy `SlayTheSpire2.pck` (about 3 GB) from your Windows Steam install
(`steamapps/common/Slay the Spire 2/`) into the app's folder. Use Windows'
*Apple Devices* app (or iTunes) → File Sharing, or the Files app.

## Phase 2: playability

Port the most important mobile fixes from the Android patches as static IL
patches: UI scale for phone screens, touch-friendly hover/tooltips, and app
suspend/resume handling.

## Known limits

- No Steam online features (achievements, cloud saves, Steam lobbies).
  Direct-IP / LAN multiplayer may work.
- No mods (see above).
- iPhone screen size: the desktop UI is small on a 6.3" display until phase 2.
- Godot's iOS C# support is marked experimental upstream.
