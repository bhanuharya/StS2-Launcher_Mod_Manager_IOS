#!/usr/bin/env python3
"""Read and write Godot 4.x .pck files (format V2/V3), stdlib only.

The iOS port uses it to pull the game's project settings and GDExtension
configs out of SlayTheSpire2.pck in CI, and to build the small bootstrap pack
that ships inside the app. Format reference: core/io/file_access_pack.cpp and
core/io/pck_packer.cpp at 4.5.1-stable.

  pck.py list    <pck> [glob ...]
  pck.py extract <pck> <out_dir> [glob ...]
  pck.py build   <out_pck> <src_dir> [--engine 4.5.1]

Globs match the path without the res:// prefix (e.g. '.godot/*' or '*.gdextension').
"""

import argparse
import fnmatch
import hashlib
import os
import struct
import sys

MAGIC = 0x43504447  # "GDPC"
DIR_ENCRYPTED = 1 << 0
REL_FILEBASE = 1 << 1
SPARSE_BUNDLE = 1 << 2
FILE_ENCRYPTED = 1 << 0
FILE_REMOVAL = 1 << 1
ALIGNMENT = 32


class Entry:
    __slots__ = ("path", "offset", "size", "md5", "flags")

    def __init__(self, path, offset, size, md5, flags):
        self.path = path
        self.offset = offset  # absolute offset in the .pck file
        self.size = size
        self.md5 = md5
        self.flags = flags


def read_pck(path):
    """Returns (header dict, [Entry]) for a standalone .pck file."""
    with open(path, "rb") as f:
        magic, version, major, minor, patch, flags, file_base = struct.unpack("<6IQ", f.read(32))
        if magic != MAGIC:
            raise ValueError(f"{path}: not a Godot pck (magic {magic:#x})")
        if version not in (2, 3):
            raise ValueError(f"{path}: unsupported pack format {version}")
        if flags & DIR_ENCRYPTED:
            raise ValueError(f"{path}: encrypted directory is not supported")
        if flags & SPARSE_BUNDLE:
            raise ValueError(f"{path}: sparse bundles are not supported")
        if version == 3:
            (dir_offset,) = struct.unpack("<Q", f.read(8))
            f.seek(dir_offset)
        else:
            f.seek(16 * 4, os.SEEK_CUR)
        (count,) = struct.unpack("<I", f.read(4))
        entries = []
        for _ in range(count):
            (name_len,) = struct.unpack("<I", f.read(4))
            name = f.read(name_len).rstrip(b"\0").decode("utf-8")
            offset, size = struct.unpack("<QQ", f.read(16))
            md5 = f.read(16)
            (eflags,) = struct.unpack("<I", f.read(4))
            entries.append(Entry(name.removeprefix("res://"), file_base + offset, size, md5, eflags))
    header = {"version": version, "engine": f"{major}.{minor}.{patch}", "flags": flags}
    return header, entries


def _matches(path, globs):
    return not globs or any(fnmatch.fnmatchcase(path, g) for g in globs)


def cmd_list(args):
    header, entries = read_pck(args.pck)
    print(f"# format v{header['version']}, engine {header['engine']}, {len(entries)} entries")
    for e in entries:
        if _matches(e.path, args.globs):
            tags = []
            if e.flags & FILE_ENCRYPTED:
                tags.append("encrypted")
            if e.flags & FILE_REMOVAL:
                tags.append("removal")
            print(f"{e.size:>12}  {e.path}{'  [' + ','.join(tags) + ']' if tags else ''}")


def cmd_extract(args):
    _, entries = read_pck(args.pck)
    count = 0
    with open(args.pck, "rb") as f:
        for e in entries:
            if not _matches(e.path, args.globs) or e.flags & (FILE_ENCRYPTED | FILE_REMOVAL):
                continue
            dest = os.path.join(args.out_dir, *e.path.split("/"))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            f.seek(e.offset)
            data = f.read(e.size)
            if hashlib.md5(data).digest() != e.md5:
                raise ValueError(f"md5 mismatch for {e.path}")
            with open(dest, "wb") as out:
                out.write(data)
            count += 1
    print(f"extracted {count} files to {args.out_dir}")


def _pad(n, alignment=ALIGNMENT):
    return (alignment - n % alignment) % alignment


def cmd_build(args):
    major, minor, patch = (int(x) for x in args.engine.split("."))
    files = []
    for root, _, names in os.walk(args.src_dir):
        for name in sorted(names):
            full = os.path.join(root, name)
            files.append((os.path.relpath(full, args.src_dir).replace(os.sep, "/"), full))
    files.sort()

    with open(args.out_pck, "wb") as f:
        # Header (V3): file_base and dir_offset are patched in once known.
        f.write(struct.pack("<6I", MAGIC, 3, major, minor, patch, REL_FILEBASE))
        f.write(struct.pack("<QQ", 0, 0))
        f.write(b"\0" * (16 * 4))
        f.write(b"\0" * _pad(f.tell()))
        file_base = f.tell()

        directory = []
        for rel, full in files:
            with open(full, "rb") as src:
                data = src.read()
            directory.append((rel, f.tell() - file_base, len(data), hashlib.md5(data).digest()))
            f.write(data)
            f.write(b"\0" * _pad(f.tell()))

        dir_offset = f.tell()
        f.write(struct.pack("<I", len(directory)))
        for rel, offset, size, md5 in directory:
            name = rel.encode("utf-8")
            name += b"\0" * _pad(len(name), 4)
            f.write(struct.pack("<I", len(name)))
            f.write(name)
            f.write(struct.pack("<QQ", offset, size))
            f.write(md5)
            f.write(struct.pack("<I", 0))

        f.seek(6 * 4)
        f.write(struct.pack("<QQ", file_base, dir_offset))
    print(f"built {args.out_pck} with {len(directory)} files")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("list")
    s.add_argument("pck")
    s.add_argument("globs", nargs="*")
    s.set_defaults(func=cmd_list)

    s = sub.add_parser("extract")
    s.add_argument("pck")
    s.add_argument("out_dir")
    s.add_argument("globs", nargs="*")
    s.set_defaults(func=cmd_extract)

    s = sub.add_parser("build")
    s.add_argument("out_pck")
    s.add_argument("src_dir")
    s.add_argument("--engine", default="4.5.1")
    s.set_defaults(func=cmd_build)

    args = p.parse_args(argv)
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
