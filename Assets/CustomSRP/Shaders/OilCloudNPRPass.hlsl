#ifndef CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED
#define CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED

#include "OilPaintNoise.hlsl"

// Ring of atlas cards. Shape is the cloud-map SDF.
// Color is discrete oil slabs (umbra / body / light / knife), not soft vapor.

struct CloudAttributes
{
    float3 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    // 0 on the low ring, high-ring SDF bias on the baked upper copy.
    float ringBias : TEXCOORD1;
};

struct CloudVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : VAR_CLOUD_UV;
    float2 noiseUV : VAR_NOISE_UV;
    float3 positionWS : VAR_POSITION;
    float ringBias : VAR_RING_BIAS;
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
    output.ringBias = input.ringBias;
    float yaw = _OilCloudWind.y;
    output.noiseUV = input.uv * _NoiseMap_ST.xy + _NoiseMap_ST.zw + float2(yaw * 0.10, yaw * 0.04);
    return output;
}

half CloudLuma(half3 color)
{
    return dot(color, half3(0.299h, 0.587h, 0.114h));
}

// Two plate sizes — coarse slabs + finer knife ridges (zhanweitu1 thick dabs).
void SampleCloudStrokes(float2 cloudUV, out half4 strokeA, out half4 strokeB)
{
    strokeA = SAMPLE_TEXTURE2D(_CloudBrush, sampler_CloudBrush, cloudUV * float2(0.95, 0.72));
#if defined(OIL_MOBILE_PERF)
    strokeB = strokeA;
#else
    strokeB = SAMPLE_TEXTURE2D(
        _CloudBrush, sampler_CloudBrush, cloudUV * float2(2.15, 1.55) + float2(0.17, 0.31));
#endif
}

half CloudBrushAmount(half4 stroke)
{
    if (max(stroke.r, stroke.a) < 1e-4h)
    {
        return 0.5h;
    }

    half ridge = saturate(CloudLuma(stroke.rgb));
    return saturate((ridge - 0.18h) / 0.42h);
}

half CloudStrokeHeight(half4 stroke)
{
    if (max(stroke.r, stroke.a) < 1e-4h)
    {
        return 0.5h;
    }

    return saturate((CloudLuma(stroke.rgb) - 0.16h) / 0.55h);
}

// Hard oil plates: pick among 4 authored pigments. Narrow transitions = 色块感.
half3 CloudImpastoSlabs(
    half3 umbra,
    half3 body,
    half3 light,
    half3 knife,
    half height,
    half belly,
    half facing,
    half edge,
    half dither01)
{
    // Height from brush + belly + sun face. Dither breaks striping into dabs.
    half h = saturate(height * 0.62h + belly * 0.28h + facing * 0.22h);
    h = saturate(h + (dither01 - 0.5h) * 0.10h);

    // 4 slabs. Gates stay narrow so neighbors do not milky-blend.
    half toBody = smoothstep(0.28h, 0.40h, h);
    half toLight = smoothstep(0.52h, 0.64h, h);
    half toKnife = smoothstep(0.74h, 0.86h, h) * saturate(edge * 1.35h + height * 0.55h);

    half3 col = umbra;
    col = lerp(col, body, toBody);
    col = lerp(col, light, toLight);
    col = lerp(col, knife, toKnife);

    // Second-pass trough dig on low brush — raised paint needs cavities.
    half trough = saturate((0.42h - height) * 2.2h);
    col *= lerp(1.0h, 0.78h, trough);

    // Knife catch: opaque cream stroke on peaks, not additive haze.
    half peak = saturate((height - 0.62h) * 3.2h);
    col = lerp(col, knife, peak * 0.42h);
    return saturate(col);
}

// Shipped stroke scale. Day slabs use the coarser pair above.
half4 SampleCloudStrokeOnline(float2 cloudUV)
{
    return SAMPLE_TEXTURE2D(_CloudBrush, sampler_CloudBrush, cloudUV * float2(1.72, 1.18));
}

half CloudBrushAmountOnline(half4 stroke)
{
    if (max(stroke.r, stroke.a) < 1e-4h)
    {
        return 0.5h;
    }

    half ridge = saturate(CloudLuma(stroke.rgb));
    return saturate((ridge - 0.28h) / 0.52h);
}

