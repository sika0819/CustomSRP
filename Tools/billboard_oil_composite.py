#!/usr/bin/env python3
"""Composite generated oil-paint RGB onto original billboard alpha; kill black fringe."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage


def _nn_fill(rgb: np.ndarray, seed: np.ndarray) -> np.ndarray:
    """Nearest-neighbor spread: every pixel takes color of nearest seed pixel."""
    if not np.any(seed):
        return rgb.copy()
    _, (iy, ix) = ndimage.distance_transform_edt(~seed, return_distances=True, return_indices=True)
    out = rgb.copy()
    out[~seed] = rgb[iy[~seed], ix[~seed]]
    return out


def _erode(mask: np.ndarray, steps: int) -> np.ndarray:
    if steps <= 0:
        return mask.copy()
    struct = ndimage.generate_binary_structure(2, 1)
    return ndimage.binary_erosion(mask, structure=struct, iterations=steps)


def _dilate_mask(mask: np.ndarray, steps: int) -> np.ndarray:
    if steps <= 0:
        return mask.copy()
    struct = ndimage.generate_binary_structure(2, 1)
    return ndimage.binary_dilation(mask, structure=struct, iterations=steps)


def _inward_sample(rgb: np.ndarray, alpha: np.ndarray, edge_px: int, inward: int) -> np.ndarray:
    """Replace silhouette ring with colors sampled deeper inside the opaque region."""
    dist = ndimage.distance_transform_edt(alpha)
    # Gradient of dist points toward deeper interior
    gy, gx = np.gradient(dist.astype(np.float32))
    mag = np.sqrt(gy * gy + gx * gx) + 1e-6
    gy /= mag
    gx /= mag

    h, w = alpha.shape
    yy, xx = np.mgrid[0:h, 0:w]
    # Sample inward by `inward` pixels along gradient
    sy = np.clip(np.rint(yy + gy * inward).astype(np.int32), 0, h - 1)
    sx = np.clip(np.rint(xx + gx * inward).astype(np.int32), 0, w - 1)

    sampled = rgb[sy, sx]
    # Only trust samples that land on opaque interior
    valid = alpha[sy, sx] & (dist[sy, sx] >= max(1.0, inward * 0.5))
    out = rgb.copy()
    ring = alpha & (dist > 0) & (dist <= edge_px)
    use = ring & valid
    out[use] = sampled[use]

    # Fallback for any ring pixel not filled: NN from deep interior
    deep = alpha & (dist >= edge_px + 1)
    if np.any(ring & ~use) and np.any(deep):
        filled = _nn_fill(rgb, deep)
        miss = ring & ~use
        out[miss] = filled[miss]
    return out


def _lift_dark_edge(rgb: np.ndarray, alpha: np.ndarray, edge_px: int, min_lum: float) -> np.ndarray:
    """Raise only too-dark silhouette pixels toward foliage luminance (no washed beige fringe)."""
    dist = ndimage.distance_transform_edt(alpha)
    ring = alpha & (dist > 0) & (dist <= edge_px)
    if not np.any(ring):
        return rgb

    # Prefer target from interior foliage of this paint, not an external bright floor
    mid = alpha & (dist >= edge_px + 2) & (dist <= edge_px + 14)
    mid_lum = rgb.astype(np.float32).mean(axis=2)
    if np.any(mid):
        interior_target = float(np.median(mid_lum[mid]))
    else:
        interior_target = float(min_lum)
    # Only lift edges that are clearly darker than foliage (black outline), not all edges
    target = max(70.0, min(interior_target * 0.95, float(min_lum)))

    out = rgb.astype(np.float32)
    lum = out.mean(axis=2)
    # Replace very dark edge with NN from healthy foliage first
    very_dark = ring & (lum < target * 0.55)
    healthy = alpha & (mid_lum >= target * 0.85)
    if np.any(very_dark) and np.any(healthy):
        filled = _nn_fill(rgb, healthy)
        out[very_dark] = filled[very_dark]
        lum = out.mean(axis=2)

    need = ring & (lum < target) & (lum > 1e-3)
    if np.any(need):
        scale = (target / lum[need])[:, None]
        out[need] = np.minimum(out[need] * scale, 255.0)
    return np.clip(out, 0, 255).astype(np.uint8)


def composite(
    original_path: Path,
    generated_path: Path,
    output_path: Path,
    dilate_px: int = 12,
    defringe_px: int = 8,
    edge_min_lum: float = 95.0,
) -> None:
    original = Image.open(original_path).convert("RGBA")
    generated = Image.open(generated_path).convert("RGB")
    w, h = original.size
    if generated.size != (w, h):
        generated = generated.resize((w, h), Image.Resampling.LANCZOS)

    orig = np.array(original, dtype=np.uint8)
    gen = np.array(generated, dtype=np.uint8)
    alpha_bin = orig[:, :, 3] > 0

    rgb = gen.copy()
    lum = rgb.astype(np.float32).mean(axis=2)

    # 1) Fill black holes inside silhouette
    content = alpha_bin & (lum >= 28.0)
    holes = alpha_bin & ~content
    if np.any(holes):
        if np.any(content):
            filled = _nn_fill(rgb, content)
            rgb[holes] = filled[holes]
        still = alpha_bin & (rgb.astype(np.float32).mean(axis=2) < 28.0)
        if np.any(still):
            rgb[still] = orig[:, :, 0:3][still]

    # 2) Pull edge colors from deeper interior (kills baked black outline)
    rgb = _inward_sample(rgb, alpha_bin, edge_px=defringe_px, inward=defringe_px + 2)

    # 3) Lift remaining dark silhouette so it won't halo against sky
    rgb = _lift_dark_edge(rgb, alpha_bin, edge_px=max(3, defringe_px // 2), min_lum=edge_min_lum)

    # 4) Transparent RGB: flood with nearest opaque foliage color (never black).
    #    Black under alpha=0 is what creates 黑边 in mips / soft filter / RGB preview.
    out = np.zeros((h, w, 4), dtype=np.uint8)
    if np.any(alpha_bin):
        padded = _nn_fill(rgb, alpha_bin)
        out[:, :, 0:3] = padded
        out[alpha_bin, 0:3] = rgb[alpha_bin]
    else:
        out[:, :, 0:3] = 0
    out[:, :, 3] = np.where(alpha_bin, 255, 0)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(out, mode="RGBA").save(output_path, format="PNG", optimize=True)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--original", type=Path, required=True)
    p.add_argument("--generated", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--dilate", type=int, default=12)
    p.add_argument("--defringe", type=int, default=8)
    p.add_argument("--edge-min-lum", type=float, default=95.0)
    args = p.parse_args()
    if not args.original.is_file():
        print(f"missing original: {args.original}", file=sys.stderr)
        return 1
    if not args.generated.is_file():
        print(f"missing generated: {args.generated}", file=sys.stderr)
        return 1
    composite(
        args.original,
        args.generated,
        args.output,
        dilate_px=args.dilate,
        defringe_px=args.defringe,
        edge_min_lum=args.edge_min_lum,
    )
    print(f"wrote {args.output}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
