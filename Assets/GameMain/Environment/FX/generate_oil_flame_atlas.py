#!/usr/bin/env python3
"""4x4 oil-paint flame stroke atlas — opaque impasto slabs (white RGB + alpha).

Designed for a *single* ParticleSystem: particle Color over Lifetime supplies
inner (cream) → outer (deep orange/red). Atlas is shape + paint grain only.
"""

from __future__ import annotations

import math
import random
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent
OUT_FX = ROOT / "FX Mats" / "OilFlameStrokeAtlas.png"
OUT_RES = ROOT.parent.parent / "Resources" / "OilFlameStrokeAtlas.png"
COLS = 4
ROWS = 4
CELL = 160
SIZE = CELL * COLS


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
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", compressed)
        + chunk(b"IEND", b"")
    )


def smoothstep(e0: float, e1: float, x: float) -> float:
    t = max(0.0, min(1.0, (x - e0) / max(1e-6, e1 - e0)))
    return t * t * (3.0 - 2.0 * t)


def clamp01(x: float) -> float:
    return max(0.0, min(1.0, x))


def flame_silhouette(
    u: float,
    v: float,
    kind: int,
    lean: float,
    tip_cut: float,
) -> float:
    """Continuous oil-slab alpha — no stipple / point-cloud grain."""
    curve = math.sin(v * math.pi) * lean
    x = u - 0.5 - curve

    if kind == 0:  # broad core slab
        base_w, tip_w, height, edge = 0.46, 0.10, 0.93, 0.055
    elif kind == 1:  # medium tongue
        base_w, tip_w, height, edge = 0.32, 0.07, 0.96, 0.045
    elif kind == 2:  # thin wisp
        base_w, tip_w, height, edge = 0.18, 0.04, 0.98, 0.035
    else:  # detached tip / short streak (still a solid stroke)
        base_w, tip_w, height, edge = 0.12, 0.03, 0.88, 0.03
        x = u - 0.5 - lean * (v - 0.25)

    if v < 0.015 or v > height * tip_cut:
        return 0.0

    t = clamp01(v / (height * tip_cut))
    # Flame: wide base, tapering tip.
    half = tip_w + (base_w - tip_w) * (1.0 - t) ** 0.72
    # Soft palette-knife serration (low frequency — reads as paint edge, not noise).
    jag = 0.018 * math.sin(v * 14.0 + lean * 9.0) + 0.010 * math.sin(v * 23.0)
    half = max(0.02, half + jag)

    dist = abs(x)
    body = 1.0 - smoothstep(half - edge, half + edge * 0.25, dist)
    body *= smoothstep(0.0, 0.10, v)
    body *= 1.0 - smoothstep(height * tip_cut - 0.08, height * tip_cut, v)

    # Soft heart: denser center, feathered rim (oil mass, not dots).
    core = 1.0 - smoothstep(0.0, half * 0.85, dist)
    body = clamp01(body * (0.55 + 0.45 * core))
    return body


def impasto_value(u: float, v: float, x_norm: float, seed: float) -> float:
    """Broad brush ridges — low frequency only."""
    ridge = 0.78 + 0.22 * abs(math.sin(x_norm * math.pi * 1.25 + v * 7.0 + seed))
    knife = 0.92 + 0.08 * math.sin((u + v) * 9.0 + seed * 2.0)
    # Very soft canvas weave, amplitude tiny so it never becomes grain cloud.
    weave = 0.97 + 0.03 * math.sin(u * 28.0) * math.cos(v * 22.0)
    return clamp01(ridge * knife * weave)


def paint_cell(buf: bytearray, col: int, row: int, kind: int, seed: int) -> None:
    rng = random.Random(seed)
    lean = rng.uniform(-0.16, 0.16)
    tip_cut = rng.uniform(0.92, 1.0)
    ox = col * CELL
    oy = row * CELL
    for ly in range(CELL):
        for lx in range(CELL):
            u = (lx + 0.5) / CELL
            v = 1.0 - (ly + 0.5) / CELL  # tip up in Unity particle UV
            a = flame_silhouette(u, v, kind, lean, tip_cut)
            if a < 0.02:
                continue
            # Feather only the extreme rim — keep interior opaque for oil slabs.
            a = smoothstep(0.02, 0.55, a)
            if a < 0.01:
                continue
            curve = math.sin(v * math.pi) * lean
            x_norm = (u - 0.5 - curve) / 0.5
            val = impasto_value(u, v, x_norm, seed * 0.01)
            # Near-white pigment; particle vertex color tints the oil.
            g = int(255 * (0.88 + 0.12 * val))
            o = ((oy + ly) * SIZE + (ox + lx)) * 4
            buf[o] = g
            buf[o + 1] = g
            buf[o + 2] = g
            buf[o + 3] = int(255 * a)


def main() -> None:
    buf = bytearray(SIZE * SIZE * 4)
    # Row0 broad core, Row1 medium, Row2 wisps, Row3 short tips — 4 variants each.
    kinds = [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3]
    for i, kind in enumerate(kinds):
        paint_cell(buf, i % COLS, i // COLS, kind, seed=2200 + i * 31)
    data = bytes(buf)
    write_png(OUT_FX, data, SIZE, SIZE)
    write_png(OUT_RES, data, SIZE, SIZE)
    print(f"Wrote {OUT_FX}")
    print(f"Wrote {OUT_RES}")
    print(f"  {SIZE}x{SIZE}, {COLS}x{ROWS} cells @ {CELL}px")


if __name__ == "__main__":
    main()
