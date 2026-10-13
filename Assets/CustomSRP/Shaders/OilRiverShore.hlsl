#ifndef CUSTOM_OIL_RIVER_SHORE_INCLUDED
#define CUSTOM_OIL_RIVER_SHORE_INCLUDED

// 沿岸白边：窄浅水带 + 噪声碎边。宽带/实心白填会盖死河色。

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

    // 中等带宽：比实心白带窄，比「几乎看不见」宽。
    float lipReach = max(_IntersectionLength, 0.25);
    float interSecGradient = 1.0 - smoothstep(0.015, lipReach, viewDepth);
    interSecGradient = pow(saturate(interSecGradient), 1.65);

    float2 offsetVector = saturate(float2(crest, crest) + tangentNormalXZCrest.xy);
    float2 interUv = flowUv + offsetVector * _IntersectionDistortion;
    float2 interTime = float2(t * 0.15, -t) * _IntersectionSpeed;

    float dist = saturate(interSecGradient / max(_IntersectionFalloff, 1e-4));
    float2 nUV = interUv * max(_IntersectionTiling, 0.02);
    half noise = (half)SAMPLE_TEXTURE2D(intersectionNoise, intersectionSampler, nUV + interTime).r;
    noise = saturate(noise * (half)(dist * 1.2) + (half)(dist * 0.4));
    half softLo = (half)max(_IntersectionClipping - 0.12, 0.0);
    half softHi = (half)min(_IntersectionClipping + 0.16, 1.0);
    half intersection = smoothstep(softLo, softHi, noise)
        * (half)_IntersectionColor.a
        * (half)saturate(interSecGradient * 1.15);

    half shoreWash = (half)saturate(interSecGradient * interSecGradient)
        * (half)_IntersectionColor.a
        * 0.55h;

    float inFrontOfWater = saturate((surfaceEye - sceneEye) * 4.0);
    intersection *= (half)(1.0 - inFrontOfWater);
    shoreWash *= (half)(1.0 - inFrontOfWater);

    half joinFade = (half)smoothstep(0.0, 0.18, min(meshUv.y, 1.0 - saturate(meshUv.y)));
    intersection *= joinFade;
    shoreWash *= joinFade;

    return half2(saturate(intersection), saturate(shoreWash));
}

#endif
