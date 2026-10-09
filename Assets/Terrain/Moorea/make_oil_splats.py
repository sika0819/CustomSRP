#!/usr/bin/env python3
"""Author seamless oil-paint terrain splats missing from the Moorea set.

Sand / grass / rock already exist. Forest and built-up are generated here
so WorldCover classes have a matching albedo. Strokes wrap, then opposite
edges are blended to the same pixels (Repeat seam ≈ 0).
"""
from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
SIZE = 1024


def stamp_ellipse(
    img: np.ndarray,
    cx: float,
    cy: float,
    rx: float,
    ry: float,
    ang: float,
    color: np.ndarray,
    alpha: float,
) -> None:
    """Short loaded brush: flat core, soft rim. Coordinates wrap."""
    size = img.shape[0]
    r = int(np.ceil(max(rx, ry))) + 1
    yy, xx = np.mgrid[-r : r + 1, -r : r + 1]
    ca = float(np.cos(ang))
    sa = float(np.sin(ang))
    lx = xx * ca + yy * sa
    ly = -xx * sa + yy * ca
    norm = (lx / max(rx, 0.5)) ** 2 + (ly / max(ry, 0.5)) ** 2
    core = np.clip((1.05 - norm) / 0.55, 0.0, 1.0)
    weight = core * core * (3.0 - 2.0 * core)
    if float(weight.max()) <= 0:
        return
    ys = (int(round(cy)) + yy) % size
    xs = (int(round(cx)) + xx) % size
    w = (alpha * weight)[..., None]
    img[ys, xs] = img[ys, xs] * (1.0 - w) + color * w


def paint(size: int, palette: np.ndarray, seed: int, strokes: int) -> np.ndarray:
    """Dense impasto dabs, same family as SplatGrass / SplatRock — not hairline strokes."""
    rng = np.random.default_rng(seed)
    img = np.empty((size, size, 3), np.float32)
    img[:] = palette[len(palette) // 2]

    for _ in range(strokes):
        color = palette[int(rng.integers(0, palette.shape[0]))]
        color = np.clip(color + rng.normal(0, 0.012, 3), 0, 1).astype(np.float32)
        ang = float(rng.uniform(0, np.pi))
        bend = float(rng.uniform(-0.35, 0.35))
        length = float(rng.uniform(6, 16))
        rx = float(rng.uniform(7, 14))
        ry = rx * float(rng.uniform(0.55, 0.9))
        alpha = float(rng.uniform(0.78, 1.0))
        x = float(rng.uniform(0, size))
        y = float(rng.uniform(0, size))
        steps = int(rng.integers(2, 4))
        for s in range(steps):
            t = s / max(1, steps - 1) - 0.5
            wobble = bend * t
            stamp_ellipse(
                img,
                x + np.cos(ang) * t * length - np.sin(ang) * wobble * length,
                y + np.sin(ang) * t * length + np.cos(ang) * wobble * length,
                rx * float(rng.uniform(0.92, 1.08)),
                ry * float(rng.uniform(0.92, 1.08)),
                ang + wobble,
                color,
                alpha,
            )
    return np.clip(img, 0, 1)


def force_seamless(img: np.ndarray, band: int = 96) -> None:
    size = img.shape[0]
    img[:, 0] = img[:, -1] = 0.5 * (img[:, 0] + img[:, -1])
    img[0, :] = img[-1, :] = 0.5 * (img[0, :] + img[-1, :])
    for i in range(1, band):
        t = i / band
        alpha = 0.55 * (1 - t) * (1 - t)
        a = img[:, i]
        b = img[:, size - 1 - i]
        avg = 0.5 * (a + b)
        img[:, i] = a * (1 - alpha) + avg * alpha
        img[:, size - 1 - i] = b * (1 - alpha) + avg * alpha
        a = img[i, :]
        b = img[size - 1 - i, :]
        avg = 0.5 * (a + b)
        img[i, :] = a * (1 - alpha) + avg * alpha
        img[size - 1 - i, :] = b * (1 - alpha) + avg * alpha
    # Corners must agree after both passes.
    corner = 0.25 * (img[0, 0] + img[0, -1] + img[-1, 0] + img[-1, -1])
    img[0, 0] = img[0, -1] = img[-1, 0] = img[-1, -1] = corner


def save(name: str, img: np.ndarray) -> None:
    force_seamless(img)
    rgb = (np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)
    Image.fromarray(rgb, mode="RGB").save(ROOT / name)
    lr = np.abs(rgb[:, 0].astype(np.int16) - rgb[:, -1].astype(np.int16)).mean()
    tb = np.abs(rgb[0].astype(np.int16) - rgb[-1].astype(np.int16)).mean()
    print(name, "mean", rgb.mean(axis=(0, 1)).round(1), "seam", round(float(lr), 3), round(float(tb), 3))


def main() -> None:
    # 基础配色尝试.xlsx → 季节·夏
    # 叶绿顶 #529E29 / 叶绿底 #2E661A；泥 #A3855C
    forest = np.array(
        [
            [0.18, 0.40, 0.10],  # #2E661A
            [0.25, 0.51, 0.13],
            [0.32, 0.62, 0.16],  # #529E29
            [0.18, 0.40, 0.10],
            [0.32, 0.62, 0.16],
        ],
        dtype=np.float32,
    )
    built = np.array(
        [
            [0.64, 0.52, 0.36],  # #A3855C
            [0.48, 0.36, 0.24],
            [0.76, 0.64, 0.48],
            [0.58, 0.46, 0.30],
            [0.70, 0.58, 0.42],
        ],
        dtype=np.float32,
    )
    save("SplatForest.png", paint(SIZE, forest, seed=17, strokes=5200))
    save("SplatBuilt.png", paint(SIZE, built, seed=29, strokes=4800))


if __name__ == "__main__":
    main()
