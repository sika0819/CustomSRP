#ifndef CUSTOM_OIL_PAINT_NOISE_INCLUDED
#define CUSTOM_OIL_PAINT_NOISE_INCLUDED

// Two-octave value noise. Used to drag UVs and tilt paint, not as a normal map.

float OilHash21(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float OilValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = OilHash21(i);
    float b = OilHash21(i + float2(1.0, 0.0));
    float c = OilHash21(i + float2(0.0, 1.0));
    float d = OilHash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float OilFbm(float2 p)
{
    float v = OilValueNoise(p) * 0.65;
    v += OilValueNoise(p * 2.13 + float2(1.7, 9.2)) * 0.35;
    return v;
}

// Screen noise first, then the step. Without the noise the bands are solid stripes.
half3 OilDitherQuantize(half3 color, half dither01, half quantize)
{
    half noise = (dither01 - 0.5h) * (1.0h / 12.0h);
    half3 bands = floor(saturate(color + noise) * 12.0h) / 12.0h;
    return lerp(color, bands, saturate(quantize));
}

// Canvas tooth. One sine, no texture. Value only — keep authored hue.
half3 OilWeaveTint(half3 color, float2 p)
{
    half w = (half)sin(p.x * 1.7 + p.y * 0.8) * 0.5h + 0.5h;
    half weave = (half)sin(p.x * 4.1 - p.y * 3.3) * 0.5h + 0.5h;
    // Deeper tooth so weave reads as raised canvas under paint.
    half tooth = lerp(0.84h, 1.16h, w) * lerp(0.93h, 1.07h, weave);
    return color * tooth;
}

// Moon_Oil sits at about 0.70–0.90 linear. Keep the body under the sun and
// leave its warm bias — a cream wash was reading as gray, not oil yellow.
half3 OilMoonAlbedo(half3 albedo)
{
    half luma = dot(albedo, half3(0.2126h, 0.7152h, 0.0722h));
    half shade = saturate(0.32h + (luma - 0.78h) * 0.50h);
    half3 graded = albedo * (shade * rcp(max(luma, 1e-3h)));
    return graded * half3(1.05h, 0.97h, 0.80h);
}

// Hundreds of meters. Value climate only — no warm/cool palette drift.
half3 OilClimateTint(half3 albedo, float3 positionWS)
{
    float climate = OilFbm(positionWS.xz * 0.002);
    half lift = lerp(0.94h, 1.07h, (half)climate);
    return albedo * lift;
}

#endif
