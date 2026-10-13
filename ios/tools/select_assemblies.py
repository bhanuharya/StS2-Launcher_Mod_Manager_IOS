#!/usr/bin/env python3
"""Copy the game's own managed assemblies out of its data_* folder.

The PC build ships its whole .NET runtime next to sts2.dll (System.*.dll,
coreclr, hostfxr, ...) plus native libraries (steam_api64.dll, ...). NativeAOT
for iOS brings its own runtime, so only the game and its NuGet dependencies are
wanted. That means managed DLLs that are not part of the shared framework, and
not GodotSharp.dll, which comes from the Godot.NET.Sdk package instead.

  select_assemblies.py <data_dir> <shared_framework_dir> <out_dir>
"""

import os
import shutil
import struct
import sys

SKIP = {"godotsharp.dll"}


def is_managed(path):
    """True if the PE file has a CLI (.NET) header."""
    try:
        with open(path, "rb") as f:
            data = f.read(4096)
        if data[:2] != b"MZ":
            return False
        (pe,) = struct.unpack_from("<I", data, 0x3C)
        if data[pe : pe + 4] != b"PE\0\0":
            return False
        opt = pe + 24
        (magic,) = struct.unpack_from("<H", data, opt)
        dirs = opt + (96 if magic == 0x10B else 112)
        (count,) = struct.unpack_from("<I", data, dirs - 4)
        if count <= 14:
            return False
        rva, size = struct.unpack_from("<II", data, dirs + 14 * 8)
        return rva != 0 and size != 0
    except (OSError, struct.error):
        return False


def main(data_dir, framework_dir, out_dir):
    framework = {n.lower() for n in os.listdir(framework_dir) if n.lower().endswith(".dll")}
    if "system.private.corelib.dll" not in framework:
        raise SystemExit(f"{framework_dir} doesn't look like a Microsoft.NETCore.App folder")
    os.makedirs(out_dir, exist_ok=True)
    kept, skipped = [], 0
    for name in sorted(os.listdir(data_dir)):
        path = os.path.join(data_dir, name)
        lower = name.lower()
        if not lower.endswith(".dll") or not os.path.isfile(path):
            continue
        if lower in framework or lower in SKIP or not is_managed(path):
            skipped += 1
            continue
        shutil.copy2(path, os.path.join(out_dir, name))
        kept.append(name)
    if "sts2.dll" not in {k.lower() for k in kept}:
        raise SystemExit(f"sts2.dll not found among managed assemblies in {data_dir}")
    print(f"selected {len(kept)} assemblies ({skipped} runtime/native files skipped):")
    for k in kept:
        print(f"  {k}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    main(*sys.argv[1:])
