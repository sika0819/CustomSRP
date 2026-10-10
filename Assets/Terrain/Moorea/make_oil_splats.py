#!/usr/bin/env python3
"""Author seamless oil-paint terrain splats for the Moorea set.

Strokes wrap, so a freshly painted tile is already periodic. Do not average
opposite edges: that weld is a soft cross when the texture repeats.
Existing tiles that were welded that way are repaired by quilting the soft
band with interior strokes.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
SIZE = 1024


def stamp_ellipse(
    img: np.ndarray,
    cx: float,
    cy: float,
    rx: float,
    ry: float,
    ang: float,
    color: np.ndarray,
    alpha: float,
) -> None:
    """Short loaded brush: flat core, soft rim. Coordinates wrap."""
    size = img.shape[0]
    r = int(np.ceil(max(rx, ry))) + 1
    yy, xx = np.mgrid[-r : r + 1, -r : r + 1]
    ca = float(np.cos(ang))
    sa = float(np.sin(ang))
    lx = xx * ca + yy * sa
    ly = -xx * sa + yy * ca
    norm = (lx / max(rx, 0.5)) ** 2 + (ly / max(ry, 0.5)) ** 2
    core = np.clip((1.05 - norm) / 0.55, 0.0, 1.0)
    weight = core * core * (3.0 - 2.0 * core)
    if float(weight.max()) <= 0:
        return
    ys = (int(round(cy)) + yy) % size
    xs = (int(round(cx)) + xx) % size
    w = (alpha * weight)[..., None]
    img[ys, xs] = img[ys, xs] * (1.0 - w) + color * w


def paint(size: int, palette: np.ndarray, seed: int, strokes: int) -> np.ndarray:
    """Dense impasto dabs, same family as SplatGrass / SplatRock — not hairline strokes."""
    rng = np.random.default_rng(seed)
    img = np.empty((size, size, 3), np.float32)
    img[:] = palette[len(palette) // 2]

    for _ in range(strokes):
        color = palette[int(rng.integers(0, palette.shape[0]))]
        color = np.clip(color + rng.normal(0, 0.012, 3), 0, 1).astype(np.float32)
        ang = float(rng.uniform(0, np.pi))
        bend = float(rng.uniform(-0.35, 0.35))
        length = float(rng.uniform(6, 16))
        rx = float(rng.uniform(7, 14))
        ry = rx * float(rng.uniform(0.55, 0.9))
        alpha = float(rng.uniform(0.78, 1.0))
        x = float(rng.uniform(0, size))
        y = float(rng.uniform(0, size))
        steps = int(rng.integers(2, 4))
        for s in range(steps):
            t = s / max(1, steps - 1) - 0.5
            wobble = bend * t
            stamp_ellipse(
                img,
                x + np.cos(ang) * t * length - np.sin(ang) * wobble * length,
                y + np.sin(ang) * t * length + np.cos(ang) * wobble * length,
                rx * float(rng.uniform(0.92, 1.08)),
                ry * float(rng.uniform(0.92, 1.08)),
                ang + wobble,
                color,
                alpha,
            )
    return np.clip(img, 0, 1)


def _min_cut_rows(cost: np.ndarray) -> np.ndarray:
    """Top-to-bottom path. cost is (height, width) -> x index per row."""
    height, width = cost.shape
    acc = cost.astype(np.float64).copy()
    back = np.zeros((height, width), np.int32)
    idx = np.arange(width)
    for y in range(1, height):
        prev = acc[y - 1]
        left = np.empty(width, np.float64)
        right = np.empty(width, np.float64)
        left[0] = np.inf
        left[1:] = prev[:-1]
        right[-1] = np.inf
        right[:-1] = prev[1:]
        stack = np.stack((left, prev, right), axis=0)
        choice = np.argmin(stack, axis=0)
        acc[y] = cost[y] + stack[choice, idx]
        back[y] = idx + (choice - 1)
    x = int(np.argmin(acc[-1]))
    path = np.empty(height, np.int32)
    for y in range(height - 1, -1, -1):
        path[y] = x
        x = int(back[y, x])
    return path


def _best_source(
    double: np.ndarray,
    y: int,
    patch_h: int,
    dest_x: int,
    win_w: int,
    overlap: int,
    hole_w: int,
    filled_rows: int,
    rng: np.random.Generator,
    tries: int,
) -> np.ndarray:
    height2, width, _ = double.shape
    left_hi = dest_x - win_w
    right_lo = dest_x + win_w
    right_hi = width - win_w
    dest = double[y : y + patch_h, dest_x : dest_x + win_w]
    best = None
    best_cost = np.inf
    for _ in range(tries):
        if left_hi > 0 and (right_hi <= right_lo or rng.random() < 0.5):
            sx = int(rng.integers(0, left_hi + 1))
        else:
            sx = int(rng.integers(right_lo, right_hi + 1))
        sy = int(rng.integers(0, height2 - patch_h + 1))
        if not (sy + patch_h <= y or sy >= y + patch_h):
            if not (sx + win_w <= dest_x or sx >= dest_x + win_w):
                continue
        src = double[sy : sy + patch_h, sx : sx + win_w]
        diff = src - dest
        cost = float(np.sum(diff[:, :overlap] ** 2) + np.sum(diff[:, -overlap:] ** 2))
        if filled_rows > 0:
            band = diff[:filled_rows, overlap : overlap + hole_w]
            cost += float(np.sum(band * band))
        if cost < best_cost:
            best_cost = cost
            best = src.copy()
    if best is None:
        raise RuntimeError("no quilt source")
    return best


def _paste(dest: np.ndarray, src: np.ndarray, overlap: int, hole_w: int, filled_rows: int) -> None:
    left_cost = np.sum((dest[:, :overlap] - src[:, :overlap]) ** 2, axis=-1)
    right_cost = np.sum((dest[:, -overlap:] - src[:, -overlap:]) ** 2, axis=-1)
    left_path = _min_cut_rows(left_cost)
    right_path = _min_cut_rows(right_cost)
    painted = dest.copy()
    hole_end = overlap + hole_w
    for row in range(painted.shape[0]):
        cut_l = int(left_path[row])
        painted[row, cut_l:overlap] = src[row, cut_l:overlap]
        painted[row, overlap:hole_end] = src[row, overlap:hole_end]
        cut_r = int(right_path[row])
        painted[row, hole_end : hole_end + cut_r] = src[row, hole_end : hole_end + cut_r]
    if filled_rows > 0:
        band = min(filled_rows, painted.shape[0])
        old = dest[:band].copy()
        cost = np.sum((old - painted[:band]) ** 2, axis=-1)
        path = _min_cut_rows(cost.T)
        for x in range(painted.shape[1]):
            cut = int(path[x])
            painted[:cut, x] = old[:cut, x]
    dest[:] = painted


def _fill_vertical(
    img: np.ndarray,
    x0: int,
    x1: int,
    overlap: int,
    patch: int,
    rng: np.random.Generator,
    tries: int = 48,
) -> None:
    """Fill img[:, x0:x1] from sharp columns outside that span. Toroidal along Y."""
    height, width, _ = img.shape
    hole_w = x1 - x0
    win_w = hole_w + 2 * overlap
    dest_x = x0 - overlap
    if dest_x < 0 or dest_x + win_w > width:
        raise ValueError(f"band/overlap too wide for {width}")

    double = np.concatenate([img, img], axis=0)
    span0 = height // 2
    span1 = span0 + height
    step = max(8, patch - overlap)
    ys = list(range(span0, span1 - patch + 1, step))
    if not ys or ys[-1] != span1 - patch:
        ys.append(span1 - patch)

    prev_end = span0
    for y in ys:
        filled_rows = max(0, prev_end - y)
        dest = double[y : y + patch, dest_x : dest_x + win_w]
        src = _best_source(
            double, y, patch, dest_x, win_w, overlap, hole_w, filled_rows, rng, tries
        )
        _paste(dest, src, overlap, hole_w, filled_rows)
        double[y : y + patch, dest_x : dest_x + win_w] = dest
        prev_end = y + patch

    img[: height // 2] = double[height : height + height // 2]
    img[height // 2 :] = double[height // 2 : height]


def repair_tile(
    img: np.ndarray,
    band: int = 80,
    overlap: int = 36,
    patch: int = 96,
    seed: int = 0,
) -> np.ndarray:
    """Replace the softened opposite-edge band with quilted interior strokes.

    Roll so the bad frame becomes a center cross, cover each arm with patches
    from the sharp quadrants, then roll back. The join sits on the tile edge
    and continues through it.
    """
    src = np.clip(img.astype(np.float32), 0, 1)
    height, width = src.shape[:2]
    rolled = np.roll(np.roll(src, height // 2, 0), width // 2, 1)
    rng = np.random.default_rng(seed)
    _fill_vertical(rolled, width // 2 - band, width // 2 + band, overlap, patch, rng)
    transposed = np.swapaxes(rolled, 0, 1).copy()
    _fill_vertical(transposed, height // 2 - band, height // 2 + band, overlap, patch, rng)
    rolled = np.swapaxes(transposed, 0, 1)
    out = np.roll(np.roll(rolled, -(height // 2), 0), -(width // 2), 1)
    return np.clip(out, 0, 1)


def wrap_ratio(img: np.ndarray) -> tuple[float, float]:
    step_h = np.abs(img[:, 1:] - img[:, :-1]).mean()
    step_v = np.abs(img[1:] - img[:-1]).mean()
    wrap_h = np.abs(img[:, 0] - img[:, -1]).mean()
    wrap_v = np.abs(img[0] - img[-1]).mean()
    return float(wrap_h / (step_h + 1e-8)), float(wrap_v / (step_v + 1e-8))


def needs_repair(img: np.ndarray) -> bool:
    """Welded tiles set the opposite edge pixels equal, so the wrap step is ~0."""
    horizontal, vertical = wrap_ratio(img)
    return horizontal < 0.55 or vertical < 0.55


def save_rgb(path: Path, img: np.ndarray) -> None:
    rgb = (np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)
    Image.fromarray(rgb, mode="RGB").save(path)
    horizontal, vertical = wrap_ratio(rgb.astype(np.float32))
    print(path.name, "wrap", round(horizontal, 2), round(vertical, 2))


def main() -> None:
    # 基础配色尝试.xlsx → 季节·夏
    # 叶绿顶 #529E29 / 叶绿底 #2E661A；泥 #A3855C
    authored = {
        "SplatSand.png": 103,
        "SplatGrass.png": 101,
        "SplatForest.png": 100,
        "SplatRock.png": 102,
        "SplatBuilt.png": 104,
    }
    forest = np.array(
        [
            [0.18, 0.40, 0.10],  # #2E661A
            [0.25, 0.51, 0.13],
            [0.32, 0.62, 0.16],  # #529E29
            [0.18, 0.40, 0.10],
            [0.32, 0.62, 0.16],
        ],
        dtype=np.float32,
    )
    built = np.array(
        [
            [0.64, 0.52, 0.36],  # #A3855C
            [0.48, 0.36, 0.24],
            [0.76, 0.64, 0.48],
            [0.58, 0.46, 0.30],
            [0.70, 0.58, 0.42],
        ],
        dtype=np.float32,
    )
    for name, seed in authored.items():
        path = ROOT / name
        if path.exists():
            img = np.asarray(Image.open(path).convert("RGB")).astype(np.float32) / 255.0
            if needs_repair(img):
                save_rgb(path, repair_tile(img, seed=seed))
            else:
                horizontal, vertical = wrap_ratio(img)
                print(name, "already seamless", round(horizontal, 2), round(vertical, 2))
            continue
        if name == "SplatForest.png":
            save_rgb(path, paint(SIZE, forest, seed=17, strokes=5200))
        elif name == "SplatBuilt.png":
            save_rgb(path, paint(SIZE, built, seed=29, strokes=4800))


if __name__ == "__main__":
    main()
