#!/usr/bin/env python3
"""Crop ESA WorldCover 10 m 2021 onto the Moorea heightmap grid.

The 3×3° source tile is cached outside Assets (Unity would import it as a
36000² texture). Outputs next to this script, row 0 = north, same georef as
moorea_heightmap.json:

  moorea_landuse.png         color reference
  moorea_landuse_index.png   class codes (0 = nodata)
  moorea_landuse.json        legend, citation, class counts
"""
from __future__ import annotations

import json
import struct
import tempfile
import urllib.request
import zlib
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
META_JSON = ROOT / "moorea_heightmap.json"
OUT_COLOR = ROOT / "moorea_landuse.png"
OUT_INDEX = ROOT / "moorea_landuse_index.png"
OUT_JSON = ROOT / "moorea_landuse.json"

TILE_NAME = "ESA_WorldCover_10m_2021_v200_S18W150_Map.tif"
TILE_URL = (
    "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/" + TILE_NAME
)
CACHE = Path(tempfile.gettempdir()) / TILE_NAME
USER_AGENT = "CustomSRP-TerrainFetch/1.0 (Moorea land cover)"

CLASS_NAMES = {
    0: ("nodata", "无数据"),
    10: ("tree_cover", "林地"),
    20: ("shrubland", "灌木"),
    30: ("grassland", "草地"),
    40: ("cropland", "耕地"),
    50: ("built_up", "建成区"),
    60: ("bare_sparse", "裸地/稀疏植被"),
    70: ("snow_ice", "冰雪"),
    80: ("water", "水体"),
    90: ("herbaceous_wetland", "草本湿地"),
    95: ("mangroves", "红树林"),
    100: ("moss_lichen", "苔藓"),
}


def download_tile() -> Path:
    if CACHE.exists() and CACHE.stat().st_size > 1_000_000:
        return CACHE
    print("downloading", TILE_URL)
    req = urllib.request.Request(TILE_URL, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=600) as resp:
        CACHE.write_bytes(resp.read())
    return CACHE


def _ifd_entries(data: bytes) -> list[tuple[int, int, int, int]]:
    if data[:2] != b"II" or struct.unpack_from("<H", data, 2)[0] != 42:
        raise SystemExit("expected little-endian classic TIFF")
    ifd = struct.unpack_from("<I", data, 4)[0]
    n = struct.unpack_from("<H", data, ifd)[0]
    entries = []
    pos = ifd + 2
    for i in range(n):
        tag, typ, count, val = struct.unpack_from("<HHII", data, pos + i * 12)
        entries.append((tag, typ, count, val))
    return entries


def _tag_bytes(data: bytes, typ: int, count: int, val: int) -> bytes:
    type_size = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 12: 8}[typ]
    nbytes = type_size * count
    if nbytes <= 4:
        return struct.pack("<I", val)[:nbytes]
    return data[val : val + nbytes]


