#!/usr/bin/env python3
"""Generate OilRain splash flipbook atlas.

Output : RainSplashFlipbook.png  (RGBA32, clamp wrap, bilateral filtered)
Layout : C columns × R rows of square cells, each CELL_PX pixels wide.
         Frame f → cell (f % C, f // C)  (top-left = frame 0).

A raindrop splash on water has three visual components baked across 5 frames:

  Frame 0 — initial impact flash
            A tight bright disc of spray + central column burst.
            Crown barely visible yet.

  Frame 1 — crown begins to form
            Central column collapses outward; a thin expanding crown
            ring is just becoming visible at ~0.35r.
            A few micro-droplets fly upward/outward.

  Frame 2 — crown fully formed, peak
            The classic "Crown" shape: a torus of water expanding.
            Several secondary droplets launch from the crown rim.
            Alpha peaks here.

  Frame 3 — crown breaks, droplets scatter
            Crown starts to break into droplets.
            The original ring fragments; separate micro-splats appear.
            Alpha begins to fall.

  Frame 4 — fade
            All elements are dissipating.
            Very soft, nearly transparent fill.
            Alpha near zero.

Each frame has:
  • A primary crown ring (bright, thin band)
  • Upward/outward micro-droplets (random positions, sizes)
  • A subtle central impact glow
  • Radial micro-noise to avoid a perfect circle

Alpha encodes coverage so the shader can clip(a - 0.004).
RGB is a sky-tint suggestion (light blue-white) so the atlas looks
reasonable even in a texture previewer.
"""

from __future__ import annotations

import math
import random
from typing import List, Tuple

import numpy as np
from PIL import Image

# ---------------------------------------------------------------------------
# Tunables
# ---------------------------------------------------------------------------
CELL_PX   = 256          # pixels per cell
COLUMNS   = 5
ROWS      = 1
FRAMES    = COLUMNS * ROWS   # 5 frames
OUTPUT    = "RainSplashFlipbook.png"

# Physics per frame [t 0..1]
#   crown_radius : where the main crown ring sits (cell fraction)
#   crown_width  : thickness of the crown band (cell fraction)
#   crown_amp    : alpha peak of the crown (0..1)
#   droplets     : how many micro-droplets visible
#   central_glow : size of the central impact flash (0..0.5)
FRAME_PARAMS = [
    #  f0        f1        f2        f3        f4
    dict(r=0.00,  w=0.08,  amp=0.00,  ndrop=0,  glow=0.20),   # impact flash
    dict(r=0.32,  w=0.09,  amp=0.55,  ndrop=3,  glow=0.15),   # crown forming
    dict(r=0.55,  w=0.11,  amp=0.95,  ndrop=8,  glow=0.06),   # crown peak
    dict(r=0.70,  w=0.13,  amp=0.55,  ndrop=12, glow=0.00),   # crown breaking
    dict(r=0.82,  w=0.18,  amp=0.20,  ndrop=6,  glow=0.00),   # fade
]

# Fixed random seed for reproducible droplets within a frame
DROPLET_SEED = 42


def _vnoise2d(u: np.ndarray, v: np.ndarray, scale: float) -> np.ndarray:
    """2D value noise on float grids (returns [-1, 1])."""
    ix = np.floor(u * scale).astype(int)
    iy = np.floor(v * scale).astype(int)
    fx = u * scale - ix
    fy = v * scale - iy
    fx2 = fx * fx * (3.0 - 2.0 * fx)
    fy2 = fy * fy * (3.0 - 2.0 * fy)

    def _hash(x, y, s):
        return np.mod(x * 127.1 + y * 311.7 + s, 1.0)

    v00 = _hash(ix,     iy,     0.0)
    v10 = _hash(ix + 1, iy,     0.0)
    v01 = _hash(ix,     iy + 1, 0.0)
    v11 = _hash(ix + 1, iy + 1, 0.0)

    v0 = v00 * (1.0 - fx2) + v10 * fx2
    v1 = v01 * (1.0 - fx2) + v11 * fx2
    return (v0 * (1.0 - fy2) + v1 * fy2) * 2.0 - 1.0


def _droplet_positions(
    frame: int, n: int, r_max: float, seed: int
) -> List[Tuple[float, float, float, float]]:
    """Return list of (u, v, radius, alpha) for micro-droplets.

    Droplets fly outward from the impact point.
    They appear at the crown rim on early frames, scatter further later.
    """
    rng = random.Random(seed + frame * 1000)
    droplets = []
    for i in range(n):
        angle = rng.uniform(0.0, 2.0 * math.pi)
        # Droplets appear slightly outside the crown ring + scatter
        r = r_max * rng.uniform(0.9, 1.35)
        u = 0.5 + r * math.cos(angle)
        v = 0.5 + r * math.sin(angle)

        # Larger droplets near crown, smaller ones far out
        droplet_r = rng.uniform(0.010, 0.028) * (1.0 - (r / (r_max + 0.01)) * 0.4)
        droplet_a = rng.uniform(0.45, 0.90)

        # Skip if outside cell bounds
        if 0.02 <= u <= 0.98 and 0.02 <= v <= 0.98:
            droplets.append((u, v, droplet_r, droplet_a))
    return droplets


