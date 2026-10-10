#!/usr/bin/env python3
"""Normal and mask maps for the Moorea terrain layers.

Height is the seamless high-pass of each splat's luminance, so brush dabs
become relief. Mask packing matches Unity TerrainLayer:

    R metallic, G ambient occlusion, B height, A smoothness
"""
from __future__ import annotations

import uuid
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

ROOT = Path(__file__).resolve().parent

# fine/coarse: band-pass radii in pixels (dab body, not the hairline edge).
# strength: gradient scale before normalize.
LAYERS = {
    "Sand": dict(
        albedo="SplatSand.png",
        layer="LayerSand.terrainlayer",
        fine=5.5,
        coarse=36.0,
        strength=6.5,
        smooth=0.34,
        smooth_var=0.05,
        ao_floor=0.88,
        metallic=0.0,
    ),
    "Grass": dict(
        albedo="SplatGrass.png",
        layer="LayerGrass.terrainlayer",
        fine=4.2,
        coarse=28.0,
        strength=8.5,
        smooth=0.24,
        smooth_var=0.045,
        ao_floor=0.74,
        metallic=0.0,
    ),
    "Forest": dict(
        albedo="SplatForest.png",
        layer="LayerForest.terrainlayer",
        fine=3.6,
        coarse=22.0,
        strength=9.5,
        smooth=0.18,
        smooth_var=0.04,
        ao_floor=0.58,
        metallic=0.0,
    ),
    "Rock": dict(
        albedo="SplatRock.png",
        layer="LayerRock.terrainlayer",
        fine=5.0,
        coarse=34.0,
        strength=12.0,
        smooth=0.16,
        smooth_var=0.045,
        ao_floor=0.40,
        metallic=0.0,
    ),
    "Built": dict(
        albedo="SplatBuilt.png",
        layer="LayerBuilt.terrainlayer",
        fine=3.4,
        coarse=20.0,
        strength=9.0,
        smooth=0.22,
        smooth_var=0.04,
        ao_floor=0.66,
        metallic=0.0,
    ),
}


def srgb_to_linear(c: np.ndarray) -> np.ndarray:
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def luminance(rgb: np.ndarray) -> np.ndarray:
    lin = srgb_to_linear(rgb)
    return 0.2126 * lin[..., 0] + 0.7152 * lin[..., 1] + 0.0722 * lin[..., 2]


def relief(y: np.ndarray, fine: float, coarse: float) -> np.ndarray:
    """Dab-sized mounds. A wide blur is removed so the tile has no global slope."""
    band = gaussian_filter(y, fine, mode="wrap") - gaussian_filter(y, coarse, mode="wrap")
    scale = float(np.percentile(np.abs(band), 98)) + 1e-6
    return np.clip(band / scale, -1.0, 1.0)


def normals_from_height(h: np.ndarray, strength: float) -> np.ndarray:
    """OpenGL tangent space. Image row 0 is +V (top of the PNG)."""
    dh_du = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5
    dh_dv = (np.roll(h, 1, axis=0) - np.roll(h, -1, axis=0)) * 0.5
    n = np.stack((-dh_du * strength, -dh_dv * strength, np.ones_like(h)), axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True) + 1e-8
    return np.clip(n * 0.5 + 0.5, 0.0, 1.0)


def mask_from_height(h: np.ndarray, spec: dict) -> np.ndarray:
    height = np.clip(h * 0.5 + 0.5, 0.0, 1.0)
    # Low paint sits in the crevice. Floor keeps sand open and rock pockets dark.
    ao = spec["ao_floor"] + (1.0 - spec["ao_floor"]) * height
    smooth = np.clip(spec["smooth"] + h * spec["smooth_var"], 0.02, 0.95)
    metal = np.full_like(h, spec["metallic"])
    return np.stack((metal, ao, height, smooth), axis=-1)


