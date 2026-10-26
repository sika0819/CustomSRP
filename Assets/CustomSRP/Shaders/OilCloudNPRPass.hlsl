#ifndef CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED
#define CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED

// Ring of atlas cards. Shape is the cloud-map SDF.
// Paint language matches skybox (canvas groove/ridge) + ocean (impasto warm/cool).

#include "OilPaintNoise.hlsl"
#include "OilImpasto.hlsl"
#include "../ShaderLibrary/OilBrdfCommon.hlsl"

struct CloudAttributes
{
    float3 positionOS : POSITION;
    float2 uv : TEXCOORD0;
};

struct CloudVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : VAR_CLOUD_UV;
    float2 noiseUV : VAR_NOISE_UV;
    float3 positionWS : VAR_POSITION;
};

CloudVaryings CloudVertex(CloudAttributes input)
{
    CloudVaryings output;
    float3 positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS = TransformWorldToHClip(positionWS);
    // Same depth as the skybox: only empty sky pixels pass. Terrain stays in front.
#if UNITY_REVERSED_Z
    output.positionCS.z = 1.0e-4 * output.positionCS.w;
#else
    output.positionCS.z = output.positionCS.w - 1.0e-4;
#endif
    output.positionWS = positionWS;
    output.uv = input.uv;
    float yaw = _OilCloudWind.y;
    output.noiseUV = input.uv * _NoiseMap_ST.xy + _NoiseMap_ST.zw + float2(yaw * 0.10, yaw * 0.04);
    return output;
}

half CloudLuma(half3 color)
{
    return dot(color, half3(0.299h, 0.587h, 0.114h));
}

half4 SampleCloudStroke(float2 cloudUV)
{
    float2 uvA = cloudUV * float2(1.72, 1.18);
    return SAMPLE_TEXTURE2D(_CloudBrush, sampler_CloudBrush, uvA);
}

// 0.5 leaves the contour alone when the stroke map is missing.
half CloudBrushAmount(half4 stroke)
{
    if (max(stroke.r, stroke.a) < 1e-4h)
    {
        return 0.5h;
    }

    half ridge = saturate(CloudLuma(stroke.rgb));
    return saturate((ridge - 0.28h) / 0.52h);
}

// Canvas ridges along yaw / elevation — same layout as OilSkybox CanvasStroke.
half CloudCanvasStroke(float3 dirW)
{
    float yaw = atan2(dirW.x, dirW.z);
    float elev = asin(clamp(dirW.y, -1.0, 1.0));
    float scale = max(_BrushScale, 0.5);
    float turns = max(round(scale * _OilCanvas_ST.x), 1.0);
    float2 uv = float2(
        yaw * (turns / (2.0 * PI)) + _OilCanvas_ST.z,
        elev * (scale * 0.16) * _OilCanvas_ST.y + _OilCanvas_ST.w);

#if defined(OIL_MOBILE_PERF)
    half canvas = (half)SAMPLE_TEXTURE2D(_OilCanvas, sampler_OilCanvas, uv).r;
#else
    float2 f = frac(uv);
    float2 edge = min(f, 1.0 - f);
    half w = (half)smoothstep(0.0, 0.16, min(edge.x, edge.y));
    half a = (half)SAMPLE_TEXTURE2D(_OilCanvas, sampler_OilCanvas, uv).r;
    half b = (half)SAMPLE_TEXTURE2D(_OilCanvas, sampler_OilCanvas, uv + 0.5).r;
    half canvas = lerp(b, a, w);
#endif
    half contrast = max((half)_BrushContrast, 0.5h);
    return saturate((canvas - 0.5h) * contrast + 0.5h);
}

// Lighter than skybox PaintSky — cloud pigment is already set by TOD tables.
half3 PaintCloudBody(half3 color, half stroke)
{
    half strength = saturate((half)_BrushStrength) * 0.72h;
    half depth = max(saturate((half)_BrushRelief), 0.55h) * strength;
    half3 groove = color * half3(0.48h, 0.58h, 0.78h);
    half3 ridge = min(color * half3(1.22h, 1.10h, 0.96h) + 0.035h, half3(1.15h, 1.15h, 1.15h));
    half3 painted = lerp(groove, ridge, smoothstep(0.12h, 0.88h, stroke));
    return lerp(color, painted, depth);
}

