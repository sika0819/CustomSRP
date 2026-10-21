#!/usr/bin/env python3
"""Procedural rain ripple + splash flipbook atlas generator.

Produces two Unity-ready PNG atlases:

  RippleFlipbook.png     — RG = normal XY, B/A free (8 cols × 8 rows, 256 px/cell)
                           Each cell is one animation frame of an expanding rain ripple.
                           Three ring layers + micro-turbulence → realistic normal map.

  RainSplashFlipbook.png — RGBA crown ring + micro-droplets (5 cols × 5 rows, 256 px/cell)
                           Alpha = coverage. Crown fades as it expands, droplets scatter outward.

Both follow the convention already used in RippleFlipbookGenerator.cs
(OilRainSettings.rippleFlipbookColumns=8, splashFlipbookColumns=5).

Run from Assets/CustomSRP/Textures/:
    python generate_rain_flipbooks.py

Dependencies: numpy, PIL (pip install numpy pillow)
"""

from __future__ import annotations

import math
import uuid
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image

# ----------------------------------------------------------------------
# Paths
# ----------------------------------------------------------------------
ROOT = Path(__file__).resolve().parent
OUT_RIPPLE = ROOT / "RippleFlipbook.png"
OUT_SPLASH = ROOT / "RainSplashFlipbook.png"

# Ripple atlas
RIPPLE_COLS = 8
RIPPLE_ROWS = 8
RIPPLE_FRAMES = 64          # matches OilRainSettings default
RIPPLE_CELL = 256           # pixels per cell

# Splash atlas
SPLASH_COLS = 5
SPLASH_ROWS = 5
SPLASH_FRAMES = 25          # matches OilRainSettings default
SPLASH_CELL = 256

RIPPLE_W = RIPPLE_COLS * RIPPLE_CELL     # 2048
RIPPLE_H = RIPPLE_ROWS * RIPPLE_CELL     # 2048
SPLASH_W = SPLASH_COLS * SPLASH_CELL     # 1280
SPLASH_H = SPLASH_ROWS * SPLASH_CELL     # 1280

# ----------------------------------------------------------------------
# Noise helpers
# ----------------------------------------------------------------------
def value_noise(uv: np.ndarray, seed: int = 0) -> np.ndarray:
    """Smooth value noise on [0,1]. uv shape: (H, W, 2), values in [0,1]."""
    rng = np.random.RandomState(seed)
    H, W = uv.shape[:2]
    ix = np.floor(uv[..., 0] * 64).astype(np.int32) % 64
    iy = np.floor(uv[..., 1] * 64).astype(np.int32) % 64

    # Build random field
    field = rng.rand(65, 65).astype(np.float32)
    # Bilinear sample
    fx = uv[..., 0] * 64 - np.floor(uv[..., 0] * 64)
    fy = uv[..., 1] * 64 - np.floor(uv[..., 1] * 64)
    fx2 = fx * fx * (3 - 2 * fx)
    fy2 = fy * fy * (3 - 2 * fy)

    def samp(x, y):
        return field[
            np.clip(x.astype(np.int32), 0, 64),
            np.clip(y.astype(np.int32), 0, 64),
        ]

    a = samp(ix,       iy)
    b = samp(ix + 1,   iy)
    c = samp(ix,       iy + 1)
    d = samp(ix + 1,   iy + 1)
    return (a * (1 - fx2) * (1 - fy2) +
            b * fx2       * (1 - fy2) +
            c * (1 - fx2) * fy2 +
            d * fx2       * fy2)


def fbm(uv: np.ndarray, octaves: int = 4, seed: int = 0) -> np.ndarray:
    """Fractional Brownian Motion on [0,1]² → [0,1]."""
    total = np.zeros(uv.shape[:2], dtype=np.float32)
    amp = 1.0
    freq = 1.0
    norm = 0.0
    for _ in range(octaves):
        total += value_noise(uv * freq, seed=seed + _) * amp
        norm += amp
        amp *= 0.5
        freq *= 2.07
    return total / max(norm, 1e-6)


