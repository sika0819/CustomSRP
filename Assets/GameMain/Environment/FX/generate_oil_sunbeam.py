#!/usr/bin/env python3
"""SunBeam oil mask — near-white RGB + paint-slab alpha (no hue).

Source rays are grayscale light masks; particle _BaseColor supplies warmth.
Soft AI oil color + washed alpha reads as fog, not oil. Rebuild as vertical
palette-knife slabs guided by the original alpha envelope.
"""

from __future__ import annotations

import math
import random
import struct
import zlib
from pathlib import Path

try:
    from PIL import Image
except ImportError as e:
    raise SystemExit("Pillow required: pip3 install pillow") from e

ROOT = Path(__file__).resolve().parent
SRC = (
    ROOT.parent
    / "ThirdParty"
    / "PolygonNatureBiomes"
    / "PNB_Meadow_Forest"
    / "FX"
    / "FX_Textures"
    / "SunBeam_Meadows.png"
)
OUTS = [
    ROOT.parent / "Art" / "SunBeam_Meadows_oil.png",
    ROOT.parent
    / "ThirdParty"
    / "PolygonNatureBiomes"
    / "PNB_Meadow_Forest"
    / "FX"
    / "FX_Textures"
    / "SunBeam_Meadows_oil.png",
]


def write_png(path: Path, rgba: bytes, w: int, h: int) -> None:
    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    raw = b"".join(b"\x00" + rgba[y * w * 4 : (y + 1) * w * 4] for y in range(h))
    compressed = zlib.compress(raw, 9)
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", compressed)
        + chunk(b"IEND", b"")
    )


def smoothstep(e0: float, e1: float, x: float) -> float:
    t = max(0.0, min(1.0, (x - e0) / max(1e-6, e1 - e0)))
    return t * t * (3.0 - 2.0 * t)


def clamp01(x: float) -> float:
    return max(0.0, min(1.0, x))


def load_guide_alpha(path: Path) -> tuple[list[float], int, int]:
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = im.load()
    alpha = [0.0] * (w * h)
    for y in range(h):
        for x in range(w):
            alpha[y * w + x] = px[x, y][3] / 255.0
    return alpha, w, h


def build_beam_centers(guide: list[float], w: int, h: int, rng: random.Random) -> list[dict]:
    """Detect vertical light columns from the mid-band of the guide."""
    mid_y0, mid_y1 = int(h * 0.35), int(h * 0.65)
    col = [0.0] * w
    for y in range(mid_y0, mid_y1):
        row = y * w
        for x in range(w):
            col[x] += guide[row + x]
    span = max(1, mid_y1 - mid_y0)
    col = [v / span for v in col]

    # Smooth slightly so noise doesn't spawn 50 micro beams.
    sm = col[:]
    for x in range(2, w - 2):
        sm[x] = (
            col[x - 2] + 2 * col[x - 1] + 3 * col[x] + 2 * col[x + 1] + col[x + 2]
        ) / 9.0

    peaks: list[int] = []
    for x in range(3, w - 3):
        if sm[x] < 0.12:
            continue
        if sm[x] >= sm[x - 1] and sm[x] >= sm[x + 1] and sm[x] >= sm[x - 2]:
            if not peaks or x - peaks[-1] >= 7:
                peaks.append(x)
            elif sm[x] > sm[peaks[-1]]:
                peaks[-1] = x

    beams: list[dict] = []
    for i, cx in enumerate(peaks):
        strength = sm[cx]
        # Width from half-max around peak.
        half = max(2.5, min(14.0, 4.0 + strength * 18.0))
        beams.append(
            {
                "cx": cx + rng.uniform(-1.2, 1.2),
                "half": half * rng.uniform(0.85, 1.15),
                "strength": clamp01(0.45 + strength * 1.4),
                "lean": rng.uniform(-0.04, 0.04),
                "phase": rng.uniform(0, math.tau),
                "ridge_freq": rng.uniform(0.9, 1.6),
                "seed": 1000 + i * 17,
            }
        )
    return beams


