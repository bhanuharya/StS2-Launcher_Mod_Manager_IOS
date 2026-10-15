<!-- launcher-dialog -->
StS2 Launcher Perf {{VERSION}} (based on upstream {{UPSTREAM_TAG}}): English launcher, and a Perf settings panel next to the Debug button (FPS cap, vsync, particles, resolution, effects, frame-time log with the CPU/GPU split and what caused each stutter, faster card rendering, card trails off, idle transition layer skip, a one-shot scene probe that measures what the fight screen costs the GPU, experimental JIT warm-up).
<!-- /launcher-dialog -->

**StS2 Launcher Perf {{VERSION}}**: the [StS2 Launcher Mod Manager](https://github.com/{{UPSTREAM_REPO}}) {{UPSTREAM_TAG}} release with a performance add-on, for lower-end Android devices.

It installs **next to** the original app (package `{{PACKAGE}}`, app name "StS2 Launcher Perf"), so your current install and its data stay untouched.

### What's different from upstream {{UPSTREAM_TAG}}

- **Perf settings** button next to **Debug**: FPS cap, vsync, particles, resolution, full-screen effects, **Card rendering → Fast** (cards drawn without their offscreen pass: a fight on a Pad 5 at full resolution went from ~25 to ~15 ms of GPU per frame), **Card trails → Off**, **Idle transition layer → Skip**, a frame-time log that splits frame time into game logic, CPU drawing and GPU, and names what caused each stutter (JIT compiles, GC, shaders, textures, new nodes), a one-shot **Scene probe** that measures what each part of the fight screen costs the GPU, and an experimental **JIT warm-up** that compiles the game code earlier sessions needed at the main menu, so it isn't compiled mid-fight. Defaults change nothing. Settings live in `sts2perf.cfg`, which the app re-reads every 2 seconds, so they can also be changed over adb while playing; see the README.
- The launcher's Korean-only dialogs (Save Manager, backups, cloud sync, Mod Hub messages) are in English.
- **CHECK LAUNCHER UPDATE** looks for new versions on this fork's Releases page.

Everything else (Steam login, downloads, Workshop, saves, cloud sync, LAN) is upstream's code, unchanged.

### Install

1. Download `{{APK}}` below and install it. Allow installs from your browser or file manager if Android asks.
2. Open **StS2 Launcher Perf**, allow "All files access", log in to Steam, and download the game (about 3 GB).
3. Your saves come back through the Save Manager / Steam Cloud. Mods in `/storage/emulated/0/StS2LauncherMM/Mods` are shared with the original app.

Details and build instructions: [android-perf/README.md](https://github.com/{{REPO}}/blob/{{SHA}}/android-perf/README.md)

Unofficial community build. Slay the Spire 2 is by Mega Crit; you need to own it on Steam.