def crop_worldcover(tile_path: Path, georef: dict) -> tuple[np.ndarray, dict]:
    data = tile_path.read_bytes()
    tags = {tag: (typ, count, val) for tag, typ, count, val in _ifd_entries(data)}

    def short1(tag: int) -> int:
        typ, count, val = tags[tag]
        return struct.unpack("<H", _tag_bytes(data, typ, count, val))[0]

    width = short1(256)
    height = short1(257)
    compression = short1(259)
    tile_w = short1(322)
    tile_h = short1(323)
    if compression != 8:
        raise SystemExit(f"expected deflate compression, got {compression}")

    typ, count, val = tags[33550]
    scale_x, scale_y, _ = struct.unpack("<3d", _tag_bytes(data, typ, count, val))
    typ, count, val = tags[33922]
    tie = struct.unpack("<6d", _tag_bytes(data, typ, count, val))
    origin_lon, origin_lat = tie[3], tie[4]

    typ, count, val = tags[324]
    offsets = struct.unpack(f"<{count}I", _tag_bytes(data, typ, count, val))
    typ, count, val = tags[325]
    counts = struct.unpack(f"<{count}I", _tag_bytes(data, typ, count, val))
    ntx = (width + tile_w - 1) // tile_w

    lat_n = georef["lat_north"]
    lat_s = georef["lat_south"]
    lon_w = georef["lon_west"]
    lon_e = georef["lon_east"]
    res = 2049

    # Output row 0 is north, matching moorea_heightmap.png.
    ts = np.linspace(0.0, 1.0, res, dtype=np.float64)
    lats = lat_n + (lat_s - lat_n) * ts
    lons = lon_w + (lon_e - lon_w) * ts
    rows = (origin_lat - lats) / scale_y
    cols = (lons - origin_lon) / scale_x
    row_i = np.clip(np.rint(rows).astype(np.int32), 0, height - 1)
    col_i = np.clip(np.rint(cols).astype(np.int32), 0, width - 1)

    r0, r1 = int(row_i.min()) // tile_h, int(row_i.max()) // tile_h
    c0, c1 = int(col_i.min()) // tile_w, int(col_i.max()) // tile_w
    window = np.zeros(((r1 - r0 + 1) * tile_h, (c1 - c0 + 1) * tile_w), dtype=np.uint8)
    for ty in range(r0, r1 + 1):
        for tx in range(c0, c1 + 1):
            idx = ty * ntx + tx
            raw = zlib.decompress(data[offsets[idx] : offsets[idx] + counts[idx]])
            th = min(tile_h, height - ty * tile_h)
            tw = min(tile_w, width - tx * tile_w)
            tile = np.frombuffer(raw, dtype=np.uint8).reshape(th, tw)
            y0 = (ty - r0) * tile_h
            x0 = (tx - c0) * tile_w
            window[y0 : y0 + th, x0 : x0 + tw] = tile

    local_r = row_i - r0 * tile_h
    local_c = col_i - c0 * tile_w
    out = window[local_r[:, None], local_c[None, :]]

    typ, count, val = tags[320]
    cmap = np.frombuffer(_tag_bytes(data, typ, count, val), dtype="<u2").reshape(3, 256)
    rgb8 = (cmap >> 8).astype(np.uint8)
    info = {
        "source_size": [width, height],
        "pixel_deg": scale_x,
        "origin_lon": origin_lon,
        "origin_lat": origin_lat,
        "palette_rgb": {str(k): rgb8[:, k].tolist() for k in CLASS_NAMES if k != 0},
    }
    return out, info


def colorize(index: np.ndarray, palette_rgb: dict) -> np.ndarray:
    rgb = np.zeros(index.shape + (3,), dtype=np.uint8)
    rgb[:] = (18, 28, 38)  # nodata / outside class: dark water
    for key, color in palette_rgb.items():
        rgb[index == int(key)] = color
    return rgb


def main() -> None:
    georef = json.loads(META_JSON.read_text(encoding="utf-8"))["georef"]
    tile = download_tile()
    index, info = crop_worldcover(tile, georef)
    rgb = colorize(index, info["palette_rgb"])
    Image.fromarray(rgb, mode="RGB").save(OUT_COLOR)
    Image.fromarray(index, mode="L").save(OUT_INDEX)

    counts = {int(v): int(n) for v, n in zip(*np.unique(index, return_counts=True))}
    classes = []
    for code, (name, name_zh) in CLASS_NAMES.items():
        if code == 0:
            continue
        classes.append(
            {
                "code": code,
                "name": name,
                "name_zh": name_zh,
                "rgb": info["palette_rgb"].get(str(code)),
                "pixels": counts.get(code, 0),
            }
        )
    doc = {
        "name": "Moorea land cover",
        "name_zh": "莫雷阿岛土地覆盖",
        "source": "ESA WorldCover 10 m 2021 v200",
        "source_url": TILE_URL,
        "tile": "S18W150",
        "citation": "Zanaga, D., Van De Kerchove, R., et al. 2022. ESA WorldCover 10 m 2021 v200. https://doi.org/10.5281/zenodo.7254221",
        "attribution": "© ESA WorldCover project / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium",
        "license": "CC BY 4.0",
        "aligned_to": "moorea_heightmap.json georef",
        "width": int(index.shape[1]),
        "height": int(index.shape[0]),
        "row0": "north",
        "index_encoding": "uint8 class code; 0 = nodata",
        "georef": georef,
        "classes": classes,
        "files": {"color": OUT_COLOR.name, "index": OUT_INDEX.name},
        "usage": "Overlay moorea_landuse.png on moorea_preview.png. Index PNG is the class codes.",
    }
    OUT_JSON.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    labeled = int(index.size - counts.get(0, 0))
    print(f"wrote {OUT_COLOR.name} labeled {100.0 * labeled / index.size:.1f}%")
    for item in classes:
        if item["pixels"]:
            print(f"  {item['code']:3d} {item['name']:20} {item['name_zh']:8} {item['pixels']}")


if __name__ == "__main__":
    main()
