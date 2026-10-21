#ifndef OIL_RAIN_RIPPLE_FLIPBOOK_INCLUDED
#define OIL_RAIN_RIPPLE_FLIPBOOK_INCLUDED

// =============================================================================
// OilRainRippleFlipbook.hlsl
// Rain-ripple normals as a baked flipbook atlas, blended into the water normal.
//
// Why a flipbook instead of spawning ripple objects or a screen-space blit:
//   - zero CPU objects, zero draw calls (one extra texture sample in a shader
//     that is already running),
//   - it perturbs the NORMAL, so ripples correctly affect lighting, specular and
//     refraction offset, which a screen-space distortion pass cannot do,
//   - RG channels carry the normal, so ocean swell / rain ripple / flow can share
//     one fetch (RGBA: R,G = rain ripple normal, B,A = free for flow normals).
//
// Include this AFTER Common.hlsl. Then call ApplyRainRippleNormal() at the point
// where the shading normal is finalised, BEFORE lighting/refraction consume it.
// =============================================================================

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

// Set globally once per frame by OilRainFlipbookDriver (never per material).
TEXTURE2D(_OilRainRippleAtlas);
SAMPLER(sampler_OilRainRippleAtlas);

float4 _OilRainRippleParams;  // x = atlas cell scale (world units per tile)
                               // y = animation speed (frames/sec)
                               // z = normal strength
                               // w = frame count
float2 _OilRainRippleGrid;    // x = columns, y = rows
float2 _OilRainRippleTexel;   // x = 1/atlasWidth, y = 1/atlasHeight
float  _OilRainRippleEnabled;

// -----------------------------------------------------------------------------
// Sample one flipbook frame at a world-space location.
// Cell-local UV is inset by half a texel so neighbouring frames never bleed in.
// -----------------------------------------------------------------------------
float2 OilRainRippleFrameUV(float2 worldXZ, float time, float scale, float speed, float2 scroll)
{
    float frames = max(_OilRainRippleParams.w, 1.0);
    float frame  = floor(frac(time * speed) * frames);

    float2 cell = float2(_OilRainRippleGrid.x, _OilRainRippleGrid.y);
    float2 framePos = float2(frac(frame / cell.x), floor(frame / cell.x));

    // Half-texel inset keeps the bilinear tap inside the current frame.
    float2 inset = _OilRainRippleTexel * 0.5;
    float2 cellUV = (float2(1.0, 1.0) - inset * 2.0 * cell) / cell;

    float2 local = frac((worldXZ + scroll) * (_OilRainRippleParams.x * scale));
    return framePos + inset + local * cellUV;
}

float3 OilRainRippleNormalLayer(float2 worldXZ, float time, float scale, float speed, float2 scroll)
{
    if (_OilRainRippleEnabled < 0.5) return float3(0.0, 1.0, 0.0);

    float2 uv = OilRainRippleFrameUV(worldXZ, time, scale, speed, scroll);
    // RG = normal XY. Reconstruct an up-facing tangent normal.
    float2 n = SAMPLE_TEXTURE2D(_OilRainRippleAtlas, sampler_OilRainRippleAtlas, uv).rg * 2.0 - 1.0;
    return float3(n.x, 1.0, n.y);
}

// -----------------------------------------------------------------------------
// Perturb a (near-horizontal) shading normal by the rain-ripple flipbook.
// Two layers at non-harmonic scale/speed so the repeat period is long enough
// to be invisible. Mobile drops to a single layer.
// -----------------------------------------------------------------------------
half3 ApplyRainRippleNormal(half3 normalWS, float3 positionWS)
{
    if (_OilRainRippleEnabled < 0.5) return normalWS;

    float time = _Time.y;
    float2 xz = positionWS.xz;

    float2 n0 = OilRainRippleNormalLayer(xz, time, 1.0, 1.0, float2(0.0, 0.0)).xz;
    float2 n1 = OilRainRippleNormalLayer(xz, time, 0.37, 0.63, float2(31.7, 11.3)).xz;

    float strength = _OilRainRippleParams.z;
    float2 offset = n0 * strength;
#if defined(OIL_MOBILE_PERF)
    offset += n1 * strength * 0.5;
#else
    offset += n1 * strength * 0.7;
#endif

    // Water is near-horizontal, so perturbing XZ in place is both correct and
    // tangent-frame free.
    normalWS.xz += offset;
    return normalize(normalWS);
}

#endif // OIL_RAIN_RIPPLE_FLIPBOOK_INCLUDED