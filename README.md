# Slay the Spire 2 on Android and iOS

**English** | [한국어](README.ko.md)

Play your own Steam copy of **Slay the Spire 2** on Android phones and
tablets, and on iPhone and iPad.

This repository is a fork of [iunius612/StS2-Launcher_Mod_Manager](https://github.com/iunius612/StS2-Launcher_Mod_Manager),
the Android launcher and mod manager. It adds an **iOS port** (`ios/`) and a
**performance build for lower-end Android devices** (`android-perf/`). The
Android launcher's own code (`src/`, `android/`) is upstream's, unchanged; its
releases and issues live upstream.

| | Android launcher | Android Perf | iPhone / iPad |
|---|---|---|---|
| What it is | The upstream launcher and mod manager | The upstream launcher with performance settings and English text | A native iOS build of your own copy of the game |
| Get it | APK from [upstream Releases](https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases/latest) | APK from [this fork's Releases](../../releases/latest) | Build it with GitHub Actions on your fork, install with Sideloadly |
| Steam login and download | In the app | In the app | On GitHub's build machine; game data comes from the build's download |
| Steam Cloud saves | Yes | Yes | No, saves are copied by hand |
| Mods and Workshop | Yes | Yes | No |
| Performance settings | No | Yes | No |
| Status | Stable | Stable | Experimental. The game runs (v0.107.1), with rough edges |

> **Disclaimer.** Unofficial community project. Slay the Spire 2 is developed
> and published by Mega Crit Games. You need a Steam account that owns the
> game. Game files come from Steam with your login; none are in this
> repository, and the iOS build is only ever uploaded encrypted.

- [Android](#android)
  - [Install the launcher](#install-the-launcher)
  - [Android perf build (lower-end devices)](#android-perf-build-lower-end-devices)
  - [Mods](#mods)
  - [LAN multiplayer](#lan-multiplayer)
  - [What the launcher adds](#what-the-launcher-adds)
- [iPhone and iPad (experimental)](#iphone-and-ipad-experimental)
- [Development](#development)
- [License](#license)

## Android

### Install the launcher

1. Download the latest `StS2Launcher-vX.Y.Z.apk` from the
   [upstream Releases page](https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases/latest)
   and install it.
2. Open it, allow "All files access", and log in with Steam (Steam Guard is
   supported).
3. Pick a Steam branch, download the game (about 3 GB), and tap **PLAY**.

Updating is a plain APK install over the old one; saves, login and the game
download are kept. Step-by-step guide with screenshots (Korean):
[docs/USER_GUIDE.md](docs/USER_GUIDE.md).

### Android perf build (lower-end devices)

**StS2 Launcher Perf** is the upstream APK with a small add-on, published on
[this fork's Releases page](../../releases/latest). It was made on a Xiaomi
Pad 5.

- **Perf settings** button next to **Debug**: FPS cap, vsync, particle
  amount, a lower render resolution, full-screen effects, and a frame-time log
  for benchmarking. The defaults change nothing.
- Settings live in a text file the app re-reads every 2 seconds, so they can
  also be changed over adb while you play.
- The launcher's Korean-only dialogs are in English.
- Its own package (`com.game.sts2launcher.modmanager.perf`), so it installs
  **next to** the original app without touching its data.

On a 120 Hz tablet, the biggest win was the FPS cap: 40 or 60 fps (a divisor
of the refresh rate) instead of the game's 30. Details, the settings file, and
how releases are built: [android-perf/README.md](android-perf/README.md).

### Mods

**Workshop (recommended):** tap **MOD MANAGER** on the launch screen, browse
or search the WORKSHOP tab, and subscribe. Downloads, installs and updates are
automatic.

**Manual:** put each mod in its own folder under
`/storage/emulated/0/StS2LauncherMM/Mods/`. A mod folder holds the mod's
`.dll`, an optional `.pck`, and a `<ModId>.json` manifest, the same layout as
`Steam\steamapps\common\Slay the Spire 2\mods\` on PC.

![Mods folder layout](docs/images/mods_folder.jpg)

Tap PLAY and accept the game's "Load mods?" dialog; the game restarts once
with mods loaded. If a mod doesn't show up, turn on the Debug toggle and look
for `[Mods]` lines in the log.

Mods that call **Steamworks.NET** directly (for example QuickReload) crash on
PLAY: Valve doesn't ship the Steamworks library for Android, and the
launcher's stub only satisfies the linker. Some Steamworks features (SteamID,
auth tickets, achievements, cloud, UGC, leaderboards) could be bridged through
SteamKit2 per mod; open an issue upstream if you need one. Steam networking
P2P, the overlay and Steam Input can't be.

### LAN multiplayer

Both devices must be on the same network. The app finds nearby games by UDP
broadcast, or you can enter the PC's IP address.

On the PC, add `--fastmp` to the launch options (**Steam → Slay the Spire 2 →
Properties → Launch Options**). Keep the PC and the phone on the **same Steam
branch**; cloud saves from one branch can't be read on another.

### What the launcher adds

The launcher descends from [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher),
whose mod loader stopped working with newer game builds. Upstream (iunius612)
added the following; per-version notes are on the
[upstream Releases page](https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases).

- **Mod loading that follows game updates.** A Harmony IL transpiler points
  the game's own mod scanner at `StS2LauncherMM/Mods/`, kept working through
  the v0.107 and v0.108 changes. `tools/memberref-audit` checks the patches
  against each new game build before release.
- **Steam Workshop (Mod Hub).** Browse, search, subscribe, update, and stash
  mods without deleting them, using SteamKit2; no PC needed.
- **Save safety.** A Save Manager with an explicit Keep Local / Keep Cloud
  choice on every PLAY, a guard against empty or abnormal cloud writes, and
  manual plus automatic local backups.
- **Quality of life.** In-app game and launcher updates, a Steam branch
  picker, landscape rotation and foldable/split-screen layouts, smooth touch
  scrolling, and a Debug toggle that saves logs to `StS2LauncherMM/Logs/`.
- **Mod compatibility shims.** BaseLib 3.x and its dependents load (its async
  hooks are disabled on Android), `release_info.json` is provided for the
  version label and LAN handshake, and several LAN fixes (4 players,
  persistent client IDs, stale cloud runs).

Core features: SteamKit2 login with tokens encrypted by Android Keystore,
Steam depot download with branch selection, full Steam Cloud sync, LAN
multiplayer, and engine patches for shader stutter (Vulkan pipeline cache
persistence and canvas ubershaders).

## iPhone and iPad (experimental)

Runs your own copy of the game **natively** on iOS. The real game (v0.107.1)
starts and plays on an iPhone.

The Android launcher can't simply be rebuilt for iOS, because iOS doesn't
allow the JIT compiler that runtime patching and mod loading need. So the iOS
port works differently:

- **Compiled ahead of time.** A GitHub macOS runner downloads the game's code
  from Steam with your login, applies the mobile fixes to `sts2.dll`, and
  compiles everything to native ARM64 with .NET NativeAOT. No Mac needed on
  your side.
- **Your game data stays yours.** The app doesn't contain
  `SlayTheSpire2.pck`. You copy it from your PC, and the app checks that it
  matches the build.
- **Private by default.** This repository is public, so the app is only
  uploaded inside a zip encrypted with your password.

### Known issues

- **Short freezes.** Now and then the game stops for a moment and then carries
  on; it doesn't crash. Being looked into.
- **No Steam features.** No cloud saves, achievements or Steam lobbies. Saves
  are copied between PC and phone by hand.
- **No mods.** Mods are compiled code, and iOS can't load new code.
- **Re-sign every 7 days.** A free Apple ID signature lasts 7 days; refresh it
  with Sideloadly. In October 2026, SideStore and AltStore sign-ins were
  failing for everyone (Apple error -80045); Sideloadly on a PC with Apple's
  iTunes and iCloud worked.
- **Small UI on phones.** It's the PC interface; the Android launcher's mobile
  UI fixes aren't ported yet.

### What you need

- A Steam account that owns the game, with **Steam Guard in the Steam app**
  (or e-mail codes).
- A GitHub account with a fork of this repository.
- A **Windows PC or Mac** with Steam (for `SlayTheSpire2.pck` and your saves)
  and **Sideloadly**, plus Apple's **iTunes** and **iCloud** from apple.com
  (not the Microsoft Store versions).
- An iPhone or iPad on iOS 16 or later, a USB data cable, and an Apple ID (a
  spare one is fine).

### Set up once

1. In your fork, open the **Actions** tab and enable workflows.
2. **Settings → General → Features:** turn on **Issues**. The build asks for
   your Steam Guard code there.
3. **Settings → Secrets and variables → Actions:** add `STEAM_USERNAME`,
   `STEAM_PASSWORD`, and `IPA_PASSWORD` (a password you make up for the
   download).
4. On the iPhone: **Settings → Privacy & Security → Developer Mode** on.

### Build and install

1. **Actions → "iOS game build" → Run workflow**, source `steam`.
2. About a minute later GitHub notifies you of an issue, **"Steam Guard code
   for iOS build #N"**. Open the Steam app's **Steam Guard** tab and reply
   with the code. If the Steam app then asks to approve a sign-in, **don't
   approve it**; Steam blocks approvals from the build machine's location,
   and the code already signed it in.
3. After about 15 minutes, download the **StS2-ios-…** artifact from the run
   (kept for 3 days). Open the zip inside it with **7-Zip** and your
   `IPA_PASSWORD`.
4. Install `StS2.ipa` with **Sideloadly** over USB. Then on the iPhone:
   **Settings → General → VPN & Device Management** → your Apple ID →
   **Trust**.
5. Copy `SlayTheSpire2.pck` **from the same zip** (the build converts its
   BC textures to ASTC — Apple GPUs can't read BC — and the app checks that
   the file matches the build) into the app's folder: in the **Apple
   Devices** app (Windows 11) or **iTunes → File Sharing** (Windows 10),
   choose **StS2** and add the file. It ends up in **Files → On My iPhone →
   StS2**.
6. Open **StS2**. If the `.pck` is missing or from another game version, the
   app tells you instead of starting.

When Steam updates the game, build again and copy the new `.pck`; the two
have to match. Full guide, troubleshooting, and how the build works:
**[ios/README.md](ios/README.md)**.

### Future improvements

Planned, roughly in this order. The daily update watch (4) is built; the
rest are not.

1. **Find and fix the short freezes.** The next build logs every slow frame
   with its cause (graphics pipelines compiled, textures loaded, or .NET
   garbage collection).
2. **Steam login in the app, with Steam Cloud saves.** The Android launcher
   talks to Steam through SteamKit2, a pure .NET library, so the same login
   and the Save Manager (Keep Local / Keep Cloud) can be compiled into the iOS
   build. Signing in from your own phone also avoids Steam blocking the build
   machine's location. This replaces copying saves by hand. The first step is
   checking that SteamKit2 works when compiled ahead of time.
3. **Download `SlayTheSpire2.pck` in the app** from Steam, as the Android
   launcher downloads the game, so the PC is no longer needed for game data.
4. **Automatic builds on game updates — built.** A daily workflow asks
   Steam anonymously for the game's current build and starts a new iOS
   build (with `STEAM_REFRESH_TOKEN`) whenever it changed; see
   *Updates* in [ios/README.md](ios/README.md). Updating then means one
   Sideloadly install, which a free Apple ID needs every 7 days anyway.
5. **Phone-friendly UI.** Port the Android launcher's mobile fixes as
   build-time patches: UI scale for phone screens, touch-friendly tooltips,
   and suspend/resume handling.
6. **Achievements**, probably possible through the same Steam connection.

What iOS rules out:

- **Game updates without reinstalling.** A game update changes the game's
  code, and iOS doesn't let an app load new code, so every update is a new
  build and a reinstall. Running new code would take a Godot build with a
  .NET interpreter: a large engine project, and the game would run
  noticeably slower.
- **Mods**, for the same reason.
- **Steam lobbies and P2P multiplayer**, and the Steam overlay.

## Development

### Android launcher

At startup, `STS2Mobile.dll` is loaded via `coreclr_create_delegate` and
applies [Harmony](https://github.com/pardeike/Harmony) patches that adapt the
desktop game to mobile. The launcher intercepts `GameStartupWrapper()` to
show a Steam login screen before the game starts. With no game files present,
a small `bootstrap.pck` provides just the launcher UI.

The engine is Godot 4.5.1 with Android patches: Vulkan pipeline cache
persistence (saved when the app loses focus, so Android killing the process
doesn't force recompiles) and canvas ubershaders (no blocking pipeline
compiles for 2D effects).

Building needs binaries that aren't in this repository: the custom Godot
engine ([godotengine/godot](https://github.com/godotengine/godot) based),
Harmony ([Ekyso/Harmony](https://github.com/Ekyso/Harmony), built for .NET 9),
the game files in `upstream/godot-export/`, and the FMOD SDK in
`vendor/fmod-sdk/`. FMOD and Spine can't be redistributed; see
[THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md). With those in place
(.NET 9 SDK, Android SDK and NDK per `android/config.gradle`, Python 3):

```bash
bash scripts/build.sh      # publish the patcher, copy DLLs, build the APK
adb install -r android/build/outputs/apk/mono/release/StS2Launcher-v*.apk
```

Other scripts: `scripts/make-bootstrap-pck.py` (only if `project.godot`
changes), `scripts/build-godot.sh` (engine), `src/stubs/build_stubs.sh`
(no-op Steam API and Sentry libraries, needs the NDK).

### Android perf build

Built on GitHub from upstream's published APK; no game files and no engine
build involved. A Mono.Cecil patcher makes the launcher load an add-on DLL,
and the add-on hooks the launcher with Harmony. See
[android-perf/README.md](android-perf/README.md).

### iOS port

Built entirely on GitHub's macOS runners. `ios/scripts/rehearse-linux.sh`
runs the platform-independent half on Linux against a stand-in game, and
pushes to `ios/` run the full iOS build on that stand-in game in CI. See
[ios/README.md](ios/README.md).

### Project structure

```
src/STS2Mobile/            Android launcher: entry point, Harmony patches,
                           launcher UI, Steam login/download/cloud/Workshop
src/stubs/                 No-op native libraries (Steam API, Sentry)
android/                   Godot Android Gradle project
tools/memberref-audit/     Checks the IL patches against a new game build
scripts/                   Android build scripts
docs/                      User guide (Korean) and images
android-perf/              Android perf build: add-on, patcher, APK tools
ios/                       iOS port
  probe/                   Small test app (device and toolchain check)
  game/                    Stub Godot project that drives the iOS export
  aot/                     NativeAOT of the prebuilt sts2.dll
  support/                 Managed hooks compiled into the iOS build
  boot/                    ios_boot GDExtension: mounts the game's .pck
  tools/                   IL patcher, .pck and project.binary tools,
                           SteamFetch (Steam download), Steam Guard relay
  scripts/                 Build scripts and the Linux rehearsal
  test/                    Stand-in game for pipeline tests
.github/workflows/         iOS game and probe builds, SteamFetch, Android perf
```

## License

[MIT License](LICENSE). Third-party licenses:
[THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md). FMOD requires a
commercial license if your project makes money; the Spine Runtimes require a
Spine Editor license.
