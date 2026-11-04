#ifndef CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED
#define CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED

// Faithful port of Boluo/Sky/GenshinCloudsMobile frag (non-SIMPLE).
// No OilCanvasNormal flank — sky keeps brush ridges; clouds stay SDF + atlas lighting.
// Intentionally ignores global OIL_MOBILE_PERF — that keyword strips Cloud_B + noise and
// was why Oil clouds read softer than Boluo on device. FBX atlas is UV1 (Boluo);
// procedural fallback: atlas UV0, UV1.x = SDF bias. Yaw is on the draw matrix.

struct CloudAttributes
{
    float3 positionOS : POSITION;
    float2 uv0 : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
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
    // Boluo GenshinCloudsMobile: real clip depth (ring ≈786m). Keep farClip ≥ 1000.
    output.positionCS = TransformWorldToHClip(positionWS);
    output.positionWS = positionWS;
    // Boluo: atlas = UV1 if non-zero, else UV0.
    // Procedural ring: UV1.y==0 and |UV1.x|≤0.15 stores SDF bias, atlas on UV0.
    float2 uv1 = input.uv1;
    float2 atlasUV;
    float bias = 0.0;
    if (abs(uv1.x) + abs(uv1.y) > 1e-5)
    {
        if (abs(uv1.y) <= 1e-5 && abs(uv1.x) <= 0.15)
        {
            atlasUV = input.uv0;
            bias = uv1.x;
        }
        else
        {
            atlasUV = uv1;
        }
    }
    else
    {
        atlasUV = input.uv0;
    }

    output.uv = atlasUV;
    output.ringBias = bias;
    float yaw = _OilCloudWind.y;
    output.noiseUV = atlasUV * _NoiseMap_ST.xy + _NoiseMap_ST.zw + float2(yaw * 0.10, yaw * 0.04);
    return output;
}