half3 CloudPainterly(half3 litColor, half4 strokeA, half edge)
{
    if (max(strokeA.r, strokeA.a) < 1e-4h)
    {
        return litColor;
    }

    half ridge = saturate(CloudLuma(strokeA.rgb));
    half n = saturate((ridge - 0.28h) / 0.52h);
    half grain = lerp(0.78h, 1.22h, n);
    half chop = saturate(edge) * lerp(0.16h, 0.38h, saturate(strokeA.b));
    half3 color = litColor * grain;
    color *= lerp(1.0h, lerp(0.88h, 1.10h, saturate(strokeA.g)), chop);
    return saturate(color);
}

// Shipped NightDistanceFog #0A1828, linear. Sky fog can move; night clouds do not.
float3 OnlineNightCloudInk()
{
    return float3(0.00303527, 0.00913406, 0.02121901);
}

float3 ApplyOnlineNightCloudFog(float3 color, float3 viewDirWS, half stroke)
{
    float master = saturate(_OilAtmosphereFogAmount);
    if (master < 1e-4 && _CustomFogEnabled < 0.5)
    {
        return color;
    }

    float lenSq = dot(viewDirWS, viewDirWS);
    float3 dir = lenSq > 1e-8 ? viewDirWS * rsqrt(lenSq) : float3(0.0, 1.0, 0.0);
    float concentration = AtmosphereConcentration();
    float cover = AtmosphereFogCover(dir, concentration);
    float amount = master > 1e-4 ? master : 1.0;
    float opacity = lerp(0.10, 0.32, concentration) * amount;
    float fogAmt = saturate(cover * opacity);
    float retain = lerp(0.18, 0.06, concentration) * fogAmt;
    float3 fogCol = OilFogPigment(OnlineNightCloudInk(), SoftFogStroke(stroke), fogAmt);
    float3 glazed = lerp(color, fogCol, fogAmt);
    return lerp(glazed, color, retain);
}

half3 CloudHorizonWash(half3 color, float3 dirW, half night)
{
    // Prefer atmosphere fog dye over sky-horizon wash (wash read as see-through).
    half band = saturate(1.0h - abs((half)dirW.y) / 0.22h);
    // Day lock 0.03. Shipped night lock 0.07.
    half lock = band * lerp(0.03h, 0.07h, night);
    return lerp(color, (half3)_OilCloudHorizon.rgb, lock);
}

// Live night cloud: thick-paint value, shipped weights, shipped ink.
// Day slabs stay on the other path and crossfade with _OilCloudNight.
half3 CloudShadeOnline(
    float3 dirW,
    float3 lightDir,
    float sunProx,
    float sdf,
    float sdfThreshold,
    float soft,
    float shape,
    float dawn,
    half nightAmt,
    half belly,
    half brush,
    half4 stroke,
    half edgeMap)
{
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

        half3 dawnBody = lerp(body, amb, 0.22h * (half)dawn);
        body = lerp(body, dawnBody, (half)dawnW * 0.55h);
        highlight = lerp(highlight, dawnBody, (half)dawnW * 0.35h);
    }

    // Shipped material: highlight 0.92, edge 1.55. Night crushes chalk.
    half lightW = 0.92h * lerp(1.0h, 0.18h, nightAmt);
    half edgeW = 1.55h * lerp(1.0h, 0.35h, nightAmt);
    half3 zenithCol = (half3)_OilCloudZenith.rgb;
    half3 shadowTop = lerp(body, zenithCol, (half)_TopShadow * (1.0h - belly * 0.35h));

    float thick = saturate((sdf - sdfThreshold) / max(soft * 2.6, 0.20));
    float shade = saturate(thick * (1.12 - (float)belly));
    half3 color = shadowTop * (half)lerp(1.0, lerp(0.60, 0.92, (float)belly), shade * 0.72);

    half facing = (half)saturate(dot(dirW, lightDir));
    half top = saturate(max(belly, (half)thick));
    half topMask = saturate(top * 0.85h + facing * 0.7h);
    color = lerp(color, highlight, saturate(topMask * lightW));

    float outer = saturate(4.0 * shape * (1.0 - shape));
    float band = 1.0 - saturate(abs(sdf - sdfThreshold) / max(soft * 0.38, 0.03));
    band *= band;
    float fringe = max(outer, max(band, (float)edgeMap));
    fringe = saturate(fringe);
    fringe *= lerp(1.0, fringe, 0.35);
    fringe *= shape;
    half rim = (half)fringe * edgeW * (half)lerp(0.4, 1.0, sunProx);
    color += highlight * rim * 0.45h;
    color += half3(1.00h, 0.96h, 0.86h) * rim * (half)dawn * 0.35h;

    color = CloudHorizonWash(color, dirW, nightAmt);
    color = CloudPainterly(color, stroke, edgeMap);
    color = (half3)ApplyOnlineNightCloudFog((float3)color, dirW, brush);
    color = lerp(color, (half3)OnlineNightCloudInk(), nightAmt * 0.45h);
    return color;
}

