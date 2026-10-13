#!/usr/bin/env python3
"""Inspect and edit Godot 4.x binary project settings (project.binary).

Format (core/config/project_settings.cpp, _load/_save_settings_binary):
"ECFG", u32 count, then per entry: u32 key length, UTF-8 key, u32 value
length, encode_variant() bytes. Values we don't touch are copied through
verbatim, so we never need a full Variant decoder; only the few simple types
we write (bool, int, string) are encoded here.

  projbin.py list <project.binary> [glob ...]
  projbin.py edit <in> <out> [--drop-autoload REGEX] [--remove GLOB]
                  [--set-bool KEY=true|false] [--set-int KEY=N] [--set-string KEY=VALUE]

Entry order is kept (autoload order follows it). New keys are appended.
"""

import argparse
import fnmatch
import re
import struct
import sys

# Variant::Type values used here (core/variant/variant.h).
T_NIL, T_BOOL, T_INT, T_FLOAT, T_STRING = 0, 1, 2, 3, 4
T_STRING_NAME = 21
ENCODE_FLAG_64 = 1 << 16
TYPE_MASK = 0xFF


def read_entries(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"ECFG":
        raise ValueError(f"{path}: not a binary project settings file")
    (count,) = struct.unpack_from("<I", data, 4)
    pos = 8
    entries = []
    for _ in range(count):
        (klen,) = struct.unpack_from("<I", data, pos)
        pos += 4
        key = data[pos : pos + klen].decode("utf-8")
        pos += klen
        (vlen,) = struct.unpack_from("<I", data, pos)
        pos += 4
        entries.append([key, data[pos : pos + vlen]])
        pos += vlen
    return entries


def write_entries(path, entries):
    out = bytearray(b"ECFG")
    out += struct.pack("<I", len(entries))
    for key, value in entries:
        k = key.encode("utf-8")
        out += struct.pack("<I", len(k)) + k
        out += struct.pack("<I", len(value)) + value
    with open(path, "wb") as f:
        f.write(out)


def encode_bool(v):
    return struct.pack("<II", T_BOOL, 1 if v else 0)


def encode_int(v):
    if -(2**31) <= v < 2**31:
        return struct.pack("<Ii", T_INT, v)
    return struct.pack("<Iq", T_INT | ENCODE_FLAG_64, v)


def encode_string(s):
    b = s.encode("utf-8")
    return struct.pack("<II", T_STRING, len(b)) + b + b"\0" * ((4 - len(b) % 4) % 4)


def describe(value):
    """Human-readable form of the simple types; others show type and size."""
    (header,) = struct.unpack_from("<I", value, 0)
    t = header & TYPE_MASK
    if t == T_NIL:
        return "null"
    if t == T_BOOL:
        return "true" if struct.unpack_from("<I", value, 4)[0] else "false"
    if t == T_INT:
        fmt = "<q" if header & ENCODE_FLAG_64 else "<i"
        return str(struct.unpack_from(fmt, value, 4)[0])
    if t == T_FLOAT:
        fmt = "<d" if header & ENCODE_FLAG_64 else "<f"
        return repr(struct.unpack_from(fmt, value, 4)[0])
    if t in (T_STRING, T_STRING_NAME):
        (n,) = struct.unpack_from("<I", value, 4)
        return '"' + value[8 : 8 + n].decode("utf-8", "replace") + '"'
    return f"<variant type {t}, {len(value)} bytes>"


def string_value(value):
    (header,) = struct.unpack_from("<I", value, 0)
    if header & TYPE_MASK not in (T_STRING, T_STRING_NAME):
        return None
    (n,) = struct.unpack_from("<I", value, 4)
    return value[8 : 8 + n].decode("utf-8", "replace")


def cmd_list(args):
    for key, value in read_entries(args.file):
        if not args.globs or any(fnmatch.fnmatchcase(key, g) for g in args.globs):
            print(f"{key} = {describe(value)}")


def _split(assignment):
    if "=" not in assignment:
        raise SystemExit(f"expected KEY=VALUE, got {assignment!r}")
    return assignment.split("=", 1)


def cmd_edit(args):
    entries = read_entries(args.input)

    def drop(key, value):
        if any(fnmatch.fnmatchcase(key, g) for g in args.remove):
            return True
        if key.startswith("autoload/") and args.drop_autoload:
            target = string_value(value) or ""
            return any(re.search(p, key + " " + target, re.IGNORECASE) for p in args.drop_autoload)
        return False

    kept = []
    for key, value in entries:
        if drop(key, value):
            print(f"removed {key} = {describe(value)}")
        else:
            kept.append([key, value])

    updates = []
    for a in args.set_bool:
        k, v = _split(a)
        if v.lower() not in ("true", "false"):
            raise SystemExit(f"--set-bool needs true/false, got {v!r}")
        updates.append((k, encode_bool(v.lower() == "true")))
    for a in args.set_int:
        k, v = _split(a)
        updates.append((k, encode_int(int(v))))
    for a in args.set_string:
        k, v = _split(a)
        updates.append((k, encode_string(v)))

    index = {key: i for i, (key, _) in enumerate(kept)}
    for key, encoded in updates:
        if key in index:
            print(f"set {key}: {describe(kept[index[key]][1])} -> {describe(encoded)}")
            kept[index[key]][1] = encoded
        else:
            print(f"added {key} = {describe(encoded)}")
            index[key] = len(kept)
            kept.append([key, encoded])

    write_entries(args.output, kept)
    print(f"wrote {args.output} ({len(kept)} entries)")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("list")
    s.add_argument("file")
    s.add_argument("globs", nargs="*")
    s.set_defaults(func=cmd_list)

    s = sub.add_parser("edit")
    s.add_argument("input")
    s.add_argument("output")
    s.add_argument("--remove", action="append", default=[], metavar="GLOB")
    s.add_argument("--drop-autoload", action="append", default=[], metavar="REGEX",
                   help="drop autoload/* entries whose name or path matches")
    s.add_argument("--set-bool", action="append", default=[], metavar="KEY=VALUE")
    s.add_argument("--set-int", action="append", default=[], metavar="KEY=N")
    s.add_argument("--set-string", action="append", default=[], metavar="KEY=VALUE")
    s.set_defaults(func=cmd_edit)

    args = p.parse_args(argv)
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
