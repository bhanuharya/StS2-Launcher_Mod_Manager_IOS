#!/usr/bin/env python3
"""Edits a compiled (binary) AndroidManifest.xml taken from an APK.

Only what the perf build needs, without decoding/re-encoding the whole APK
with apktool:

  --package NEW       rename the application id. Provider authorities that
                      start with the old id are renamed with it. Component
                      class names (e.g. <old id>.GodotApp) are left alone.
  --label TEXT        replace every android:label that is a literal string
  --version-name S    android:versionName
  --version-code N    android:versionCode
  --add-provider CLASS=AUTHORITY
                      add a <provider> for CLASS, not exported, next to
                      androidx.startup's (its element is the template)

Strings are edited in place in the string pool (same index), so the XML tree
and the resource map keep their offsets; only the pool is re-encoded. An added
provider's two strings go at the end of the pool; its attribute names and
values otherwise are the template's own.

  axml.py edit IN OUT [options]
  axml.py dump IN                 print elements and attributes
"""
import argparse
import struct
import sys

RES_XML_TYPE = 0x0003
RES_STRING_POOL_TYPE = 0x0001
RES_XML_START_ELEMENT_TYPE = 0x0102
RES_XML_END_ELEMENT_TYPE = 0x0103
UTF8_FLAG = 0x100

TYPE_STRING = 0x03
TYPE_INT_DEC = 0x10
TYPE_INT_HEX = 0x11

ANDROID_NS = "http://schemas.android.com/apk/res/android"


def _read_len8(buf, pos):
    n = buf[pos]
    if n & 0x80:
        return ((n & 0x7F) << 8) | buf[pos + 1], pos + 2
    return n, pos + 1


def _read_len16(buf, pos):
    n = struct.unpack_from("<H", buf, pos)[0]
    if n & 0x8000:
        return ((n & 0x7FFF) << 16) | struct.unpack_from("<H", buf, pos + 2)[0], pos + 4
    return n, pos + 2


def _enc_len8(n):
    if n > 0x7FFF:
        raise ValueError("string too long for the UTF-8 pool")
    return bytes([0x80 | (n >> 8), n & 0xFF]) if n > 0x7F else bytes([n])


def _enc_len16(n):
    if n > 0x7FFFFFFF:
        raise ValueError("string too long")
    if n > 0x7FFF:
        return struct.pack("<HH", 0x8000 | (n >> 16), n & 0xFFFF)
    return struct.pack("<H", n)


