#!/usr/bin/env python3
"""Composite Cursor oil texture onto original atlas alpha (exact shapes, no dark rim)."""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image, ImageEnhance, ImageFilter
from scipy import ndimage

ROOT = Path(__file__).resolve().parent
ASSETS = Path("/Users/sika/.cursor/projects/Users-sika-Desktop-code-CustomSRP/assets")
CREAM = np.array([250.0, 248.0, 242.0], np.float32)
W = np.array([0.299, 0.587, 0.114], np.float32)

JOBS = [
    ("WaterMist_5x5.png", "WaterMist_5x5_OilPainting_from_src.jpg", "WaterMist_5x5_OilPainting.png"),
    ("WaterfallSplash_5x5.png", "WaterfallSplash_5x5_OilPainting_from_src.jpg", "WaterfallSplash_5x5_OilPainting.png"),
]


def process(src_path: Path, oil_path: Path, out_path: Path) -> None:
    src = Image.open(src_path).convert("RGBA")
    oil = Image.open(oil_path).convert("RGB").resize(src.size, Image.Resampling.LANCZOS)
    oil = ImageEnhance.Contrast(oil).enhance(1.2)
    oil = ImageEnhance.Sharpness(oil).enhance(1.35)

    src_a = np.array(src)
    alpha = src_a[:, :, 3].astype(np.float32) / 255.0
    oil_a = np.array(oil).astype(np.float32)
    oil_lum = oil_a @ W
    paint = np.clip((oil_lum - 20.0) / 60.0, 0.0, 1.0)

    br = oil_a.copy()
    br[oil_lum < 40] = 0
    bimg = Image.fromarray(np.clip(br, 0, 255).astype(np.uint8), "RGB")
    for _ in range(2):
        bimg = bimg.filter(ImageFilter.MaxFilter(5))
    dil = np.array(bimg).astype(np.float32)
    dl = dil @ W
    for c in range(3):
        dil[:, :, c] = np.where(dl < 1, CREAM[c], dil[:, :, c])

    use = paint > 0.2
    rgb = np.zeros_like(oil_a)
    for c in range(3):
        rgb[:, :, c] = np.where(use, oil_a[:, :, c], dil[:, :, c])
        rgb[:, :, c] = np.where((alpha > 0.02) & (~use), dil[:, :, c], rgb[:, :, c])

    blur = np.array(oil.filter(ImageFilter.GaussianBlur(2.5))).astype(np.float32)
    rgb = np.clip(rgb + (oil_a - blur) * 0.25 * (alpha > 0.02)[:, :, None], 0, 255)

    body = alpha > 0.04
    dist = ndimage.distance_transform_edt(body).astype(np.float32)
    rim = body & (dist <= 3.0)
    deep = body & (dist >= 4.5)

    lum = rgb @ W
    t = np.where(rim, np.clip((160.0 - lum) / 100.0, 0, 1), 0.0)
    t = np.where(rim & (lum < 130), 1.0, t)
    rgb = rgb * (1.0 - t[:, :, None]) + dil * t[:, :, None]
    for c in range(3):
        rgb[:, :, c] = np.where(rim, np.maximum(rgb[:, :, c], 200.0), rgb[:, :, c])

    soft = (alpha > 0.02) & (alpha < 0.45)
    for c in range(3):
        rgb[:, :, c] = np.where(soft, np.maximum(rgb[:, :, c], 205.0), rgb[:, :, c])

    lum2 = rgb @ W
    deep_too_black = deep & (lum2 < 25)
    for c in range(3):
        rgb[:, :, c] = np.where(deep_too_black, np.maximum(rgb[:, :, c], 35.0), rgb[:, :, c])

    out = np.zeros((src.size[1], src.size[0], 4), np.float32)
    for c in range(3):
        out[:, :, c] = np.where(alpha > 0.01, rgb[:, :, c], CREAM[c])
    out[:, :, 3] = src_a[:, :, 3]
    out_u8 = np.clip(np.round(out), 0, 255).astype(np.uint8)
    Image.fromarray(out_u8, "RGBA").save(out_path, "PNG", optimize=True)
    print(f"wrote {out_path}")


def main() -> None:
    for src_name, oil_name, out_name in JOBS:
        oil = ASSETS / oil_name
        if not oil.exists():
            print(f"missing oil gen: {oil}")
            continue
        process(ROOT / src_name, oil, ROOT / out_name)


if __name__ == "__main__":
    main()