// Thick-paint warm ridge / cool groove from cloud brush + canvas.
half3 CloudOilPaint(half3 litColor, half4 strokeA, half canvasStroke, half edge)
{
    half brushStroke = 0.5h;
    if (max(strokeA.r, strokeA.a) >= 1e-4h)
    {
        brushStroke = PaintStroke(CloudLuma(strokeA.rgb), max((half)_BrushContrast, 0.5h));
    }

    half relief = max(saturate((half)_BrushRelief), 0.55h);
    half3 color = ImpastoDeviation(litColor, brushStroke, relief * 0.65h);
    color = PaintCloudBody(color, canvasStroke);

    half chop = saturate(edge) * lerp(0.12h, 0.28h, saturate(strokeA.b));
    color = lerp(color, ImpastoDeviation(color, brushStroke, 1.0h), chop);
    return saturate(color);
}

half3 CloudHorizonWash(half3 color, float3 dirW)
{
    // Prefer atmosphere fog dye over sky-horizon wash (wash read as see-through).
    half band = saturate(1.0h - abs((half)dirW.y) / 0.22h);
    half night = saturate((half)_OilCloudNight);
    half lock = band * lerp(0.04h, 0.07h, night);
    return lerp(color, (half3)_OilCloudHorizon.rgb, lock);
}

half4 CloudFragment(CloudVaryings input) : SV_Target
{
    float3 dirW = normalize(input.positionWS - _WorldSpaceCameraPos);
    float3 lightDir = normalize(lerp(_OilCloudSunDir, _OilCloudMoonDir, _OilCloudParams.x));
    float sunProx = saturate(dot(dirW, lightDir));
    sunProx *= sunProx;

#if defined(OIL_MOBILE_PERF)
    float noise = 0.5;
    float disturb = 0.0;
#else
    float noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, input.noiseUV).b;
    float disturb = max(_UVDisturbance, _OilCloudWind.z);
