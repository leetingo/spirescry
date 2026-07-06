#!/usr/bin/env python3
"""Minimal Godot 4.x .pck extractor for the localization/ subtree."""
import mmap
import struct
import os
import sys
import pathlib

MAGIC = b"GDPC"
PACK_FLAG_DIR_ENCRYPTED = 1
PACK_FLAG_REL_FILEBASE = 2
FILE_FLAG_ENCRYPTED = 1


def main(pck_path: str, out_dir: str, prefix: str = "res://localization/"):
    out = pathlib.Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    out_resolved = out.resolve()

    # The pack is gigabytes and we want <1% of it — map instead of read,
    # so only the header, directory, and matched bodies get paged in.
    with open(pck_path, "rb") as f:
        data = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)

    base = 0
    if data[base:base + 4] != MAGIC:
        offset = data.find(MAGIC)
        if offset < 0:
            sys.exit("magic not found")
        base = offset
    pos = base + 4
    pack_format = struct.unpack_from("<I", data, pos)[0]; pos += 4
    major, minor, rev = struct.unpack_from("<III", data, pos); pos += 12
    print(f"pck format={pack_format} godot={major}.{minor}.{rev}", file=sys.stderr)

    flags = 0
    file_base = 0
    dir_offset = 0
    if pack_format >= 2:
        flags = struct.unpack_from("<I", data, pos)[0]; pos += 4
        file_base = struct.unpack_from("<Q", data, pos)[0]; pos += 8
        # STS2 pck has the directory at the END of the file. The
        # 8 bytes here are the directory offset (uint64). Standard
        # Godot 4.x pcks put the directory right after the header.
        dir_offset = struct.unpack_from("<Q", data, pos)[0]; pos += 8
        print(f"flags={flags} file_base=0x{file_base:x} dir_offset=0x{dir_offset:x}", file=sys.stderr)
        if flags & PACK_FLAG_DIR_ENCRYPTED:
            sys.exit("encrypted directory — not supported")
    # Reserved 16 uint32 → skip whatever's left of the 16-uint32 reserved
    # area. We've already consumed 8 of those 64 bytes above (dir_offset).
    pos += 14 * 4

    if dir_offset and dir_offset < len(data):
        # Jump to directory at end of file
        pos = dir_offset

    file_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    print(f"file_count={file_count}", file=sys.stderr)

    # Pck paths are stored without the "res://" scheme prefix.
    # Strip it from the user's prefix arg if present.
    scheme = "res://"
    clean_prefix = prefix[len(scheme):] if prefix.startswith(scheme) else prefix

    extracted = 0
    for _ in range(file_count):
        path_len = struct.unpack_from("<I", data, pos)[0]; pos += 4
        path_raw = data[pos:pos + path_len]
        pos += path_len
        path = path_raw.rstrip(b"\x00").decode("utf-8", errors="replace")
        offset = struct.unpack_from("<Q", data, pos)[0]; pos += 8
        size = struct.unpack_from("<Q", data, pos)[0]; pos += 8
        pos += 16  # md5
        file_flags = 0
        if pack_format >= 2:
            file_flags = struct.unpack_from("<I", data, pos)[0]; pos += 4
        if not path.startswith(clean_prefix):
            continue
        if file_flags & FILE_FLAG_ENCRYPTED:
            print(f"skip (encrypted): {path}", file=sys.stderr)
            continue
        actual_offset = offset
        if flags & PACK_FLAG_REL_FILEBASE:
            actual_offset = file_base + offset
        body = data[actual_offset:actual_offset + size]
        rel = path[len("res://"):] if path.startswith("res://") else path
        out_path = (out / rel).resolve()
        if not out_path.is_relative_to(out_resolved):
            print(f"skip (escapes out_dir): {path}", file=sys.stderr)
            continue
        out_path.parent.mkdir(parents=True, exist_ok=True)
        with open(out_path, "wb") as g:
            g.write(body)
        extracted += 1
    print(f"extracted {extracted} files matching '{prefix}' to {out_dir}", file=sys.stderr)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("usage: extract_loc.py PCK OUTDIR [prefix]", file=sys.stderr)
        sys.exit(2)
    pref = sys.argv[3] if len(sys.argv) > 3 else "res://localization/"
    main(sys.argv[1], sys.argv[2], pref)
