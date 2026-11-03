#ifndef CUSTOM_OIL_FILTER_PASS_INCLUDED
#define CUSTOM_OIL_FILTER_PASS_INCLUDED

// Ultra-lite wet-oil: free fwidth edge + optional 1-tap depth, then 4 diagonal taps.
// Interior ≈ 1 color (+1 depth); edge ≈ 5 color (+1 depth).
// Goal: flat paint sectors with crisp silhouettes — not a soft wash.
// Editor capture boost: OilFilterHqPassFragment uses 16-tap (4 quadrants × 2×2) Kuwahara.

float4 _OilFilterTexelSize;
float _OilFilterHasDepth;
// Authored for ~1080 short-side buffers. C# scales down on large TVs and on phone half-res
// so standstill wet-oil does not paint ~2× the reference screen fraction.
float _OilFilterRadiusScale;
// 1 = authored blend. Phone half-res is below 1 so the dab stays lighter while standing still.
float _OilFilterStrength;
// Editor preset capture: 1 = drop the 0.50–0.78 damper and take the full sector color.
float _OilFilterCaptureMax;
// Pad standing: 1 = stronger edge blend / chroma (still 4-tap, not CaptureMax).
float _OilFilterVisBoost;

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

float OilFilterEdgeResScale()
{
    // fwidth shrinks on >1080 buffers (pad @0.75, TV). Compensate so wet-oil
    // still lights up silhouettes; phone half-res stays at 1 (no weaken).
    float shortSide = min(_OilFilterTexelSize.z, _OilFilterTexelSize.w);
    return max(shortSide / OIL_FILTER_REF_SHORT, 1.0);
}

float OilFilterEdgeWeight(half sharpL, float eye)
{
    float res = OilFilterEdgeResScale();
    // Higher threshold → interiors stay crisp; only real folds/silhouettes wet-oil.
    half lumaThresh = 0.012h / (half)res;
    half lumaGain = 32.0h * (half)res;
    half lumaEdge = saturate((abs(ddx(sharpL)) + abs(ddy(sharpL)) - lumaThresh) * lumaGain);
    float depthEdge = 0.0;
    if (_OilFilterHasDepth > 0.5)
    {
        // Per-pixel depth jump also shrinks with resolution — scale before thresh.
        float jump = (abs(ddx(eye)) + abs(ddy(eye))) * res;
        float farSilW = smoothstep(2.5, 22.0, eye);
        // Near contact / silhouette: lower thresh so outline strokes fire.
        float nearThresh = max(0.055, eye * 0.010);
        float farThresh = max(0.40, eye * 0.038);
        float thresh = lerp(nearThresh, farThresh, farSilW);
        depthEdge = saturate((jump - thresh) / (thresh * 2.0 + 0.3));
    }

    // Near: emphasize outline strokes. Far: mild lift. (Was near×0.85 → near looked dry.)
    float nearStroke = 1.0 - smoothstep(1.5, 10.0, eye);
    float farEdgeW = smoothstep(2.5, 26.0, eye);
    float edgeMul = lerp(1.0, 1.08, farEdgeW) + nearStroke * 0.22;
    float depthMul = lerp(1.18, 1.12, farEdgeW) + nearStroke * 0.10;
    return saturate(max((float)lumaEdge, depthEdge * depthMul) * edgeMul);
}

float OilFilterRadius(float eye, float sil)
{
    float farW = smoothstep(OIL_FILTER_NEAR_M, OIL_FILTER_FAR_M, eye);
    // Near: tight stroke outline (not soft smear). Far: slightly wider slabs.
    float r = lerp(2.7, 5.2, farW);
    r = lerp(r, r * 1.10, sil);
    // Larger buffer → smaller texel radius (same UV smear looks mushier on big screens).
    // Unset/0 → treat as 1 so a missed SetGlobal does not kill the pass.
    float radiusScale = _OilFilterRadiusScale > 1e-4 ? saturate(_OilFilterRadiusScale) : 1.0;
    return r * radiusScale;
}

half3 OilFilterQuadPick(float2 uv, float2 texel, float eye, float sil, out half oilL)
{
    float2 t = texel * OilFilterRadius(eye, sil);

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

    // Less second-sector blend → flatter slabs, less mush.
    half wet = saturate(bv / max(sv, 1e-4h) * 0.85h);
    half3 col = lerp(best, second, wet * 0.16h);
    oilL = OilFilterLuma(col);
    return col;
}