#endif
    // 2×4 atlas. Disturbance stays inside the cell so cards do not slide into a neighbor.
    float2 cells = float2(2.0, 4.0);
    float2 uv = saturate(input.uv);
    float2 cell = min(floor(uv * cells - 1e-4), cells - 1.0);
    float2 cellMin = cell / cells;
    float2 cellMax = (cell + 1.0) / cells;
    float2 cloudUV = input.uv + (noise - 0.5) * disturb;
    cloudUV = clamp(cloudUV, cellMin + float2(0.003, 0.006), cellMax - float2(0.003, 0.006));
    float4 baseMap = SAMPLE_TEXTURE2D_LOD(_CloudMap, sampler_CloudMap, cloudUV, 0);
    float sdf = baseMap.b;

    float3 sunD = _OilCloudSunDir;
    if (dot(sunD, sunD) < 1e-6)
    {
        sunD = float3(0.55, 0.15, 0.35);
    }

    sunD = normalize(sunD);
    float dawn = saturate(_OilCloudDawn);

    float coverage = saturate(_OilCloudParams.y);
    float sdfThreshold = lerp(_SdfMax, _SdfMin, coverage) + _OilCloudRingBias;
    // Derivatives before any return. A branch here would break the rim mip.
    float aa = max(fwidth(sdf), 1e-4);
    float atlas = baseMap.a;
    float soft = _SdfSoftness * lerp(1.0, 1.75, dawn);
    half4 stroke = SampleCloudStroke(cloudUV);
    half brush = CloudBrushAmount(stroke);
    // Bristle-cut contour (was 0.5×aa — airbrush soft). Alpha stays a hard oil fill.
    float threshold = sdfThreshold + ((float)brush - 0.5) * 2.0 * aa;
    float aaRim = aa * 0.3;
    float shape = smoothstep(threshold - aaRim, threshold + aaRim, sdf);
    // Padding gate only — never use atlas.a as a soft opacity multiply.
    float pad = step(0.05, atlas);
    // Alpha 0 matches the old clip, and it leaves early-Z on. clip() made Mali
    // shade the whole ring, including cards sitting behind the terrain.
    if (shape * pad < 0.02)
    {
        return half4(0.0h, 0.0h, 0.0h, 0.0h);
    }

    // Mapped cloud is opaque paint — dye fog on RGB later, never thin alpha.
    float alpha = 1.0;

    half3 body = (half3)_OilCloudColor.rgb;
    half3 highlight = (half3)_OilCloudHighlight.rgb;
    float dawnW = smoothstep(0.12, 0.38, dawn);
    if (dawnW > 1e-4)
    {
        half y = dirW.y;
        half3 amb = y >= 0.0
            ? lerp((half3)_OilCloudAmbientEquator.rgb, (half3)_OilCloudAmbientSky.rgb, saturate(y))
            : lerp((half3)_OilCloudAmbientEquator.rgb, (half3)_OilCloudAmbientGround.rgb, saturate(-y));
        if (dot(amb, amb) < 1e-6h)
        {
            amb = body;
        }

        // Soft dawn wash — keep thick-paint readable (was over-stained by ambient).
        half3 dawnBody = lerp(body, amb, 0.22h * (half)dawn);
        body = lerp(body, dawnBody, (half)dawnW * 0.55h);
        highlight = lerp(highlight, dawnBody, (half)dawnW * 0.35h);
    }

    half nightAmt = saturate((half)_OilCloudNight);
    half shadowW = (half)_TopShadow;
    // Night: crush day chalk highlights — moon ink, not white paint.
    half lightW = (half)_TopHighlight * lerp(1.0h, 0.18h, nightAmt);
    half edgeW = (half)_EdgeIntensity * lerp(1.0h, 0.35h, nightAmt);
    half belly = (half)saturate(baseMap.r);
    half3 zenithCol = (half3)_OilCloudZenith.rgb;
    half3 shadowTop = lerp(body, zenithCol, shadowW * (1.0h - belly * 0.35h));

    float thick = saturate((sdf - sdfThreshold) / max(soft * 2.6, 0.20));
    // Light oil shade steps — chunky masses, soft feather between bands.
    thick = (float)OilQuantizeNdotL((half)thick, OilBrdfShadeSteps());
    float shade = saturate(thick * (1.12 - (float)belly));
    half3 color = shadowTop * (half)lerp(1.0, lerp(0.60, 0.92, (float)belly), shade * 0.72);

    // Cloud body takes the highlight even when the sun is on the horizon.
    // The sun-facing side takes more.
    half facing = OilQuantizeNdotL((half)saturate(dot(dirW, lightDir)), 3.0h);
    half top = saturate(max(belly, (half)thick));
    half topMask = saturate(top * 0.85h + facing * 0.7h);
    color = lerp(color, highlight, saturate(topMask * lightW));

    float outer = saturate(4.0 * shape * (1.0 - shape));
    float band = 1.0 - saturate(abs(sdf - threshold) / max(soft * 0.38, 0.03));
    band *= band;
    float fringe = max(outer, max(band, saturate(baseMap.g)));
    fringe = saturate(fringe);
    fringe *= lerp(1.0, fringe, 0.35);
    half rim = (half)fringe * edgeW * (half)lerp(0.4, 1.0, sunProx);
    color += highlight * rim * 0.45h;
    color += half3(1.00h, 0.96h, 0.86h) * rim * (half)dawn * 0.35h;
    // Rim light stays on color only — do not re-feather alpha with a soft halo.

    half canvasStroke = CloudCanvasStroke(dirW);
    color = CloudHorizonWash(color, dirW);
    color = CloudOilPaint(color, stroke, canvasStroke, (half)baseMap.g);
#if defined(OIL_MOBILE_PERF)
    color = OilWeaveTintMobile(color, dirW.xz * 6.0);
#else
    color = OilWeaveTint(color, dirW.xz * 6.0);
#endif
    color = (half3)ApplyOilCloudFog((float3)color, dirW, canvasStroke);
    // Night: gentle NightFog tip — keep brush grain (was 0.85 and milky).
    if (nightAmt > 1e-3h)
    {
        half3 nightFog = _OilAtmosphereFogAmount > 1e-4
            ? (half3)_OilAtmosphereFogColor.rgb
            : (half3)_CustomFogColor.rgb;
        color = lerp(color, nightFog, nightAmt * 0.45h);
    }

    return half4(color, saturate((half)alpha));
}

#endif