def _draw_droplet(
    cell: np.ndarray, u: float, v: float, radius: float, alpha: float
) -> None:
    """Draw one droplet (soft disc) into the cell alpha channel."""
    h, w = cell.shape[:2]
    px = u * w
    py = v * h
    r_px = radius * w

    # Bounding box
    x0 = max(0, int(px - r_px * 2.5 - 1))
    x1 = min(w, int(px + r_px * 2.5 + 1))
    y0 = max(0, int(py - r_px * 2.5 - 1))
    y1 = min(h, int(py + r_px * 2.5 + 1))
    if x0 >= x1 or y0 >= y1:
        return

    yy, xx = np.ogrid[y0:y1, x0:x1]
    du = (xx.astype(float) - px) / (r_px + 1e-6)
    dv = (yy.astype(float) - py) / (r_px + 1e-6)
    d = np.hypot(du, dv)
    # Soft disc: sharp core, feathered edge
    disc = np.clip(1.0 - d, 0.0, 1.0)
    disc = disc * disc * (3.0 - 2.0 * disc)  # quintic feather
    cell[y0:y1, x0:x1, 3] = np.maximum(cell[y0:y1, x0:x1, 3], disc * alpha)


def generate_frame(f: int) -> np.ndarray:
    """Generate one cell: RGBA32, (CELL_PX, CELL_PX, 4)."""
    h, w = CELL_PX, CELL_PX
    cell = np.zeros((h, w, 4), dtype=np.float32)
    p = FRAME_PARAMS[f]
    t = f / max(FRAMES - 1, 1)

    yy, xx = np.ogrid[:h, :w]
    u = xx.astype(np.float32) / w
    v = yy.astype(np.float32) / h
    dx = u - 0.5
    dy = v - 0.5
    d = np.hypot(dx, dy)

    # ---- Crown ring ----
    r = p["r"]
    w_ring = p["w"]
    crown_amp = p["amp"]

    # Band
    band = np.clip((w_ring - np.abs(d - r)) / w_ring, 0.0, 1.0)
    band = band * band * band * (band * (band * 6.0 - 15.0) + 10.0)

    # Micro-noise on the crown to break circular symmetry
    micro = _vnoise2d(u, v, 8.0 + f * 3.0)
    band = band * (1.0 + micro * 0.18)

    # Crown alpha
    crown_alpha = band * crown_amp

    # ---- Secondary inner ring (only on frames 1-3) ----
    inner_r = r * 0.60 if r > 0.05 else 0.0
    inner_w = w_ring * 0.7
    inner_band = np.clip((inner_w - np.abs(d - inner_r)) / inner_w, 0.0, 1.0)
    inner_band = inner_band * inner_band * (3.0 - 2.0 * inner_band)
    inner_alpha = inner_band * crown_amp * 0.30

    # ---- Central impact glow ----
    glow_r = p["glow"]
    if glow_r > 0:
        glow = np.clip(glow_r - d, 0.0, glow_r) / glow_r
        glow = glow * glow * (3.0 - 2.0 * glow)  # smooth
        glow_alpha = glow * 0.70
    else:
        glow_alpha = np.zeros_like(d)

    # ---- Combine alpha ----
    cell[..., 3] = np.maximum.reduce([
        crown_alpha,
        inner_alpha,
        glow_alpha,
    ])

    # ---- Micro-droplets ----
    droplets = _droplet_positions(f, p["ndrop"], r if r > 0.05 else 0.45, DROPLET_SEED)
    for (du, dv, dr, da) in droplets:
        # Also add some noise-driven jitter so droplets don't feel grid-locked
        jitter_u = du + micro[int(dv * h) % h, int(du * w) % w] * 0.015
        jitter_v = dv + _vnoise2d(u, v, 9.0 + f * 2.0)[int(dv * h) % h, int(du * w) % w] * 0.015
        _draw_droplet(cell, jitter_u, jitter_v, dr, da)

    # ---- Encode RGB ----
    # Slight sky-tint suggestion: R=235, G=242, B=252 (overcast rain light)
    # Fade with alpha so darker areas look wet, not colored.
    sky_r = 235.0 / 255.0
    sky_g = 242.0 / 255.0
    sky_b = 252.0 / 255.0

    # Boost highlight on crown
    highlight = crown_alpha * 0.25
    r_col = np.clip(sky_r + highlight, 0.0, 1.0)
    g_col = np.clip(sky_g + highlight * 0.5, 0.0, 1.0)
    b_col = np.clip(sky_b + highlight * 0.2, 0.0, 1.0)

    cell[..., 0] = r_col * 255.0
    cell[..., 1] = g_col * 255.0
    cell[..., 2] = b_col * 255.0
    # Alpha was accumulated in 0..1 coverage; scale to 0..255 for the u8 cast.
    cell[..., 3] = np.clip(cell[..., 3], 0.0, 1.0) * 255.0

    return np.clip(cell, 0, 255).astype(np.uint8)


def main() -> None:
    atlas_w = CELL_PX * COLUMNS
    atlas_h = CELL_PX * ROWS
    atlas = np.zeros((atlas_h, atlas_w, 4), dtype=np.uint8)

    for f in range(FRAMES):
        cx = f % COLUMNS
        cy = f // COLUMNS
        frame = generate_frame(f)
        px = cx * CELL_PX
        py = cy * CELL_PX
        atlas[py:py + CELL_PX, px:px + CELL_PX] = frame

    img = Image.fromarray(atlas, mode="RGBA")
    img.save(OUTPUT, format="PNG")
    print(f"Wrote {OUTPUT}  ({atlas_w}×{atlas_h} RGBA, {FRAMES} frames, {CELL_PX}px/cell)")


if __name__ == "__main__":
    main()
