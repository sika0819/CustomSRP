#ifndef OIL_RAIN_STREAK_QUAD_PASS_INCLUDED
#define OIL_RAIN_STREAK_QUAD_PASS_INCLUDED

// Camera-space rain curtain.
// Depth layers (far/mid/near) + per-column size: large streaks fall faster,
// small ones slower; slight tilt so directions are not perfectly parallel.
// Additive, no clip/discard (Mali/MT tile GPUs turn discard into gray quads).

CBUFFER_START(UnityPerMaterial)
    half4 _RainColor;
    float _Intensity;
    float _Density;
    float _RainTime;
    float _Wind;
    float _Quality;
    float _Mist;
    float _StreakLength;
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

// One depth layer. depth 0 = far (small/slow/faint), 1 = near (large/fast/bright).
// uvBase: mesh UV + wind shear, NO shared Y scroll — each column scrolls at its own speed.
float OilRainStreakLayer(float2 uvBase, float time, float seed, float keep, float lengthScale,
                         float depth, float densityScale)
{
    // Far = denser grid / thinner; near = fewer, thicker columns.
    float cols = lerp(34.0, 14.0, depth) * densityScale;
    float rows = lerp(14.0, 7.0, depth);
    cols = max(cols, 4.0);
    rows = max(rows, 3.0);

    float baseSpeed = lerp(0.55, 1.75, depth);
    float alphaMul = lerp(0.28, 1.05, depth);
    float thickBase = lerp(22.0, 9.5, depth);
    float tiltAmt = lerp(0.035, 0.11, depth);

    float2 uv = uvBase;
    // Mild layer-scale so far/near don't share the same column lattice.
    uv *= lerp(1.15, 0.88, depth);
    uv += float2(seed * 0.17, seed * 0.09);

    float col = floor(uv.x * cols);
    float hSize = OilRainHash21(float2(col, seed + 2.3));
    // Size drives length, thickness, and fall speed: big → fast, small → slow.
    float size = hSize;
    float speed = baseSpeed * lerp(0.45, 1.55, size);
    float tilt = (OilRainHash21(float2(col, seed + 4.1)) - 0.5) * tiltAmt;
    // Extra micro-tilt so neighbours are not parallel.
    tilt += (OilRainHash21(float2(col, seed + 6.7)) - 0.5) * tiltAmt * 0.45;

    // Shear UV by tilt, then scroll Y at this column's speed.
    float2 uvShear = uv;
    uvShear.x += (uv.y - 0.5) * tilt;
    float yScroll = uvShear.y * rows + time * speed + OilRainHash21(float2(col, seed + 0.5)) * 17.0;
    float row = floor(yScroll);

    float2 id = float2(col, row);
    float h = OilRainHash21(id + seed);
    float alive = step(h, keep * lerp(1.05, 0.78, depth));

    float hOff = OilRainHash21(id + float2(5.19, seed));
    float hBright = OilRainHash21(id + float2(8.13, seed));
    float hX = OilRainHash21(id + float2(2.11, seed));

    float lenMax = clamp(lengthScale, 0.58, 1.0);
    // Large drops get longer dashes.
    float len = lerp(0.58, lenMax, size);
    float y0 = lerp(0.02, max(0.02, 1.0 - len - 0.02), hOff);
    float y1 = y0 + len;

    float2 f = float2(frac(uvShear.x * cols), frac(yScroll));
    // Local tilt inside the cell (direction jitter).
    f.x += (f.y - 0.5) * tilt * 1.6;

    float2 center = float2(lerp(0.18, 0.82, hX), 0.5);
    // Large = thicker (lower thickness multiplier).
    float thickness = thickBase * lerp(1.45, 0.72, size);
    float across = 1.0 - saturate(abs(f.x - center.x) * thickness);
    across = across * across;

    float soft = max(len * 0.28, 0.035);
    float along = smoothstep(y0, y0 + soft, f.y) * (1.0 - smoothstep(y1 - soft, y1, f.y));

    float bright = lerp(0.30, 1.0, hBright) * lerp(0.55, 1.15, size);
    return across * along * alive * bright * alphaMul;
}

float OilRainMistNoise(float2 uv, float t)
{
    float n = OilRainValueNoise(uv * 2.4 + float2(t * 0.05, t * 0.02));
    n += OilRainValueNoise(uv * 5.1 + float2(t * -0.03, t * 0.04)) * 0.45;
    return saturate(n * 0.55);
}

// uvWind: mesh UV with wind shear only (no shared time scroll).
half4 OilRainStreakQuadFragment(float2 uvWind, float2 uvMesh)
{
    float intensity = saturate(_Intensity);
    float quality = saturate(_Quality);
    float keep = lerp(0.22, 0.58, intensity) * lerp(0.78, 1.0, quality);
    float densityScale = lerp(0.72, 1.0, quality) *
                         lerp(0.85, 1.2, saturate(_Density * 0.38));
    float lengthScale = _StreakLength > 0.01 ? _StreakLength : 0.8;
    float t = _RainTime;

    // Far / mid / near — parallax via different speed, scale, opacity.
    float farL = OilRainStreakLayer(uvWind, t, 0.0, keep * 1.05, lengthScale * 0.92, 0.12, densityScale);
    float midL = OilRainStreakLayer(uvWind, t, 11.3, keep, lengthScale, 0.48, densityScale);
    float nearL = 0.0;
    if (quality > 0.42)
        nearL = OilRainStreakLayer(uvWind, t, 23.7, keep * 0.72, lengthScale, 0.92, densityScale * 0.9);

    float streak = saturate(farL * 0.55 + midL * 0.85 + nearL * 1.15);

    float2 edge = abs(uvMesh - 0.5) * 2.0;
    float vignette = 1.0 - smoothstep(0.78, 1.0, max(edge.x, edge.y));

    float mist = OilRainMistNoise(uvMesh, t) * _Mist * lerp(0.55, 1.0, intensity) * 0.07;
    float alpha = (streak * _RainColor.a * lerp(0.62, 1.0, intensity) + mist) * vignette;
    alpha *= step(0.008, intensity);

    return half4(_RainColor.rgb, alpha);
}

#endif // OIL_RAIN_STREAK_QUAD_PASS_INCLUDED
