#ifndef OIL_RAIN_STREAK_QUAD_PASS_INCLUDED
#define OIL_RAIN_STREAK_QUAD_PASS_INCLUDED

// Camera-space rain curtain (ported from Boluo/Mobile/Rain).
// UV scroll + wind shear in the vertex shader; additive streaks, no clip/discard
// (Mali/MT tile GPUs turn discard into gray quads).

CBUFFER_START(UnityPerMaterial)
    half4 _RainColor;
    float _Intensity;
    float _Density;
    float _RainTime;
    float _Wind;
    float _Quality;
    float _Mist;
CBUFFER_END

float OilRainHash21(float2 p)
{
    p = frac(p * float2(123.34, 345.45));
    p += dot(p, p + 34.345);
    return frac(p.x * p.y);
}

float OilRainValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = OilRainHash21(i);
    float b = OilRainHash21(i + float2(1, 0));
    float c = OilRainHash21(i + float2(0, 1));
    float d = OilRainHash21(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float OilRainStreakCell(float2 uv, float2 grid, float seed, float keep, float thickness)
{
    float2 id = floor(uv * grid);
    float2 f = frac(uv * grid);
    float h = OilRainHash21(id + seed);
    float alive = step(h, keep);

    float2 center = float2(lerp(0.28, 0.72, OilRainHash21(id + float2(2.1, seed))), 0.5);
    float across = 1.0 - saturate(abs(f.x - center.x) * thickness);
    across = across * across;
    float along = smoothstep(0.0, 0.12, f.y) * (1.0 - smoothstep(0.78, 1.0, f.y));
    return across * along * alive;
}

float OilRainMistNoise(float2 uv, float t)
{
    float n = OilRainValueNoise(uv * 2.4 + float2(t * 0.05, t * 0.02));
    n += OilRainValueNoise(uv * 5.1 + float2(t * -0.03, t * 0.04)) * 0.45;
    return saturate(n * 0.55);
}

half4 OilRainStreakQuadFragment(float2 uvScroll, float2 uvMesh)
{
    float intensity = saturate(_Intensity);
    float quality = saturate(_Quality);
    float keep = lerp(0.18, 0.54, intensity) * lerp(0.78, 1.0, quality);
    float cols = lerp(16.0, 38.0, saturate(_Density * 0.38)) * lerp(0.72, 1.0, quality);
    float rows = lerp(3.6, 6.2, quality);
    float2 grid = float2(cols, rows);
    float thickness = lerp(16.0, 9.5, intensity);

    float s0 = OilRainStreakCell(uvScroll, grid, 0.0, keep, thickness);
    float s1 = 0.0;
    if (quality > 0.48)
    {
        float2 uv2 = uvScroll * float2(1.08, 1.0) + float2(0.41, 0.17);
        s1 = OilRainStreakCell(uv2, grid * float2(0.76, 0.88), 9.7, keep * 0.85, thickness * 1.1);
    }
    float streak = saturate(s0 + s1 * 0.5);

    float2 edge = abs(uvMesh - 0.5) * 2.0;
    float vignette = 1.0 - smoothstep(0.78, 1.0, max(edge.x, edge.y));

    float mist = OilRainMistNoise(uvMesh, _RainTime) * _Mist * lerp(0.55, 1.0, intensity) * 0.07;
    float alpha = (streak * _RainColor.a * lerp(0.62, 1.0, intensity) + mist) * vignette;
    alpha *= step(0.008, intensity);

    return half4(_RainColor.rgb, alpha);
}

#endif // OIL_RAIN_STREAK_QUAD_PASS_INCLUDED
