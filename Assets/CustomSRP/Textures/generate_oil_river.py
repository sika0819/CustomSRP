#!/usr/bin/env python3
"""Regenerate Monet-style OilRiverPaint / OilRiverGlint tiles.

Primary art comes from Cursor image gen (OilRiver*_gen in .cursor assets).
This script re-applies seamless edge blend + glint crush for Unity import.
"""

from __future__ import annotations

import re
import uuid
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
OUT_PAINT = ROOT / "OilRiverPaint.png"
OUT_GLINT = ROOT / "OilRiverGlint.png"

# Cursor-generated sources (copied next to this script if present).
CANDIDATE_PAINT = [
    ROOT / "OilRiverPaint_src.jpg",
    ROOT / "OilRiverPaint_src.png",
    Path.home()
    / ".cursor/projects/Users-sika-Desktop-code-CustomSRP/assets/OilRiverPaint_gen.jpg",
]
CANDIDATE_GLINT = [
    ROOT / "OilRiverGlint_src.jpg",
    ROOT / "OilRiverGlint_src.png",
    Path.home()
    / ".cursor/projects/Users-sika-Desktop-code-CustomSRP/assets/OilRiverGlint_gen.jpg",
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


def make_single_sun_column(
    src: Image.Image,
    width: int = 1024,
    height: int | None = None,
    *,
    near_frac: float = 0.20,
    far_scale: float = 2.05,
    near_scale: float = 0.28,
) -> Image.Image:
    """One centered vertical gold path — near short / far long (no multi roads).

    Texture layout (Unity V): bottom = near (short dabs, near_frac of height),
    top = far (long strokes, remaining height). Matches OilRiverSunGlint along↑.
    """
    sw, sh = src.size
    if height is None:
        height = max(width, int(round(width * sh / max(sw, 1))))
        # Prefer a tall tile so far band has room (default ~3:4).
        height = max(height, int(round(width * 4 / 3)))
        height += height % 2
    base = np.asarray(
        src.convert("RGB").resize((width, height), Image.Resampling.LANCZOS),
        dtype=np.float32,
    )
    h, w, _ = base.shape
    luma = base.max(axis=2)
    peak = int(np.argmax(luma.mean(axis=0)))
    cx = (w - 1) * 0.5
    base = np.roll(base, int(cx - peak), axis=1)

    # Pack near source into bottom near_frac; stretch far into top band.
    dst_t = np.linspace(0.0, 1.0, h, dtype=np.float32)  # 0=top(far), 1=bottom(near)
    src_near = 0.42
    src_t = np.empty_like(dst_t)
    far_mask = dst_t <= (1.0 - near_frac)
    src_t[far_mask] = dst_t[far_mask] / max(1.0 - near_frac, 1e-6) * (1.0 - src_near)
    u = (dst_t[~far_mask] - (1.0 - near_frac)) / max(near_frac, 1e-6)
    src_t[~far_mask] = (1.0 - src_near) + u * src_near
    src_rows = np.clip(src_t * (h - 1), 0, h - 1)
    r0 = np.floor(src_rows).astype(np.int32)
    r1 = np.minimum(r0 + 1, h - 1)
    wf = (src_rows - r0).astype(np.float32)[:, None, None]
    remapped = base[r0] * (1.0 - wf) + base[r1] * wf

    out = np.zeros_like(remapped)
    xx = np.arange(w, dtype=np.float32)
    for i in range(h):
        t = i / max(h - 1, 1)
        if t < (1.0 - near_frac):
            far_u = t / max(1.0 - near_frac, 1e-6)
            scale = far_scale - 0.15 * far_u
        else:
            near_u = (t - (1.0 - near_frac)) / max(near_frac, 1e-6)
            e = near_u * near_u * (3.0 - 2.0 * near_u)
            scale = (far_scale - 0.15) * (1.0 - e) + near_scale * e
        src_x = (xx - cx) / max(scale, 1e-4) + cx
        x0 = np.floor(src_x).astype(np.int32)
        wx = (src_x - x0).astype(np.float32)
        valid = (src_x >= 0) & (src_x <= w - 1)
        x0c = np.clip(x0, 0, w - 1)
        x1c = np.clip(x0 + 1, 0, w - 1)
        row = remapped[i]
        samp = row[x0c] * (1.0 - wx)[:, None] + row[x1c] * wx[:, None]
        samp[~valid] = 0.0
        out[i] = samp

    for i in range(h):
        t = i / max(h - 1, 1)
        if t < (1.0 - near_frac):
            far_u = t / max(1.0 - near_frac, 1e-6)
            half = 0.28 - 0.04 * far_u
        else:
            near_u = (t - (1.0 - near_frac)) / max(near_frac, 1e-6)
            e = near_u * near_u * (3.0 - 2.0 * near_u)
            half = 0.24 * (1.0 - e) + 0.04 * e
        sigma = half * w
        win = np.exp(-0.5 * ((xx - cx) / max(sigma, 1.0)) ** 2)
        crush = np.clip((out[i].max(axis=1) - 16.0) / 65.0, 0.0, 1.0)
        out[i] *= (win * crush)[:, None]

    # Soft fade only — do NOT blend top↔bottom (would mix near/far).
    m = int(w * 0.03)
    out[:, :m] = 0.0
    out[:, -m:] = 0.0
    b = min(20, h // 20)
    for i in range(b):
        fade = (i / max(b, 1)) ** 2
        out[i] *= fade
        out[h - 1 - i] *= fade
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8), "RGB")


def main() -> None:
    paint = make_seamless(
        Image.open(find_src(CANDIDATE_PAINT)).resize((1024, 1024), Image.Resampling.LANCZOS),
        80,
    )
    glint = make_single_sun_column(Image.open(find_src(CANDIDATE_GLINT)), 1024)

    to_rgba(paint).save(OUT_PAINT, "PNG")
    to_rgba(glint).save(OUT_GLINT, "PNG")
    ensure_meta(OUT_PAINT)
    ensure_meta(OUT_GLINT)
    print(f"Wrote {OUT_PAINT}")
    print(f"Wrote {OUT_GLINT} (single sun column)")


if __name__ == "__main__":
    main()
