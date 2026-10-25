#!/usr/bin/env python3
"""Validate .nes files the same way the app does (iNES/NES 2.0 header, sizes, mapper).

usage: scripts/validate-rom.py FILE.nes [...]
exit code 0 if every file is importable, 1 otherwise.
"""
import hashlib
import sys

SUPPORTED_MAPPERS = {0, 1, 2, 3, 4, 7}
MAX_ROM_BYTES = 32 * 1024 * 1024


def check(data: bytes):
    """Returns (status, info). status is 'ok', 'invalid', 'unsupported' or 'too large'."""
    if len(data) > MAX_ROM_BYTES:
        return "too large", {}
    if len(data) < 16 or data[:4] != b"NES\x1a":
        return "invalid", {"reason": "not an iNES file"}
    h = data[:16]
    nes2 = (h[7] & 0x0C) == 0x08
    prg, chr_ = h[4], h[5]
    if nes2:
        if (h[9] & 0x0F) == 0x0F or (h[9] >> 4) == 0x0F:
            return "invalid", {"reason": "exponent size notation unsupported"}
        prg |= (h[9] & 0x0F) << 8
        chr_ |= (h[9] >> 4) << 8
        mapper = (h[6] >> 4) | (h[7] & 0xF0) | ((h[8] & 0x0F) << 8)
    else:
        junk = any(h[12:16])
        mapper = (h[6] >> 4) | (0 if junk else (h[7] & 0xF0))
    if prg == 0:
        return "invalid", {"reason": "no PRG ROM"}
    need = 16 + (512 if h[6] & 0x04 else 0) + prg * 16384 + chr_ * 8192
    info = {"mapper": mapper, "format": "NES 2.0" if nes2 else "iNES", "prg_kib": prg * 16,
            "chr_kib": chr_ * 8, "battery": bool(h[6] & 0x02)}
    if len(data) < need:
        return "invalid", dict(info, reason=f"truncated: {len(data)} < {need} bytes")
    if mapper not in SUPPORTED_MAPPERS:
        return "unsupported", info
    return "ok", info


def main(argv):
    if len(argv) < 2:
        print(__doc__.strip())
        return 2
    ok = True
    for path in argv[1:]:
        try:
            with open(path, "rb") as f:
                data = f.read(MAX_ROM_BYTES + 1)
        except OSError as e:
            print(f"{path}: cannot read ({e})")
            ok = False
            continue
        status, info = check(data)
        sha = hashlib.sha256(data).hexdigest()
        print(f"{path}: {status.upper()} {info} sha256={sha}")
        ok &= status == "ok"
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
