#!/usr/bin/env python3
"""Regenerate Van Gogh Rhône-style OilOcean / Foam / Glint tiles.

Primary art comes from Cursor image gen (OilOcean*_gen in .cursor assets).
This script re-applies seamless edge blend + foam/glint crush for Unity import.
"""

from __future__ import annotations

import re
import uuid
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
ASSETS = (
    Path.home()
    / ".cursor/projects/Users-sika-Desktop-code-CustomSRP/assets"
)

OUT_PAINT = ROOT / "OilOcean.png"
OUT_FOAM = ROOT / "OilOceanFoam.png"
OUT_GLINT = ROOT / "OilOceanGlint.png"

CANDIDATE_PAINT = [
    ROOT / "OilOcean_src.jpg",
    ROOT / "OilOcean_src.png",
    ASSETS / "OilOcean_gen.jpg",
    ASSETS / "OilOcean_gen.png",
]
CANDIDATE_FOAM = [
    ROOT / "OilOceanFoam_src.jpg",
    ROOT / "OilOceanFoam_src.png",
    ASSETS / "OilOceanFoam_gen.jpg",
    ASSETS / "OilOceanFoam_gen.png",
]
CANDIDATE_GLINT = [
    ROOT / "OilOceanGlint_src.jpg",
    ROOT / "OilOceanGlint_src.png",
    ASSETS / "OilOceanGlint_gen.jpg",
    ASSETS / "OilOceanGlint_gen.png",
]


def find_src(cands: list[Path]) -> Path:
    for p in cands:
        if p.is_file():
            return p
    raise FileNotFoundError(f"No source among {[str(c) for c in cands]}")


def make_seamless(img: Image.Image, blend: int = 96) -> Image.Image:
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


def crush_luma_mask(rgb: Image.Image, loft: float, span: float) -> Image.Image:
    g = np.asarray(rgb, dtype=np.float32)
    luma = g.max(axis=2)
    mask = np.clip((luma - loft) / max(span, 1.0), 0.0, 1.0)[..., None]
    return Image.fromarray(np.clip(g * mask, 0, 255).astype(np.uint8), "RGB")


