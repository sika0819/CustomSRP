#!/usr/bin/env python3
"""Normal and mask maps for the Island terrain layers.

Height is a seamless band-pass of each splat's luminance: the dab body plus
a little of the bristle, so oil strokes stay relief instead of a flat tint.
Mask packing matches Unity TerrainLayer:

    R metallic, G ambient occlusion, B height, A smoothness
"""
from __future__ import annotations

import importlib.util
import re
import uuid
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

ROOT = Path(__file__).resolve().parent
TEX = ROOT / "Textures"
LAYERS = ROOT / "Layers"

# fine/coarse: dab body in pixels. bristle: hairline stroke that the body blur drops.
SPECS = (
    dict(
        name="Sand",
        albedo="Sand.png",
        layer="SWS_OceanFloorSmooth.terrainlayer",
        fine=2.2,
        coarse=14.0,
        bristle=1.1,
        bristle_gain=0.2,
        strength=3.2,
        smooth=0.16,
        smooth_var=0.03,
        ao_floor=0.92,
    ),
    dict(
        name="Grass",
        albedo="Grass.png",
        layer="Grass.terrainlayer",
        fine=3.6,
        coarse=26.0,
        bristle=1.15,
        bristle_gain=0.42,
        strength=8.0,
        smooth=0.10,
        smooth_var=0.03,
        ao_floor=0.84,
    ),
    dict(
        name="Path",
        albedo="Path.png",
        layer="Path.terrainlayer",
        fine=4.6,
        coarse=32.0,
        bristle=1.2,
        bristle_gain=0.38,
        strength=7.0,
        smooth=0.20,
        smooth_var=0.04,
        ao_floor=0.88,
    ),
    dict(
        name="Rock",
        albedo="Rock.png",
        layer="SWS_DemoRock.terrainlayer",
        fine=4.2,
        coarse=30.0,
        bristle=1.15,
        bristle_gain=0.34,
        strength=11.0,
        smooth=0.08,
        smooth_var=0.03,
        ao_floor=0.78,
    ),
)


def _moorea():
    path = ROOT.parent / "Moorea" / "make_layer_maps.py"
    spec = importlib.util.spec_from_file_location("moorea_layer_maps", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def unit(band: np.ndarray) -> np.ndarray:
    scale = float(np.percentile(np.abs(band), 98)) + 1e-6
    return np.clip(band / scale, -1.0, 1.0)


def relief(y: np.ndarray, spec: dict) -> np.ndarray:
    """Dab body, plus the bristle the body blur would erase. No global slope."""
    body = gaussian_filter(y, spec["fine"], mode="wrap") - gaussian_filter(y, spec["coarse"], mode="wrap")
    hair = y - gaussian_filter(y, spec["bristle"], mode="wrap")
    gain = spec["bristle_gain"]
    mixed = unit(body) * (1.0 - gain) + unit(hair) * gain
    contrast = float(np.percentile(y, 98) - np.percentile(y, 2))
    if contrast < 0.05:
        mixed *= contrast / 0.05
    return unit(mixed)


def mask_from_height(h: np.ndarray, spec: dict) -> np.ndarray:
    height = np.clip(h * 0.5 + 0.5, 0.0, 1.0)
    ao = spec["ao_floor"] + (1.0 - spec["ao_floor"]) * height
    smooth = np.clip(spec["smooth"] + h * spec["smooth_var"], 0.02, 0.95)
    metal = np.zeros_like(h)
    return np.stack((metal, ao, height, smooth), axis=-1)


def assign_layer(layer_path: Path, normal_guid: str, mask_guid: str, smooth: float) -> None:
    text = layer_path.read_text()
    text = re.sub(
        r"m_NormalMapTexture: \{fileID: [^}]+\}",
        f"m_NormalMapTexture: {{fileID: 2800000, guid: {normal_guid}, type: 3}}",
        text,
        count=1,
    )
    text = re.sub(
        r"m_MaskMapTexture: \{fileID: [^}]+\}",
        f"m_MaskMapTexture: {{fileID: 2800000, guid: {mask_guid}, type: 3}}",
        text,
        count=1,
    )
    text = re.sub(
        r"m_MaskMapRemapMax: \{x: [^}]+\}",
        "m_MaskMapRemapMax: {x: 1, y: 1, z: 1, w: 1}",
        text,
        count=1,
    )
    text = re.sub(r"^  m_Smoothness: .+$", f"  m_Smoothness: {smooth}", text, count=1, flags=re.M)
    layer_path.write_text(text)


def main() -> None:
    shared = _moorea()
    for spec in SPECS:
        rgb = np.asarray(Image.open(TEX / spec["albedo"]).convert("RGB"), np.float32) / 255.0
        h = relief(shared.luminance(rgb), spec)
        strength = spec["strength"]
        contrast = float(np.percentile(shared.luminance(rgb), 98) - np.percentile(shared.luminance(rgb), 2))
        if contrast < 0.05:
            strength *= contrast / 0.05
        normal = shared.normals_from_height(h, strength)
        mask = mask_from_height(h, spec)

        normal_path = TEX / f"{spec['name']}_n.png"
        mask_path = TEX / f"{spec['name']}_mask.png"
        Image.fromarray(shared.u8(normal), mode="RGB").save(normal_path)
        Image.fromarray(shared.u8(mask), mode="RGBA").save(mask_path)

        normal_meta = Path(str(normal_path) + ".meta")
        mask_meta = Path(str(mask_path) + ".meta")
        normal_guid = shared.existing_guid(normal_meta) or uuid.uuid4().hex
        mask_guid = shared.existing_guid(mask_meta) or uuid.uuid4().hex
        if not normal_meta.exists():
            shared.write_meta(normal_meta, normal_guid, normal=True)
        if not mask_meta.exists():
            shared.write_meta(mask_meta, mask_guid, normal=False)
        assign_layer(LAYERS / spec["layer"], normal_guid, mask_guid, spec["smooth"])

        n = normal * 2.0 - 1.0
        nseam = shared.wrap_ratio(shared.u8(normal))
        mseam = shared.wrap_ratio(shared.u8(mask))
        print(
            spec["name"],
            "contrast",
            round(contrast, 3),
            "nz",
            round(float(n[..., 2].mean()), 3),
            "ao",
            round(float(mask[..., 1].mean()), 3),
            "height",
            round(float(mask[..., 2].mean()), 3),
            "smooth",
            round(float(mask[..., 3].mean()), 3),
            "normal wrap",
            round(nseam[0], 2),
            round(nseam[1], 2),
            "mask wrap",
            round(mseam[0], 2),
            round(mseam[1], 2),
        )


if __name__ == "__main__":
    main()
