# Fake game for pipeline tests

`fakegame` stands in for Slay the Spire 2. It has the same assembly name
(`sts2`), the type and method names that `ios/tools/IlPatcher` rewrites, a
P/Invoke into `steam_api64` through a fake `Steamworks.NET`, and an atlas
JSON shaped like the game's. It lets the whole iOS pipeline (patching,
NativeAOT, bootstrap pack, ios_boot mount) run without game files or Steam
credentials, both on Linux (`ios/scripts/rehearse-linux.sh`) and in CI.

When the scene runs, it prints `FAKEGAME ...` lines and quits. A patched
build prints `FAKEGAME RESULT PASS`.