def stroke_alpha(
    x: float,
    y: float,
    w: int,
    h: int,
    beam: dict,
    guide: float,
) -> tuple[float, float]:
    """Return (alpha, impasto_value) for one beam at pixel."""
    u = x / w
    v = y / h
    # Slight lean / wave so shafts aren't CNC-straight.
    lean = beam["lean"] * h
    wave = math.sin(v * math.pi * 2.0 + beam["phase"]) * (beam["half"] * 0.35)
    dx = x - (beam["cx"] + lean * (v - 0.5) + wave)
    half = beam["half"] * (0.88 + 0.22 * math.sin(v * math.pi))
    # Palette-knife serration on the beam edge.
    jag = 1.1 * math.sin(v * 22.0 + beam["phase"]) + 0.7 * math.sin(
        v * 41.0 + beam["seed"] * 0.01
    )
    half = max(1.4, half + jag)

    dist = abs(dx)
    # Harder oil slab: interior stays dense; only rim feathers.
    edge = half * 0.35
    body = 1.0 - smoothstep(half - edge, half + 0.6, dist)
    if body <= 0.0:
        return 0.0, 0.0

    # Guide envelope + radial vignette already in source — keep soft falloff.
    env = smoothstep(0.02, 0.35, guide)
    # Vertical fade from source vignette feel, but keep mid opaque.
    radial = math.hypot((u - 0.5) * 1.15, (v - 0.5) * 1.05)
    vignette = 1.0 - smoothstep(0.42, 0.92, radial)

    # Impasto ridges along the stroke (value variation in RGB, not chroma).
    across = dx / max(1e-3, half)
    ridge = 0.72 + 0.28 * abs(
        math.sin(across * math.pi * beam["ridge_freq"] + v * 9.0 + beam["phase"])
    )
    knife = 0.90 + 0.10 * math.sin((u * 3.0 + v) * 11.0 + beam["seed"] * 0.02)
    weave = 0.96 + 0.04 * math.sin(u * 36.0) * math.cos(v * 28.0)
    impasto = clamp01(ridge * knife * weave)

    # Quantize opacity into paint slabs so soft alpha doesn't wash out oil.
    a = body * beam["strength"] * env * vignette
    a = smoothstep(0.04, 0.62, a)
    # Slightly boost mid so additive particles still show brush mass.
    a = clamp01(a * (0.75 + 0.35 * impasto))
    return a, impasto


def main() -> None:
    if not SRC.is_file():
        raise SystemExit(f"missing source {SRC}")

    guide, w, h = load_guide_alpha(SRC)
    rng = random.Random(42)
    beams = build_beam_centers(guide, w, h, rng)
    if len(beams) < 8:
        # Fallback: hand-place a meadow shaft set.
        for i, t in enumerate([0.16, 0.24, 0.32, 0.40, 0.48, 0.55, 0.62, 0.70, 0.78, 0.86]):
            beams.append(
                {
                    "cx": t * w,
                    "half": rng.uniform(4.0, 11.0),
                    "strength": rng.uniform(0.55, 1.0),
                    "lean": rng.uniform(-0.04, 0.04),
                    "phase": rng.uniform(0, math.tau),
                    "ridge_freq": rng.uniform(0.9, 1.6),
                    "seed": 2000 + i * 13,
                }
            )

    # Accents: a few thinner knife cuts between majors.
    accents = []
    for i in range(len(beams) - 1):
        if rng.random() < 0.55:
            cx = 0.5 * (beams[i]["cx"] + beams[i + 1]["cx"]) + rng.uniform(-2, 2)
            accents.append(
                {
                    "cx": cx,
                    "half": rng.uniform(1.6, 3.2),
                    "strength": rng.uniform(0.25, 0.55),
                    "lean": rng.uniform(-0.05, 0.05),
                    "phase": rng.uniform(0, math.tau),
                    "ridge_freq": rng.uniform(1.2, 2.0),
                    "seed": 3000 + i * 19,
                }
            )
    beams.extend(accents)

    buf = bytearray(w * h * 4)
    for y in range(h):
        for x in range(w):
            g = guide[y * w + x]
            if g < 0.01:
                continue
            best_a = 0.0
            best_imp = 0.0
            for beam in beams:
                a, imp = stroke_alpha(x + 0.5, y + 0.5, w, h, beam, g)
                if a > best_a:
                    best_a = a
                    best_imp = imp
            if best_a < 0.01:
                continue
            # Near-white pigment only — material/particle color tints the light.
            gray = int(255 * (0.90 + 0.10 * best_imp))
            o = (y * w + x) * 4
            buf[o] = gray
            buf[o + 1] = gray
            buf[o + 2] = gray
            buf[o + 3] = int(255 * best_a)

    data = bytes(buf)
    for out in OUTS:
        write_png(out, data, w, h)
        print(f"Wrote {out}")

    # Quick QA stats
    avals = [buf[i + 3] for i in range(0, len(buf), 4)]
    chroma = 0
    for i in range(0, len(buf), 4):
        if buf[i + 3] == 0:
            continue
        chroma = max(chroma, abs(buf[i] - buf[i + 1]), abs(buf[i] - buf[i + 2]))
    nz = sum(1 for a in avals if a > 0)
    print(
        f"  {w}x{h} beams={len(beams)} nonzero={nz} "
        f"alpha_mean={sum(avals)/len(avals):.2f} max_chroma={chroma}"
    )


if __name__ == "__main__":
    main()