// Editor HQ: 4 quadrants × 2×2 = 16 taps; pick lowest-variance sector mean.
half3 OilFilterSixteenTapPick(float2 uv, float2 texel, float eye, float sil, out half oilL)
{
    float2 t = texel * OilFilterRadius(eye, sil) * 1.05;
    // Quadrant direction signs (NW, NE, SW, SE).
    float2 qDir[4] =
    {
        float2(-1.0, -1.0),
        float2(1.0, -1.0),
        float2(-1.0, 1.0),
        float2(1.0, 1.0)
    };

    half3 best = 0;
    half bestVar = 1e9h;
    half3 second = 0;
    half secondVar = 1e9h;

    [unroll]
    for (int q = 0; q < 4; q++)
    {
        half3 acc = 0;
        half sumL = 0;
        half sumL2 = 0;
        [unroll]
        for (int iy = 0; iy < 2; iy++)
        {
            [unroll]
            for (int ix = 0; ix < 2; ix++)
            {
                // Sub-offsets 0.30 / 0.80 of the quadrant radius.
                float fx = ix == 0 ? 0.30 : 0.80;
                float fy = iy == 0 ? 0.30 : 0.80;
                float2 o = float2(qDir[q].x * fx, qDir[q].y * fy) * t;
                half3 c = GetSource(uv + o).rgb;
                half l = OilFilterLuma(c);
                acc += c;
                sumL += l;
                sumL2 += l * l;
            }
        }

        half3 mean = acc * 0.25h;
        half meanL = sumL * 0.25h;
        half var = max(0.0h, sumL2 * 0.25h - meanL * meanL);
        if (var < bestVar)
        {
            second = best;
            secondVar = bestVar;
            best = mean;
            bestVar = var;
        }
        else if (var < secondVar)
        {
            second = mean;
            secondVar = var;
        }
    }

    half wet = saturate(bestVar / max(secondVar, 1e-4h) * 0.85h);
    half3 col = lerp(best, second, wet * 0.14h);
    oilL = OilFilterLuma(col);
    return col;
}

