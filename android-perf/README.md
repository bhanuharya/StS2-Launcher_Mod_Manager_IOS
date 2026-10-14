# StS2 Launcher Perf (Android, for lower-end devices)

A build of the upstream [StS2 Launcher Mod Manager](https://github.com/iunius612/StS2-Launcher_Mod_Manager)
release with a small add-on: an English launcher, performance settings you
can change without rebuilding the APK, and a frame-time log for measuring
them. It was made on and for a Xiaomi Pad 5 (Snapdragon 860, 2560×1600).

Download it from this repo's **Releases** page (`StS2LauncherPerf-v….apk`).

## What it changes

| | Upstream | Perf build |
|---|---|---|
| App | `com.game.sts2launcher.modmanager`, "StS2 Launcher Mod" | `com.game.sts2launcher.modmanager.perf`, "StS2 Launcher Perf": installs **next to** the original |
| Launcher text | Many dialogs only in Korean (Save Manager, backups, cloud sync, Mod Hub messages) | English everywhere |
| Performance settings | — | **Perf settings** panel and `sts2perf.cfg`; defaults change nothing |
| Frame-time log | — | Optional `[Perf]` stats line every N seconds |
| Launcher updates | Upstream releases | This fork's releases |

Everything else is upstream's code, unchanged: Steam login, downloads,
Workshop, mods, Save Manager, cloud sync, LAN.

## Performance settings

Tap **Perf settings** (next to **Debug**, above the launcher's console). Each
row's button steps through its values; changes are saved right away and apply
when the game starts.

| Setting | Values | What it does |
|---|---|---|
| Resolution | Full, Low res | Low res renders at the game's layout size (1728×1080 on a 2560×1600 tablet) and scales up. Softer, and on a Pad 5 it measured *worse* than Android's Game Mode downscale (below). |
| HDR / FXAA / debanding | As the game sets, Off | Turns those full-screen effects off if the game uses them. The log says which were on. |
| FPS cap | Game setting, 30, 40, 45, 60, 90, 120 | Overrides the game's FPS limit. Pick a divisor of the display's refresh rate (40 or 60 on 120 Hz) for even pacing. |
| VSync | Game setting, On, Off, Adaptive, Mailbox | Overrides the game's vsync. |
| Particles | 100%, 75%, 50%, 25%, Off | Scales how many particles every emitter spawns. |
| Frame-time log | Off, 5 s, 10 s, 30 s | See below. |

"Game setting" / 100% / Full / "As the game sets" leave the game exactly as
upstream ships it, and turning a setting back restores the game's own value.

**What mattered on the Pad 5:** the game's own FPS limit. At 30 fps on a
120 Hz panel, frames land unevenly (120/31 isn't a whole number) and it reads
as choppy. 60 fps with vsync on and the display at 60 Hz was smooth with the
GPU at mid clock; 120 fps pins it at max clock (~90% busy).

**Game Mode downscale (root):** renders at a lower resolution and lets the
compositor scale it, which costs less than Low res. It's per app, so set it
for the perf package:

```bash
adb shell su -c 'device_config put game_overlay com.game.sts2launcher.modmanager.perf "mode=2,downscaleFactor=0.75:mode=3,downscaleFactor=0.6"'
adb shell su -c 'cmd game mode performance com.game.sts2launcher.modmanager.perf'
# undo: device_config delete game_overlay <package>; cmd game mode standard <package>
```

## Settings file, live changes and benchmarking

Everything in the panel lives in
`/data/data/com.game.sts2launcher.modmanager.perf/files/sts2perf.cfg`, one
`key=value` per line (`#` starts a comment). **While the game runs, the
add-on re-reads the file every 2 seconds and applies changes**, so a script
can switch settings mid-fight with no restart and no rebuild:

| Key | Values | Default |
|---|---|---|
| `resolution` | `full`, `reduced` | `full` |
| `effects` | `on`, `off` | `on` |
| `max_fps` | `0` (game setting) or a number | `0` |
| `vsync` | `game`, `on`, `off`, `adaptive`, `mailbox` | `game` |
| `particles` | `0`–`100` (percent) | `100` |
| `frame_log` | seconds between stats lines, `0` = off | `0` |
| `root.<property>` | any property of the game's root window, e.g. `root.msaa_2d=0`, `root.use_hdr_2d=false`, `root.canvas_item_default_texture_filter=1` | — |
| `engine.<property>` | any property of `Engine`, e.g. `engine.physics_ticks_per_second=30`, `engine.max_physics_steps_per_frame=4` | — |

`root.*` and `engine.*` take bools (`true`/`false`), numbers, and enum values
as numbers, matched to the property's type. Removing a line puts the game's
value back. The older `graphics=fast|sharp` key from 0.4.2.1–0.4.2.3 still
works when `resolution` and `effects` are absent.

```bash
PKG=com.game.sts2launcher.modmanager.perf
CFG=/data/data/$PKG/files/sts2perf.cfg
adb shell su -c "printf 'frame_log=10\\nmax_fps=40\\n' > $CFG && chown \$(stat -c %u:%g /data/data/$PKG/files) $CFG && restorecon $CFG"
adb logcat | grep --line-buffered '\[Perf\]'
```

(The `chown`/`restorecon` keep the file writable by the app, so the in-app
panel can still save.)

Every change is logged (`[Perf] settings changed: …`, then what was applied),
and with `frame_log` set, a line like this every N seconds:

```
[Perf] frames 10s: 59.8 fps | ms p50 16.7 p95 17.9 p99 24.1 max 41.3 | hitches 3 | cap 60 | vsync Enabled | render 2560x1600 of 2560x1600 | resolution full | effects on (hdr2d off) | particles 100%
```

Frame times are the game's main-loop iterations, which on Android is also the
render thread. `hitches` counts frames slower than 1.5× the median. Android
can't time this app's frames from outside (its SurfaceView has no per-frame
latency data), so this is the way to compare settings.

## Install

1. Download `StS2LauncherPerf-v<version>.apk` from **Releases** and install it.
2. Open **StS2 Launcher Perf**, allow "All files access", log in to Steam, and
   download the game (about 3 GB). It keeps its own copy, separate from the
   original app.
3. Your progress comes back through the Save Manager / Steam Cloud on the
   first PLAY. The `/storage/emulated/0/StS2LauncherMM/` folder (mods, local
   backups, logs) is shared by both apps.

Once you're happy with it, you can uninstall the original app.

**Rooted devices: skip the 3 GB download.** Open the perf app once, close it,
then copy the game from the original app over adb:

```bash
adb shell su -c '
OLD=/data/data/com.game.sts2launcher.modmanager/files
NEW=/data/data/com.game.sts2launcher.modmanager.perf/files
am force-stop com.game.sts2launcher.modmanager.perf
cp -a $OLD/game $OLD/download_state $OLD/.cache_stamp $NEW/
chown -R $(stat -c %u:%g $NEW) $NEW/game $NEW/download_state $NEW/.cache_stamp
restorecon -R $NEW
'
```

The first start after that rebuilds the texture cache (30–60 s).

## Publish a release (repo owner)

The workflow `.github/workflows/android-perf.yml` builds the APK on GitHub
and publishes it as a release. It needs no game files and no Steam login.

**One-time: a signing key.** Android only installs an update when it's signed
with the same key as the installed app, so the key must stay the same for
every release. Create it once, in WSL or any Linux shell:

```bash
openssl req -x509 -newkey rsa:3072 -nodes -keyout key.pem -out cert.pem \
    -days 10000 -subj "/CN=StS2 Launcher Perf"
openssl pkcs12 -export -inkey key.pem -in cert.pem -out sts2perf.p12 -passout pass:CHOOSE_A_PASSWORD
base64 -w0 sts2perf.p12 > sts2perf.p12.txt
rm key.pem cert.pem
```

Add two secrets under **Settings → Secrets and variables → Actions → New
repository secret**:

| Name | Value |
|---|---|
| `ANDROID_KEYSTORE_B64` | the contents of `sts2perf.p12.txt` |
| `ANDROID_KEYSTORE_PASSWORD` | the password you chose |

Keep a backup of `sts2perf.p12` and its password somewhere safe. If you lose
them, users of the perf build have to uninstall it before they can install a
new version.

**Each release:**

1. Bump `PERF_BUILD` in [`perf.env`](perf.env). Reset it to 1 when moving to
   a new upstream version, and update `UPSTREAM_*` from the new upstream
   release.
2. Run **Actions → "Android perf build" → Run workflow** on `main`, or push the
   tag `v<version>` (for example `v0.4.2.1`). The release is tagged with that
   version and becomes the latest release, which is what **CHECK LAUNCHER
   UPDATE** in the app looks for.

Pushes that touch `android-perf/` run a test build with a throwaway key and
publish nothing.

## How the build works

```
upstream release APK (perf.env pins the version and its SHA-256)
  ├─ Sts2Perf/                 STS2Perf.dll, the add-on, compiled against the
  │                            launcher DLLs inside that APK (GodotSharp, Harmony,
  │                            STS2Mobile)
  ├─ tools/LauncherPatcher     STS2Mobile.dll: loads STS2Perf.dll at startup,
  │                            update check → this fork, MonoMod temp dir → new package,
  │                            Korean text → English (translations.json)
  ├─ tools/axml.py             AndroidManifest.xml: package id, provider authorities,
  │                            app name, version
  └─ tools/repack.py           new APK; then zipalign + apksigner
```

Only `AndroidManifest.xml`, `assets/dotnet_bcl/STS2Mobile.dll` and the new
`assets/dotnet_bcl/STS2Perf.dll` differ from the upstream APK. The engine,
Java code, FMOD and Spine libraries are upstream's binaries, byte for byte.

The add-on hooks the launcher's own methods with Harmony, like the launcher
hooks the game:

- `LauncherView` constructor → adds the **Perf settings** button and starts
  the settings watcher (a 2-second timer that re-reads `sts2perf.cfg`).
- The launcher closing, `UiScalePatches.ApplyScaledContentSize` and the
  game's `OnWindowChange` handlers → apply the settings. Low res switches the
  root window to the `viewport` stretch mode, which keeps the same layout and
  touch mapping but renders at the layout size. It waits for the game's own
  window handlers, because a resize during the PLAY handoff makes
  `NGame.OnWindowChange` throw.
- `AppUpdateChecker.GetInstalledVersion` → reports the perf version.
- `SaveProgressSummary.FormatCurrentRun` → "Act 2, Floor 7" (word order the
  translation table can't change).

If the add-on fails to load, the launcher logs `[STS2Perf] ... failed to load`
and runs exactly like upstream.

Build locally (Linux/WSL, with the .NET 9 SDK, Python 3 and Android
build-tools' `zipalign` and `apksigner` on `PATH` or in `$ANDROID_HOME`):

```bash
KEYSTORE=sts2perf.p12 KEYSTORE_PASSWORD=... android-perf/build.sh out
```