half4 CloudFragment(CloudVaryings input) : SV_Target
{
    float3 dirW = normalize(input.positionWS - _WorldSpaceCameraPos);
    float3 lightDir = normalize(lerp(_OilCloudSunDir, _OilCloudMoonDir, _OilCloudParams.x));
    float sunProx = saturate(dot(dirW, lightDir));
    sunProx *= sunProx;

    float noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, input.noiseUV).b;
    float disturb = max(_UVDisturbance, _OilCloudWind.z);
    // 2×4 atlas. Disturbance stays inside the cell so cards do not slide into a neighbor.
    float2 cells = float2(2.0, 4.0);
    float2 uv = saturate(input.uv);
    float2 cell = min(floor(uv * cells - 1e-4), cells - 1.0);
    float2 cellMin = cell / cells;
    float2 cellMax = (cell + 1.0) / cells;
    float2 cloudUV = input.uv + (noise - 0.5) * disturb;
    cloudUV = clamp(cloudUV, cellMin + float2(0.003, 0.006), cellMax - float2(0.003, 0.006));
    float4 baseMap = SAMPLE_TEXTURE2D_LOD(_CloudMap, sampler_CloudMap, cloudUV, 0);

    float3 sunD = _OilCloudSunDir;
    if (dot(sunD, sunD) < 1e-6)
    {
        sunD = float3(0.55, 0.15, 0.35);
    }

    sunD = normalize(sunD);
    float dawn = saturate(_OilCloudDawn);
    float sunLin = saturate(dot(dirW, sunD));

    float sdfThreshold = lerp(_SdfMax, _SdfMin, saturate(_OilCloudParams.y)) + input.ringBias;
    float soft = _SdfSoftness * lerp(1.0, 1.75, dawn);
    float softLo = sdfThreshold - soft;
    float softHi = max(sdfThreshold, softLo + 1e-3);
    float shape = smoothstep(softLo, softHi, baseMap.b);
    float alpha = shape * baseMap.a;
    // Alpha 0 instead of discard: Mali keeps early-Z; blend drops empty cards.
    if (alpha < 0.015)
    {
        return half4(0.0h, 0.0h, 0.0h, 0.0h);
    }

    // Body opaque enough to cover sky HDR sun disc; SDF soft edge stays translucent.
    // Wider opaque lift so the ring does not read glass-thin.
    alpha = lerp(alpha, 1.0, smoothstep(0.12, 0.55, alpha));
    // Night: mild zenith thin for stars — keep most mass (was 0.42, too see-through).
    float nightAmt = saturate(_OilCloudNight);
    float zenith = saturate(dirW.y);
    alpha *= lerp(1.0, lerp(1.0, 0.78, zenith), nightAmt);

    half3 body = (half3)_OilCloudColor.rgb;
    half3 highlight = (half3)_OilCloudHighlight.rgb;
    half3 dayHighlight = highlight;
    float glowAmt = 0.0;
    float dawnW = smoothstep(0.12, 0.38, dawn);
    if (dawnW > 1e-4)
    {
        // Trilight ambient (warm zenith / cool horizon) — do not wash pink-white.
        half y = (half)dirW.y;
        half3 amb = y >= 0.0h
            ? lerp((half3)_OilCloudAmbientEquator.rgb, (half3)_OilCloudAmbientSky.rgb, saturate(y))
            : lerp((half3)_OilCloudAmbientEquator.rgb, (half3)_OilCloudAmbientGround.rgb, saturate(-y));
        if (dot(amb, amb) < 1e-6h)
        {
            amb = body;
        }

        half3 dawnBody = lerp(body, amb, (half)(0.82 * dawn));
        body = lerp(body, dawnBody, (half)dawnW);
        highlight = lerp(highlight, dawnBody, (half)dawnW);
    }

    half3 shadowTop = lerp(
        body, (half3)_OilCloudZenith.rgb, (half)(_TopShadow * lerp(1.0, 0.35, dawn)));
    half3 farColor = lerp(shadowTop, body, (half)baseMap.r);
    half3 nearColor = lerp(
        lerp(shadowTop, highlight, (half)(_TopHighlight * lerp(1.0, 0.18, dawn))),
        highlight,
        (half)baseMap.r);
    half3 color = lerp(farColor, nearColor, (half)(sunProx * lerp(1.0, 0.16, dawn)));
    // Volume: SDF core takes shade, R belly takes light.
    float thick = saturate((baseMap.b - sdfThreshold) / max(soft * 2.6, 0.20));
    float belly = saturate(baseMap.r);
    float shade = saturate(thick * (1.12 - belly));
    color *= (half)lerp(1.0, lerp(0.60, 0.92, belly), shade * 0.72);
    color += highlight * (half)(belly * sunProx * 0.16 * lerp(1.0, 0.22, dawn));

    half3 dayEdge = dayHighlight * (half)(_EdgeIntensity * baseMap.g * sunProx);
    float outer = saturate(4.0 * shape * (1.0 - shape));
    float band = 1.0 - saturate(abs(baseMap.b - sdfThreshold) / max(soft * 0.62, 0.04));
    band *= band;
    float fringe = max(outer, band);
    fringe *= lerp(0.55, 1.0, saturate(baseMap.g));
    fringe = pow(saturate(fringe), 1.35);
    float wrap = lerp(0.38, 1.0, saturate(sunLin));
    glowAmt = fringe * wrap * dawn;
    half3 platinum = half3(1.00h, 0.96h, 0.86h);
    half3 dawnEdge = platinum * (half)(glowAmt * _EdgeIntensity * 0.92);
    color += lerp(dayEdge, dawnEdge, (half)dawnW);
    alpha = max(alpha, glowAmt * 0.32 * dawnW);

    half3 fogged = (half3)ApplyOilCloudFog((float3)color, dirW, 0.5h);
    half fogKeep = (1.0h - (half)glowAmt * 0.85h) * lerp(1.0h, 0.28h, (half)dawn);
    color = lerp(color, fogged, fogKeep);

    return half4(color, (half)alpha);
}

#endif
