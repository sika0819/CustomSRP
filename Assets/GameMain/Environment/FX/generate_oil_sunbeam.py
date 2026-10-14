#!/usr/bin/env python3
"""SunBeam oil mask — near-white RGB + paint-slab alpha (no hue).

Vertical light shafts rebuilt as stacked oil knife dabs with visible bristle
furrows (not soft neon tubes). Particle _BaseColor supplies warmth.
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
ART_OIL = ROOT.parent / "Art" / "SunBeam_Meadows_oil.png"
TEX_SRC = ROOT.parent / "Textures" / "SunBeam_Meadows.png"
STROKE_SRC = (
    ROOT.parent.parent.parent
    / "CustomSRP"
    / "Textures"
    / "OilSunStroke.png"
)
# Prefer existing oil alpha (layout already correct); fall back to textures.
SRC_CANDIDATES = [ART_OIL, TEX_SRC]
OUTS = [ART_OIL]


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


def load_guide(path: Path) -> tuple[list[float], int, int]:
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    # Work at 512 so brushmarks stay readable as particle sprites.
    if max(w, h) > 512:
        im = im.resize((512, 512), Image.Resampling.LANCZOS)
        w, h = im.size
    px = im.load()
    guide = [0.0] * (w * h)
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a > 8:
                guide[y * w + x] = a / 255.0
            else:
                # RGB masks (legacy meadow texture) — use luma.
                guide[y * w + x] = max(r, g, b) / 255.0
    return guide, w, h


def load_stroke_stamp(path: Path) -> tuple[list[float], int, int] | None:
    """OilSunStroke is a horizontal bristle dab; we rotate to vertical later."""
    if not path.is_file():
        return None
    im = Image.open(path).convert("L")
    # Rotate so bristles run vertically (along sunbeam).
    im = im.transpose(Image.Transpose.ROTATE_90)
    w, h = im.size
    px = im.load()
    data = [px[x, y] / 255.0 for y in range(h) for x in range(w)]
    return data, w, h


def build_beam_centers(guide: list[float], w: int, h: int, rng: random.Random) -> list[dict]:
    mid_y0, mid_y1 = int(h * 0.30), int(h * 0.70)
    col = [0.0] * w
    for y in range(mid_y0, mid_y1):
        row = y * w
        for x in range(w):
            col[x] += guide[row + x]
    span = max(1, mid_y1 - mid_y0)
    col = [v / span for v in col]

    sm = col[:]
    for x in range(2, w - 2):
        sm[x] = (
            col[x - 2] + 2 * col[x - 1] + 3 * col[x] + 2 * col[x + 1] + col[x + 2]
        ) / 9.0

    peaks: list[int] = []
    for x in range(3, w - 3):
        if sm[x] < 0.10:
            continue
        if sm[x] >= sm[x - 1] and sm[x] >= sm[x + 1] and sm[x] >= sm[x - 2]:
            if not peaks or x - peaks[-1] >= 6:
                peaks.append(x)
            elif sm[x] > sm[peaks[-1]]:
                peaks[-1] = x

    beams: list[dict] = []
    for i, cx in enumerate(peaks):
        strength = sm[cx]
        half = max(2.0, min(12.0, 3.2 + strength * 16.0))
        beams.append(
            {
                "cx": cx + rng.uniform(-1.0, 1.0),
                "half": half * rng.uniform(0.88, 1.12),
                "strength": clamp01(0.50 + strength * 1.35),
                "lean": rng.uniform(-0.05, 0.05),
                "phase": rng.uniform(0, math.tau),
                "seed": 1000 + i * 17,
            }
        )
    return beams


def make_dabs(beam: dict, h: int, rng: random.Random) -> list[dict]:
    """Stack short knife pulls along the shaft — breaks read as brush, not tube."""
    dabs: list[dict] = []
    y = rng.uniform(h * 0.04, h * 0.10)
    y_end = h * rng.uniform(0.88, 0.96)
    bi = 0
    while y < y_end:
        length = rng.uniform(h * 0.06, h * 0.16)
        # Occasional gap = broken stroke.
        if rng.random() < 0.18 and bi > 0:
            y += rng.uniform(h * 0.01, h * 0.035)
            continue
        n_bristles = rng.randint(3, 8)
        bristles = []
        half = beam["half"]
        for k in range(n_bristles):
            # Pack bristles across the dab width with irregular gaps.
            t = (k + 0.5) / n_bristles
            ox = (t - 0.5) * 2.0 * half * rng.uniform(0.55, 1.05)
            bw = half * rng.uniform(0.10, 0.28)
            bristles.append(
                {
                    "ox": ox,
                    "half": bw,
                    "gain": rng.uniform(0.55, 1.0),
                    "ridge": rng.uniform(0.8, 1.6),
                    "phase": rng.uniform(0, math.tau),
                    # Tip fray: bristle ends at different lengths.
                    "y0_mul": rng.uniform(0.0, 0.12),
                    "y1_mul": rng.uniform(0.88, 1.0),
                }
            )
        dabs.append(
            {
                "y0": y,
                "y1": y + length,
                "cx_j": rng.uniform(-1.8, 1.8),
                "lean_j": rng.uniform(-0.02, 0.02),
                "strength": beam["strength"] * rng.uniform(0.75, 1.05),
                "bristles": bristles,
                "knife_bias": rng.choice([-1.0, 1.0]) * rng.uniform(0.15, 0.45),
                "seed": beam["seed"] + bi * 31,
            }
        )
        # Overlap next dab slightly so shaft still reads continuous from afar.
        y += length * rng.uniform(0.55, 0.82)
        bi += 1
    return dabs


def sample_stamp(
    stamp: list[float],
    sw: int,
    sh: int,
    u: float,
    v: float,
) -> float:
    """Bilinear sample stamp in [0,1] UV; 0 outside."""
    if u < 0.0 or u > 1.0 or v < 0.0 or v > 1.0:
        return 0.0
    x = u * (sw - 1)
    y = v * (sh - 1)
    x0 = int(x)
    y0 = int(y)
    x1 = min(sw - 1, x0 + 1)
    y1 = min(sh - 1, y0 + 1)
    tx = x - x0
    ty = y - y0
    s00 = stamp[y0 * sw + x0]
    s10 = stamp[y0 * sw + x1]
    s01 = stamp[y1 * sw + x0]
    s11 = stamp[y1 * sw + x1]
    return (s00 * (1 - tx) + s10 * tx) * (1 - ty) + (s01 * (1 - tx) + s11 * tx) * ty


def paint_bristle(
    x: float,
    y: float,
    beam: dict,
    dab: dict,
    br: dict,
    guide: float,
    w: int,
    h: int,
) -> tuple[float, float]:
    v = y / h
    lean = (beam["lean"] + dab["lean_j"]) * h
    wave = math.sin(v * math.pi * 2.0 + beam["phase"]) * (beam["half"] * 0.28)
    cx = beam["cx"] + dab["cx_j"] + lean * (v - 0.5) + wave + br["ox"]

    y0 = dab["y0"] + (dab["y1"] - dab["y0"]) * br["y0_mul"]
    y1 = dab["y0"] + (dab["y1"] - dab["y0"]) * br["y1_mul"]
    if y < y0 or y > y1:
        return 0.0, 0.0

    # Along-stroke fade: hard-ish paint ends, slight tip scrape.
    along = (y - y0) / max(1.0, y1 - y0)
    tip = smoothstep(0.0, 0.08, along) * (1.0 - smoothstep(0.82, 1.0, along))
    # Serrated tip length (bristle fray).
    tip *= 1.0 - 0.35 * smoothstep(
        0.7, 1.0, along + 0.08 * math.sin(x * 0.7 + br["phase"])
    )

    dx = x - cx
    # Ragged bristle edge — knife chatter, not Gaussian glow.
    jag = 0.55 * math.sin(y * 0.55 + br["phase"]) + 0.35 * math.sin(
        y * 1.15 + dab["seed"] * 0.01
    )
    half = max(0.55, br["half"] + jag * 0.35)
    dist = abs(dx)
    # Hard oil slab: dense core, thin rim only.
    edge = half * 0.42
    body = 1.0 - smoothstep(half - edge, half + 0.35, dist)
    if body <= 0.0:
        return 0.0, 0.0

    # Knife scrape: thicker paint on one side of the dab.
    side = dx / max(1e-3, half)
    scrape = clamp01(0.70 + dab["knife_bias"] * side)

    env = smoothstep(0.02, 0.32, guide)
    radial = math.hypot((x / w - 0.5) * 1.15, (v - 0.5) * 1.05)
    vignette = 1.0 - smoothstep(0.45, 0.94, radial)

    # Impasto ridges along bristle (value, not chroma).
    ridge = 0.62 + 0.38 * abs(
        math.sin(side * math.pi * br["ridge"] + along * 6.5 + br["phase"])
    )
    # Fine bristle hair lines inside the strand.
    hair = 0.85 + 0.15 * abs(math.sin(dx * 3.2 + y * 0.35 + br["phase"]))
    # Soft canvas tooth — tiny, never a grain cloud.
    weave = 0.96 + 0.04 * math.sin(x * 0.55) * math.cos(y * 0.42)
    impasto = clamp01(ridge * hair * weave * scrape)

    a = body * tip * br["gain"] * dab["strength"] * env * vignette
    # Quantize toward opaque paint slabs.
    a = smoothstep(0.06, 0.58, a)
    a = clamp01(a * (0.70 + 0.40 * impasto))
    return a, impasto


def stamp_dab(
    x: float,
    y: float,
    beam: dict,
    dab: dict,
    stamp: list[float],
    sw: int,
    sh: int,
    guide: float,
    w: int,
    h: int,
) -> tuple[float, float]:
    """Optional OilSunStroke stamp layered under procedural bristles."""
    v = y / h
    lean = (beam["lean"] + dab["lean_j"]) * h
    wave = math.sin(v * math.pi * 2.0 + beam["phase"]) * (beam["half"] * 0.28)
    cx = beam["cx"] + dab["cx_j"] + lean * (v - 0.5) + wave
    y0, y1 = dab["y0"], dab["y1"]
    if y < y0 or y > y1:
        return 0.0, 0.0
    half = beam["half"] * 1.35
    u = (x - (cx - half)) / max(1e-3, 2.0 * half)
    vv = (y - y0) / max(1e-3, y1 - y0)
    s = sample_stamp(stamp, sw, sh, u, vv)
    if s < 0.05:
        return 0.0, 0.0
    env = smoothstep(0.02, 0.32, guide)
    a = smoothstep(0.08, 0.75, s) * dab["strength"] * env * 0.85
    imp = clamp01(0.55 + 0.45 * s)
    return a, imp


def main() -> None:
    src = next((p for p in SRC_CANDIDATES if p.is_file()), None)
    if src is None:
        raise SystemExit(f"missing guide among {SRC_CANDIDATES}")

    guide, w, h = load_guide(src)
    rng = random.Random(42)
    beams = build_beam_centers(guide, w, h, rng)
    if len(beams) < 8:
        for i, t in enumerate(
            [0.16, 0.24, 0.32, 0.40, 0.48, 0.55, 0.62, 0.70, 0.78, 0.86]
        ):
            beams.append(
                {
                    "cx": t * w,
                    "half": rng.uniform(3.5, 10.0),
                    "strength": rng.uniform(0.55, 1.0),
                    "lean": rng.uniform(-0.05, 0.05),
                    "phase": rng.uniform(0, math.tau),
                    "seed": 2000 + i * 13,
                }
            )

    # Thin accent knife cuts between majors.
    accents = []
    for i in range(len(beams) - 1):
        if rng.random() < 0.60:
            accents.append(
                {
                    "cx": 0.5 * (beams[i]["cx"] + beams[i + 1]["cx"])
                    + rng.uniform(-2, 2),
                    "half": rng.uniform(1.4, 2.8),
                    "strength": rng.uniform(0.30, 0.55),
                    "lean": rng.uniform(-0.06, 0.06),
                    "phase": rng.uniform(0, math.tau),
                    "seed": 3000 + i * 19,
                }
            )
    beams.extend(accents)

    dab_lists = [make_dabs(b, h, rng) for b in beams]
    stamp_pack = load_stroke_stamp(STROKE_SRC)

    buf = bytearray(w * h * 4)
    for y in range(h):
        for x in range(w):
            g = guide[y * w + x]
            if g < 0.01:
                continue
            best_a = 0.0
            best_imp = 0.0
            for beam, dabs in zip(beams, dab_lists):
                for dab in dabs:
                    if stamp_pack is not None:
                        sa, simp = stamp_dab(
                            x + 0.5,
                            y + 0.5,
                            beam,
                            dab,
                            stamp_pack[0],
                            stamp_pack[1],
                            stamp_pack[2],
                            g,
                            w,
                            h,
                        )
                        if sa > best_a:
                            best_a = sa
                            best_imp = simp
                    for br in dab["bristles"]:
                        a, imp = paint_bristle(
                            x + 0.5, y + 0.5, beam, dab, br, g, w, h
                        )
                        if a > best_a:
                            best_a = a
                            best_imp = imp
            if best_a < 0.01:
                continue
            # Near-white pigment; stronger impasto swing so ridges read in-game.
            gray = int(255 * (0.78 + 0.22 * best_imp))
            o = (y * w + x) * 4
            buf[o] = gray
            buf[o + 1] = gray
            buf[o + 2] = gray
            buf[o + 3] = int(255 * best_a)

    data = bytes(buf)
    for out in OUTS:
        write_png(out, data, w, h)
        print(f"Wrote {out}")

    avals = [buf[i + 3] for i in range(0, len(buf), 4)]
    chroma = 0
    for i in range(0, len(buf), 4):
        if buf[i + 3] == 0:
            continue
        chroma = max(chroma, abs(buf[i] - buf[i + 1]), abs(buf[i] - buf[i + 2]))
    nz = sum(1 for a in avals if a > 0)
    print(
        f"  {w}x{h} beams={len(beams)} dabs={sum(len(d) for d in dab_lists)} "
        f"stamp={'yes' if stamp_pack else 'no'} nonzero={nz} "
        f"alpha_mean={sum(avals)/len(avals):.2f} max_chroma={chroma}"
    )


if __name__ == "__main__":
    main()
