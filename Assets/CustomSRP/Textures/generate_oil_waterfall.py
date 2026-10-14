#!/usr/bin/env python3
"""Regenerate Monet-style OilWaterfallPaint / OilWaterfallMist tiles."""

from __future__ import annotations

import re
import uuid
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
OUT_PAINT = ROOT / "OilWaterfallPaint.png"
OUT_MIST = ROOT / "OilWaterfallMist.png"

CANDIDATE_PAINT = [
    Path.home()
    / ".cursor/projects/Users-sika-Desktop-code-CustomSRP/assets/OilWaterfallPaint_gen.jpg",
]
CANDIDATE_MIST = [
    Path.home()
    / ".cursor/projects/Users-sika-Desktop-code-CustomSRP/assets/OilWaterfallMist_gen.jpg",
]


def find_src(cands: list[Path]) -> Path:
    for p in cands:
        if p.is_file():
            return p
    raise FileNotFoundError(f"No source among {[str(c) for c in cands]}")


def make_seamless(img: Image.Image, blend: int = 80) -> Image.Image:
    arr = np.asarray(img.convert("RGB"), dtype=np.float32)
    h, w, _ = arr.shape
    b = min(blend, w // 4, h // 4)
    out = arr.copy()
    for i in range(b):
        t = i / b
        t = t * t * (3 - 2 * t)
        out[:, i] = out[:, i] * t + out[:, w - b + i] * (1 - t)
        out[:, w - b + i] = out[:, i]
    for i in range(b):
        t = i / b
        t = t * t * (3 - 2 * t)
        out[i, :] = out[i, :] * t + out[h - b + i, :] * (1 - t)
        out[h - b + i, :] = out[i, :]
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8), "RGB")


def to_rgba(img: Image.Image) -> Image.Image:
    rgb = img.convert("RGB")
    return Image.merge("RGBA", (*rgb.split(), Image.new("L", rgb.size, 255)))


def ensure_meta(path: Path) -> None:
    meta = path.with_suffix(path.suffix + ".meta")
    if meta.exists():
        return
    template = (ROOT / "OilOcean.png.meta").read_text()
    guid = uuid.uuid4().hex
    text = re.sub(r"^guid: .*", f"guid: {guid}", template, count=1, flags=re.M)
    meta.write_text(text)


def main() -> None:
    paint = make_seamless(
        Image.open(find_src(CANDIDATE_PAINT)).resize((1024, 1024), Image.Resampling.LANCZOS),
        80,
    )
    mist_rgb = make_seamless(
        Image.open(find_src(CANDIDATE_MIST)).resize((1024, 1024), Image.Resampling.LANCZOS),
        80,
    )
    m = np.asarray(mist_rgb, dtype=np.float32)
    luma = m.max(axis=2)
    mask = np.clip((luma - 55.0) / 100.0, 0.0, 1.0)[..., None]
    mist = Image.fromarray(
        np.clip(m * mask + 255.0 * mask * 0.15, 0, 255).astype(np.uint8), "RGB"
    )

    to_rgba(paint).save(OUT_PAINT, "PNG")
    to_rgba(mist).save(OUT_MIST, "PNG")
    ensure_meta(OUT_PAINT)
    ensure_meta(OUT_MIST)
    print(f"Wrote {OUT_PAINT}")
    print(f"Wrote {OUT_MIST}")


if __name__ == "__main__":
    main()
