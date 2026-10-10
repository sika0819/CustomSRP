#!/usr/bin/env python3
"""Seamless oil-paint terrain albedo.

Short curved strokes with a lit ridge along the upper edge, the same mark
as OilSplatGrass / OilSplatSand / OilSplatRock. Samples wrap, so the tile
is periodic. Do not average opposite edges.

Colors: 基础配色尝试.xlsx, sheet 基础配色尝试.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent / "Textures"
SIZE = 1024


def hx(text: str) -> np.ndarray:
    text = text.lstrip("#")
    return np.array([int(text[i : i + 2], 16) for i in (0, 2, 4)], np.float32) / 255.0


def mix(a: np.ndarray, b: np.ndarray, t: float) -> np.ndarray:
    return (a * (1.0 - t) + b * t).astype(np.float32)


SAND = hx("B9A07C")
MUD = hx("9B8F6C")
SHADE = hx("807E5E")
GRASS_LIT = hx("A7B86F")
GRASS_MID = hx("839E5E")
GRASS_DARK = hx("60804F")
ROCK_LIT = hx("C4C3C1")
ROCK_MID = hx("94A1AE")
ROCK_DARK = hx("7692AD")

PALETTES = {
    "sand": np.stack([mix(SHADE, MUD, 0.4), MUD, SAND, mix(SAND, hx("FBF3E9"), 0.32)]),
    "dirt": np.stack([SHADE, mix(SHADE, MUD, 0.5), MUD, mix(MUD, SAND, 0.45)]),
    "grass": np.stack([GRASS_DARK, mix(GRASS_DARK, GRASS_MID, 0.4), GRASS_MID, GRASS_LIT]),
    "rock": np.stack([ROCK_DARK, mix(ROCK_DARK, ROCK_MID, 0.45), ROCK_MID, mix(ROCK_MID, ROCK_LIT, 0.65)]),
}


def paint_stroke(
    img: np.ndarray,
    x: float,
    y: float,
    ang: float,
    length: float,
    width: float,
    bend: float,
    color: np.ndarray,
) -> None:
    size = img.shape[0]
    n = max(10, int(round(length * 2.0)))
    ts = np.linspace(0.0, 1.0, n, dtype=np.float32)
    angs = ang + bend * (ts - 0.42)
    step = length / (n - 1)
    ca = np.cos(angs)
    sa = np.sin(angs)
    posx = np.empty(n, np.float32)
    posy = np.empty(n, np.float32)
    posx[0] = x
    posy[0] = y
    posx[1:] = x + np.cumsum(ca[:-1] * step)
    posy[1:] = y + np.cumsum(sa[:-1] * step)

    belly = 0.7 + 0.3 * np.sin(np.pi * np.clip(ts, 0.0, 1.0))
    half = (width * 0.5) * belly
    n_across = max(6, int(round(width)) + 2)
    across = np.linspace(-1.0, 1.0, n_across, dtype=np.float32)
    px = posx[:, None] + (-sa)[:, None] * (across[None, :] * half[:, None])
    py = posy[:, None] + ca[:, None] * (across[None, :] * half[:, None])

    goes_up = ca[:, None] < 0.0
    lit = (np.abs(across) > 0.72)[None, :] & ((across[None, :] > 0.0) == goes_up)
    shade = np.full((n, n_across), 0.97, np.float32)
    shade[lit] = 1.22
    sine = np.maximum(np.sin(np.pi * np.clip(ts, 0.0, 1.0)), 0.0)
    taper = np.power(sine, 0.4)
    alpha = np.clip(taper * 0.98, 0.0, 1.0)[:, None]
    bristle = 0.96 + 0.04 * np.sin(across[None, :] * np.pi * 5.0 + angs[:, None] * 3.0)
    shade *= bristle
    painted = np.clip(color * shade[..., None], 0.0, 1.0)
    deposit(img, px, py, painted, alpha)


def deposit(img: np.ndarray, px: np.ndarray, py: np.ndarray, painted: np.ndarray, alpha: np.ndarray) -> None:
    """Bilinear write so stroke edges are not stair-stepped. Coordinates wrap."""
    size = img.shape[0]
    x0 = np.floor(px).astype(np.int32)
    y0 = np.floor(py).astype(np.int32)
    fx = px - x0
    fy = py - y0
    for oy, yf in ((0, 1.0 - fy), (1, fy)):
        for ox, xf in ((0, 1.0 - fx), (1, fx)):
            w = (alpha * yf * xf)[..., None]
            xi = (x0 + ox) % size
            yi = (y0 + oy) % size
            img[yi, xi] = img[yi, xi] * (1.0 - w) + painted * w


def paint(size: int, palette: np.ndarray, seed: int, strokes: int, length, width, bend) -> np.ndarray:
    rng = np.random.default_rng(seed)
    img = np.empty((size, size, 3), np.float32)
    img[:] = palette[len(palette) // 2]
    for _ in range(strokes):
        color = palette[int(rng.integers(0, palette.shape[0]))]
        color = np.clip(color * float(rng.uniform(0.97, 1.04)), 0, 1)
        paint_stroke(
            img,
            float(rng.uniform(0, size)),
            float(rng.uniform(0, size)),
            float(rng.uniform(-np.pi, np.pi)),
            float(rng.uniform(*length)),
            float(rng.uniform(*width)),
            float(rng.uniform(*bend)) * float(rng.choice([-1.0, 1.0])),
            color,
        )
    return np.clip(img, 0, 1)


def wrap_ratio(img: np.ndarray) -> tuple[float, float]:
    step_h = np.abs(img[:, 1:] - img[:, :-1]).mean()
    step_v = np.abs(img[1:] - img[:-1]).mean()
    wrap_h = np.abs(img[:, 0] - img[:, -1]).mean()
    wrap_v = np.abs(img[0] - img[-1]).mean()
    return float(wrap_h / (step_h + 1e-8)), float(wrap_v / (step_v + 1e-8))


def save_rgb(path: Path, img: np.ndarray) -> None:
    rgb = (np.clip(img, 0, 1) * 255.0 + 0.5).astype(np.uint8)
    Image.fromarray(rgb, mode="RGB").save(path)
    horizontal, vertical = wrap_ratio(rgb.astype(np.float32))
    grad = float(np.abs(rgb[:, 1:].astype(np.float32) - rgb[:, :-1].astype(np.float32)).mean())
    print(
        path.name,
        rgb.shape[1],
        "wrap",
        round(horizontal, 2),
        round(vertical, 2),
        "grad",
        round(grad, 2),
    )
    if min(horizontal, vertical) < 0.75 or max(horizontal, vertical) > 1.35:
        raise SystemExit(f"{path.name} seam check failed")


def main() -> None:
    save_rgb(ROOT / "Oil_Sand.png", paint(SIZE, PALETTES["sand"], 11, 9000, (16, 36), (7, 14), (0.15, 0.7)))
    grass = paint(SIZE, PALETTES["grass"], 23, 9800, (18, 40), (8, 15), (0.2, 0.85))
    save_rgb(ROOT / "Oil_Grass.png", grass)
    save_rgb(ROOT / "Oil_Dirt.png", paint(SIZE, PALETTES["dirt"], 37, 9200, (16, 38), (7, 14), (0.15, 0.75)))
    save_rgb(ROOT / "Oil_Rock.png", paint(SIZE, PALETTES["rock"], 41, 7600, (16, 34), (10, 18), (0.1, 0.45)))
    rng = np.random.default_rng(53)
    for _ in range(700):
        paint_stroke(
            grass,
            float(rng.uniform(0, SIZE)),
            float(rng.uniform(0, SIZE)),
            float(rng.uniform(-np.pi, np.pi)),
            float(rng.uniform(12, 28)),
            float(rng.uniform(3.5, 6.5)),
            float(rng.uniform(0.1, 0.5)) * float(rng.choice([-1.0, 1.0])),
            PALETTES["dirt"][int(rng.integers(0, 3))],
        )
    save_rgb(ROOT / "Oil_GrassDebris.png", grass)


if __name__ == "__main__":
    main()
