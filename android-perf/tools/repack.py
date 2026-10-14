#!/usr/bin/env python3
"""Copies an APK with some entries replaced or added, dropping its signature.

  repack.py IN.apk OUT.apk --put ENTRY=FILE [--put ENTRY=FILE ...]

Entries keep their original compression method (stored stays stored, e.g.
resources.arsc); new entries are deflated. The v1 signature files in
META-INF are dropped, and the v2+ signing block goes away with the rewrite.
Run zipalign and apksigner on the result.
"""
import argparse
import re
import sys
import zipfile

SIGNATURE = re.compile(r"^META-INF/([^/]+\.(SF|RSA|DSA|EC)|MANIFEST\.MF)$", re.IGNORECASE)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("output")
    ap.add_argument("--put", action="append", default=[], metavar="ENTRY=FILE")
    args = ap.parse_args()

    puts = {}
    for spec in args.put:
        entry, _, path = spec.partition("=")
        if not entry or not path:
            ap.error(f"bad --put {spec!r}")
        puts[entry] = path

    replaced = set()
    with zipfile.ZipFile(args.input) as zin, zipfile.ZipFile(args.output, "w") as zout:
        for info in zin.infolist():
            if SIGNATURE.match(info.filename):
                continue
            out = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            out.compress_type = info.compress_type
            out.external_attr = info.external_attr
            if info.filename in puts:
                with open(puts[info.filename], "rb") as f:
                    data = f.read()
                replaced.add(info.filename)
            else:
                data = zin.read(info.filename)
            zout.writestr(out, data)
        for entry, path in puts.items():
            if entry in replaced:
                continue
            with open(path, "rb") as f:
                zout.writestr(zipfile.ZipInfo(entry, date_time=(2020, 1, 1, 0, 0, 0)),
                              f.read(), compress_type=zipfile.ZIP_DEFLATED)
            print(f"  added {entry}")
        for entry in sorted(replaced):
            print(f"  replaced {entry}")


if __name__ == "__main__":
    sys.exit(main())
