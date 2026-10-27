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

// Canvas tooth. One sine, no texture.
half3 OilWeaveTint(half3 color, float2 p)
{
    half w = (half)sin(p.x * 1.7 + p.y * 0.8) * 0.5h + 0.5h;
    return color * lerp(0.94h, 1.04h, w);
}

// Hundreds of meters. Peaks drift warm, hollows drift cool, without replacing the splat hue.
half3 OilClimateTint(half3 albedo, float3 positionWS)
{
    float climate = OilFbm(positionWS.xz * 0.002);
    half3 warm = albedo * half3(1.14h, 1.04h, 0.76h);
    half3 cool = albedo * half3(0.78h, 0.98h, 0.86h);
    return lerp(albedo, lerp(cool, warm, (half)climate), 0.2h);
}

#endif