half4 CloudFragment(CloudVaryings input) : SV_Target
{
    float3 dirW = normalize(input.positionWS - _WorldSpaceCameraPos);
    float3 lightDir = normalize(lerp(_OilCloudSunDir, _OilCloudMoonDir, _OilCloudParams.x));
    float sunProx = saturate(dot(dirW, lightDir));
    sunProx *= sunProx;

    half nightAmt = saturate((half)_OilCloudNight);
#if defined(OIL_MOBILE_PERF)
    float noise = 0.5;
    float disturb = 0.0;
#else
    float noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, input.noiseUV).b;
    float disturb = max(_UVDisturbance, _OilCloudWind.z);
    // Shipped cloud disturbance was 0.04. Wind can still push it higher.
    disturb = lerp(disturb, max(0.04, _OilCloudWind.z), nightAmt);
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
    // Sunrise only. Period dawn is already high before 06:00, while the sky is still night.
    float morning = smoothstep(0.15, 0.45, saturate(_OilCloudParams.w));

    float coverage = saturate(_OilCloudParams.y);
    float sdfThreshold = lerp(_SdfMax, _SdfMin, coverage) + input.ringBias;
    // Derivatives before any return. A branch here would break the rim mip.
    float aa = max(fwidth(sdf), 1e-4);
    float atlas = baseMap.a;
    // Day fill stays hard. Night uses the shipped softness (0.14).
    float softDay = _SdfSoftness * lerp(0.85, 1.35, dawn);
    float softNight = 0.14 * lerp(1.0, 1.75, dawn);
    float soft = lerp(softDay, softNight, nightAmt);
    half4 strokeA;
    half4 strokeB;
    SampleCloudStrokes(cloudUV, strokeA, strokeB);
    half heightA = CloudStrokeHeight(strokeA);
    half heightB = CloudStrokeHeight(strokeB);
    half height = max(heightA, heightB * 0.85h);
    half brush = CloudBrushAmount(strokeA);
    half brushNight = 0.5h;
    half4 strokeOnline = half4(0.5h, 0.5h, 0.5h, 0.0h);
    if (nightAmt > 1e-3h)
    {
        strokeOnline = SampleCloudStrokeOnline(cloudUV);
        brushNight = CloudBrushAmountOnline(strokeOnline);
    }

    float thresholdDay = sdfThreshold + ((float)brush - 0.5) * 1.15 * aa;
    float thresholdNight = sdfThreshold + ((float)brushNight - 0.5) * 0.5 * aa;
    float threshold = lerp(thresholdDay, thresholdNight, nightAmt);
    float aaRim = max(aa, 1e-4);
    float shape = smoothstep(threshold - aaRim, threshold + aaRim, sdf);
    // Padding gate only — never use atlas.a as a soft opacity multiply.
    float pad = step(0.05, atlas);
    float alpha = shape * pad;
    // Alpha 0 matches the old clip, and it leaves early-Z on. clip() made Mali
    // shade the whole ring, including cards sitting behind the terrain.
    if (alpha < 0.02)
    {
        return half4(0.0h, 0.0h, 0.0h, 0.0h);
    }

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

        // Sky ambient is #5186B0. Keep the morning cloud on the warm equator.
        amb = lerp(amb, (half3)_OilCloudAmbientEquator.rgb, (half)morning);
        // Keep dawn stain modest — ambient wash kills slab contrast.
        half3 dawnBody = lerp(body, amb, 0.14h * (half)dawn);
        body = lerp(body, dawnBody, (half)dawnW * 0.40h);
        highlight = lerp(highlight, dawnBody, (half)dawnW * 0.22h);
    }

    half shadowW = (half)_TopShadow;
    half lightW = (half)_TopHighlight;
    half edgeW = (half)_EdgeIntensity;
    half belly = (half)saturate(baseMap.r);
    half3 zenithCol = (half3)_OilCloudZenith.rgb;

    // Four pigments for slab picking — cool cavity / body / sun face / knife cream.
    // (0.62, 0.68, 0.82) keeps blue and kills red. Morning shade stays on the peach hue.
    half3 cavity = lerp(half3(0.62h, 0.68h, 0.82h), half3(0.90h, 0.86h, 0.80h), (half)morning);
    half3 umbra = lerp(body * cavity, zenithCol, shadowW * lerp(0.55h, 0.0h, (half)morning));
    umbra = lerp(umbra, body, belly * 0.25h);
    half3 lightPig = lerp(body, highlight, saturate(lightW));
    half3 knife = lerp(
        highlight,
        half3(1.00h, 0.94h, 0.84h),
        lerp(0.35h, 0.70h, (half)dawn));
    knife = lerp(knife, (half3)_OilCloudHorizon.rgb, (half)dawn * 0.28h);

    half facing = (half)saturate(dot(dirW, lightDir));
    half edgeMask = (half)saturate(baseMap.g);
    half dither = (half)InterleavedGradientNoise(input.positionCS.xy, 0);

    half3 color = CloudImpastoSlabs(
        umbra, body, lightPig, knife,
        height, belly, facing * (0.55h + (half)sunProx * 0.45h),
        edgeMask, dither);

    // Rim: hard titanium stroke, not soft glow bloom.
    float outer = saturate(4.0 * shape * (1.0 - shape));
    float band = 1.0 - saturate(abs(sdf - threshold) / max(soft * 0.26, 0.02));
    band *= band;
    float fringe = saturate(max(outer, max(band, (float)edgeMask)));
    fringe *= lerp(1.0, fringe, 0.55);
    fringe *= shape;
    half rim = (half)fringe * edgeW * (half)lerp(0.55, 1.25, sunProx);
    half rimGate = smoothstep(0.35h, 0.72h, height);
    color = lerp(color, knife, saturate(rim * rimGate * 0.55h));
    color = lerp(color, half3(1.00h, 0.93h, 0.78h), saturate(rim * (half)dawn * 0.35h));

    color = CloudHorizonWash(color, dirW, 0.0h);
    // Fine plate weave from the second stroke — value only, keep slab chroma.
    half fine = heightB;
    // Weave floor of 0.86 drops morning G/B under the first posterize band (black).
    half weaveLo = lerp(0.86h, 1.0h, (half)dawn);
    color *= lerp(weaveLo, 1.16h, fine);
    color = OilWeaveTint(color, cloudUV * 14.0);
    // Coarse posterize (6 bands) — chunky oil plates, not soft 12-step wash.
    half noiseQ = (dither - 0.5h) * (1.0h / 7.0h);
    half3 slabs = floor(saturate(color + noiseQ) * 6.0h) / 6.0h;
    color = lerp(color, slabs, lerp(0.82h, 0.40h, (half)morning));

    // Lighter fog glaze — milky fog was erasing the slabs.
    color = (half3)ApplyOilSkyFogLayer((float3)color, dirW, SoftFogStroke(brush), 0.05, 0.18);
    if (nightAmt > 1e-3h)
    {
        half3 nightColor = CloudShadeOnline(
            dirW, lightDir, sunProx, sdf, thresholdNight, softNight, shape,
            dawn, nightAmt, belly, brushNight, strokeOnline, edgeMask);
        color = lerp(color, nightColor, nightAmt);
    }

    // Posterize drops morning G/B to 0. Floor stays a warm peach — 0.78 crushed poster bodies.
    half3 morningFloor = body * half3(0.88h, 0.84h, 0.76h);
    color = lerp(color, max(color, morningFloor), (half)morning);

    return half4(saturate(color), saturate((half)alpha));
}

#endif
