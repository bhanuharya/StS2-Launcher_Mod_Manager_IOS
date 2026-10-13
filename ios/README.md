# StS2 on iOS (experimental, no Mac required)

Goal: run your own copy of Slay the Spire 2 natively on an iPhone or iPad,
built in the cloud on GitHub's macOS runners and sideloaded from Windows with
a free Apple ID. Nothing in this repo contains game files.

**Status:**
- **Phase 0 (probe app):** ready to sideload.
- **Phase 1 (the game):** the build pipeline is written and passes an
  end-to-end rehearsal with a stand-in game (Linux, and macOS CI). It has not
  run the real game on a device yet, so expect a few rounds of fixes.

## Why this isn't just the Android launcher recompiled

The Android build loads the game's `sts2.dll` at runtime on Mono's JIT and
patches it live with Harmony. iOS doesn't allow JIT for sideloaded apps, so
none of that works there:

| Android launcher | iOS port |
|---|---|
| Mono JIT loads `sts2.dll` downloaded on the phone | C# is compiled ahead of time (NativeAOT) **at build time**, so the build machine downloads your `sts2.dll` from Steam |
| Harmony patches the game at runtime | The same fixes are applied to `sts2.dll` before compiling (`tools/IlPatcher`, Mono.Cecil) |
| Workshop / DLL mods loaded at runtime | **Not possible.** Mods are compiled code, and iOS can't load new code. Unmodded only. |
| FMOD / Spine `.so` from the Android APK | Official iOS builds of fmod-gdextension and spine-godot |

## Sideloading basics

A free Apple ID signature lasts **7 days** and allows 3 sideloaded apps at
once (and 10 new bundle IDs per week, so we keep the bundle IDs fixed).
Re-run Sideloadly weekly to refresh it. Your saves survive a refresh, but
**not** deleting the app. If you'd rather refresh from the phone itself,
SideStore is an alternative. Its one-time setup needs a PC; after that it
can refresh on-device.

## Phase 0: probe app (do this first)

`ios/probe` is a tiny Godot C# app. It confirms that sideloading works on your
phone and reports what the port depends on. The most important line is whether
your GPU can read the PC game's BC-compressed textures (`Texture BC7: YES/NO`).

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

## Phase 1: the game

### One-time setup

1. **Enable Actions on this fork.** Open the **Actions** tab. If GitHub asks,
   click *"I understand my workflows, go ahead and enable them"*.
2. **Add three repository secrets** under **Settings → Secrets and variables →
   Actions → New repository secret**:

   | Name | Value |
   |---|---|
   | `STEAM_USERNAME` | your Steam account name |
   | `STEAM_PASSWORD` | your Steam password |
   | `IPA_PASSWORD` | a password you make up; you'll type it to unzip the app |

   Secrets are encrypted, are never shown in logs, and aren't given to pull
   requests from other people's forks. Only workflows you start can use them.
3. **Steam Guard mobile authenticator.** You approve each build's sign-in in
   the Steam mobile app. Email-code Steam Guard can't work in CI.
4. **Install the game on your PC through Steam** (same branch you build
   for). You'll copy its `SlayTheSpire2.pck` to the phone.

### Build

1. **Actions → "iOS game build" → Run workflow.** Pick branch
   `main`, source `steam`, and Steam branch `public`
   (or the beta you play).
2. When the **Download game from Steam** step starts, open the Steam app on
   your phone and **approve the sign-in**.
3. Wait about 30–40 minutes. The run uploads **StS2-ios-<version>-run<N>**
   (kept for 3 days).

### Install

1. Download the artifact, then open the zip inside it with **7-Zip** and your
   `IPA_PASSWORD`. (Windows Explorer can't open AES-encrypted zips.)
2. Sideload `StS2.ipa` with Sideloadly, the same way as the probe. The app
   shows up as **StS2**.
3. Copy the game data: on the PC, go to **Steam → Slay the Spire 2 → Manage →
   Browse local files** and find `SlayTheSpire2.pck` (about 2 GB). Then copy
   it to the phone:
   - **Windows 11:** in the **Apple Devices** app, select your iPhone → **Files** → **StS2** → drag the `.pck` in.
   - **Windows 10:** in **iTunes**, select your iPhone → **File Sharing** → **StS2** → **Add File**.
4. Open **StS2**. If the file is missing or from a different game version than
   the build, the app says so instead of starting.

### Updates

When Steam updates the game, run the workflow again, sideload the new
`StS2.ipa`, and copy the new `SlayTheSpire2.pck`. The build and the `.pck`
must match, and the app checks this.

### If something goes wrong

The app writes Godot's log to **Files → On My iPhone → StS2 → logs →
godot.log**. Lines starting with `[iOS]` come from this port. Send that file
along with a screenshot.

Your saves are in the same folder, so copy them somewhere safe before
deleting the app.

## Performance

- **Game code:** NativeAOT compiles it to native ARM64 before install. There's
  no JIT warm-up and no interpreter, so this is the fastest option available on iOS.
- **Textures matter most.** The PC `.pck` uses BC-compressed textures. If the
  probe reports `Texture BC7: YES`, they load as they are. If it says `NO`,
  Godot expands them in memory: loads get slower and memory use goes up a lot.
  In that case a build step will convert them to ASTC, the phone's native
  format (planned, only if needed).
- **First-run hitches:** Godot compiles each graphics pipeline the first time
  an effect appears and caches it, so the first fights may stutter briefly.

## Phase 2: playability

Port the most important mobile fixes from the Android patches as IL patches:
UI scale for phone screens, touch-friendly hover/tooltips, and app
suspend/resume handling.

## Known limits

- No Steam online features (achievements, cloud saves, Steam lobbies).
  Direct-IP / LAN multiplayer may work.
- No mods (see above).
- iPhone screen size: the PC UI is small on a 6.3" display until phase 2.
- Godot's iOS C# support is marked experimental upstream.

## How the build works (for developers)

```
Steam (your account) ──► data_*/sts2.dll + deps, SlayTheSpire2.pck (CI only)
        │
        ├─ tools/select_assemblies.py   game's own managed DLLs, no .NET runtime
        ├─ tools/IlPatcher              skip Steam/Sentry init, call Sts2Ios.Hooks
        ├─ support/Sts2IosSupport       Steam stub resolver, AOT-safe atlas JSON, diagnostics
        ├─ aot/PrebuiltAot.csproj       NativeAOT of the prebuilt sts2.dll (no source needed)
        ├─ game/ (stub Godot project)   iOS export: Xcode project, frameworks
        ├─ boot/ios_boot (GDExtension)  mounts Documents/SlayTheSpire2.pck before anything loads
        └─ tools/make_bootstrap.py      app's main pack: game's project.binary + iOS configs
```

`scripts/rehearse-linux.sh` runs the whole chain on Linux against the
stand-in game in `test/` and boots it in the stock Godot template. Pushes to
`ios/**` run the macOS/iOS build of the stand-in game in CI.
