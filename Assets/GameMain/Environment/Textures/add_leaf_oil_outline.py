#!/usr/bin/env python3
"""Bake oil-painting leaf outlines into foliage atlases (Shu_Mix / Shu2_Mix).

Leaf cards disable OilNPR geometry Outline; silhouettes come from alpha clip.
Paints a slightly darker, brushy rim along each opaque UV island edge
(including holes between overlapping leaves). Alpha stays hard for clipping.
"""

from __future__ import annotations

import shutil
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[3]  # CustomSRP/
BAK_DIR = REPO / "Tools" / "leaf_outline_bak"
TARGETS = ("Shu_Mix.png", "Shu2_Mix.png")

INWARD = 3.8          # px darker paint inside leaf
OUTWARD = 2.0         # px hard opaque rim outside (becomes new silhouette)
DARKEN = 0.72         # outline = local leaf RGB * DARKEN（稍深一点）
TIP_DARKEN = 0.60     # outermost px a touch darker
WOBBLE = 1.4
SEED = 42


def _value_noise(h: int, w: int, scale: float, rng: np.random.Generator) -> np.ndarray:
    gh = max(2, int(h / scale) + 2)
    gw = max(2, int(w / scale) + 2)
    grid = rng.random((gh, gw), dtype=np.float32)
    zoom = (h / (gh - 1), w / (gw - 1))
    return ndimage.zoom(grid, zoom, order=1)[:h, :w]


def add_oil_leaf_outline(rgba: np.ndarray) -> np.ndarray:
    h, w = rgba.shape[:2]
    rng = np.random.default_rng(SEED)

    rgb = rgba[..., :3].astype(np.float32)
    alpha = rgba[..., 3].astype(np.float32)
    opaque = alpha > 128.0

    # Local leaf color so green / tan / brown each get their own darker rim.
    local = np.empty_like(rgb)
    for c in range(3):
        ch = rgb[..., c].copy()
        ch[~opaque] = 0.0
        smeared = ndimage.gaussian_filter(ch, sigma=1.8)
        weight = ndimage.gaussian_filter(opaque.astype(np.float32), sigma=1.8)
        local[..., c] = np.where(weight > 1e-3, smeared / np.maximum(weight, 1e-3), 0.0)

    outline = np.clip(local * DARKEN, 0.0, 255.0)
    tip = np.clip(local * TIP_DARKEN, 0.0, 255.0)

    dist_in = ndimage.distance_transform_edt(opaque)
    dist_out = ndimage.distance_transform_edt(~opaque)

    n1 = _value_noise(h, w, 14.0, rng)
    n2 = _value_noise(h, w, 5.5, rng)
    n3 = _value_noise(h, w, 3.0, rng)
    wobble = (n1 * 0.55 + n2 * 0.30 + n3 * 0.15 - 0.5) * 2.0 * WOBBLE

    in_limit = np.clip(INWARD + wobble, 2.0, INWARD + WOBBLE + 1.0)
    out_limit = np.clip(OUTWARD + wobble * 0.45, 1.0, OUTWARD + 1.0)

    # Brush break — keep mostly continuous so edges read clearly.
    unbroken = n3 > 0.08

    inward = opaque & (dist_in <= in_limit) & unbroken
    # Coverage stronger at silhouette, fades inward (paint rim, not flat frame).
    t = np.clip(1.0 - dist_in / np.maximum(in_limit, 1e-3), 0.0, 1.0)
    inward_w = np.where(inward, 0.55 + 0.45 * np.power(t, 0.7), 0.0)

    # Hard outward rim: full alpha, solid darker paint (oil slab, not soft fringe).
    near = ndimage.binary_dilation(opaque, iterations=int(np.ceil(OUTWARD + 1)))
    outward = (~opaque) & near & (dist_out <= out_limit) & (dist_out > 0) & (n2 > 0.06)

    # Sample with slight jitter for knife-edge irregularity.
    ox = np.clip((n1 - 0.5) * 2.0, -2, 2).astype(np.int32)
    oy = np.clip((n2 - 0.5) * 2.0, -2, 2).astype(np.int32)
    yy, xx = np.mgrid[0:h, 0:w]
    sy = np.clip(yy + oy, 0, h - 1)
    sx = np.clip(xx + ox, 0, w - 1)
    paint = outline[sy, sx]
    tip_paint = tip[sy, sx]
    # Mix tip darkness on outermost inward band.
    tip_mix = np.clip(1.0 - dist_in / 2.0, 0.0, 1.0)[..., None]
    paint_in = paint * (1.0 - tip_mix) + tip_paint * tip_mix

    out = rgba.astype(np.float32).copy()

    for c in range(3):
        out[..., c] = np.where(
            inward_w > 0,
            out[..., c] * (1.0 - inward_w) + paint_in[..., c] * inward_w,
            out[..., c],
        )

    # Outward: nearest opaque outline color via dilation, hard alpha.
    nearest = np.empty_like(rgb)
    for c in range(3):
        nearest[..., c] = ndimage.grey_dilation(
            np.where(opaque, outline[..., c], 0.0), size=(5, 5)
        )
    fallback = np.array([95.0, 112.0, 62.0], dtype=np.float32)
    for c in range(3):
        ch = np.where(nearest[..., c] > 1.0, nearest[..., c], fallback[c])
        # Slight noise in rim value for impasto.
        ch = ch * (0.92 + 0.16 * n1)
        out[..., c] = np.where(outward, ch, out[..., c])
    out[..., 3] = np.where(outward, 255.0, out[..., 3])

    # Sparse darker flecks on the rim.
    fleck = (rng.random((h, w)) > 0.96) & (inward | outward)
    for c in range(3):
        out[..., c] = np.where(fleck, out[..., c] * 0.85, out[..., c])

    out = np.clip(out, 0, 255)
    clear = out[..., 3] < 8
    out[clear, :3] = 0
    # Snap semi-transparent fringe from source to hard clip (keep atlas clean).
    mid = (out[..., 3] >= 8) & (out[..., 3] < 128) & ~outward
    out[mid, 3] = 0
    out[mid, :3] = 0

    return out.astype(np.uint8)


def process(name: str, *, from_backup: bool = True) -> Path:
    src = ROOT / name
    BAK_DIR.mkdir(parents=True, exist_ok=True)
    bak = BAK_DIR / f"{Path(name).stem}_PreOutline.png"
    legacy = ROOT / bak.name
    if legacy.exists() and not bak.exists():
        shutil.move(str(legacy), str(bak))
        legacy_meta = Path(str(legacy) + ".meta")
        if legacy_meta.exists():
            legacy_meta.unlink()

    if from_backup and bak.exists():
        shutil.copy2(bak, src)
    elif not bak.exists():
        shutil.copy2(src, bak)
        print(f"backup → {bak}")

    img = np.array(Image.open(src).convert("RGBA"))
    result = add_oil_leaf_outline(img)
    Image.fromarray(result, "RGBA").save(src, optimize=True)
    print(f"wrote {name}  opaque% {(result[..., 3] > 128).mean() * 100:.1f}")
    return src


def main() -> None:
    for name in TARGETS:
        process(name, from_backup=True)


if __name__ == "__main__":
    main()