float4 OilFilterCore(float2 uv, bool useSixteenTap)
{
    half3 sharp = GetSource(uv).rgb;
    half sharpL = OilFilterLuma(sharp);
    float eye = _OilFilterHasDepth > 0.5
        ? OilFilterSampleEyeDepth(uv)
        : OIL_FILTER_NEAR_M;

    // Shore foam / 拍岸白边：高亮低彩 → 直接跳过湿油（省 taps，白沫保持锐利）。
    half cMax = max(sharp.r, max(sharp.g, sharp.b));
    half cMin = min(sharp.r, min(sharp.g, sharp.b));
    half chroma = cMax - cMin;
    half foam = saturate((sharpL - 0.70h) * 7.0h) * saturate(1.0h - chroma * 9.0h);
    if (foam > 0.55h)
        return float4(sharp, 1.0);

    float eyeClamped = min(eye, OIL_FILTER_FAR_M);
    half nearStroke = (half)(1.0 - smoothstep(1.5, 9.0, eyeClamped));
    half farW = (half)smoothstep(OIL_FILTER_NEAR_M, OIL_FILTER_FAR_M, eyeClamped);

    // Dense grass / lavender micro-edges: skip near wet-oil before the 4 taps
    // so blades stay crisp (near stroke boost was washing them soft).
    half verdant = saturate(sharp.g - max(sharp.r, sharp.b) * 0.72h);
    half lavender = saturate(max(sharp.r, sharp.b) - sharp.g * 0.85h)
        * saturate(sharp.b * 1.4h - sharp.r * 0.35h);
    half plantHue = max(verdant, lavender * 0.85h);
    half midPlant = saturate(1.0h - abs(sharpL - 0.40h) * 3.2h);
    half microEdge = saturate((abs(ddx(sharpL)) + abs(ddy(sharpL))) * 48.0h);
    half grassHint = saturate(plantHue * midPlant * lerp(0.45h, 1.0h, microEdge));
    if (grassHint * nearStroke > 0.55h)
        return float4(sharp, 1.0);

    float edge = OilFilterEdgeWeight(sharpL, eye);
    edge *= 1.0 - (float)foam * 0.85;
    // Near outline: lower gate so fine folds still get a stroke.
    half edgeGate = lerp(0.045h, 0.028h, nearStroke);
    // Grass residual: keep a stricter gate so leftover blades don't get smeared.
    edgeGate = lerp(edgeGate, max(edgeGate, 0.055h), grassHint);
    if (edge < (float)edgeGate)
        return float4(sharp, 1.0);

    half oilL;
    half3 col = useSixteenTap
        ? OilFilterSixteenTapPick(uv, _OilFilterTexelSize.xy, eyeClamped, edge, oilL)
        : OilFilterQuadPick(uv, _OilFilterTexelSize.xy, eyeClamped, edge, oilL);

    // Near stroke: stronger edge blend + flatter mid-edge curve. Far: prior damper.
    // Capture max drops the damper so an edge blends at full strength.
    // VisBoost (pad): extra near outline without CaptureMax soft wash.
    half edgePow = lerp(0.70h, 0.55h, nearStroke);
    half edgeAmt = (half)saturate(pow(edge, edgePow));
    half dampLo = _OilFilterVisBoost > 0.5 ? 0.88h : 0.80h;
    half dampHi = _OilFilterVisBoost > 0.5 ? 0.90h : 0.78h;
    half oilAmt = _OilFilterCaptureMax > 0.5
        ? edgeAmt
        : edgeAmt * lerp(dampLo, dampHi, farW);
    oilAmt = saturate(oilAmt * lerp(1.0h, 1.16h, nearStroke));
    // Unset/0 → 1 so a missed SetGlobal does not wipe the pass.
    half strength = (half)(_OilFilterStrength > 1e-4 ? saturate(_OilFilterStrength) : 1.0);
    oilAmt *= strength;

    half sunCore = saturate((sharpL - 0.40h) * 5.0h);
    oilAmt *= 1.0h - sunCore * 0.50h;
    oilAmt *= 1.0h - foam * 0.97h;

    half cool = saturate((sharp.b + sharp.g) * 0.5h - sharp.r);
    half waterSheet = saturate((sharpL - 0.62h) * 4.5h) * saturate(cool * 2.5h + 0.15h);
    half waterBody = saturate(cool * 2.0h) * saturate((0.72h - sharpL) * 2.5h + 0.2h);
    half waterHint = saturate(max(waterSheet, waterBody));
    // Water stays sharper; NPR strokes already read as oil.
    oilAmt *= 1.0h - waterHint * 0.50h;
    // Residual grass: near cut hardest; far eases so distant meadows keep oil.
    oilAmt *= 1.0h - grassHint * lerp(0.92h, 0.55h, farW);

    // Near outline: more chroma + paint luma on the edge; far keeps lighting volume.
    half3 oilChr = col / max(oilL, 1e-3h);
    half3 sharpChr = sharp / max(sharpL, 1e-3h);
    half chrNear = _OilFilterVisBoost > 0.5 ? 0.86h : 0.78h;
    half chrFar = _OilFilterVisBoost > 0.5 ? 0.92h : 0.82h;
    half keepNear = _OilFilterVisBoost > 0.5 ? 0.48h : 0.58h;
    half keepFar = _OilFilterVisBoost > 0.5 ? 0.40h : 0.55h;
    half chrMix = lerp(chrNear, chrFar, farW);
    half keepL = lerp(keepNear, keepFar, farW);
    if (_OilFilterCaptureMax > 0.5)
    {
        chrMix = 1.0h;
        keepL = lerp(0.45h, 0.20h, farW);
    }

    half3 chr = lerp(sharpChr, oilChr, chrMix);
    // Narrow buffers: chroma-only smear is lost in the upscale. Tiny value nudge only.
    half lowRes = (half)saturate((800.0 - _OilFilterTexelSize.z) / 350.0);
    keepL = lerp(keepL, keepL * 0.90h, lowRes);
    half lumaOut = lerp(oilL, sharpL, keepL);
    col = chr * lumaOut;
    col = lerp(sharp, col, oilAmt);
    return float4(saturate(col), 1.0);
}

float4 OilFilterPassFragment(Varyings input) : SV_TARGET
{
    return OilFilterCore(input.screenUV, false);
}

float4 OilFilterHqPassFragment(Varyings input) : SV_TARGET
{
    return OilFilterCore(input.screenUV, true);
}

#endif