def daylight_paint(rgb: Image.Image) -> Image.Image:
    """Daytime sea: kill vignette, lift to cerulean/teal, keep stroke residual."""
    a = np.asarray(rgb.convert("RGB"), dtype=np.float32)
    h, w, _ = a.shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    cy, cx = (h - 1) * 0.5, (w - 1) * 0.5
    r = np.sqrt(((yy - cy) / max(cy, 1.0)) ** 2 + ((xx - cx) / max(cx, 1.0)) ** 2)
    vig = np.clip(r / 1.15, 0.0, 1.0)[..., None]
    small = Image.fromarray(np.clip(a, 0, 255).astype(np.uint8)).resize(
        (32, 32), Image.Resampling.BOX
    )
    mid = np.asarray(small.resize((w, h), Image.Resampling.BILINEAR), dtype=np.float32)
    field = a[h // 5 : 4 * h // 5, w // 5 : 4 * w // 5].mean(axis=(0, 1))
    residual = a - mid
    base = mid * (1.0 - vig * 0.9) + field[None, None, :] * (vig * 0.9)
    lift = np.maximum(field / np.maximum(mid.mean(axis=2, keepdims=True), 1.0), 1.0)
    base = base * np.clip(lift, 1.0, 1.35)
    out = np.clip(base + residual * 0.92, 0, 255)
    day = np.array([0.28, 0.55, 0.68], dtype=np.float32) * 255.0
    out = out * 0.82 + day * 0.18
    out = np.maximum(out, day * 0.5)
    a = np.clip(out / 255.0, 0.0, 1.0)
    luma = a.max(axis=2, keepdims=True)
    a = np.clip(a * 1.18 + 0.06, 0.0, 1.0)
    a[..., 0] *= 0.92
    a[..., 1] = np.clip(a[..., 1] * 1.06, 0.0, 1.0)
    a[..., 2] = np.clip(a[..., 2] * 1.02, 0.0, 1.0)
    crest = np.clip(luma - 0.45, 0.0, 1.0)
    a = np.clip(
        a + crest * 0.08 * np.array([0.55, 0.85, 1.0], dtype=np.float32), 0.0, 1.0
    )
    return Image.fromarray(np.clip(a * 255.0, 0, 255).astype(np.uint8), "RGB")


def ensure_meta(path: Path) -> None:
    meta = path.with_suffix(path.suffix + ".meta")
    if meta.exists():
        return
    template = (ROOT / "OilOcean.png.meta").read_text()
    guid = uuid.uuid4().hex
    text = re.sub(r"^guid: .*", f"guid: {guid}", template, count=1, flags=re.M)
    meta.write_text(text)


def make_single_sun_column(
    src: Image.Image,
    width: int = 1024,
    height: int | None = None,
    *,
    near_frac: float = 0.20,
    far_scale: float = 2.05,
    near_scale: float = 0.28,
) -> Image.Image:
    """One centered vertical gold path — same contract as OilRiverGlint.

    Bottom near_frac = short near dabs; top band = long far strokes.
    """
    # Keep in sync with generate_oil_river.make_single_sun_column.
    import sys

    sys.path.insert(0, str(ROOT))
    from generate_oil_river import make_single_sun_column as _river_column

    return _river_column(
        src,
        width,
        height,
        near_frac=near_frac,
        far_scale=far_scale,
        near_scale=near_scale,
    )


def monet_paint(rgb: Image.Image) -> Image.Image:
    """River-like Monet tile: kill vignette only, keep cream/ochre dabs."""
    a = np.asarray(rgb.convert("RGB"), dtype=np.float32)
    h, w, _ = a.shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    cy, cx = (h - 1) * 0.5, (w - 1) * 0.5
    r = np.sqrt(((yy - cy) / max(cy, 1.0)) ** 2 + ((xx - cx) / max(cx, 1.0)) ** 2)
    vig = np.clip(r / 1.2, 0.0, 1.0)[..., None]
    small = Image.fromarray(np.clip(a, 0, 255).astype(np.uint8)).resize(
        (32, 32), Image.Resampling.BOX
    )
    mid = np.asarray(small.resize((w, h), Image.Resampling.BILINEAR), dtype=np.float32)
    field = a[h // 5 : 4 * h // 5, w // 5 : 4 * w // 5].mean(axis=(0, 1))
    out = (a - mid) * 0.95 + mid * (1.0 - vig * 0.85) + field * (vig * 0.85)
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8), "RGB")


def main() -> None:
    paint = monet_paint(
        make_seamless(
            Image.open(find_src(CANDIDATE_PAINT)).resize(
                (1024, 1024), Image.Resampling.LANCZOS
            ),
            80,
        )
    )
    foam = crush_luma_mask(
        make_seamless(
            Image.open(find_src(CANDIDATE_FOAM)).resize(
                (1024, 1024), Image.Resampling.LANCZOS
            ),
            80,
        ),
        loft=28.0,
        span=70.0,
    )
    # Glint = single vertical column (OilRiverSunGlint UV); never seamless-tile.
    # Prefer finished river glint as-is (already near/far); else build from source.
    river_glint = ROOT / "OilRiverGlint.png"
    if river_glint.is_file():
        glint = Image.open(river_glint).convert("RGB")
    else:
        glint = make_single_sun_column(Image.open(find_src(CANDIDATE_GLINT)), width=1024)

    to_rgba(paint).save(OUT_PAINT, "PNG")
    to_rgba(foam).save(OUT_FOAM, "PNG")
    to_rgba(glint).save(OUT_GLINT, "PNG")
    ensure_meta(OUT_PAINT)
    ensure_meta(OUT_FOAM)
    ensure_meta(OUT_GLINT)
    print(f"Wrote {OUT_PAINT}")
    print(f"Wrote {OUT_FOAM}")
    print(f"Wrote {OUT_GLINT} (single sun column)")


if __name__ == "__main__":
    main()
