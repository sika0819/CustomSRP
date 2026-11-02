#!/usr/bin/env python3
"""Bake a tiling tangent-space normal from OilCanvas.png for the oil skybox.

OpenGL convention: +X along +U (yaw), +Y along +V (elevation), +Z out of the paint.
Unity imports this as a Normal Map; OilSkyboxNPR samples it with DecodeNormal.
"""

from __future__ import annotations

import struct
import zlib
from pathlib import Path

import numpy as np
from PIL import Image

SRC = Path(__file__).resolve().parent / "OilCanvas.png"
OUT = Path(__file__).resolve().parent / "OilCanvasNormal.png"

# Blur kills bristle noise so the normal follows the stroke body.
# Scale is per blurred texel. Edges land around 30–45 degrees.
BLUR_SIGMA = 1.15
HEIGHT_SCALE = 24.0


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


def blur_wrap(height: np.ndarray, sigma: float) -> np.ndarray:
    radius = max(int(round(sigma * 3.0)), 1)
    x = np.arange(-radius, radius + 1, dtype=np.float32)
    kernel = np.exp(-0.5 * (x / sigma) ** 2)
    kernel /= kernel.sum()
    blurred = height
    for axis in (1, 0):
        acc = np.zeros_like(blurred)
        for i, w in enumerate(kernel):
            acc += np.roll(blurred, int(x[i]), axis=axis) * w
        blurred = acc
    return blurred


def height_to_normal(height: np.ndarray, scale: float) -> np.ndarray:
    height = blur_wrap(height, BLUR_SIGMA)
    # Image row 0 is the top. Unity V increases toward that top after import.
    dhdu = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    dhdv = (np.roll(height, 1, axis=0) - np.roll(height, -1, axis=0)) * 0.5
    nx = -dhdu * scale
    ny = -dhdv * scale
    nz = np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack((nx / length, ny / length, nz / length), axis=-1)
    return np.clip(normal * 0.5 + 0.5, 0.0, 1.0)


def main() -> None:
    height = np.asarray(Image.open(SRC).convert("L"), dtype=np.float32) / 255.0
    encoded = height_to_normal(height, HEIGHT_SCALE)
    rgb = (encoded * 255.0 + 0.5).astype(np.uint8)
    write_png(OUT, rgb)

    n = encoded * 2.0 - 1.0
    slope = np.degrees(np.arctan(np.sqrt(n[..., 0] ** 2 + n[..., 1] ** 2) / np.maximum(n[..., 2], 1e-4)))
    print(f"Wrote {OUT}  slope deg  p50={np.median(slope):.1f}  p90={np.percentile(slope, 90):.1f}  max={slope.max():.1f}")


if __name__ == "__main__":
    main()