class StringPool:
    def __init__(self, chunk):
        (self.type, self.header_size, self.size, count, style_count, self.flags,
         strings_start, styles_start) = struct.unpack_from("<HHIIIIII", chunk, 0)
        if style_count:
            raise ValueError("string pools with styles are not supported")
        self.utf8 = bool(self.flags & UTF8_FLAG)
        offsets = struct.unpack_from(f"<{count}I", chunk, self.header_size)
        self.strings = []
        for off in offsets:
            pos = strings_start + off
            if self.utf8:
                _, pos = _read_len8(chunk, pos)  # UTF-16 length, recomputed on write
                n, pos = _read_len8(chunk, pos)
                self.strings.append(chunk[pos:pos + n].decode("utf-8"))
            else:
                n, pos = _read_len16(chunk, pos)
                self.strings.append(chunk[pos:pos + 2 * n].decode("utf-16-le"))

    def encode(self):
        data = bytearray()
        offsets = []
        for s in self.strings:
            offsets.append(len(data))
            if self.utf8:
                raw = s.encode("utf-8")
                data += _enc_len8(len(s.encode("utf-16-le")) // 2) + _enc_len8(len(raw)) + raw + b"\0"
            else:
                raw = s.encode("utf-16-le")
                data += _enc_len16(len(raw) // 2) + raw + b"\0\0"
        while len(data) % 4:
            data += b"\0"
        header_size = 28
        strings_start = header_size + 4 * len(self.strings)
        size = strings_start + len(data)
        # Sorted-ness can't be guaranteed after edits; clear the flag.
        flags = self.flags & ~0x1
        out = struct.pack("<HHIIIIII", RES_STRING_POOL_TYPE, header_size, size,
                          len(self.strings), 0, flags, strings_start, 0)
        out += struct.pack(f"<{len(offsets)}I", *offsets)
        return out + bytes(data)


class Attr:
    def __init__(self, buf, pos, element):
        self.pos = pos
        self.element = element
        (self.ns, self.name, self.raw, _size, _res0, self.data_type,
         self.data) = struct.unpack_from("<IIIHBBI", buf, pos)


class Manifest:
    def __init__(self, data):
        self.buf = bytearray(data)
        typ, hsize, size = struct.unpack_from("<HHI", self.buf, 0)
        if typ != RES_XML_TYPE or size != len(self.buf):
            raise ValueError("not a binary XML file")
        pos = hsize
        typ, _, csize = struct.unpack_from("<HHI", self.buf, pos)
        if typ != RES_STRING_POOL_TYPE:
            raise ValueError("expected the string pool first")
        self.pool_end = pos + csize
        self.pool_start = pos
        self.pool = StringPool(bytes(self.buf[pos:pos + csize]))
        self.attrs = []
        self.elements = []
        self.chunks = []
        pos = self.pool_end
        while pos < len(self.buf):
            typ, chsize, csize = struct.unpack_from("<HHI", self.buf, pos)
            self.chunks.append((pos, typ, csize))
            if typ == RES_XML_START_ELEMENT_TYPE:
                ext = pos + chsize
                _ns, name, attr_start, attr_size, attr_count = struct.unpack_from("<IIHHH", self.buf, ext)
                element = self.pool.strings[name]
                self.elements.append(element)
                for i in range(attr_count):
                    self.attrs.append(Attr(self.buf, ext + attr_start + i * attr_size, element))
            pos += csize

    def s(self, idx):
        return None if idx == 0xFFFFFFFF else self.pool.strings[idx]

    def find(self, element, name):
        return [a for a in self.attrs if a.element == element and self.s(a.name) == name
                and self.s(a.ns) in (ANDROID_NS, None)]

    def refs(self, idx):
        """Every attribute that uses string idx, as raw value or typed string."""
        return [a for a in self.attrs
                if a.raw == idx or (a.data_type == TYPE_STRING and a.data == idx)]

    def set_string(self, idx, value, allowed):
        for a in self.refs(idx):
            if (a.element, self.s(a.name)) not in allowed:
                raise SystemExit(
                    f"refusing to change {self.pool.strings[idx]!r}: also used by "
                    f"<{a.element} {self.s(a.name)}>")
        self.pool.strings[idx] = value

    def set_int(self, attr, value):
        if attr.data_type not in (TYPE_INT_DEC, TYPE_INT_HEX):
            raise SystemExit(f"{self.s(attr.name)} is not an integer attribute")
        struct.pack_into("<I", self.buf, attr.pos + 16, value)

    def add_element_like(self, element, template_name, values):
        """Copies the <element> whose android:name is template_name (and its
        end tag, not its children) to just before it, with the given
        attributes' string values replaced."""
        for i, (pos, typ, size) in enumerate(self.chunks):
            if typ != RES_XML_START_ELEMENT_TYPE:
                continue
            attrs = [a for a in self.attrs if pos < a.pos < pos + size]
            if not attrs or attrs[0].element != element or not any(
                    self.s(a.name) == "name" and a.data_type == TYPE_STRING
                    and self.s(a.data) == template_name for a in attrs):
                continue
            depth = 0
            for end_pos, end_typ, end_size in self.chunks[i:]:
                depth += {RES_XML_START_ELEMENT_TYPE: 1, RES_XML_END_ELEMENT_TYPE: -1}.get(end_typ, 0)
                if depth == 0:
                    break
            start = bytearray(self.buf[pos:pos + size])
            for name, value in values.items():
                a = one([a for a in attrs if self.s(a.name) == name], f"<{element} {name}>")
                if a.data_type != TYPE_STRING:
                    raise SystemExit(f"<{element} {name}> is not a literal string")
                self.pool.strings.append(value)
                idx = len(self.pool.strings) - 1
                struct.pack_into("<I", start, a.pos - pos + 8, idx)  # rawValue
                struct.pack_into("<I", start, a.pos - pos + 16, idx)  # typed value
            end = self.buf[end_pos:end_pos + end_size]
            self.buf[pos:pos] = bytes(start) + bytes(end)
            self.__init__(self.encode())  # offsets after the copy moved
            return
        raise SystemExit(f"no <{element}> named {template_name} to copy")

    def encode(self):
        pool = self.pool.encode()
        body = bytes(self.buf[self.pool_end:])
        total = 8 + len(pool) + len(body)
        return struct.pack("<HHI", RES_XML_TYPE, 8, total) + pool + body

    def dump(self):
        for a in self.attrs:
            if a.data_type == TYPE_STRING:
                val = repr(self.s(a.data))
            else:
                val = f"(type 0x{a.data_type:02x}) 0x{a.data:x}"
            print(f"<{a.element}> {self.s(a.name)} = {val}")


def one(attrs, what):
    if len(attrs) != 1:
        raise SystemExit(f"expected exactly one {what}, found {len(attrs)}")
    return attrs[0]


def string_attr(m, element, name):
    a = one(m.find(element, name), f"<{element} {name}>")
    if a.data_type != TYPE_STRING:
        raise SystemExit(f"<{element} {name}> is not a literal string")
    return a


def edit(m, args):
    if args.package:
        pkg_attr = string_attr(m, "manifest", "package")
        old = m.s(pkg_attr.data)
        m.set_string(pkg_attr.data, args.package, {("manifest", "package")})
        renamed = 0
        for a in m.find("provider", "authorities"):
            value = m.s(a.data)
            parts = value.split(";")
            if not any(p.startswith(old + ".") for p in parts):
                continue
            new = ";".join(args.package + p[len(old):] if p.startswith(old + ".") else p
                           for p in parts)
            m.set_string(a.data, new, {("provider", "authorities")})
            renamed += 1
        # Permissions the app defines for itself are namespaced by its id too
        # (androidx's DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION, when present).
        for element in ("permission", "uses-permission"):
            for a in m.find(element, "name"):
                value = m.s(a.data)
                if value.startswith(old + ".") and value.endswith("_PERMISSION"):
                    m.set_string(a.data, args.package + value[len(old):],
                                 {("permission", "name"), ("uses-permission", "name")})
                    renamed += 1
        print(f"package: {old} -> {args.package} ({renamed} authorities/permissions renamed)")
    if args.label:
        labels = [a for a in m.find("application", "label") + m.find("activity", "label")
                  if a.data_type == TYPE_STRING]
        if not labels:
            raise SystemExit("no literal android:label found")
        for idx in sorted({a.data for a in labels}):
            m.set_string(idx, args.label, {("application", "label"), ("activity", "label")})
        print(f"label: {args.label}")
    if args.version_name:
        a = string_attr(m, "manifest", "versionName")
        m.set_string(a.data, args.version_name, {("manifest", "versionName")})
        print(f"versionName: {args.version_name}")
    if args.version_code is not None:
        m.set_int(one(m.find("manifest", "versionCode"), "versionCode"), args.version_code)
        print(f"versionCode: {args.version_code}")
    for spec in args.add_provider:
        cls, _, authority = spec.partition("=")
        if not cls or not authority:
            raise SystemExit(f"bad --add-provider {spec!r}")
        if any(m.s(a.data) == authority for a in m.find("provider", "authorities")):
            raise SystemExit(f"a provider already uses the authority {authority}")
        m.add_element_like("provider", "androidx.startup.InitializationProvider",
                           {"name": cls, "authorities": authority})
        print(f"provider: {cls} ({authority})")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    e = sub.add_parser("edit")
    e.add_argument("input")
    e.add_argument("output")
    e.add_argument("--package")
    e.add_argument("--label")
    e.add_argument("--version-name")
    e.add_argument("--version-code", type=int)
    e.add_argument("--add-provider", action="append", default=[], metavar="CLASS=AUTHORITY")
    d = sub.add_parser("dump")
    d.add_argument("input")
    args = ap.parse_args()

    with open(args.input, "rb") as f:
        m = Manifest(f.read())
    if args.cmd == "dump":
        m.dump()
        return
    edit(m, args)
    with open(args.output, "wb") as f:
        f.write(m.encode())


if __name__ == "__main__":
    sys.exit(main())
