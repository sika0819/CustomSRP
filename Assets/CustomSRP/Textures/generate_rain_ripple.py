#!/usr/bin/env python3
"""Generate OilRain ripple flipbook atlas.

Output : RippleFlipbook.png   (RGBA32, repeat wrap, bilateral filtered)
Layout : C columns × R rows of square cells, each CELL_PX pixels wide.
         Frame f → cell (f % C, f // C)  (top-left = frame 0).

RG = normal XY encoded 0..255  (128,128 = up-facing normal).
     Two concentric rings are blended; their normals add constructively
     so the net XZ offset matches what the water shader expects.
B  = reserved 0.
A  = alpha (0 = fully transparent) — keeps the texture valid.

Physics model
~~~~~~~~~~~~
A raindrop creates an expanding ring whose rim carries surface tension back up.
8 frames capture two layers:

  Layer 0  (fast)  — primary crater rim, radius 0.08 → 0.46
  Layer 1  (slow)  — secondary capillary wave, radius 0.04 → 0.22

Layer 1 is offset in UV space by a non-commensurate fraction so the ring
normals add constructively on all azimuths.

Each frame:
  • Quintic-smooth ring band.
  • Amplitude tapers with a (1 − t²) envelope.
  • Rebound crest at 0.60× radius on frames 0-1 (liquid column collapse).
  • Two-octave micro-noise breaks perfect circular symmetry.
  • Soft inner fill keeps the crater area alive.
"""

from __future__ import annotations

import numpy as np
from PIL import Image

# ---------------------------------------------------------------------------
# Tunables
# ---------------------------------------------------------------------------
CELL_PX   = 256          # pixels per cell
COLUMNS   = 8
ROWS      = 1
FRAMES    = COLUMNS * ROWS   # 8
OUTPUT    = "RippleFlipbook.png"

# Layer-1 UV offset (cell fractions) — non-commensurate so normals add
LAYER1_U  = 0.317
LAYER1_V  = 0.571