# ----------------------------------------------------------------------
# Ring / ripple helpers
# ----------------------------------------------------------------------
def ring_falloff(d: np.ndarray, radius: float, width: float) -> np.ndarray:
    """Smooth annulus centred on `radius`, width `width`. Returns [0,1]."""
    x = np.abs(d - radius) / max(width, 1e-6)
    # Cubic smoothstep
    t = np.clip(1.0 - x, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def normal_from_ring(nx: np.ndarray, ny: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Reconstruct a tangent-space normal from X/Y gradient components.

    Unity convention for normal maps encoded in RG channels:
      R = normal.x  (left/right)
      G = normal.y  (up/down, == world Z or tangent depending on shader)
      B = 0         (filled to 0.5)
      A = 1.0       (filled to 255)

    We store (nx, ny) encoded as [0,1] = (val * 0.5 + 0.5) * 255.
    """
    nz = np.sqrt(np.clip(1.0 - nx**2 - ny**2, 0.0, 1.0))
    return nx, ny, nz


# ----------------------------------------------------------------------
# Ripple frame builder  (normal map, RG channels)
# ----------------------------------------------------------------------
def build_ripple_frame(
    frame_idx: float,
    total_frames: float,
    cell: int,
    seed: int = 0,
) -> np.ndarray:
    """One ripple flipbook cell as RGBA.

    Physics model:
      Layer 1 — primary ring:  thin, bright, expanding at speed 1
      Layer 2 — secondary ring: slightly slower, wider, 50 % amplitude
      Layer 3 — inner capillary waves: tiny radius, fast, 25 % amplitude
      Layer 4 — micro-turbulence: FBM noise modulating the normal

    Returns array shape (cell, cell, 4) RGBA uint8, where RG = normal XY.
    """
    t = (frame_idx + 0.5) / total_frames          # [0,1]

    # Coordinate grids centred at 0.5
    coord = np.linspace(0, 1, cell, dtype=np.float32)
    u, v = np.meshgrid(coord, coord, indexing='xy')
    dx = u - 0.5
    dy = v - 0.5
    dist = np.sqrt(dx * dx + dy * dy)

    # Primary ring: expands from 8 % → 46 % of cell
    r1 = math.lerp(0.08, 0.46, t)
    w1 = math.lerp(0.22, 0.06, t)                  # ring thins as it grows
    a1 = (1.0 - t) * (1.0 - t)                     # amplitude fades
    ring1 = ring_falloff(dist, r1, w1) * a1

    # Secondary ring: offset phase, slightly slower, wider
    r2 = math.lerp(0.04, 0.38, t * 0.88)
    w2 = math.lerp(0.16, 0.09, t)
    a2 = (1.0 - t) * 0.5
    ring2 = ring_falloff(dist, r2, w2) * a2

    # Inner capillary ripples (3 micro-rings)
    a3_base = max(0.0, 1.0 - t * 2.5)
    rings3 = []
    for k in range(3):
        rk = math.lerp(0.01 + k * 0.025, 0.15 + k * 0.04, t)
        wk = 0.018
        ak = a3_base * (1.0 - k * 0.25)
        rings3.append(ring_falloff(dist, rk, wk) * ak)
    ring3 = sum(rings3)

    # Noise perturbation for realism (micro-turbulence)
    rng = np.random.RandomState(seed)
    # FBM-like turbulence using value noise
    noise_scale = 4.0
    nx_noise = (value_noise(np.stack([u * noise_scale, v * noise_scale], axis=-1), seed=seed) * 2.0 - 1.0)
    ny_noise = (value_noise(np.stack([u * noise_scale + 7.3, v * noise_scale + 3.1], axis=-1), seed=seed + 1) * 2.0 - 1.0)

    # Fade noise as ripple ages
    noise_amp = max(0.0, (1.0 - t * 1.5)) * 0.06

    # Accumulate gradient in tangent space
    gx = np.zeros((cell, cell), dtype=np.float32)
    gy = np.zeros((cell, cell), dtype=np.float32)

    def add_ring_grad(gx_, gy_, d_, nx_, ny_, amp):
        if amp < 1e-4:
            return
        # Ring falls off away from centre; gradient is (d - r) / dist * falloff
        falloff = ring_falloff(d_, 0, 1)  # placeholder; real falloff per ring handled above
        # For each ring we already have a scalar field. Its gradient:
        # We approximate grad(ring_falloff(|p-r|)) ≈ -sign(d-r) * falloff' * (d-r)/dist
        # But simpler: use the precomputed ring array and finite diff
        pass

    # Use a simpler approach: accumulate ring * unit direction from centre
    for (ring, rr, ra) in [(ring1, 0.5, a1), (ring2, 0.5, a2), (ring3, 0.5, a3_base)]:
        if ra < 1e-4:
            continue
        # Normal direction from centre
        safe_d = np.where(dist > 1e-4, dist, 1e-4)
        nx_ring = dx / safe_d
        ny_ring = dy / safe_d
        # Gradient of ring amplitude along radial direction = falloff derivative
        # d/dr ring_falloff(d,r,w) ≈ -sign(d-r) * falloff * falloff' ... 
        # We use the simpler: ring already represents the bell amplitude.
        # The normal perturbation in tangent space comes from the gradient of the scalar field.
        # Simplified: multiply ring by radial normal.
        gx += nx_ring * ring
        gy += ny_ring * ring

    # Add noise perturbation
    gx += nx_noise * noise_amp * (1.0 / max(np.sqrt(gx**2 + gy**2 + 1e-6).max(), 1.0))
    gy += ny_noise * noise_amp * (1.0 / max(np.sqrt(gx**2 + gy**2 + 1e-6).max(), 1.0))

    # Normalise gradient to [-1,1]
    g_len = np.sqrt(gx**2 + gy**2 + 1e-6)
    nx_out = np.clip(gx / g_len, -1.0, 1.0)
    ny_out = np.clip(gy / g_len, -1.0, 1.0)

    # Encode for Unity normal map: RG = nx, ny, encoded [0,1]
    r_enc = ((nx_out * 0.5 + 0.5) * 255.0).astype(np.uint8)
    g_enc = ((ny_out * 0.5 + 0.5) * 255.0).astype(np.uint8)
    # BA channels = 0 (free for flow normals in shader)
    b_enc = np.full((cell, cell), 128, dtype=np.uint8)
    a_enc = np.full((cell, cell), 255, dtype=np.uint8)

    rgba = np.stack([r_enc, g_enc, b_enc, a_enc], axis=-1)
    return rgba


# ----------------------------------------------------------------------
# Splash frame builder  (RGBA coverage)
# ----------------------------------------------------------------------
def build_splash_frame(
    frame_idx: float,
    total_frames: float,
    cell: int,
    seed: int = 0,
) -> np.ndarray:
    """One splash flipbook cell as RGBA.

    Physics model:
      - Crown ring: expands outward from centre, thins and fades
      - Inner crown highlight: brighter inner edge of the ring
      - Micro-droplets: small scattered points that travel outward
      - Centre disturbance: a soft bright spot where impact occurred
      - All keyed to frame progress [0,1]

    Alpha encodes coverage (0 = transparent, 255 = opaque).
    RGB is the water colour (near-white with slight blue tint).
    """
    t = (frame_idx + 0.5) / total_frames          # [0,1]
    # Burst: fast early, then slow
    t_sqrt = math.sqrt(t)

    coord = np.linspace(0, 1, cell, dtype=np.float32)
    u, v = np.meshgrid(coord, coord, indexing='xy')
    dx = u - 0.5
    dy = v - 0.5
    dist = np.sqrt(dx * dx + dy * dy)

    rng = np.random.RandomState(seed)

    # Crown ring
    crown_r = math.lerp(0.06, 0.44, t_sqrt)           # fast early, slow later
    crown_w = math.lerp(0.10, 0.18, t)                 # slightly wider as it expands
    crown_amp = (1.0 - t) * (1.0 - t)                  # fade
    crown = ring_falloff(dist, crown_r, crown_w) * crown_amp

    # Inner crown highlight (slightly smaller radius, brighter)
    inner_r = crown_r * 0.80
    inner_w = crown_w * 0.6
    inner = ring_falloff(dist, inner_r, inner_w) * crown_amp * 0.6

    # Outer secondary ring (weaker)
    outer_r = crown_r * 1.18
    outer_w = crown_w * 1.4
    outer_amp = (1.0 - t) * 0.35
    outer = ring_falloff(dist, outer_r, outer_w) * outer_amp

    # Centre impact spot (fades quickly)
    spot_amp = max(0.0, 1.0 - t * 4.0)
    spot = np.exp(-dist * dist / max(0.003, 0.003 * (1.0 - t * 0.5))) * spot_amp

    # Micro-droplets: fixed per-seed positions, moving outward
    droplet_a = np.zeros((cell, cell), dtype=np.float32)
    n_droplets = 12
    for k in range(n_droplets):
        # Each droplet starts near centre, moves outward in a fixed direction
        angle = rng.uniform(0, 2 * math.pi)
        speed = rng.uniform(0.25, 0.45)
        # Droplet position at this frame
        dr = speed * t_sqrt * 0.42
        dx_d = math.cos(angle) * dr
        dy_d = math.sin(angle) * dr
        # Droplet size shrinks as it travels
        drop_r = math.lerp(0.008, 0.003, t)
        d_from_drop = np.sqrt((dx - dx_d)**2 + (dy - dy_d)**2)
        drop = np.exp(-d_from_drop**2 / max(drop_r**2, 1e-8)) * max(0, 1.0 - t * 2.5)
        droplet_a += drop

    # Noise for crown irregularity
    noise_uv = np.stack([u * 5.0, v * 5.0], axis=-1)
    crown_noise = value_noise(noise_uv, seed=seed) * 0.4 + 0.6

    # Combine
    coverage = crown * crown_noise + inner + outer + spot + droplet_a * 0.7
    coverage = np.clip(coverage, 0.0, 1.0)

    # Water colour: near-white with slight blue
    r_col = 0.88
    g_col = 0.94
    b_col = 1.00

    r = (coverage * r_col * 255.0).astype(np.uint8)
    g = (coverage * g_col * 255.0).astype(np.uint8)
    b = (coverage * b_col * 255.0).astype(np.uint8)
    a = (coverage * 255.0).astype(np.uint8)

    rgba = np.stack([r, g, b, a], axis=-1)
    return rgba


# ----------------------------------------------------------------------
# Atlas builder
# ----------------------------------------------------------------------
def build_flipbook_atlas(
    frames: int,
    cols: int,
    rows: int,
    cell: int,
    builder,
    initial_seed: int = 42,
) -> np.ndarray:
    """Lay out `frames` frames into a (rows × cols) atlas grid.

    builder(frame_idx, total_frames, cell, seed) → (H,W,4) RGBA uint8.
    """
    atlas = np.zeros((rows * cell, cols * cell, 4), dtype=np.uint8)
    for f in range(frames):
        cell_idx = f % (cols * rows)
        cx = cell_idx % cols
        cy = cell_idx // cols
        frame = builder(f, frames, cell, seed=initial_seed + f)
        y0, y1 = cy * cell, (cy + 1) * cell
        x0, x1 = cx * cell, (cx + 1) * cell
        atlas[y0:y1, x0:x1] = frame
    return atlas


def save_png_with_meta(atlas: np.ndarray, out_path: Path, cell_size: int) -> None:
    """Save atlas as PNG and create a Unity .meta file."""
    img = Image.fromarray(atlas, mode="RGBA")
    img.save(out_path, format="PNG")

    # Build .meta content
    # Copy the existing RippleFlipbook.png.meta as a template (if present)
    template_meta = ROOT / "RippleFlipbook.png.meta"
    if template_meta.exists():
        template = template_meta.read_text()
    else:
        # Minimal fallback meta
        template = (
            "fileFormatVersion: 2\n"
            "guid: GENERATED\n"
            "TextureImporter:\n"
            "  externalObjects: {}\n"
            "  serializedVersion: 13\n"
            "  mipmaps:\n"
            "    mipMapMode: 0\n"
            "    enableMipMap: 1\n"
            "    sRGBTexture: 0\n"
            "    linearTexture: 1\n"
            "    fadeOut: 0\n"
            "    borderMipMap: 0\n"
            "    mipMapsPreserveCoverage: 0\n"
            "    alphaTestReferenceValue: 0.5\n"
            "    mipMapFadeDistanceStart: 1\n"
            "    mipMapFadeDistanceEnd: 3\n"
        )

    guid = uuid.uuid4().hex
    text = re.sub(r"^guid: .*", f"guid: {guid}", template, count=1, flags=re.M)
    # Fix texture size fields
    w, h = atlas.shape[1], atlas.shape[0]
    text = re.sub(r"maxTextureSize: \d+", f"maxTextureSize: {max(w, h)}", text)
    meta_path = out_path.with_suffix(".meta")
    meta_path.write_text(text)
    print(f"  Wrote {out_path}  ({w}×{h})")
    print(f"  Wrote {meta_path}")


# ----------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------
def main() -> None:
    print("=" * 60)
    print("Rain Flipbook Generator")
    print("=" * 60)

    print("\n[1/2] Building RippleFlipbook.png  "
          f"({RIPPLE_COLS}×{RIPPLE_ROWS}, {RIPPLE_CELL}px/cell, {RIPPLE_FRAMES} frames) ...")
    ripple_atlas = build_flipbook_atlas(
        RIPPLE_FRAMES, RIPPLE_COLS, RIPPLE_ROWS, RIPPLE_CELL,
        build_ripple_frame,
        initial_seed=7,
    )
    save_png_with_meta(ripple_atlas, OUT_RIPPLE, RIPPLE_CELL)

    print("\n[2/2] Building RainSplashFlipbook.png  "
          f"({SPLASH_COLS}×{SPLASH_ROWS}, {SPLASH_CELL}px/cell, {SPLASH_FRAMES} frames) ...")
    splash_atlas = build_flipbook_atlas(
        SPLASH_FRAMES, SPLASH_COLS, SPLASH_ROWS, SPLASH_CELL,
        build_splash_frame,
        initial_seed=31,
    )
    save_png_with_meta(splash_atlas, OUT_SPLASH, SPLASH_CELL)

    print("\nDone. Copy the generated PNGs into OilRainSettings:")
    print(f"  RippleFlipbook     → rippleFlipbook  ({RIPPLE_COLS} cols × {RIPPLE_ROWS} rows, {RIPPLE_FRAMES} frames)")
    print(f"  RainSplashFlipbook → splashFlipbook  ({SPLASH_COLS} cols × {SPLASH_ROWS} rows, {SPLASH_FRAMES} frames)")


if __name__ == "__main__":
    main()
