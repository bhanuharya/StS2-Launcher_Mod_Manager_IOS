#!/usr/bin/env python3
"""Build the app's main pack (bootstrap pck).

Starts from the pack Godot exported for the iOS stub project (ios/game), which
holds the iOS .gdextension configs for ios_boot, FMOD, and Spine. It then
overlays the game's edited project.binary and .godot metadata, so the engine
boots with the game's settings, and rewrites the extension list so ios_boot
loads first. The real game pck is mounted at runtime by ios_boot.

  make_bootstrap.py --overlay <dir> --out <pck> [--base <exported.pck>] [--addon <ios/boot/addon>]
"""

import argparse
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pck  # noqa: E402

IOS_BOOT = "res://addons/ios_boot/ios_boot.gdextension"


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--base", help="pack exported from the iOS stub project")
    p.add_argument("--overlay", required=True, help="prepare-game.sh bootstrap/ folder")
    p.add_argument("--addon", help="ios/boot/addon (when the base pack doesn't already have it)")
    p.add_argument("--out", required=True)
    args = p.parse_args()

    with tempfile.TemporaryDirectory() as tmp:
        root = os.path.join(tmp, "pack")
        os.makedirs(root)
        if args.base:
            pck.main(["extract", args.base, root])
        if args.addon:
            shutil.copytree(args.addon, os.path.join(root, "addons", "ios_boot"), dirs_exist_ok=True)
        shutil.copytree(args.overlay, root, dirs_exist_ok=True)

        # The game's project.binary replaces the stub's settings entirely.
        stray = os.path.join(root, "project.godot")
        if os.path.exists(stray):
            os.remove(stray)

        extensions = []
        for dirpath, _, names in os.walk(root):
            for name in names:
                if name.endswith(".gdextension"):
                    rel = os.path.relpath(os.path.join(dirpath, name), root).replace(os.sep, "/")
                    extensions.append("res://" + rel)
        if IOS_BOOT not in extensions:
            raise SystemExit(f"{IOS_BOOT} missing from the bootstrap pack")
        ordered = [IOS_BOOT] + sorted(e for e in extensions if e != IOS_BOOT and "sentry" not in e.lower())
        os.makedirs(os.path.join(root, ".godot"), exist_ok=True)
        with open(os.path.join(root, ".godot", "extension_list.cfg"), "w") as f:
            f.write("\n".join(ordered) + "\n")
        print("extension load order:\n  " + "\n  ".join(ordered))

        pck.main(["build", args.out, root])


if __name__ == "__main__":
    main()
