#!/usr/bin/env python3
"""Bake ShiTou_OilBrushNormal from ShiTou_OilTile (1:1 UV).

Same convention as generate_oil_paint_normals.py: height from paint value,
plus a soft luminance form so dark fissures sink. Sample with _BumpTiling = 1
(same triplanar UVs as _BaseMap).
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "Assets/GameMain/Environment/Textures/ShiTou_OilTile.png"
OUT = ROOT / "Assets/GameMain/Environment/Textures/ShiTou_OilBrushNormal.png"

BLUR_SIGMA = 1.7
HEIGHT_SCALE = 12.0


def write_png(path: Path, rgb: np.ndarray) -> None:
    h, w, _ = rgb.shape
    raw = b"".join(b"\x00" + rgb[y].tobytes() for y in range(h))
    compressed = zlib.compress(raw, 9)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", compressed) + chunk(b"IEND", b"")
    )


def blur_wrap(a: np.ndarray, sigma: float) -> np.ndarray:
    if sigma <= 0:
        return a.astype(np.float32)
    radius = max(int(round(sigma * 3.0)), 1)
    x = np.arange(-radius, radius + 1, dtype=np.float32)
    kernel = np.exp(-0.5 * (x / sigma) ** 2)
    kernel /= kernel.sum()
    out = a.astype(np.float32)
    for axis in (1, 0):
        acc = np.zeros_like(out)
        for i, weight in enumerate(kernel):
            acc += np.roll(out, int(x[i]), axis=axis) * weight
        out = acc
    return out


def height_to_normal(height: np.ndarray, scale: float) -> np.ndarray:
    height = blur_wrap(height, BLUR_SIGMA)
    dhdu = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    dhdv = (np.roll(height, 1, axis=0) - np.roll(height, -1, axis=0)) * 0.5
    nx = -dhdu * scale
    ny = -dhdv * scale
    nz = np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack((nx / length, ny / length, nz / length), axis=-1)
    return np.clip(normal * 0.5 + 0.5, 0.0, 1.0)


def bake() -> None:
    rgb = np.asarray(Image.open(SRC).convert("RGB"), dtype=np.float32) / 255.0
    paint = rgb.max(axis=2)
    luma = 0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]

    # Direct OilTile lock: paint body + luminance fissures (same pixels as albedo)
    height = 0.62 * paint + 0.38 * luma
    # Soften only pigment grit; keep slab / crack structure
    height = blur_wrap(height, 0.85)
    lo, hi = np.percentile(height, 2), np.percentile(height, 98)
    height = np.clip((height - lo) / (hi - lo + 1e-6), 0.0, 1.0)
    height = 0.5 + (height - 0.5) * 0.80

    encoded = height_to_normal(height, HEIGHT_SCALE)
    write_png(OUT, (encoded * 255.0 + 0.5).astype(np.uint8))

    n = encoded * 2.0 - 1.0
    slope = np.degrees(
        np.arctan(np.sqrt(n[..., 0] ** 2 + n[..., 1] ** 2) / np.maximum(n[..., 2], 1e-4))
    )
    gx = np.abs(np.roll(luma, -1, 1) - np.roll(luma, 1, 1))
    gy = np.abs(np.roll(luma, 1, 0) - np.roll(luma, -1, 0))
    edge = (gx + gy).ravel()
    sm = np.sqrt(n[..., 0] ** 2 + n[..., 1] ** 2).ravel()
    corr = float(np.corrcoef(edge, sm)[0, 1])
    print(
        f"Wrote {OUT.name}  {rgb.shape[1]}x{rgb.shape[0]}"
        f"  slope deg  p50={np.median(slope):.1f}  p90={np.percentile(slope, 90):.1f}"
        f"  max={slope.max():.1f}  edgeCorr={corr:.3f}"
    )


if __name__ == "__main__":
    bake()
