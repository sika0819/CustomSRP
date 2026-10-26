#ifndef CUSTOM_OIL_FILTER_PASS_INCLUDED
#define CUSTOM_OIL_FILTER_PASS_INCLUDED

// Lite (mobile): free fwidth edge + optional 1-tap depth, then 4 diagonal taps.
// HQ (desktop/Editor): same edge gate, then 4-sector × 4-sample (~16-tap) Kuwahara.
// Interior ≈ 1 color (+1 depth); edge Lite ≈ 5 / HQ ≈ 17 color (+1 depth).
//
// Mid-ground focus (DoF-like): keep wet-oil strokes on dock/island;
// soften screen-bottom beach + far mountains/sky so the filter is not a flat full-screen wash.

float4 _OilFilterTexelSize;
float _OilFilterHasDepth;
// Authored for ~1080 short-side buffers; C# scales down on large buffers so wet-oil stays sharp.
float _OilFilterRadiusScale;
float _OilFilterStrength;
float _OilFilterInteriorFloor;

#define OIL_FILTER_NEAR_M 1.0
#define OIL_FILTER_FAR_M 18.0
#define OIL_FILTER_REF_SHORT 1080.0
// Focus band (eye metres): rise off the near beach, hold mid, fall into far hills/sky.
#define OIL_FILTER_FOCUS_NEAR0 1.8
#define OIL_FILTER_FOCUS_NEAR1 7.5
#define OIL_FILTER_FOCUS_FAR0 20.0
#define OIL_FILTER_FOCUS_FAR1 52.0

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

// 1 = mid-ground focus (dock/island strokes); 0 = near beach / far sky soften.
float OilFilterFocusWeight(float eye, float2 uv)
{
    float nearRise = smoothstep(OIL_FILTER_FOCUS_NEAR0, OIL_FILTER_FOCUS_NEAR1, eye);
    float farFall = 1.0 - smoothstep(OIL_FILTER_FOCUS_FAR0, OIL_FILTER_FOCUS_FAR1, eye);
    float depthFocus = nearRise * farFall;

    // Screen-bottom beach (uv.y = 0 at bottom): extra soft so foreground sand does not shout.
    float bottom = smoothstep(0.02, 0.28, uv.y);
    float screenFocus = lerp(0.28, 1.0, bottom);

    // Whisper floor so near/far still keep readable edge wet-oil (not a dead mask).
    return saturate(lerp(0.42, 1.0, depthFocus * screenFocus));
}

// Cheap 4-tap diagonal soft for out-of-focus / far softness (DoF whisper).
half3 OilFilterSoftBox(float2 uv, float2 texel, float radius)
{
    float2 t = texel * radius;
    half3 c0 = GetSource(uv + float2(-t.x, -t.y)).rgb;
    half3 c1 = GetSource(uv + float2(t.x, -t.y)).rgb;
    half3 c2 = GetSource(uv + float2(-t.x, t.y)).rgb;
    half3 c3 = GetSource(uv + float2(t.x, t.y)).rgb;
    return (c0 + c1 + c2 + c3) * 0.25h;
}

float OilFilterEdgeWeight(half sharpL, float eye)
{
    // Derivatives are free across the quad — no neighbor color fetches.
    // Soft campfire pools are gentle luma ramps; aggressive edge pick redraws them
    // as wet-oil chalk rings. Wide deadzone keeps geometry silhouettes, skips ramps.
    half lumaEdge = saturate((abs(ddx(sharpL)) + abs(ddy(sharpL)) - 0.020h) * 22.0h);
    float depthEdge = 0.0;
    if (_OilFilterHasDepth > 0.5)
    {
        float jump = abs(ddx(eye)) + abs(ddy(eye));
        float farSilW = smoothstep(2.5, 22.0, eye);
        float thresh = lerp(max(0.06, eye * 0.010), max(0.28, eye * 0.028), farSilW);
        depthEdge = saturate((jump - thresh) / (thresh * 1.6 + 0.22));
    }

    float nearW = 1.0 - smoothstep(2.5, 26.0, eye);
    return saturate(max((float)lumaEdge, depthEdge * lerp(1.35, 1.05, nearW)) * lerp(1.2, 1.05, nearW));
}

