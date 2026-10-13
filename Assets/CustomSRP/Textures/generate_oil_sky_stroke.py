#!/usr/bin/env python3
"""Broad curved strokes for OilSkyboxNPR (_SkyStrokeMap). Repeatable tile."""

from __future__ import annotations

import math
import random
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent / "OilSkyStroke.png"
SIZE = 512


def write_png(path: Path, rgba: bytes, w: int, h: int) -> None:
    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    raw = b"".join(b"\x00" + rgba[y * w * 4 : (y + 1) * w * 4] for y in range(h))
    compressed = zlib.compress(raw, 9)
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", compressed)
        + chunk(b"IEND", b"")
    )
    path.write_bytes(png)


def main() -> None:
    rng = random.Random(42)
    pixels = [0.5] * (SIZE * SIZE)

    for _ in range(28):
        cx = rng.uniform(0, SIZE)
        cy = rng.uniform(0, SIZE)
        angle = rng.uniform(0, math.tau)
        length = rng.uniform(SIZE * 0.35, SIZE * 1.1)
        width = rng.uniform(18, 42)
        amp = rng.uniform(0.12, 0.38)
        freq = rng.uniform(0.008, 0.022)
        phase = rng.uniform(0, math.tau)
        dx = math.cos(angle)
        dy = math.sin(angle)
        px = -dy
        py = dx
        for y in range(SIZE):
            for x in range(SIZE):
                fx = x + 0.5
                fy = y + 0.5
                along = (fx - cx) * dx + (fy - cy) * dy
                across = (fx - cx) * px + (fy - cy) * py
                wave = math.sin(along * freq + phase) * width * amp
                dist = abs(across - wave)
                if abs(along) > length * 0.5:
                    continue
                t = 1.0 - dist / width
                if t <= 0:
                    continue
                t = t * t * (3 - 2 * t)
                idx = y * SIZE + x
                pixels[idx] = min(1.0, pixels[idx] + t * amp)

    rgba = bytearray(SIZE * SIZE * 4)
    for i, v in enumerate(pixels):
        g = int(max(0, min(255, v * 255)))
        o = i * 4
        rgba[o : o + 4] = bytes((g, g, g, 255))

    write_png(OUT, bytes(rgba), SIZE, SIZE)
    print(f"Wrote {OUT}")


if __name__ == "__main__":
    main()
