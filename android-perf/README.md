# StS2 Launcher Perf (Android, for lower-end devices)

A build of the upstream [StS2 Launcher Mod Manager](https://github.com/iunius612/StS2-Launcher_Mod_Manager)
release with a small performance add-on. Made for tablets and phones that
struggle with the game at full resolution, like the Xiaomi Pad 5 (Snapdragon
860, 2560×1600).

Download it from this repo's **Releases** page (`StS2LauncherPerf-v….apk`).

## What it changes

| | Upstream | Perf build |
|---|---|---|
| Render resolution | Full screen resolution | **Graphics: Fast** (default): the game's layout resolution, about 1080p, scaled up to the screen |
| Full-screen effects | As the game sets them | Fast turns off HDR 2D, FXAA and debanding if the game uses them |
| App | `com.game.sts2launcher.modmanager`, "StS2 Launcher Mod" | `com.game.sts2launcher.modmanager.perf`, "StS2 Launcher Perf": installs **next to** the original |
| Launcher text | Many dialogs only in Korean (Save Manager, backups, cloud sync, Mod Hub messages) | English everywhere |
| Launcher updates | Upstream releases | This fork's releases |

Everything else is upstream's code, unchanged: Steam login, downloads,
Workshop, mods, Save Manager, cloud sync, LAN.

**How much it saves.** The launcher lays the game out on a 1680×1080 canvas
(stretched to your screen's shape, and smaller as you raise UI Scale). Fast
renders that canvas at its own size instead of at screen resolution:

| Screen | UI Scale | Fast renders | Pixels vs. full |
|---|---|---|---|
| 2560×1600 tablet (Pad 5) | 100% | 1728×1080 | 46% |
| 2400×1080 phone | 100% | 2400×1080 | 100% (no change) |
| 3200×1440 phone | 100% | 2400×1080 | 56% |

Text gets a little softer, about like Android's Game Mode "performance"
downscale. A larger UI Scale lowers the Fast resolution further.

**Switching.** On the launcher's main screen, the **Graphics: Fast / Sharp**
button sits next to **Debug** above the console. The change applies the next
time the game starts. **Sharp** is exactly upstream's rendering. The launcher
console and the Debug log show what Fast did, in lines starting with `[Perf]`.

If you set up Android Game Mode downscaling for the original app, you don't
need it for this one. Game Mode settings are per app, so the perf build starts
without them.

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

- `LauncherView` constructor → adds the Graphics button.
- `UiScalePatches.ApplyScaledContentSize`, the game's `OnWindowChange`
  handlers, and the launcher closing → switch the root window to the
  `viewport` stretch mode in Fast. This keeps the same layout and touch mapping
  but renders at the layout size.
- `AppUpdateChecker.GetInstalledVersion` → reports the perf version.
- `SaveProgressSummary.FormatCurrentRun` → "Act 2, Floor 7" (word order the
  translation table can't change).

**Translations.** `translations.json` maps each Korean string literal in the
upstream `STS2Mobile.dll` to English, matched exactly. Interpolated strings
are split into pieces at their `{…}` holes, so each piece is translated on its
own. After moving to a new upstream release, the build prints
`WARNING: untranslated: …` for any new Korean text; add it to the table.
`LauncherPatcher --list-strings STS2Mobile.dll` lists them all. The texture
cache rebuild screen shown after a game update is drawn by the Java side
and stays in Korean.

If the add-on fails to load, the launcher logs `[STS2Perf] ... failed to load`
and runs exactly like upstream.

Build locally (Linux/WSL, with the .NET 9 SDK, Python 3 and Android
build-tools' `zipalign` and `apksigner` on `PATH` or in `$ANDROID_HOME`):

```bash
KEYSTORE=sts2perf.p12 KEYSTORE_PASSWORD=... android-perf/build.sh out
```