def wrap_ratio(img: np.ndarray) -> tuple[float, float]:
    """1 means the tile edge steps like an interior pixel. 0 is a welded edge."""
    a = img.astype(np.float32)
    step_h = np.abs(a[:, 1:] - a[:, :-1]).mean()
    step_v = np.abs(a[1:] - a[:-1]).mean()
    wrap_h = np.abs(a[:, 0] - a[:, -1]).mean()
    wrap_v = np.abs(a[0] - a[-1]).mean()
    return float(wrap_h / (step_h + 1e-8)), float(wrap_v / (step_v + 1e-8))


def u8(img: np.ndarray) -> np.ndarray:
    return (np.clip(img, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)


def write_meta(path: Path, guid: str, normal: bool) -> None:
    srgb = 0
    texture_type = 1 if normal else 0
    aniso = 2 if normal else 4
    path.write_text(
        f"""fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: {srgb}
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: {aniso}
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 1
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
  spriteGeometrySubdivision: -1
  textureType: {texture_type}
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 1024
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: Standalone
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: Android
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: iOS
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData: 
    physicsShape: []
    bones: []
    spriteID: 
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName: 
  pSDRemoveMatte: 0
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""
    )


def existing_guid(meta: Path) -> str | None:
    if not meta.exists():
        return None
    for line in meta.read_text().splitlines():
        if line.startswith("guid: "):
            return line.split("guid: ", 1)[1].strip()
    return None


def assign_layer(layer_path: Path, normal_guid: str, mask_guid: str) -> None:
    text = layer_path.read_text()
    text = text.replace(
        "m_NormalMapTexture: {fileID: 0}",
        f"m_NormalMapTexture: {{fileID: 2800000, guid: {normal_guid}, type: 3}}",
    )
    text = text.replace(
        "m_MaskMapTexture: {fileID: 0}",
        f"m_MaskMapTexture: {{fileID: 2800000, guid: {mask_guid}, type: 3}}",
    )
    layer_path.write_text(text)


def main() -> None:
    for name, spec in LAYERS.items():
        rgb = np.asarray(Image.open(ROOT / spec["albedo"]).convert("RGB"), np.float32) / 255.0
        h = relief(luminance(rgb), spec["fine"], spec["coarse"])
        normal = normals_from_height(h, spec["strength"])
        mask = mask_from_height(h, spec)

        normal_name = f"Splat{name}Normal.png"
        mask_name = f"Splat{name}Mask.png"
        normal_path = ROOT / normal_name
        mask_path = ROOT / mask_name
        Image.fromarray(u8(normal), mode="RGB").save(normal_path)
        Image.fromarray(u8(mask), mode="RGBA").save(mask_path)

        normal_meta = normal_path.with_suffix(".png.meta")
        mask_meta = mask_path.with_suffix(".png.meta")
        normal_guid = existing_guid(normal_meta) or uuid.uuid4().hex
        mask_guid = existing_guid(mask_meta) or uuid.uuid4().hex
        if not normal_meta.exists():
            write_meta(normal_meta, normal_guid, normal=True)
        if not mask_meta.exists():
            write_meta(mask_meta, mask_guid, normal=False)
        assign_layer(ROOT / spec["layer"], normal_guid, mask_guid)

        n = normal * 2.0 - 1.0
        nz = float(n[..., 2].mean())
        nseam = wrap_ratio(u8(normal))
        mseam = wrap_ratio(u8(mask))
        print(
            name,
            "nz",
            round(nz, 3),
            "smooth",
            round(float(mask[..., 3].mean()), 3),
            "ao",
            round(float(mask[..., 1].mean()), 3),
            "height",
            round(float(mask[..., 2].mean()), 3),
            "normal wrap",
            round(nseam[0], 2),
            round(nseam[1], 2),
            "mask wrap",
            round(mseam[0], 2),
            round(mseam[1], 2),
        )


if __name__ == "__main__":
    main()
