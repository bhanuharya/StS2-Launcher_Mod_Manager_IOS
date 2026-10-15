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
| Card rendering | As the game, Fast | Fast draws each card straight to the screen instead of offscreen first. On a Pad 5 the cards in hand were half of a fight's GPU time. Loses the cards' ripple shader. |
| Card trails | On, Off | Off hides the trail behind cards flying to a pile, where end-of-turn discards dropped to ~10 fps. |
| Idle transition layer | Draw (game), Skip | Skip stops drawing the game's full-screen transition layer while it's idle and invisible (2.2 ms per frame at full resolution on a Pad 5). Transitions still show. |
| Frame-time log | Off, 5 s, 10 s, 30 s | Stats every N seconds, plus a line for every stutter saying what caused it. See below. |
| JIT warm-up (experimental) | Off, At main menu | Compiles the game code your earlier sessions needed while you're at the main menu, so it isn't compiled mid-fight. See below. |
| Scene probe (once) | Off, Next fight, Slow moments | Measures what each part of the fight screen costs the GPU, once, then turns itself off. Slow moments tests what's behind the next five slowdowns instead. See below. |

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
| `jit_warmup` | `off`, `on` | `off` |
| `probe` | `off`, `next_fight` (runs once, then back to `off`), `spikes` (the next five slowdowns) | `off` |
| `card_fx` | `game`, `fast` | `game` |
| `card_trails` | `on`, `off` | `on` |
| `idle_overlay` | `draw`, `skip` | `draw` |
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
[Perf] frames 10s: 59.8 fps | ms p50 16.7 p95 17.9 p99 24.1 max 41.3 | hitches 3 | cap 60 | vsync Enabled | render 2560x1600 of 2560x1600 | resolution full | effects on (hdr2d off) | particles 100% | jit +412 methods 151 ms | gc minor +14 major +0 | nodes 2140 (+380) | draw calls avg 180 max 260 | cpu: logic p50 3.1 p95 5.0, draw p50 1.2 p95 2.0 | gpu p50 11.8 p95 16.0 | live scenes: card x14, power_icon x9
```

and one line for every frame over 50 ms, with what happened during it:

```
[Perf] hitch 183 ms: jit +412 methods 151 ms (Sts2.Core.Combat.Foo x40, ...); gc minor +1; nodes +214 (card x3, shiv_hit x6) | 210 draw calls
```

Frame times are the game's main-loop iterations, which on Android is also the
render thread. `hitches` counts frames slower than 1.5× the median. Android
can't time this app's frames from outside (its SurfaceView has no per-frame
latency data), so this is the way to compare settings. What the parts mean:

- **cpu / gpu**: where each frame's time goes. `logic` is the game's
  per-frame work (scripts, animations, tweens), `draw` the CPU side of
  rendering, `gpu` the GPU's own time. When `gpu` is close to the frame time
  (`ms p50`), the GPU is the limit: fewer pixels or effects help. When it's
  well below, the CPU is.
- **live scenes**: instanced scenes in the game right now, most first, so a
  screen full of cards shows how many there are.
- **jit**: the game's .NET code runs on Mono's JIT on Android, so every method
  is compiled the first time it runs. A first Blade Dance or a first new enemy
  can stall a frame on that alone. The names in brackets are the classes that
  were compiled. **JIT warm-up** is the fix for these.
- **gc**: Mono's garbage collections, minor (nursery) and major. Mono keeps no
  pause times, and .NET's GC latency modes don't exist on Mono.
- **shaders compiled**, **texture memory**: Godot compiling a graphics
  pipeline or loading textures during the frame.
- **nodes**: nodes the game added, named by the scenes it instanced (cards,
  effects, damage numbers).
- None of these: the game's own code or the GPU.

A JIT hitch is logged a second or so late, once the compiled methods' names
arrive. Up to 20 hitch lines per window; the stats line counts the rest.

### Finding what's slow with a lot of cards

1. **Perf settings** → **Frame-time log** → Every 10 s. Tap **Debug** so it
   reads ON (it saves the log to a file), then restart the app.
2. Play a fight until the hand is full (Blade Dance, Cloak and Dagger) and
   stay on that turn for 20–30 seconds.
3. For a comparison, on the same kind of turn, set **Resolution → Low res**
   for 20 seconds, then **Particles → Off** for 20 seconds. Settings apply
   within 2 seconds, mid-fight.
4. The log is in `StS2LauncherMM/Logs` on the tablet's storage (the Debug
   button shows the exact file). The `[Perf] frames` lines show fps, the
   cpu/gpu split and how many cards are live; `[Perf] hitch` lines show
   one-off stutters.

### Cards: the biggest cost in a fight

What the 0.4.2.6 scene probe measured in a Silent fight on a Pad 5 at full
resolution, out of 26 ms of GPU time per frame (60 fps needs under 16.7):

| Part | GPU time |
|---|---|
| Cards in hand (~7) | 12.7 ms |
| Background (6 full-screen layers) | ~6.6 ms |
| Enemies and Silent | ~3 ms |
| Transition overlay, invisible but drawn | 2.2 ms |

Each card is a CanvasGroup: Godot draws the card offscreen, then composites
it with the card_ripple shader. On a tile-based mobile GPU each offscreen
pass costs about a full-screen read and write, so every card in hand costs
~1.8 ms per frame. **Card rendering → Fast** draws the cards' parts straight
to the screen. The ripple effect goes, and while a card fades its parts can
show through each other. Measured on the same Pad 5 at full resolution: a
fight's GPU time went from about 25 ms to 14–15 ms per frame, and the median
frame from 30 to 60 fps.

At the end of a turn, discarded cards fly to the pile with a trail, and
frames ran at about 90 ms for 2–3 seconds. **Card trails → Off** hides the
trails.

Still open: at the start of the enemy turn (the banner, cards flying off) the
GPU runs at about 89 ms per frame for 2–3 seconds, with nothing unusual in
view. **Scene probe → Slow moments** hides one thing at a time while that
happens and logs what the GPU time drops to.

### Scene probe: what the GPU spends its time on

On a Pad 5 a fight takes the GPU 40–50 ms per frame at full resolution and
about 25 ms at Low res, against 6–9 ms on the map, even with effects and
particles off. **Scene probe → Next fight** finds where that goes. A few
seconds into the next fight (leave the screen alone for about a minute):

1. It logs what the screen is made of: items covering half the screen or
   more (each one a full-screen fill), the shaders in use and which read the
   screen, CanvasGroups and clip masks (drawn offscreen first), 2D lights,
   SubViewports with their own GPU time, Spine skeletons, and hidden nodes
   that still run code every frame.
2. It hides the biggest parts of the screen one at a time, for a moment
   each, and logs how much GPU time each one costs:
   `probe:   +11.8 ms: Background/Layers (Control), 40 nodes`.
3. It tries candidate fixes one at a time and logs what each saves: card
   CanvasGroups drawn directly, clip masks off, the most used shaders
   removed, background layers hidden (together and one by one), the
   transition overlay hidden.

With the frame log on, a run of GPU frames over 60 ms also logs a `spike:`
snapshot of what's on screen at that moment, and what was added in the last
5 seconds (up to 6 per session). With **Scene probe → Slow moments**, each of
the next five slowdowns is also tested: while it lasts, things added in the
last few seconds and then the biggest parts of the screen are hidden one at
a time (`spike test: without enemy_turn_banner …: GPU 15.2 ms`). A test
that the slowdown outlasted is logged as not conclusive.

Then it switches itself off. Collect the log the same way as above
(`[Perf] probe:` lines).

### JIT warm-up (experimental)

With **JIT warm-up** on, every game method Mono compiles is added to
`sts2perf_jit.txt` in the app's files. From the next launch on, once the main
menu is up, those are compiled again before you play, 6 ms per frame so the
menu keeps drawing (the log says when it's done, usually well under a
minute). The list grows as you play, so the first session with it on only
records; a session that played a shiv turn makes the next session's shiv
turns smoother.

Only methods the game compiled in normal play are warmed, never the whole
game: compiling a method also runs some of the game's static setup early, so
untouched code (like Steam's own startup, which the launcher disables) stays
untouched. If the game misbehaves with it on, turn it off and delete
`sts2perf_jit.txt`. Generic code (async helpers, LINQ) still compiles on
first use.

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

## ASTC textures for non-Adreno GPUs (optional, PC + rooted device)

The game data Steam ships is made for PCs: its textures are BC-compressed.
Snapdragon's Adreno GPUs read BC natively, but **Mali** (Samsung Exynos,
MediaTek) and **PowerVR** GPUs cannot, so on those devices Godot expands
every texture to raw RGBA in memory — around 4x the texture memory, slower
loads, and more hitching. `ios/tools/repack_astc.py` (shared with the iOS
port) converts the pack's BC textures to **ASTC**, the format those GPUs
sample natively, at the same size.

On the PC (macOS, Linux, or Windows through WSL — like the signing-key
steps below) with the game installed:

```bash
pip install texture2ddecoder
# astcenc: download a binary from
# https://github.com/ARM-software/astc-encoder/releases and put it on PATH
python3 ios/tools/repack_astc.py selftest                      # toolchain check
python3 ios/tools/repack_astc.py repack "…/Slay the Spire 2/SlayTheSpire2.pck" StS2-astc.pck
```

`--dry-run` lists what would convert; `--block 8x8` shrinks textures to a
quarter of BC's size for very low-memory devices, at a visible quality cost;
`--quality medium|thorough` buys sharpness for longer converts.

Then push it over the game's copy (rooted, like the skip-the-download tip
above; adapt the package name for the original app):

```bash
adb push StS2-astc.pck /data/local/tmp/SlayTheSpire2.pck
adb shell su -c '
PKG=com.game.sts2launcher.modmanager.perf
GAME=/data/data/$PKG/files/game
am force-stop $PKG
cp /data/local/tmp/SlayTheSpire2.pck $GAME/SlayTheSpire2.pck
chown $(stat -c %u:%g $GAME) $GAME/SlayTheSpire2.pck
restorecon $GAME/SlayTheSpire2.pck
'
```

Notes: convert the **same game version** the launcher downloaded, and
re-push after every game update (the launcher's download overwrites the
file). The app only checks that the file is a Godot pack, so a mismatched
version would fail at boot with a game error instead — the frame-time log
(`Perf` stats line) shows the load-time difference. Without root there is
no way to replace the file in the app's private storage.

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
