#!/usr/bin/env python3
"""Convert a .pck's BC-compressed textures to ASTC for mobile GPUs.

The game's PC .pck stores textures as BC7/BC3/BC1 (CompressedTexture2D
".ctex" files). Desktop and Adreno GPUs sample those natively, but Apple
GPUs and many Android GPUs (Mali, PowerVR) cannot: Godot detects this and
expands every such texture to raw RGBA in memory at load time — about 4x
the VRAM and much slower loads. ASTC is the format those GPUs do support
natively. This tool rewrites each BC .ctex in the pack to ASTC with the
same dimensions and mip chain, so the game looks the same while skipping
the expansion entirely.

Sizes: ASTC 4x4 and BC7/BC3 are both 16 bytes per 4x4 block (8 bpp), so
the default conversion keeps the pck almost exactly the same size. BC1
(4 bpp) doubles its bytes with 4x4; --block 8x8 shrinks all textures to
2 bpp instead, for very low-memory devices, at a visible quality cost.

How: the pack is extracted to a temporary directory (converted textures
written, everything else copied through), then rebuilt with pck.py's
`build` — the same extract-and-build flow make_bootstrap.py uses.

Dependencies: astcenc (ARM astc-encoder release binary or distribution
package) on PATH, and `pip install texture2ddecoder` for BC decoding.
With no BC textures found, the pack is copied through unchanged.

  repack_astc.py <in.pck> <out.pck> [--block 4x4|8x8] [--quality ...]
                 [--jobs N] [--dry-run] [--verbose]
  repack_astc.py selftest
"""

import argparse
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
from concurrent.futures import ProcessPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pck  # noqa: E402

CTEX_MAGIC = b"GST2"

# Godot 4.5 Image::Format values (core/io/image.h). Only the ones this tool
# reads or writes are listed.
FMT_DXT1 = 17
FMT_DXT3 = 18
FMT_DXT5 = 19
FMT_BPTC_RGBA = 22
FMT_ASTC_4X4 = 35
FMT_ASTC_8X8 = 37

# source format -> (texture2ddecoder function, bytes per 4x4 block).
# DXT3 (explicit alpha) has no decoder in texture2ddecoder; BC6H is HDR;
# RGTC are 2-channel — all are left untouched (and are rare in a 2D game).
DECODERS = {
    FMT_DXT1: ("decode_bc1", 8),
    FMT_DXT5: ("decode_bc3", 16),
    FMT_BPTC_RGBA: ("decode_bc7", 16),
}
BLOCK_SIZES = {"4x4": (FMT_ASTC_4X4, 4, 4), "8x8": (FMT_ASTC_8X8, 8, 8)}
QUALITIES = ("fast", "medium", "thorough", "exhaustive")

# Release archives ship SIMD-specific binary names; any of them works, and
# ensure_astcenc_cmd() stages a plain `astcenc` link for the first found.
ASTCENC_NAMES = ("astcenc", "astcenc-avx2", "astcenc-sse4.1", "astcenc-sse2", "astcenc-neon")

# .ctex layout, from Godot 4.5 scene/resources/compressed_texture.cpp
# (_load_data + load_image_from_file) and editor/import/
# resource_importer_texture.cpp (_save_ctex): "GST2", u32 version, u32 outer
# width, u32 outer height, u32 flags, u32 mipmap_limit, u32 reserved[3], then
# the embedded image: u32 data_format (0 = raw Image data), u16 w, u16 h,
# u32 mipmap count minus 1, u32 Image::Format, and the mip chain as one
# contiguous block-data blob. The format field sits at byte 48; data at 52.
HEADER_SIZE = 52
FORMAT_OFFSET = 48

BATCH = 64  # textures read into memory at a time (a big pck is ~2 GB)

_WORKER = {}  # per-process config, set by _init_worker


class Skip(Exception):
    """Texture left unchanged, with the reason."""


# --- file writes -----------------------------------------------------------
# Every write goes through openat-style primitives: a single validated path
# component opened relative to a directory fd with O_NOFOLLOW, so no "..",
# absolute component, or symlink in pck entry data can escape the target
# directory.


