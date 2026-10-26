#ifndef CUSTOM_OIL_RIVER_SHORE_INCLUDED
#define CUSTOM_OIL_RIVER_SHORE_INCLUDED

// 沿岸白沫：Depth 窄交线 × 噪声碎边。shoreWash 只作极浅浅绿，禁止实心白填。

half2 OilRiverShoreFoamMask(
    Fragment fragment,
    float3 tangentNormalXZCrest,
    float2 flowUv,
    float2 meshUv,
    float crest,
    float t,
    Texture2D intersectionNoise,
    SamplerState intersectionSampler)
{
    float surfaceEye = fragment.depth;
    float sceneEye = fragment.bufferDepth;
    float viewDepth = max(sceneEye - surfaceEye, 0.0);

    if (sceneEye < 1e-3 || viewDepth > 80.0)
    {
        return half2(0.0h, 0.0h);
    }

    // 带宽由 _ShoreFoamWidth（米，眼深）直接控制；过宽会拖出跟镜头走的半透白带。
    float lipReach = OilRiverShoreFoamWidthM();
    float interSecGradient = 1.0 - smoothstep(0.02, lipReach, viewDepth);
    interSecGradient = pow(saturate(interSecGradient), 2.1);

    float2 offsetVector = saturate(float2(crest, crest) + tangentNormalXZCrest.xy);
    float2 interUv = flowUv + offsetVector * _IntersectionDistortion;
    float2 interTime = float2(t * 0.15, -t) * _IntersectionSpeed;

    float dist = saturate(interSecGradient / max(_IntersectionFalloff, 1e-4));
    float2 nUV = interUv * max(_IntersectionTiling, 0.02);
    half noise = (half)SAMPLE_TEXTURE2D(intersectionNoise, intersectionSampler, nUV + interTime).r;
    noise = saturate(noise * (half)(dist * 1.35) + (half)(dist * 0.28));
    half softLo = (half)max(_IntersectionClipping - 0.14, 0.0);
    half softHi = (half)min(_IntersectionClipping + 0.18, 1.0);
    half foamA = (half)saturate(_IntersectionAlpha);
    half intersection = smoothstep(softLo, softHi, noise)
        * foamA
        * (half)saturate(interSecGradient * 1.25);

    // 浅水 wash 只给一点点浅绿，不作奶白渐变。
    half shoreWash = (half)saturate(interSecGradient * interSecGradient * interSecGradient)
        * foamA
        * 0.22h;

    float inFrontOfWater = saturate((surfaceEye - sceneEye) * 4.0);
    intersection *= (half)(1.0 - inFrontOfWater);
    shoreWash *= (half)(1.0 - inFrontOfWater);

    half joinFade = (half)smoothstep(0.0, 0.18, min(meshUv.y, 1.0 - saturate(meshUv.y)));
    intersection *= joinFade;
    shoreWash *= joinFade;

    return half2(saturate(intersection), saturate(shoreWash));
}

#endif