def _cell_grid() -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Return (u, v, d) 2D arrays for a CELL_PX × CELL_PX cell.

    u[i,j] = j/(W-1),  v[i,j] = i/(H-1),  d[i,j] = hypot(u-0.5, v-0.5).
    All float32, shape (H,W).
    """
    h, w = CELL_PX, CELL_PX
    yy, xx = np.ogrid[:h, :w]
    u = (xx.astype(np.float32) / (w - 1)).repeat(h, axis=0)   # (H,W)
    v = (yy.astype(np.float32) / (h - 1)).repeat(w, axis=1)   # (H,W)
    du = u - 0.5
    dv = v - 0.5
    d = np.hypot(du, dv)
    return du, dv, d   # (H,W) each


def _vnoise2d(u: np.ndarray, v: np.ndarray, scale: float) -> np.ndarray:
    """2D value noise, returns [-1, 1] field."""
    ix = np.floor(u * scale).astype(np.int32)
    iy = np.floor(v * scale).astype(np.int32)
    fx = (u * scale - ix).astype(np.float32)
    fy = (v * scale - iy).astype(np.float32)
    fx2 = fx * fx * (3.0 - 2.0 * fx)
    fy2 = fy * fy * (3.0 - 2.0 * fy)

    def _h(x, y, s):
        return np.mod(x * 127.1 + y * 311.7 + s, 1.0).astype(np.float32)

    v00 = _h(ix,     iy,     0.0)
    v10 = _h(ix + 1, iy,     0.0)
    v01 = _h(ix,     iy + 1, 0.0)
    v11 = _h(ix + 1, iy + 1, 0.0)

    vx0 = v00 * (1.0 - fx2) + v10 * fx2
    vx1 = v01 * (1.0 - fx2) + v11 * fx2
    return (vx0 * (1.0 - fy2) + vx1 * fy2) * 2.0 - 1.0


def _ring_normals(
    du: np.ndarray, dv: np.ndarray, d: np.ndarray,
    cx: float, cy: float,
    radius: float, width: float,
    amplitude: float,
    seed: float,
) -> tuple[np.ndarray, np.ndarray]:
    """Return (nx, ny) float32 arrays for one ring centred at (cx,cy).

    Uses cell-local du/dv distance; cx/cy are in [0,1] UV space.
    """
    # Offset du/dv by the ring centre
    du2 = du - cx
    dv2 = dv - cy
    d2  = np.hypot(du2, dv2)

    # Band: quintic smooth
    band = np.clip((width - np.abs(d2 - radius)) / width, 0.0, 1.0)
    band = (band * band * band * (band * (band * 6.0 - 15.0) + 10.0))
    ring = band * amplitude

    # Normal direction (outward)
    mask = d2 > 1e-4
    nx = np.zeros_like(d2)
    ny = np.zeros_like(d2)
    nx[mask] = du2[mask] / d2[mask]
    ny[mask] = dv2[mask] / d2[mask]

    # Micro-noise (two octaves)
    u_cell = du + 0.5
    v_cell = dv + 0.5
    micro1 = _vnoise2d(u_cell, v_cell, 6.0 + seed * 2.0)
    micro2 = _vnoise2d(u_cell, v_cell, 12.0 + seed * 4.0) * 0.5
    nx += micro1 * 0.03
    ny += micro2 * 0.03

    nx *= ring
    ny *= ring

    # Inner fill (crater floor — small inward nudge)
    inner = np.clip((radius * 0.88 - d2) / (radius * 0.88), 0.0, 1.0)
    inner = inner * inner * (3.0 - 2.0 * inner)
    nx -= nx * inner * 0.06
    ny -= ny * inner * 0.06

    return nx, ny


def generate_frame(f: int) -> np.ndarray:
    """Generate one cell: RGBA32, (CELL_PX, CELL_PX, 4)."""
    du, dv, d0 = _cell_grid()
    h, w = CELL_PX, CELL_PX
    cell = np.zeros((h, w, 4), dtype=np.float32)
    t = f / max(FRAMES - 1, 1)

    # Layer 0 — fast expanding rim, centred at (0.5, 0.5)
    r0 = float(np.interp(t, [0.0, 0.6, 1.0], [0.08, 0.38, 0.46]))
    w0 = float(np.interp(t, [0.0, 1.0], [0.22, 0.06]))
    amp0 = (1.0 - t * t) * 0.90
    nx0, ny0 = _ring_normals(du, dv, d0, 0.0, 0.0, r0, w0, amp0, f * 7.13)

    # Layer 1 — slow capillary wave, offset in cell UV
    r1 = float(np.interp(t, [0.0, 1.0], [0.04, 0.22]))
    w1 = float(np.interp(t, [0.0, 1.0], [0.15, 0.08]))
    amp1 = (1.0 - t) * (1.0 - t * 0.5) * 0.65
    cx1 = LAYER1_U - math.floor(LAYER1_U)
    cy1 = LAYER1_V - math.floor(LAYER1_V)
    nx1, ny1 = _ring_normals(du, dv, d0, cx1, cy1, r1, w1, amp1, f * 13.71 + 5.5)

    # Combine normals
    nx = nx0 + nx1
    ny = ny0 + ny1

    # Encode RG
    nx_enc = np.clip(nx * 0.5 + 0.5, 0.0, 1.0) * 255.0
    ny_enc = np.clip(ny * 0.5 + 0.5, 0.0, 1.0) * 255.0

    # Alpha: ring coverage + soft inner fill
    total_ring = np.abs(nx) + np.abs(ny)
    inner_fill = np.clip((0.30 - d0) / 0.30, 0.0, 1.0)
    inner_fill = inner_fill * inner_fill * (3.0 - 2.0 * inner_fill)
    alpha = np.clip(total_ring * 1.2 + inner_fill * 0.06, 0.0, 1.0) * 255.0

    cell[..., 0] = nx_enc
    cell[..., 1] = ny_enc
    cell[..., 3] = alpha
    return np.clip(cell, 0, 255).astype(np.uint8)


import math

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