def _component(name):
    if not name or name in (".", "..") or "/" in name or "\\" in name or "\0" in name:
        raise SystemExit(f"unsafe path component: {name!r}")
    return name


def write_under(dir_fd, name, data):
    fd = os.open(_component(name), os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o644, dir_fd=dir_fd)
    try:
        view = memoryview(data)
        while view:
            view = view[os.write(fd, view) :]
    finally:
        os.close(fd)


def read_under(dir_fd, name):
    fd = os.open(_component(name), os.O_RDONLY | os.O_NOFOLLOW, dir_fd=dir_fd)
    try:
        chunks = []
        while True:
            chunk = os.read(fd, 1 << 20)
            if not chunk:
                return b"".join(chunks)
            chunks.append(chunk)
    finally:
        os.close(fd)


def extract_file(root_real, rel, data):
    parts = [_component(part) for part in rel.split("/")]
    fd = os.open(root_real, os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in parts[:-1]:
            try:
                os.mkdir(part, 0o755, dir_fd=fd)
            except FileExistsError:
                pass
            sub = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = sub
        write_under(fd, parts[-1], data)
    finally:
        os.close(fd)


def safe_entry_path(path):
    """Entry names are external input; keep them plain relative paths."""
    parts = path.split("/")
    if os.path.isabs(path) or any(p == ".." for p in parts):
        raise SystemExit(f"unsafe entry path in pck: {path!r}")
    return path


# --- encoder ----------------------------------------------------------------


def ensure_astcenc_cmd():
    """Make `astcenc` resolvable by that exact name on PATH.

    The encoder is only ever invoked as the literal command "astcenc"; if
    PATH only has a SIMD-specific name (astcenc-avx2 and friends), a link
    by the plain name is staged into a private temp directory prepended to
    PATH. Call before spawning the worker pool so children inherit it.
    """
    if shutil.which(ASTCENC_NAMES[0]):
        return
    for name in ASTCENC_NAMES[1:]:
        found = shutil.which(name)
        if found:
            staging = tempfile.mkdtemp(prefix="astcenc-cmd")
            link = os.path.join(staging, ASTCENC_NAMES[0])
            try:
                os.symlink(os.path.abspath(found), link)
            except OSError:  # e.g. symlink privilege on Windows
                shutil.copy2(found, link)
                os.chmod(link, 0o755)
            os.environ["PATH"] = staging + os.pathsep + os.environ.get("PATH", "")
            return
    raise SystemExit("astcenc not found on PATH; install the astc-encoder package or binary")


# --- .ctex -----------------------------------------------------------------


def parse_ctex(data):
    if len(data) < HEADER_SIZE or data[:4] != CTEX_MAGIC:
        raise Skip("not a GST2 ctex")
    version, outer_w, outer_h, flags = struct.unpack_from("<4I", data, 4)
    (data_format, w, h, mips_minus_1) = struct.unpack_from("<IHHI", data, 36)
    fmt = struct.unpack_from("<I", data, FORMAT_OFFSET)[0]
    if version != 1:
        raise Skip(f"ctex version {version}")
    if data_format != 0:  # DATA_FORMAT_IMAGE; PNG/WEBP/Basis are not BC
        raise Skip(f"data format {data_format}")
    return {"w": w, "h": h, "mips": mips_minus_1, "fmt": fmt, "blob": data[HEADER_SIZE:]}


def mip_layout(w, h, mips_minus_1, block_w, block_h, bytes_per_block):
    """[(level, w, h, offset, size)] within the contiguous mip blob."""
    out = []
    offset = 0
    for level in range(mips_minus_1 + 1):
        lw, lh = max(1, w >> level), max(1, h >> level)
        blocks = math.ceil(lw / block_w) * math.ceil(lh / block_h)
        size = blocks * bytes_per_block
        out.append((level, lw, lh, offset, size))
        offset += size
    return out


def encode_astc(bgra, w, h, block, quality, dir_fd, cwd):
    """RGBA pixels -> raw ASTC block data (astc file header stripped)."""
    if block not in BLOCK_SIZES or quality not in QUALITIES:
        raise Skip(f"bad astcenc arguments: {block!r} {quality!r}")
    # Uncompressed 32-bit top-down TGA stores B,G,R,A — the exact byte order
    # texture2ddecoder returns — so no channel shuffle is needed.
    write_under(
        dir_fd,
        "mip.tga",
        struct.pack("<BBBHHBHHHHBB", 0, 0, 2, 0, 0, 0, 0, 0, w, h, 32, 0x28) + bgra,
    )
    # -cl (LDR linear): the stored BC values pass through unmodified and the
    # encoder minimizes raw value error, matching however Godot ends up
    # sampling the texture (sRGB-ness is a usage flag, not in the file).
    # Constant command and in/out file names inside a private temp dir (cwd);
    # block and quality are validated against fixed sets above. astcenc wants
    # the block size bare ("4x4") and the quality with a dash ("-fast").
    subprocess.run(
        ["astcenc", "-cl", "mip.tga", "mip.astc", block, "-" + quality, "-silent", "-j", "1"],
        cwd=cwd,
        check=True,
        capture_output=True,
        shell=False,
    )
    out = read_under(dir_fd, "mip.astc")
    magic, bw, bh, bd = struct.unpack_from("<IBBB", out, 0)
    if magic != 0x5CA1AB13 or bd != 1 or (bw, bh) != tuple(int(x) for x in block.split("x")):
        raise Skip("unexpected astcenc output header")
    blocks = math.ceil(w / bw) * math.ceil(h / bh)
    data = out[16:]
    if len(data) != blocks * 16:
        raise Skip(f"astcenc wrote {len(data)} bytes, expected {blocks * 16}")
    return data


def _init_worker(block, quality):
    import texture2ddecoder

    _WORKER.update(t2d=texture2ddecoder, block=block, quality=quality)


def transcode(data):
    """One .ctex file -> (new bytes, note for the log), or raises Skip."""
    t2d = _WORKER["t2d"]
    block, quality = _WORKER["block"], _WORKER["quality"]
    tex = parse_ctex(data)
    if tex["fmt"] not in DECODERS:
        raise Skip(f"format {tex['fmt']}")
    decoder, src_bpb = DECODERS[tex["fmt"]]
    dst_fmt = BLOCK_SIZES[block][0]
    layout = mip_layout(tex["w"], tex["h"], tex["mips"], 4, 4, src_bpb)
    expected = layout[-1][3] + layout[-1][4]
    if expected != len(tex["blob"]):
        raise Skip(f"mip chain is {len(tex['blob'])} bytes, header says {expected}")

    decode = getattr(t2d, decoder)
    parts = []
    for _, lw, lh, offset, size in layout:
        bgra = decode(bytes(tex["blob"][offset : offset + size]), lw, lh)
        with tempfile.TemporaryDirectory() as tmp:
            tmp_real = os.path.realpath(tmp)
            dir_fd = os.open(tmp_real, os.O_RDONLY | os.O_DIRECTORY)
            try:
                parts.append(encode_astc(bgra, lw, lh, block, quality, dir_fd, tmp_real))
            finally:
                os.close(dir_fd)
    new_blob = b"".join(parts)
    return data[:FORMAT_OFFSET] + struct.pack("<I", dst_fmt) + new_blob, (
        f"{tex['fmt']}->{dst_fmt} {tex['w']}x{tex['h']}x{tex['mips'] + 1}"
    )


def _transcode_job(item):
    path, data = item
    try:
        new, note = transcode(data)
        return path, new, note, None
    except Skip as why:
        return path, None, None, str(why)
    except Exception as e:  # keep the pack intact; one odd texture is not fatal
        return path, None, None, f"error: {type(e).__name__}: {e}"


# --- driver ----------------------------------------------------------------


def scan_only(in_pck, ctex_entries, verbose):
    """--dry-run: parse headers only, no decode, no output."""
    stats = {"converted": 0, "skipped": 0}
    reasons = {}
    with open(in_pck, "rb") as f:
        for e in ctex_entries:
            f.seek(e.offset)
            try:
                tex = parse_ctex(f.read(e.size))
                reason = None if tex["fmt"] in DECODERS else f"format {tex['fmt']}"
            except Skip as why:
                reason = str(why)
            if reason:
                stats["skipped"] += 1
                reasons[reason] = reasons.get(reason, 0) + 1
            else:
                stats["converted"] += 1
                if verbose:
                    print(f"  would convert {e.path}: format {tex['fmt']} {tex['w']}x{tex['h']}")
    print(f"dry run: would convert {stats['converted']}, would keep {stats['skipped']}")
    return reasons


def repack(in_pck, out_pck, args):
    header, entries = pck.read_pck(in_pck)
    for e in entries:
        e.path = safe_entry_path(e.path)
        if e.flags & (pck.FILE_ENCRYPTED | pck.FILE_REMOVAL):
            raise SystemExit(f"{e.path}: encrypted/removal entries are not supported")
    ctex = [e for e in entries if e.path.endswith(".ctex")]
    print(
        f"{in_pck}: format v{header['version']}, engine {header['engine']}, "
        f"{len(entries)} files, {len(ctex)} ctex"
    )

    if args.dry_run:
        for reason, count in sorted(scan_only(in_pck, ctex, args.verbose).items(), key=lambda kv: -kv[1]):
            print(f"  would keep {count:4d} x {reason}")
        return

    converted = set()
    stats = {"converted": 0, "skipped": 0}
    reasons = {}
    started = time.time()
    done = 0
    # Convert batch by batch, extracting each batch's files as they finish so
    # memory stays at one batch (the real pack is ~2 GB of textures), then
    # rebuild the pck from the extracted tree with pck.py's builder — the
    # same flow make_bootstrap.py uses.
    with tempfile.TemporaryDirectory(prefix="repack_astc") as tmp:
        root = os.path.realpath(tmp)
        in_bytes = out_bytes = 0
        with open(in_pck, "rb") as f, ProcessPoolExecutor(
            args.jobs, initializer=_init_worker, initargs=(args.block, args.quality)
        ) as pool:
            for start in range(0, len(ctex), BATCH):
                batch = ctex[start : start + BATCH]
                datas = {}
                for e in batch:
                    f.seek(e.offset)
                    datas[e.path] = f.read(e.size)
                results = {path: result for path, *result in pool.map(
                    _transcode_job, list(datas.items()), chunksize=1
                )}
                for e in batch:
                    new, note, why = results[e.path]
                    done += 1
                    if why is not None:
                        stats["skipped"] += 1
                        reasons[why] = reasons.get(why, 0) + 1
                        if args.verbose and not why.startswith("format "):
                            print(f"  skip {e.path}: {why}")
                        extract_file(root, e.path, datas[e.path])
                    else:
                        stats["converted"] += 1
                        converted.add(e.path)
                        in_bytes += e.size
                        out_bytes += len(new)
                        if args.verbose:
                            print(f"  ok   {e.path}: {note}")
                        extract_file(root, e.path, new)
                del datas, results
                rate = done / max(time.time() - started, 1)
                print(f"  {done}/{len(ctex)} textures ({rate:.1f}/s)")

        with open(in_pck, "rb") as f:
            extracted = converted | {c.path for c in ctex}
            for e in entries:
                if e.path in extracted:
                    continue  # already extracted above
                f.seek(e.offset)
                extract_file(root, e.path, f.read(e.size))
        pck.main(["build", out_pck, tmp])
    print(
        f"wrote {out_pck}: {stats['converted']} converted, {stats['skipped']} kept, "
        f"{in_bytes} -> {out_bytes} bytes of textures "
        f"({(out_bytes - in_bytes) / max(in_bytes, 1) * 100:+.1f}%)"
    )
    for reason, count in sorted(reasons.items(), key=lambda kv: -kv[1]):
        print(f"  kept {count:4d} x {reason}")


def selftest():
    """Transcode hand-built BC1/BC3 textures through the whole pipeline."""
    import texture2ddecoder as t2d

    ensure_astcenc_cmd()

    def expand5(v):
        return (v << 3) | (v >> 2)

    def expand6(v):
        return (v << 2) | (v >> 4)

    r, g, b = 200, 60, 120
    c565 = (r >> 3) << 11 | (g >> 2) << 5 | (b >> 3)
    want_bgr = bytes((expand5(b >> 3), expand6(g >> 2), expand5(r >> 3)))
    bc1_color = struct.pack("<HH", c565, c565) + b"\0\0\0\0"

    def ctex(fmt, w, h, mips, blob):
        head = (
            CTEX_MAGIC
            + struct.pack("<4I", 1, w, h, 0)  # version, outer w/h, flags
            + struct.pack("<4I", 0, 0, 0, 0)  # mipmap_limit + reserved[3]
            + struct.pack("<IHHI", 0, w, h, mips)  # data_format, w, h, mips
            + struct.pack("<I", fmt)
        )
        assert len(head) == HEADER_SIZE
        return head + blob

    # 8x8, two mip levels (4 blocks + 1 block); BC3 adds 4-bit alpha 0xAB.
    bc1 = ctex(FMT_DXT1, 8, 8, 1, bc1_color * 4 + bc1_color)
    bc3 = ctex(FMT_DXT5, 8, 8, 1, (b"\xbb" * 8 + bc1_color) * 4 + b"\xbb" * 8 + bc1_color)
    for name, data, want_alpha in (("bc1", bc1, 0xFF), ("bc3", bc3, 0xBB)):
        _init_worker("4x4", "fast")
        new, note = transcode(data)
        fmt = struct.unpack_from("<I", new, FORMAT_OFFSET)[0]
        assert fmt == FMT_ASTC_4X4, f"{name}: format field is {fmt}"
        assert len(new) == HEADER_SIZE + 16 * 5, f"{name}: wrong data size {len(new)}"
        back = t2d.decode_astc(new[HEADER_SIZE:], 8, 8, 4, 4)
        got, want = back[:4], bytes((want_bgr[0], want_bgr[1], want_bgr[2], want_alpha))
        diffs = [abs(x - y) for x, y in zip(got, want)]
        assert max(diffs) <= 2, f"{name}: pixel {got.hex()} != {want.hex()} (diffs {diffs})"
        print(f"selftest {name}: ok ({note}), pixel diff {diffs}")

    # Odd (non-multiple-of-4) dimensions: partial blocks decode and encode.
    odd = ctex(FMT_DXT1, 6, 6, 0, bc1_color * 4)
    _init_worker("4x4", "fast")
    new, _ = transcode(odd)
    assert len(new) == HEADER_SIZE + 16 * 4, "6x6 should be 4 blocks"
    back = t2d.decode_astc(new[HEADER_SIZE:], 6, 6, 4, 4)
    want = bytes((want_bgr[0], want_bgr[1], want_bgr[2], 0xFF))
    diffs = [abs(x - y) for x, y in zip(back[:4], want)]
    assert max(diffs) <= 2, f"odd: pixel {back[:4].hex()} (diffs {diffs})"
    print(f"selftest odd-dims: ok (pixel diff {diffs})")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd")
    s = sub.add_parser("repack")
    s.add_argument("in_pck")
    s.add_argument("out_pck")
    s.add_argument("--block", choices=sorted(BLOCK_SIZES), default="4x4")
    s.add_argument("--quality", choices=list(QUALITIES), default="medium")
    s.add_argument("--jobs", type=int, default=os.cpu_count() or 2)
    s.add_argument("--dry-run", action="store_true", help="report what would convert, write nothing")
    s.add_argument("--verbose", action="store_true")
    s.set_defaults(func=lambda a: repack(a.in_pck, a.out_pck, a))
    sub.add_parser("selftest").set_defaults(func=lambda a: selftest())

    args = p.parse_args(argv)
    if args.cmd == "selftest" or (args.cmd == "repack" and not args.dry_run):
        if os.open not in os.supports_dir_fd:
            raise SystemExit(
                "this tool needs Python's dir_fd support (Unix, macOS, WSL); "
                "on Windows run it inside WSL"
            )
    if args.cmd == "repack" and not args.dry_run:
        ensure_astcenc_cmd()
        try:
            import texture2ddecoder  # noqa: F401
        except ImportError:
            raise SystemExit("texture2ddecoder missing; run: pip install texture2ddecoder")
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
