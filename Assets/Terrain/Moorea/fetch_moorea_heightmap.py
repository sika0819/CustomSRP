#!/usr/bin/env python3
"""Fetch real 1:1 Moorea (French Polynesia) terrain from NASA SRTM 1″ + OSM reference layers.

Outputs (for Unity Create Moorea Terrain Scene):
  moorea_heightmap.{raw,png,json,preview}
  moorea_osm_reference.png  — roads (red), waterways (blue), waterfalls (cyan)
  moorea_osm_features.geojson — same vectors for GIS / QGIS

Requires: numpy, pillow, scipy; network for SRTM + Overpass (run once).
"""
from __future__ import annotations

import gzip
import json
import math
import urllib.parse
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

ROOT = Path(__file__).resolve().parent
TILE_URL = "https://elevation-tiles-prod.s3.amazonaws.com/skadi/S18/S18W150.hgt.gz"
TILE_GZ = ROOT / "S18W150.hgt.gz"
OUT_RAW = ROOT / "moorea_heightmap.raw"
OUT_PNG = ROOT / "moorea_heightmap.png"
OUT_JSON = ROOT / "moorea_heightmap.json"
OUT_PREV = ROOT / "moorea_preview.png"
OUT_OSM_PNG = ROOT / "moorea_osm_reference.png"
OUT_OSM_GEO = ROOT / "moorea_osm_features.geojson"

RES = 2049
# Full island + lagoon margin (~16.5 km). 2049² → ~8 m/px at 1:1.
CROP_SIDE_M = 16500.0
# Geographic center of Moorea (ring road / dual bays).
CENTER_LAT = -17.538
CENTER_LON = -149.830
SEA_LEVEL_M = 1.0
USER_AGENT = "CustomSRP-TerrainFetch/1.0 (Moorea 1:1 demo)"


def download_tile() -> None:
    if TILE_GZ.exists() and TILE_GZ.stat().st_size > 1_000_000:
        return
    print("downloading", TILE_URL)
    req = urllib.request.Request(TILE_URL, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=600) as resp:
        TILE_GZ.write_bytes(resp.read())


