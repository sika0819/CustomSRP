#ifndef CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED
#define CUSTOM_OIL_CLOUD_NPR_PASS_INCLUDED

// Ring of atlas cards. Shape is the cloud-map SDF. Paint is two stroke maps.

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

// Thick-paint value only. Lighting weights stay in the color passed in.
half3 CloudPainterly(half3 litColor, float2 cloudUV, half edge)
{
    float2 uvA = cloudUV * float2(1.72, 1.18);
    half4 strokeA = SAMPLE_TEXTURE2D(_CloudBrush, sampler_CloudBrush, uvA);
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

half3 CloudHorizonWash(half3 color, float3 dirW)
{
    half band = saturate(1.0h - abs((half)dirW.y) / 0.18h);
    half night = saturate((half)_OilCloudNight);
    half lock = band * lerp(0.28h, 0.40h, night);
    return lerp(color, (half3)_OilCloudHorizon.rgb, lock);
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
    // Mip 0 coverage is a one-texel cliff. Block compression turns that cliff
    // into squares. A low mip feathers the rim; belly paint stays on mip 0.
    float4 rimMap = SAMPLE_TEXTURE2D_LOD(_CloudMap, sampler_CloudMap, cloudUV, 1.5);
    float sdf = lerp(baseMap.b, rimMap.b, 0.85);

    float3 sunD = _OilCloudSunDir;
    if (dot(sunD, sunD) < 1e-6)
    {
        sunD = float3(0.55, 0.15, 0.35);
    }

    sunD = normalize(sunD);
    float dawn = saturate(_OilCloudDawn);

    float coverage = saturate(_OilCloudParams.y);
    float sdfThreshold = lerp(_SdfMax, _SdfMin, coverage) + _OilCloudRingBias;
    float soft = _SdfSoftness * lerp(1.0, 1.75, dawn);
    float softLo = sdfThreshold - soft;
    float softHi = max(sdfThreshold, softLo + 1e-3);
    float shape = smoothstep(softLo, softHi, sdf);
    float alpha = shape * rimMap.a;
    if (alpha < 0.008)
    {
        clip(-1.0);
        return half4(0.0h, 0.0h, 0.0h, 0.0h);
    }

    // Opaque core still covers the sun. Crushing from 0.25 made the rim a
    // one-texel stair, so only the core is forced opaque.
    alpha = lerp(alpha, 1.0, smoothstep(0.58, 0.90, alpha));
    float nightAmt = saturate(_OilCloudNight);
    float zenith = saturate(dirW.y);
    alpha *= lerp(1.0, lerp(1.0, 0.42, zenith), nightAmt);

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

        half3 dawnBody = lerp(body, amb, 0.82h * (half)dawn);
        body = lerp(body, dawnBody, (half)dawnW);
        highlight = lerp(highlight, dawnBody, (half)dawnW);
    }

    half shadowW = (half)_TopShadow;
    half lightW = (half)_TopHighlight;
    half edgeW = (half)_EdgeIntensity;
    half belly = (half)saturate(baseMap.r);
    half3 zenithCol = (half3)_OilCloudZenith.rgb;
    half3 shadowTop = lerp(body, zenithCol, shadowW * (1.0h - belly * 0.35h));

    float thick = saturate((sdf - sdfThreshold) / max(soft * 2.6, 0.20));
    float shade = saturate(thick * (1.12 - (float)belly));
    half3 color = shadowTop * (half)lerp(1.0, lerp(0.60, 0.92, (float)belly), shade * 0.72);

    // Cloud body takes the highlight even when the sun is on the horizon.
    // The sun-facing side takes more.
    half facing = (half)saturate(dot(dirW, lightDir));
    half top = saturate(max(belly, (half)thick));
    half topMask = saturate(top * 0.85h + facing * 0.7h);
    color = lerp(color, highlight, saturate(topMask * lightW));

    float outer = saturate(4.0 * shape * (1.0 - shape));
    float band = 1.0 - saturate(abs(sdf - sdfThreshold) / max(soft * 0.62, 0.04));
    band *= band;
    float fringe = max(outer, max(band, saturate(baseMap.g)));
    fringe = saturate(fringe);
    fringe *= lerp(1.0, fringe, 0.35);
    half rim = (half)fringe * edgeW * (half)lerp(0.4, 1.0, sunProx);
    color += highlight * rim * 0.45h;
    color += half3(1.00h, 0.96h, 0.86h) * rim * (half)dawn * 0.35h;
    alpha = max(alpha, (float)rim * 0.2);

    color = CloudHorizonWash(color, dirW);
    color = CloudPainterly(color, cloudUV, (half)baseMap.g);
    return half4(color, saturate((half)alpha));
}

#endif
