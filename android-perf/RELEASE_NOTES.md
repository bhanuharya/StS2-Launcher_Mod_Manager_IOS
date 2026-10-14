<!-- launcher-dialog -->
StS2 Launcher Perf {{VERSION}} (based on upstream {{UPSTREAM_TAG}}). Graphics: Fast renders the game at a lower resolution for smoother play on weaker devices; switch it next to the Debug button.
<!-- /launcher-dialog -->

**StS2 Launcher Perf {{VERSION}}**: the [StS2 Launcher Mod Manager](https://github.com/{{UPSTREAM_REPO}}) {{UPSTREAM_TAG}} release with a performance add-on, for lower-end Android devices.

It installs **next to** the original app (package `{{PACKAGE}}`, app name "StS2 Launcher Perf"), so your current install and its data stay untouched.

### What's different from upstream {{UPSTREAM_TAG}}

- **Graphics: Fast** (default): the game renders at its layout resolution (about 1080p) and is scaled up to the screen. On a 2560×1600 tablet that's 1728×1080, under half the pixels. Fast also turns off HDR 2D, FXAA and debanding if the game uses them. On a 1080p phone at 100% UI scale, the resolution doesn't change.
- **Graphics: Sharp**: full resolution, exactly like upstream.
- The switch is on the launcher's main screen, next to **Debug**. It takes effect when the game starts.
- The launcher's Korean-only dialogs (Save Manager, backups, cloud sync, Mod Hub messages) are in English.
- **CHECK LAUNCHER UPDATE** looks for new versions on this fork's Releases page.

Everything else (Steam login, downloads, Workshop, saves, cloud sync, LAN) is upstream's code, unchanged.

### Install

1. Download `{{APK}}` below and install it. Allow installs from your browser or file manager if Android asks.
2. Open **StS2 Launcher Perf**, allow "All files access", log in to Steam, and download the game (about 3 GB).
3. Your saves come back through the Save Manager / Steam Cloud. Mods in `/storage/emulated/0/StS2LauncherMM/Mods` are shared with the original app.

Details and build instructions: [android-perf/README.md](https://github.com/{{REPO}}/blob/{{SHA}}/android-perf/README.md)

Unofficial community build. Slay the Spire 2 is by Mega Crit; you need to own it on Steam.
