#ifndef CUSTOM_OIL_BRDF_COMMON_INCLUDED
#define CUSTOM_OIL_BRDF_COMMON_INCLUDED

// Shared oil-paint shade / IBL helpers for Lit BRDF and Oil NPR.

// Soft poster bands: keep oil masses, feather edges so shadow steps aren't chalk lines.
half OilQuantizeNdotL(half ndotL, half steps)
{
    steps = max(steps, 2.0h);
    half n = saturate(ndotL) * (steps - 1.0h);
    half i = floor(n);
    half f = frac(n);
    half feather = 0.58h;
    f = smoothstep(0.5h - feather * 0.5h, 0.5h + feather * 0.5h, f);
    return (i + f) / (steps - 1.0h);
}

// Posterize luma into oil color blocks; keep hue.
half3 OilPosterizeLuma(half3 c, half steps)
{
    steps = max(steps, 2.0h);
    half L = max(c.r, max(c.g, c.b));
    half Lq = floor(L * steps + 0.5h) / steps;
    return c * (L > 1e-4h ? (Lq / L) : 1.0h);
}

half OilBrdfShadeSteps()
{
#if defined(OIL_MOBILE_PERF)
    return 3.0h;
#else
    return 4.0h;
#endif
}

half OilBrdfIblSteps()
{
#if defined(OIL_MOBILE_PERF)
    return 3.0h;
#else
    // Chunkier than 5 — SpecCube reads as paint masses, not photo IBL.
    return 4.0h;
#endif
}

half OilBrdfSpecSteps()
{
    return 3.0h;
}

// Soft Blinn-Phong (same curve as OilNPRSpecularTerm) → oil glaze masses.
// Replaces sharp GGX peaks for Lit direct specular.
half OilBrdfOilSpecularTerm(half3 N, half3 H, half smoothness)
{
    half NdotH = saturate(dot(N, H));
    half shininess = exp2(5.0h * saturate(smoothness) + 1.0h);
    return pow(NdotH, shininess);
}

// Threshold + softstep into discrete glint blobs (River/Waterfall oil look).
half OilBrdfStepSpecMass(half spec, half smoothness)
{
    // Rougher → lower threshold (softer, wider paint dabs).
    half thresh = lerp(0.28h, 0.68h, saturate(smoothness));
    half edge = max(0.06h, 1.0h - thresh);
    half stepped = smoothstep(thresh - edge, thresh + edge * 0.25h, spec);
    return lerp(spec, stepped * spec, 0.88h);
}

// Cheap hash for reflection brush warp (no OilPaintNoise dependency).
float OilBrdfHash21(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Extra SpecCube mip so even smooth Lit isn't mirror-sharp.
float OilBrdfEnvMipBias(float perceptualRoughness)
{
    float gloss = saturate(1.0 - perceptualRoughness);
#if defined(OIL_MOBILE_PERF)
    return 1.25 + gloss * 1.0;
#else
    // Glossier → more blur (oil glaze pools), rough stays near physical mip.
    return 1.75 + gloss * 2.0;
#endif
}

// Nudge R with world-space noise so env doesn't track the camera like chrome.
float3 OilBrdfWarpReflectDir(float3 R, float3 positionWS, float perceptualRoughness)
{
    float gloss = saturate(1.0 - perceptualRoughness);
#if defined(OIL_MOBILE_PERF)
    float amt = gloss * 0.06;
#else
    float amt = gloss * 0.12;
#endif
    if (amt < 1e-4)
    {
        return R;
    }

    float2 p = positionWS.xz * 0.28;
    float n0 = OilBrdfHash21(p);
    float n1 = OilBrdfHash21(p + float2(17.3, 3.1));
    float n2 = OilBrdfHash21(positionWS.xy * 0.28 + float2(9.1, 5.7));
    // Quantize the nudge into brush-sized chunks.
    float3 nudge = float3(n0, n1, n2) * 2.0 - 1.0;
    nudge = floor(nudge * 5.0 + 0.5) / 5.0;
    return normalize(R + nudge * amt);
}

// SpecCube → oil color blocks + warm glaze; crush hot HDR into paint.
half3 OilStylizeEnvSpec(half3 env)
{
    half3 blocks = OilPosterizeLuma(env, OilBrdfIblSteps());
    half L = max(blocks.r, max(blocks.g, blocks.b));
    // Warm oil glaze; slight desat so sky/probe doesn't look photographic.
    half3 warm = half3(L, L * 0.96h, L * 0.86h);
    half3 glaze = lerp(blocks, warm, 0.22h);
    half chroma = L - min(blocks.r, min(blocks.g, blocks.b));
    glaze = lerp(glaze, blocks, saturate(chroma * 2.5h) * 0.35h);
    half hot = saturate((L - 0.72h) * 3.2h);
    glaze *= lerp(1.0h, 0.68h, hot);
#if defined(OIL_MOBILE_PERF)
    return lerp(env, glaze, 0.82h);
#else
    return glaze;
#endif
}

#endif