def load_hgt() -> tuple[np.ndarray, float, float]:
    raw = gzip.decompress(TILE_GZ.read_bytes())
    side = int(math.sqrt(len(raw) // 2))
    if side * side * 2 != len(raw):
        raise SystemExit(f"unexpected HGT size {len(raw)}")
    elev = np.frombuffer(raw, dtype=">i2").reshape(side, side).astype(np.float32)
    elev[elev < -500] = np.nan
    # Tile S18W150: row 0 = 17°S, row increases south; col 0 = 150°W, col increases east.
    lat_north = -17.0
    lon_west = -150.0
    return elev, lat_north, lon_west


def pixel_to_ll(row: float, col: float, side: int, lat_north: float, lon_west: float) -> tuple[float, float]:
    lat = lat_north - (row / (side - 1))
    lon = lon_west + (col / (side - 1))
    return lat, lon


def ll_to_pixel(lat: float, lon: float, side: int, lat_north: float, lon_west: float) -> tuple[float, float]:
    row = (lat_north - lat) * (side - 1)
    col = (lon - lon_west) * (side - 1)
    return row, col


def meters_per_degree(lat: float) -> tuple[float, float]:
    m_per_deg_lat = 111_132.92 - 559.82 * math.cos(2 * math.radians(lat)) + 1.175 * math.cos(
        4 * math.radians(lat)
    )
    m_per_deg_lon = 111_412.84 * math.cos(math.radians(lat)) - 93.5 * math.cos(3 * math.radians(lat))
    return m_per_deg_lat, m_per_deg_lon


def sample_elev_bilinear(elev: np.ndarray, row: float, col: float) -> float:
    side = elev.shape[0]
    if row < 0 or col < 0 or row > side - 1 or col > side - 1:
        return 0.0
    r0 = int(math.floor(row))
    c0 = int(math.floor(col))
    r1 = min(r0 + 1, side - 1)
    c1 = min(c0 + 1, side - 1)
    fr = row - r0
    fc = col - c0
    v00 = elev[r0, c0]
    v01 = elev[r0, c1]
    v10 = elev[r1, c0]
    v11 = elev[r1, c1]
    if any(math.isnan(x) for x in (v00, v01, v10, v11)):
        vals = [x for x in (v00, v01, v10, v11) if not math.isnan(x)]
        return float(np.mean(vals)) if vals else 0.0
    return float(
        v00 * (1 - fr) * (1 - fc)
        + v01 * (1 - fr) * fc
        + v10 * fr * (1 - fc)
        + v11 * fr * fc
    )


def build_height_grid(
    elev: np.ndarray, lat_north: float, lon_west: float
) -> tuple[np.ndarray, dict]:
    side = elev.shape[0]
    m_lat, m_lon = meters_per_degree(CENTER_LAT)
    m_per_px_lat = m_lat / (side - 1)
    m_per_px_lon = m_lon / (side - 1)

    center_row, center_col = ll_to_pixel(CENTER_LAT, CENTER_LON, side, lat_north, lon_west)
    half_px_lat = (CROP_SIDE_M * 0.5) / m_per_px_lat
    half_px_lon = (CROP_SIDE_M * 0.5) / m_per_px_lon

    row0 = center_row - half_px_lat
    row1 = center_row + half_px_lat
    col0 = center_col - half_px_lon
    col1 = center_col + half_px_lon

    lat_south, lon_west_crop = pixel_to_ll(row1, col0, side, lat_north, lon_west)
    lat_north_crop, lon_east_crop = pixel_to_ll(row0, col1, side, lat_north, lon_west)

    # Vectorized bilinear sample (row0 of output = north).
    ts = np.linspace(0.0, 1.0, RES, dtype=np.float64)
    lats = lat_north_crop + (lat_south - lat_north_crop) * ts[:, None]
    lons = lon_west_crop + (lon_east_crop - lon_west_crop) * ts[None, :]
    rows = (lat_north - lats) * (side - 1)
    cols = (lons - lon_west) * (side - 1)
    rows, cols = np.broadcast_arrays(rows, cols)
    elev_fill = np.nan_to_num(elev, nan=0.0)
    out = ndimage.map_coordinates(
        elev_fill, [rows, cols], order=1, mode="nearest"
    ).astype(np.float32)
    out[out < SEA_LEVEL_M] = 0.0
    out = clean_shoreline(out)

    land = out >= SEA_LEVEL_M
    if land.sum() < 100:
        raise SystemExit("crop has almost no land — check CENTER_LAT/LON")

    ys, xs = np.where(land)
    meta = {
        "crop_side_m": CROP_SIDE_M,
        "center_lat": CENTER_LAT,
        "center_lon": CENTER_LON,
        "lat_north": lat_north_crop,
        "lat_south": lat_south,
        "lon_west": lon_west_crop,
        "lon_east": lon_east_crop,
        "meters_per_pixel": {
            "east_west": CROP_SIDE_M / (RES - 1),
            "north_south": CROP_SIDE_M / (RES - 1),
        },
        "island_bbox_px": {
            "y0": int(ys.min()),
            "y1": int(ys.max()),
            "x0": int(xs.min()),
            "x1": int(xs.max()),
        },
    }
    return out, meta


def _shore_noise(shape: tuple[int, int]) -> np.ndarray:
    """Low-frequency field for scalloped coasts and swash streaks (≈ tile-safe)."""
    h, w = shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    n = (
        np.sin(xx * 0.012 + yy * 0.009) * 0.45
        + np.sin(xx * 0.031 - yy * 0.027 + 1.4) * 0.30
        + np.sin((xx * 0.7 + yy) * 0.019 + 2.2) * 0.25
    ).astype(np.float32)
    return n


def clean_shoreline(height_m: np.ndarray) -> np.ndarray:
    """Natural wave-washed coast: flat seabed, scalloped edge, soft beach, swash grooves.

    Unity Terrain triangulates hard land|sea steps into diagonal spikes. Keep the
    main island, open thin fingers, then carve a longer soft beach with noise
    fringe and shore-normal wash marks so the mesh edge sits below sea level.
    """
    land = height_m >= SEA_LEVEL_M
    land = ndimage.binary_opening(land, structure=np.ones((5, 5), dtype=bool), iterations=1)
    land = ndimage.binary_closing(land, structure=np.ones((5, 5), dtype=bool), iterations=2)

    labeled, n = ndimage.label(land)
    if n == 0:
        return height_m
    counts = np.bincount(labeled.ravel())
    counts[0] = 0
    keep = int(np.argmax(counts))
    land = labeled == keep

    # Outer ring forced flat (below Ocean y ≈ 0.35 m).
    flat_band = 2
    land_core = ndimage.binary_erosion(
        land, structure=np.ones((3, 3), dtype=bool), iterations=flat_band
    )
    dist = ndimage.distance_transform_edt(land_core).astype(np.float32)
    noise = _shore_noise(height_m.shape)

    # Scalloped shoreline: noise widens/narrows the beach ramp (~±2 samples).
    ramp_samples = np.clip(9.0 + noise * 2.5, 6.0, 13.0)
    # Extra wash: pull the first samples farther down (concave beach).
    t = np.clip(dist / ramp_samples, 0.0, 1.0)
    # Bias toward a long toe then a firmer berm (wave-worn profile).
    ramp = t * t * (3.0 - 2.0 * t)
    ramp = np.power(np.clip(ramp, 0.0, 1.0), 1.35)

    out = np.zeros_like(height_m)
    beach = land_core & (dist < 14.0)
    carved = height_m * ramp

    # Swash grooves: shallow shore-normal undulations only on the wet sand band.
    gy, gx = np.gradient(dist)
    gnorm = np.sqrt(gx * gx + gy * gy) + 1e-5
    # Shore-parallel coordinate from a rotated noise of position.
    h, w = height_m.shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    along = (-gy * xx + gx * yy) / gnorm
    groove = np.sin(along * 0.55 + noise * 2.5) * 0.5 + 0.5
    groove *= np.sin(dist * 0.9 + 0.4) * 0.5 + 0.5
    wash = np.clip((6.5 - dist) / 6.5, 0.0, 1.0)
    wash = wash * wash * (beach.astype(np.float32))
    carved = carved * (1.0 - wash * groove * 0.22)

    # Mild berm: a slight shelf mid-beach, then runnel before the dune toe.
    berm = np.exp(-((dist - 5.5) ** 2) / (2 * 1.6**2)) * beach.astype(np.float32)
    runnel = np.exp(-((dist - 3.0) ** 2) / (2 * 1.1**2)) * beach.astype(np.float32)
    carved = carved * (1.0 + berm * 0.08 - runnel * 0.12)

    out[land_core] = carved[land_core]
    # Soft blur only in the beach band so interior peaks stay sharp.
    blurred = ndimage.gaussian_filter(out, sigma=0.85)
    blend = np.clip((10.0 - dist) / 10.0, 0.0, 1.0) * land_core.astype(np.float32)
    out = out * (1.0 - blend * 0.65) + blurred * (blend * 0.65)
    out[~land_core] = 0.0
    out[out < SEA_LEVEL_M] = 0.0
    return out.astype(np.float32)


def encode_uint16(height_m: np.ndarray, peak_m: float) -> np.ndarray:
    peak = max(peak_m, 1.0)
    norm = np.clip(height_m / peak, 0.0, 1.0)
    return (norm * 65535.0 + 0.5).astype(np.uint16)


def fetch_osm_geojson(lat_north: float, lat_south: float, lon_west: float, lon_east: float) -> dict:
    pad = 0.015
    s, w, n, e = lat_south - pad, lon_west - pad, lat_north + pad, lon_east + pad
    query = f"""
    [out:json][timeout:120];
    (
      way["highway"]({s},{w},{n},{e});
      way["waterway"]({s},{w},{n},{e});
      node["natural"="waterfall"]({s},{w},{n},{e});
      node["waterway"="waterfall"]({s},{w},{n},{e});
    );
    out geom;
    """
    req = urllib.request.Request(
        "https://overpass-api.de/api/interpreter",
        data=urllib.parse.urlencode({"data": query}).encode(),
        headers={"User-Agent": USER_AGENT},
    )
    with urllib.request.urlopen(req, timeout=180) as resp:
        return json.loads(resp.read().decode())


def osm_to_geojson(overpass: dict) -> dict:
    features = []
    for el in overpass.get("elements", []):
        tags = el.get("tags") or {}
        if el["type"] == "node":
            lat, lon = el["lat"], el["lon"]
            kind = "waterfall"
            props = {**tags, "kind": kind}
            geom = {"type": "Point", "coordinates": [lon, lat]}
            features.append({"type": "Feature", "geometry": geom, "properties": props})
        elif el["type"] == "way" and "geometry" in el:
            coords = [[p["lon"], p["lat"]] for p in el["geometry"]]
            if len(coords) < 2:
                continue
            if "highway" in tags:
                kind = "road"
            elif "waterway" in tags:
                kind = "waterway"
            else:
                continue
            props = {**tags, "kind": kind}
            features.append(
                {
                    "type": "Feature",
                    "geometry": {"type": "LineString", "coordinates": coords},
                    "properties": props,
                }
            )
    return {"type": "FeatureCollection", "features": features}


def ll_to_grid(lat: float, lon: float, meta: dict) -> tuple[float, float]:
    lat_n, lat_s = meta["lat_north"], meta["lat_south"]
    lon_w, lon_e = meta["lon_west"], meta["lon_east"]
    u = (lon - lon_w) / max(lon_e - lon_w, 1e-9)
    t = (lat_n - lat) / max(lat_n - lat_s, 1e-9)
    x = u * (RES - 1)
    y = t * (RES - 1)
    return x, y


def rasterize_osm(geo: dict, meta: dict) -> None:
    img = Image.new("RGB", (RES, RES), (24, 28, 32))
    draw = ImageDraw.Draw(img)
    # faint land hint from heightmap if present
    if OUT_PNG.exists():
        hm = np.array(Image.open(OUT_PNG), dtype=np.uint16)
        land = (hm > 0).astype(np.uint8) * 40
        base = np.stack([land + 20, land + 24, land + 28], axis=-1).astype(np.uint8)
        img = Image.fromarray(base, mode="RGB")
        draw = ImageDraw.Draw(img)

    for feat in geo.get("features", []):
        props = feat.get("properties") or {}
        kind = props.get("kind")
        geom = feat["geometry"]
        if geom["type"] == "LineString":
            pts = [ll_to_grid(c[1], c[0], meta) for c in geom["coordinates"]]
            xy = [(p[0], p[1]) for p in pts]
            if kind == "road":
                draw.line(xy, fill=(220, 70, 60), width=2)
            elif kind == "waterway":
                draw.line(xy, fill=(70, 140, 230), width=2)
        elif geom["type"] == "Point":
            lon, lat = geom["coordinates"]
            x, y = ll_to_grid(lat, lon, meta)
            r = 5
            draw.ellipse((x - r, y - r, x + r, y + r), fill=(80, 230, 220), outline=(20, 60, 60))

    img.save(OUT_OSM_PNG)
    print("wrote", OUT_OSM_PNG.name)


def island_size_m(meta: dict, height_m: np.ndarray) -> tuple[float, float]:
    land = height_m >= SEA_LEVEL_M
    ys, xs = np.where(land)
    mpp = meta["meters_per_pixel"]["east_west"]
    ew = (xs.max() - xs.min()) * mpp
    ns = (ys.max() - ys.min()) * mpp
    return float(ns), float(ew)


def main() -> None:
    download_tile()
    elev, lat_north, lon_west = load_hgt()
    height_m, meta = build_height_grid(elev, lat_north, lon_west)
    land = height_m >= SEA_LEVEL_M
    peak = float(np.max(height_m[land]))
    surveyed = 1207.0  # Mont Tohiea / Rotui area
    encode_peak = max(peak, surveyed * 0.95)
    out16 = encode_uint16(height_m, encode_peak)
    out16[~land] = 0

    OUT_PNG.write_bytes(b"")  # touch for rasterize order
    Image.fromarray(out16).save(OUT_PNG)
    OUT_RAW.write_bytes(out16.astype("<u2").tobytes())

    prev8 = (out16.astype(np.float32) / 65535 * 255).astype(np.uint8)
    Image.fromarray(prev8, mode="L").resize((1024, 1024), Image.Resampling.LANCZOS).save(OUT_PREV)

    ns_m, ew_m = island_size_m(meta, height_m)
    doc = {
        "name": "Moorea",
        "name_zh": "莫雷阿岛",
        "country": "French Polynesia",
        "source": "NASA SRTM 1 arc-second",
        "source_url": TILE_URL,
        "tile": "S18W150",
        "width": RES,
        "height": RES,
        "row0": "north",
        "meters_per_pixel": meta["meters_per_pixel"],
        "elevation_min_m": 0,
        "elevation_max_m": round(encode_peak, 1),
        "surveyed_peak_m": surveyed,
        "encoding": f"uint16, 0 = 0 m, 65535 = {encode_peak:.1f} m, linear",
        "raw_byte_order": "little-endian",
        "raw_layout": "row-major, no header, first row is north",
        "unity_terrain": {
            "resolution": f"{RES} x {RES}",
            "terrain_height_m": round(encode_peak, 1),
            "byte_order": "Windows (little-endian)",
            "flip": "first row is north (row0); Import Raw matches CreateMooreaTerrainScene loader",
            "recommended_terrain_size_m": {
                "x": CROP_SIDE_M,
                "z": CROP_SIDE_M,
                "y": round(encode_peak, 1),
            },
            "scale_note": "1:1 meters — 7.2 km crop centered on Opunohu / Afareaitu (ring road + valleys)",
        },
        "georef": {
            "center_lat": CENTER_LAT,
            "center_lon": CENTER_LON,
            "lat_north": meta["lat_north"],
            "lat_south": meta["lat_south"],
            "lon_west": meta["lon_west"],
            "lon_east": meta["lon_east"],
        },
        "osm_reference": {
            "png": OUT_OSM_PNG.name,
            "geojson": OUT_OSM_GEO.name,
            "attribution": "© OpenStreetMap contributors (ODbL)",
            "usage": "Overlay in image editor on moorea_preview.png / heightmap for road & river paint",
        },
        "note": "Real SRTM + OSM vectors; splat roads manually using moorea_osm_reference.png.",
        "files": {
            "heightmap_raw": OUT_RAW.name,
            "heightmap_png": OUT_PNG.name,
            "preview": OUT_PREV.name,
        },
        "island_bbox_px": meta["island_bbox_px"],
        "island_size_m": {"north_south": round(ns_m, 1), "east_west": round(ew_m, 1)},
    }
    OUT_JSON.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    print("wrote heightmap peak", encode_peak, "land px", land.sum())

    print("fetching OSM (may take ~30s)...")
    try:
        overpass = fetch_osm_geojson(
            meta["lat_north"], meta["lat_south"], meta["lon_west"], meta["lon_east"]
        )
        geo = osm_to_geojson(overpass)
        OUT_OSM_GEO.write_text(json.dumps(geo, indent=2) + "\n", encoding="utf-8")
    except Exception as exc:
        print("OSM fetch failed, reusing geojson if present:", exc)
        if not OUT_OSM_GEO.exists():
            raise
        geo = json.loads(OUT_OSM_GEO.read_text(encoding="utf-8"))
    rasterize_osm(geo, meta)
    n_road = sum(1 for f in geo["features"] if f["properties"].get("kind") == "road")
    n_w = sum(1 for f in geo["features"] if f["properties"].get("kind") == "waterway")
    n_wf = sum(1 for f in geo["features"] if f["properties"].get("kind") == "waterfall")
    print(f"OSM in crop: roads={n_road} waterways={n_w} waterfalls={n_wf}")
    land_pct = 100.0 * float(land.mean())
    print(f"land coverage in crop: {land_pct:.1f}% (want ~25–50% with ocean margin)")


if __name__ == "__main__":
    main()
