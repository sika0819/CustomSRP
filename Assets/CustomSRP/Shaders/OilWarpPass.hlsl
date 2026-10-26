#ifndef CUSTOM_OIL_WARP_PASS_INCLUDED
#define CUSTOM_OIL_WARP_PASS_INCLUDED

// Local oil paint flow warp — soft amplitude, never rests while strength > 0.
// Dual looped phases keep the field morphing; pattern returns every OIL_WARP_LOOP_SEC.

float _OilWarpStrength;
float _OilWarpTime;
float _OilWarpScale;
float _OilWarpMaxTexels;
float4 _OilWarpTexelSize;

#define OIL_WARP_LOOP_SEC 8.0
#define OIL_WARP_TWO_PI 6.2831853

static const float2x2 kOilWarpRot = float2x2(0.80, 0.60, -0.60, 0.80);

float OilWarpHash21(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float OilWarpNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = OilWarpHash21(i);
    float b = OilWarpHash21(i + float2(1.0, 0.0));
    float c = OilWarpHash21(i + float2(0.0, 1.0));
    float d = OilWarpHash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float OilWarpFbm(float2 p)
{
    float f = 0.0;
    f += 0.5000 * OilWarpNoise(p); p = mul(kOilWarpRot, p) * 2.02;
    f += 0.2500 * OilWarpNoise(p); p = mul(kOilWarpRot, p) * 2.03;
    f += 0.1250 * OilWarpNoise(p); p = mul(kOilWarpRot, p) * 2.01;
    f += 0.0625 * OilWarpNoise(p);
    return f;
}

float2 OilWarpLoopPhase(float t, float period)
{
    float a = OIL_WARP_TWO_PI * t / period;
    float s, c;
    sincos(a, s, c);
    return float2(s, c);
}

// Soft local flow; dual phases so motion never settles mid-loop.
float2 OilWarpFlow(float2 uv, float t, float scale)
{
    float2 p = uv * (2.2 * scale);
    float2 a = OilWarpLoopPhase(t, OIL_WARP_LOOP_SEC);
    // Incommensurate second loop (~5.0s) — always some drift when a is slow.
    float2 b = OilWarpLoopPhase(t, 5.0);
    float2 drift = a * 0.42 + b * 0.28;

    float2 q;
    q.x = OilWarpFbm(p + drift + float2(0.0, 0.0));
    q.y = OilWarpFbm(p + drift + float2(5.2, 1.3));

    float2 r;
    r.x = OilWarpFbm(p + 3.5 * q + float2(a.y, -a.x) * 0.35 + float2(1.7, 9.2));
    r.y = OilWarpFbm(p + 3.5 * q + b * 0.35 + float2(8.3, 2.8));

    // Curl-ish: keep paint sliding, avoid pure radial settle.
    float2 g = r - 0.5;
    return float2(-g.y, g.x) * 0.65 + g * 0.35;
}

float4 OilWarpPassFragment(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    float strength = saturate(_OilWarpStrength);
    if (strength < 1e-4)
        return GetSource(uv);

    float scale = _OilWarpScale > 1e-4 ? _OilWarpScale : 1.0;
    float2 flow = OilWarpFlow(uv, _OilWarpTime, scale);

    float2 texel = _OilWarpTexelSize.xy;
    float stepPx = max(texel.x, texel.y);
    float maxTexels = _OilWarpMaxTexels > 1e-4 ? _OilWarpMaxTexels : 5.0;
    maxTexels = clamp(maxTexels, 2.5, 8.0);
    // Smaller amp — whisper oil, not dunk.
    float amp = strength * maxTexels * stepPx;
    float2 offset = flow * amp;
    float2 uvW = uv + offset;

    float fl = length(flow);
    float2 dir = fl > 1e-5 ? flow / fl : float2(0.0, 1.0);
    float2 o = dir * (amp * 0.3);
    half3 c0 = GetSource(uvW - o).rgb;
    half3 c1 = GetSource(uvW).rgb;
    half3 c2 = GetSource(uvW + o).rgb;
    half3 col = (c0 + c1 * 2.0h + c2) * 0.25h;
    return float4(col, 1.0);
}

#endif