half3 OilFilterQuadPick(float2 uv, float2 texel, float eye, float sil, out half oilL)
{
    float farW = smoothstep(OIL_FILTER_NEAR_M, OIL_FILTER_FAR_M, eye);
    // Near strokes larger; far strokes smaller (oil perspective).
    float r = lerp(9.2, 4.4, farW);
    r = lerp(r, r * 1.22, sil);
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

// One Kuwahara sector: 4 samples → mean + luma variance.
half3 OilFilterSectorMeanVar(
    float2 uv, float2 o0, float2 o1, float2 o2, float2 o3, out half var)
{
    half3 c0 = GetSource(uv + o0).rgb;
    half3 c1 = GetSource(uv + o1).rgb;
    half3 c2 = GetSource(uv + o2).rgb;
    half3 c3 = GetSource(uv + o3).rgb;
    half3 mean = (c0 + c1 + c2 + c3) * 0.25h;
    half m = OilFilterLuma(mean);
    half d0 = OilFilterLuma(c0) - m;
    half d1 = OilFilterLuma(c1) - m;
    half d2 = OilFilterLuma(c2) - m;
    half d3 = OilFilterLuma(c3) - m;
    var = (d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3) * 0.25h;
    return mean;
}

void OilFilterConsiderSector(half3 mean, half var, inout half3 best, inout half bv, inout half3 second, inout half sv)
{
    if (var < bv)
    {
        second = best;
        sv = bv;
        best = mean;
        bv = var;
    }
    else if (var < sv)
    {
        second = mean;
        sv = var;
    }
}

// Desktop/Editor HQ: 4 quadrants × 4 taps (~16). Larger radius than Lite for visible stroke.
half3 OilFilterSectorPickHQ(float2 uv, float2 texel, float eye, float sil, out half oilL)
{
    float farW = smoothstep(OIL_FILTER_NEAR_M, OIL_FILTER_FAR_M, eye);
    float r = lerp(14.5, 6.2, farW);
    r = lerp(r, r * 1.28, sil);
    float radiusScale = _OilFilterRadiusScale > 1e-4 ? saturate(_OilFilterRadiusScale) : 1.0;
    r *= radiusScale;
    float2 t = texel * r;
    float2 tHalf = t * 0.45;

    // NW / NE / SW / SE — inner + outer axial/diagonal taps per quadrant.
    half v0, v1, v2, v3;
    half3 m0 = OilFilterSectorMeanVar(
        uv,
        float2(-tHalf.x, tHalf.y), float2(-t.x, tHalf.y),
        float2(-tHalf.x, t.y), float2(-t.x, t.y),
        v0);
    half3 m1 = OilFilterSectorMeanVar(
        uv,
        float2(tHalf.x, tHalf.y), float2(t.x, tHalf.y),
        float2(tHalf.x, t.y), float2(t.x, t.y),
        v1);
    half3 m2 = OilFilterSectorMeanVar(
        uv,
        float2(-tHalf.x, -tHalf.y), float2(-t.x, -tHalf.y),
        float2(-tHalf.x, -t.y), float2(-t.x, -t.y),
        v2);
    half3 m3 = OilFilterSectorMeanVar(
        uv,
        float2(tHalf.x, -tHalf.y), float2(t.x, -tHalf.y),
        float2(tHalf.x, -t.y), float2(t.x, -t.y),
        v3);

    half3 best = m0;
    half bv = v0;
    half3 second = m1;
    half sv = v1;
    if (sv < bv)
    {
        half3 tmp = best;
        half ts = bv;
        best = second;
        bv = sv;
        second = tmp;
        sv = ts;
    }
    OilFilterConsiderSector(m2, v2, best, bv, second, sv);
    OilFilterConsiderSector(m3, v3, best, bv, second, sv);

    half wet = saturate(bv / max(sv, 1e-4h) * 0.85h);
    half3 col = lerp(best, second, wet * 0.28h);
    oilL = OilFilterLuma(col);
    return col;
}

float4 OilFilterCompose(
    float2 uv,
    half3 sharp,
    half sharpL,
    float eye,
    float edge,
    half foam,
    half3 col,
    half oilL,
    bool hq,
    bool softBox)
{
    half farW = (half)smoothstep(OIL_FILTER_NEAR_M, OIL_FILTER_FAR_M, min(eye, OIL_FILTER_FAR_M));
    half nearW = 1.0h - smoothstep(2.5h, 26.0h, (half)eye);
    half focusW = (half)OilFilterFocusWeight(eye, uv);
    // Edge wet-oil is the signature look — keep it stronger than the soft interior floor.
    half edgeAmt = (half)saturate(pow(edge, hq ? 0.32h : 0.42h))
        * lerp(lerp(0.96h, 1.0h, nearW), lerp(0.86h, 0.96h, farW), 0.5h);
    // Mobile UltraLite (half QuadPick): slight edge boost so strokes still read without SoftBox.
    if (!softBox && !hq)
        edgeAmt = saturate(edgeAmt * 1.08h);

    // Interior floor: HQ default ~0.22; Lite uses authored floor (~0.12) × near weight.
    half floorAmt = (half)_OilFilterInteriorFloor;
    half interiorAmt = 0.0h;
    if (hq)
    {
        half hqFloor = max(floorAmt, 0.22h);
        interiorAmt = hqFloor * (1.0h - foam) * lerp(1.0h, 0.65h, farW) * focusW;
    }
    else if (floorAmt > 1e-4h)
    {
        interiorAmt = floorAmt * (1.0h - foam) * nearW * focusW;
    }

    half oilAmt = max(edgeAmt, interiorAmt);
    half strength = (half)(_OilFilterStrength > 1e-4 ? _OilFilterStrength : 1.0);
    oilAmt *= strength;
    // Strong edges ignore focus taper; only soft interior wash follows mid-ground focus.
    half edgeKeep = saturate((half)edge * 2.2h);
    oilAmt *= lerp(focusW, 1.0h, edgeKeep);

    half sunCore = saturate((sharpL - 0.40h) * 5.0h);
    oilAmt *= 1.0h - sunCore * (hq ? 0.28h : 0.42h);
    oilAmt *= 1.0h - foam * 0.97h;

    half cool = saturate((sharp.b + sharp.g) * 0.5h - sharp.r);
    half waterSheet = saturate((sharpL - 0.62h) * 4.5h) * saturate(cool * 2.5h + 0.15h);
    half waterBody = saturate(cool * 2.0h) * saturate((0.72h - sharpL) * 2.5h + 0.2h);
    half waterHint = saturate(max(waterSheet, waterBody));
    // Water still softens interior smear; silhouette edges keep more wet-oil.
    oilAmt *= 1.0h - waterHint * (hq ? 0.28h : 0.40h) * (1.0h - edgeKeep * 0.55h);

    // Keep source lighting volume; smear chrominance (HQ also pulls more luma structure).
    half3 oilChr = col / max(oilL, 1e-3h);
    half3 sharpChr = sharp / max(sharpL, 1e-3h);
    half3 chr = lerp(sharpChr, oilChr, hq ? lerp(0.95h, 0.82h, farW) : lerp(0.88h, 0.72h, farW));
    half keepL = hq ? lerp(0.42h, 0.28h, farW) : lerp(0.75h, 0.45h, farW);
    half lumaOut = lerp(oilL, sharpL, keepL);
    col = chr * lumaOut;
    col = lerp(sharp, col, oilAmt);

    // Mild DoF soft: out-of-focus (beach / far / sky) leans toward a soft box.
    // UltraLite skips SoftBox — oil signature is QuadPick, not DoF whisper.
    if (softBox)
    {
        half softAmt = (1.0h - focusW) * (hq ? 0.40h : 0.30h) * (1.0h - edgeKeep);
        if (softAmt > 0.02h)
        {
            float softR = lerp(1.4, 2.6, (float)(1.0h - focusW));
            half3 soft = OilFilterSoftBox(uv, _OilFilterTexelSize.xy, softR);
            col = lerp(col, soft, softAmt);
        }
    }

    return float4(saturate(col), 1.0);
}

// Lite: near always processes (weak interior); far uses soft edge gate.
// HQ: foam-only early-out (soft interior stroke via compose).
bool OilFilterPrepare(
    float2 uv,
    bool hq,
    out half3 sharp,
    out half sharpL,
    out float eye,
    out float edge,
    out half foam)
{
    sharp = GetSource(uv).rgb;
    sharpL = OilFilterLuma(sharp);
    eye = _OilFilterHasDepth > 0.5
        ? OilFilterSampleEyeDepth(uv)
        : OIL_FILTER_NEAR_M;

    // Shore foam / 拍岸白边：高亮低彩 → 直接跳过湿油（白沫保持锐利）。
    half cMax = max(sharp.r, max(sharp.g, sharp.b));
    half cMin = min(sharp.r, min(sharp.g, sharp.b));
    half chroma = cMax - cMin;
    foam = saturate((sharpL - 0.70h) * 7.0h) * saturate(1.0h - chroma * 9.0h);
    if (foam > 0.55h)
    {
        edge = 0.0;
        return false;
    }

    edge = OilFilterEdgeWeight(sharpL, eye);
    edge *= 1.0 - (float)foam * 0.85;
    if (hq)
        return true;
    // Near: always QuadPick so interiorFloor can read; far: soft edge gate.
    if (eye < 12.0)
        return true;
    return edge >= 0.008;
}

float4 OilFilterSoftOnly(float2 uv, half3 sharp, float eye)
{
    half focusW = (half)OilFilterFocusWeight(eye, uv);
    half softAmt = (1.0h - focusW) * 0.42h;
    if (softAmt <= 0.02h)
        return float4(sharp, 1.0);
    float softR = lerp(1.6, 3.2, (float)(1.0h - focusW));
    half3 soft = OilFilterSoftBox(uv, _OilFilterTexelSize.xy, softR);
    return float4(saturate(lerp(sharp, soft, softAmt)), 1.0);
}

float4 OilFilterPassFragment(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    half3 sharp;
    half sharpL;
    float eye;
    float edge;
    half foam;
    if (!OilFilterPrepare(uv, false, sharp, sharpL, eye, edge, foam))
    {
        // Foam stays sharp; far/edge early-out still gets mid-focus DoF soft.
        if (foam > 0.55h)
            return float4(sharp, 1.0);
        return OilFilterSoftOnly(uv, sharp, eye);
    }

    half oilL;
    half3 col = OilFilterQuadPick(uv, _OilFilterTexelSize.xy, min(eye, OIL_FILTER_FAR_M), edge, oilL);
    return OilFilterCompose(uv, sharp, sharpL, eye, edge, foam, col, oilL, false, true);
}

float4 OilFilterPassFragmentHQ(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    half3 sharp;
    half sharpL;
    float eye;
    float edge;
    half foam;
    if (!OilFilterPrepare(uv, true, sharp, sharpL, eye, edge, foam))
    {
        if (foam > 0.55h)
            return float4(sharp, 1.0);
        return OilFilterSoftOnly(uv, sharp, eye);
    }

    half oilL;
    half3 col = OilFilterSectorPickHQ(uv, _OilFilterTexelSize.xy, min(eye, OIL_FILTER_FAR_M), edge, oilL);
    return OilFilterCompose(uv, sharp, sharpL, eye, edge, foam, col, oilL, true, true);
}

// Mobile UltraLite pass 1: QuadPick oil color at half res (TexelSize = full buffer).
float4 OilFilterPassFragmentHalf(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    half3 sharp;
    half sharpL;
    float eye;
    float edge;
    half foam;
    if (!OilFilterPrepare(uv, false, sharp, sharpL, eye, edge, foam))
        return float4(sharp, 1.0);

    half oilL;
    half3 col = OilFilterQuadPick(uv, _OilFilterTexelSize.xy, min(eye, OIL_FILTER_FAR_M), edge, oilL);
    return float4(col, 1.0);
}

// Mobile UltraLite pass 2: full-res sharp + half-res oil, no SoftBox.
float4 OilFilterPassFragmentCompose(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    half3 sharp;
    half sharpL;
    float eye;
    float edge;
    half foam;
    if (!OilFilterPrepare(uv, false, sharp, sharpL, eye, edge, foam))
        return float4(sharp, 1.0);

    half3 col = GetSource2(uv).rgb;
    half oilL = OilFilterLuma(col);
    return OilFilterCompose(uv, sharp, sharpL, eye, edge, foam, col, oilL, false, false);
}

// UltraLite compose + LUT + canvas → camera target (skips full-res Oil Filter RT + Final blit).
float4 OilFilterPassFragmentComposeFinal(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    half3 sharp;
    half sharpL;
    float eye;
    float edge;
    half foam;
    float4 oil;
    if (!OilFilterPrepare(uv, false, sharp, sharpL, eye, edge, foam))
        oil = float4(sharp, 1.0);
    else
    {
        half3 col = GetSource2(uv).rgb;
        half oilL = OilFilterLuma(col);
        oil = OilFilterCompose(uv, sharp, sharpL, eye, edge, foam, col, oilL, false, false);
    }

    oil.rgb = FinalizePostColor(oil.rgb, uv);
    return oil;
}

// HQ wet-oil + LUT + canvas → camera target.
float4 OilFilterPassFragmentHQFinal(Varyings input) : SV_TARGET
{
    float2 uv = input.screenUV;
    half3 sharp;
    half sharpL;
    float eye;
    float edge;
    half foam;
    float4 oil;
    if (!OilFilterPrepare(uv, true, sharp, sharpL, eye, edge, foam))
    {
        if (foam > 0.55h)
            oil = float4(sharp, 1.0);
        else
            oil = OilFilterSoftOnly(uv, sharp, eye);
    }
    else
    {
        half oilL;
        half3 col = OilFilterSectorPickHQ(
            uv, _OilFilterTexelSize.xy, min(eye, OIL_FILTER_FAR_M), edge, oilL);
        oil = OilFilterCompose(uv, sharp, sharpL, eye, edge, foam, col, oilL, true, true);
    }

    oil.rgb = FinalizePostColor(oil.rgb, uv);
    return oil;
}

#endif
