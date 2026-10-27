#ifndef CUSTOM_OIL_FILTER_PASS_INCLUDED
#define CUSTOM_OIL_FILTER_PASS_INCLUDED

// Ultra-lite wet-oil: free fwidth edge + optional 1-tap depth, then 4 diagonal taps.
// Interior ≈ 1 color (+1 depth); edge ≈ 5 color (+1 depth). No 16-tap Kuwahara.

float4 _OilFilterTexelSize;
float _OilFilterHasDepth;
// Authored for ~1080 short-side buffers; C# scales down on large (TV) buffers so wet-oil stays sharp.
float _OilFilterRadiusScale;

#define OIL_FILTER_NEAR_M 1.0
#define OIL_FILTER_FAR_M 18.0
#define OIL_FILTER_REF_SHORT 1080.0

half OilFilterLuma(half3 c)
{
    return dot(c, half3(0.299h, 0.587h, 0.114h));
}

half OilFilterSampleEyeDepth(float2 uv)
{
    float raw = SAMPLE_DEPTH_TEXTURE_LOD(
        _CameraDepthTexture, sampler_point_clamp, uv, 0);
    return IsOrthographicCamera()
        ? OrthographicDepthBufferToLinear(raw)
        : LinearEyeDepth(raw, _ZBufferParams);
}

float OilFilterEdgeWeight(half sharpL, float eye)
{
    // Derivatives are free across the quad — no neighbor color fetches.
    half lumaEdge = saturate((abs(ddx(sharpL)) + abs(ddy(sharpL)) - 0.007h) * 26.0h);
    float depthEdge = 0.0;
    if (_OilFilterHasDepth > 0.5)
    {
        float jump = abs(ddx(eye)) + abs(ddy(eye));
        float farSilW = smoothstep(2.5, 22.0, eye);
        float thresh = lerp(max(0.08, eye * 0.012), max(0.35, eye * 0.035), farSilW);
        depthEdge = saturate((jump - thresh) / (thresh * 2.0 + 0.3));
    }

    float nearW = 1.0 - smoothstep(2.5, 26.0, eye);
    return saturate(max((float)lumaEdge, depthEdge * lerp(1.25, 1.0, nearW)) * lerp(1.12, 1.0, nearW));
}

half3 OilFilterQuadPick(float2 uv, float2 texel, float eye, float sil, out half oilL)
{
    float farW = smoothstep(OIL_FILTER_NEAR_M, OIL_FILTER_FAR_M, eye);
    float r = lerp(7.5, 5.5, farW);
    r = lerp(r, r * 1.15, sil);
    // Larger buffer → smaller texel radius (same UV smear looks mushier on big screens).
    // Unset/0 → treat as 1 so a missed SetGlobal does not kill the pass.
    float radiusScale = _OilFilterRadiusScale > 1e-4 ? saturate(_OilFilterRadiusScale) : 1.0;
    r *= radiusScale;
    float2 t = texel * r;

    half3 c0 = GetSource(uv + float2(-t.x, -t.y)).rgb;
    half3 c1 = GetSource(uv + float2(t.x, -t.y)).rgb;
    half3 c2 = GetSource(uv + float2(-t.x, t.y)).rgb;
    half3 c3 = GetSource(uv + float2(t.x, t.y)).rgb;
    half3 mean = (c0 + c1 + c2 + c3) * 0.25;

    // Prefer the flattest sector color (closest to local mean) — Kuwahara spirit, 4 taps.
    half3 best = c0;
    half bv = OilFilterLuma(abs(c0 - mean));
    half3 second = c1;
    half sv = OilFilterLuma(abs(c1 - mean));
    if (sv < bv)
    {
        half3 tmp = best;
        half ts = bv;
        best = second;
        bv = sv;
        second = tmp;
        sv = ts;
    }

    half s2 = OilFilterLuma(abs(c2 - mean));
    if (s2 < bv)
    {
        second = best;
        sv = bv;
        best = c2;
        bv = s2;
    }
    else if (s2 < sv)
    {
        second = c2;
        sv = s2;
    }

    half s3 = OilFilterLuma(abs(c3 - mean));
    if (s3 < bv)
    {
        second = best;
        sv = bv;
        best = c3;
        bv = s3;
    }
    else if (s3 < sv)
    {
        second = c3;
        sv = s3;
    }

    half wet = saturate(bv / max(sv, 1e-4h) * 0.85h);
    half3 col = lerp(best, second, wet * 0.28h);
    oilL = OilFilterLuma(col);
    return col;
}

float4 OilFilterPassFragment(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    half3 sharp = GetSource(uv).rgb;
    half sharpL = OilFilterLuma(sharp);
    float eye = _OilFilterHasDepth > 0.5
        ? OilFilterSampleEyeDepth(uv)
        : OIL_FILTER_NEAR_M;

    // Shore foam / 拍岸白边：高亮低彩 → 直接跳过湿油（省 4-tap，白沫保持锐利）。
    half cMax = max(sharp.r, max(sharp.g, sharp.b));
    half cMin = min(sharp.r, min(sharp.g, sharp.b));
    half chroma = cMax - cMin;
    half foam = saturate((sharpL - 0.70h) * 7.0h) * saturate(1.0h - chroma * 9.0h);
    if (foam > 0.55h)
        return float4(sharp, 1.0);

    float edge = OilFilterEdgeWeight(sharpL, eye);
    edge *= 1.0 - (float)foam * 0.85;
    if (edge < 0.02)
        return float4(sharp, 1.0);

    half oilL;
    half3 col = OilFilterQuadPick(uv, _OilFilterTexelSize.xy, min(eye, OIL_FILTER_FAR_M), edge, oilL);

    half farW = (half)smoothstep(OIL_FILTER_NEAR_M, OIL_FILTER_FAR_M, min(eye, OIL_FILTER_FAR_M));
    half nearW = 1.0h - smoothstep(2.5h, 26.0h, (half)eye);
    half oilAmt = (half)saturate(pow(edge, 0.55h))
        * lerp(lerp(0.92h, 0.98h, nearW), lerp(0.78h, 0.90h, farW), 0.5h);

    half sunCore = saturate((sharpL - 0.40h) * 5.0h);
    oilAmt *= 1.0h - sunCore * 0.50h;
    oilAmt *= 1.0h - foam * 0.97h;

    half cool = saturate((sharp.b + sharp.g) * 0.5h - sharp.r);
    half waterSheet = saturate((sharpL - 0.62h) * 4.5h) * saturate(cool * 2.5h + 0.15h);
    half waterBody = saturate(cool * 2.0h) * saturate((0.72h - sharpL) * 2.5h + 0.2h);
    half waterHint = saturate(max(waterSheet, waterBody));
    oilAmt *= 1.0h - waterHint * 0.55h;

    // Keep source lighting volume; only smear chrominance on edges.
    half3 oilChr = col / max(oilL, 1e-3h);
    half3 sharpChr = sharp / max(sharpL, 1e-3h);
    half3 chr = lerp(sharpChr, oilChr, lerp(0.88h, 0.72h, farW));
    half keepL = lerp(0.75h, 0.45h, farW);
    // Narrow buffers: chroma-only smear is lost in the upscale. Shift value too. Same taps.
    half lowRes = (half)saturate((800.0 - _OilFilterTexelSize.z) / 350.0);
    keepL = lerp(keepL, keepL * 0.55h, lowRes);
    half lumaOut = lerp(oilL, sharpL, keepL);
    col = chr * lumaOut;
    col = lerp(sharp, col, oilAmt);
    return float4(saturate(col), 1.0);
}

#endif
