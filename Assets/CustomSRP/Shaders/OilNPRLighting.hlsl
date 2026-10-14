#ifndef CUSTOM_OIL_NPR_LIGHTING_INCLUDED
#define CUSTOM_OIL_NPR_LIGHTING_INCLUDED

// Single-tap shadow for GLES3 / half fragment shaders (PCF float[] vs min16float[] breaks compile).
#define OIL_NPR_HARD_DIRECTIONAL_SHADOW

#include "../ShaderLibrary/Surface.hlsl"
#include "../ShaderLibrary/Shadows.hlsl"
#include "../ShaderLibrary/Light.hlsl"
#include "OilPaintNoise.hlsl"

// OilSkyboxTime writes the current period fill (环境光 地平/天顶 mix). a>0.5 replaces the material ambient.
float4 _OilPeriodAmbient;

half3 OilPeriodAmbient(half3 materialAmbient)
{
    if (_OilPeriodAmbient.a > 0.5h)
    {
        return (half3)_OilPeriodAmbient.rgb;
    }

    return materialAmbient;
}

// Soft poster bands: keep oil masses, feather edges so shadow steps aren't chalk lines.
half QuantizeNdotL(half ndotL, half steps)
{
    steps = max(steps, 2.0h);
    half n = saturate(ndotL) * (steps - 1.0h);
    half i = floor(n);
    half f = frac(n);
    half feather = 0.58h;
    f = smoothstep(0.5h - feather * 0.5h, 0.5h + feather * 0.5h, f);
    return (i + f) / (steps - 1.0h);
}

// Wide lit falloff. The brush only nicks the edge; the body stays a soft diffuse wash.
half PainterlyLitAmount(half attenuation, half brush, half wobble)
{
    half warped = attenuation + (brush - 0.5h) * wobble * 0.85h;
    return smoothstep(0.02h, 0.92h, warped);
}

// Two pigment tones inside the umbra, striped by brush mask.
// _OIL_NO_VIOLET_UMBRA swaps the hardcoded violet pigment for white, so the
// shadow side reads as the material's own coolTint instead of pushing magenta.
// Water planes sit in heavy shadow (banks, cliffs) and the violet was dominating.
half3 PainterlyUmbra(
    half3 albedo,
    half3 ambient,
    half3 coolTint,
    half3 warmTint,
    half brush,
    half lift)
{
    // Umbra is a cool violet pigment. Warm paint only breaks the stroke, it does not gray it out.
#if defined(_OIL_NO_VIOLET_UMBRA)
    half3 violet = half3(1.0h, 1.0h, 1.0h);
#else
    half3 violet = half3(0.50h, 0.38h, 0.76h);
#endif
    half3 cool = albedo * lerp(coolTint, violet, 0.62h);
    half3 warm = albedo * lerp(warmTint, violet, 0.28h);
    half3 pigment = lerp(warm, cool, saturate(0.64h + brush * 0.36h));

    pigment *= lerp(0.9h, 1.12h, brush);

    half3 wash = ambient * albedo;
    half3 umbra = wash * 1.05h + pigment * max(lift, 0.8h);
#if !defined(_OIL_NO_VIOLET_UMBRA)
    umbra = max(umbra, albedo * violet * 0.42h);
#endif
    return umbra;
}

// forcedAttenuation < 0 samples the camera cascade. A large flat receiver
// otherwise draws that sphere as a disk that slides with the camera.
#ifndef OIL_SHADE_BAND
#define OIL_SHADE_BAND 0.28h
#endif

// Mobile campfire fill: range×spot×1/r², no point-shadow tap.
Light GetOtherLightNoShadow(int index, Surface surfaceWS)
{
    OtherLightData data = _OtherLightData[index];
    Light light;
    light.color = data.color.rgb;
    float3 position = data.position.xyz;
    float3 ray = position - surfaceWS.position;
    light.direction = normalize(ray);
    float distanceSqr = max(dot(ray, ray), 0.00001);
    float rangeAttenuation = Square(
        saturate(1.0 - Square(distanceSqr * data.position.w)));
    float3 spotDirection = data.directionAndMask.xyz;
    light.renderingLayerMask = asuint(data.directionAndMask.w);
    float spotAttenuation = Square(
        saturate(dot(spotDirection, light.direction) *
            data.spotAngle.x + data.spotAngle.y));
    light.attenuation = spotAttenuation * rangeAttenuation / distanceSqr;
    return light;
}

// Forward+ Point/Spot：油画分带 + 暖色本地填充（篝火等）。
// 平躺水面定义 OIL_NPR_NO_OTHER_LIGHTS，避免点光衰减读成圆形光斑。
half3 OilNPROtherLights(
    Surface surfaceWS,
    half3 albedo,
    half3 N,
    half shadeSteps,
    float2 screenUV)
{
#if defined(OIL_NPR_NO_OTHER_LIGHTS)
    return 0.0h;
#else
    if (GetOtherLightCount() <= 0)
    {
        return 0.0h;
    }

    ForwardPlusTile tile = GetForwardPlusTile(screenUV);
    int first = tile.GetFirstLightIndexInTile();
    int last = tile.GetLastLightIndexInTile();
#if defined(OIL_MOBILE_PERF)
    // Campfire + one spare; skip GetOtherShadowAttenuation.
    last = min(last, first + 1);
#endif
    half steps = max(shadeSteps * 0.5h, 2.0h);
    half3 sum = 0.0h;
#if !defined(OIL_MOBILE_PERF)
    ShadowData shadowData = GetShadowData(surfaceWS);
#endif
    for (int j = first; j <= last; j++)
    {
#if defined(OIL_MOBILE_PERF)
        Light light = GetOtherLightNoShadow(tile.GetLightIndex(j), surfaceWS);
#else
        Light light = GetOtherLight(tile.GetLightIndex(j), surfaceWS, shadowData);
#endif
        if ((surfaceWS.renderingLayerMask & light.renderingLayerMask) == 0u)
        {
            continue;
        }

        half3 L = (half3)light.direction;
        half att = (half)light.attenuation;
        if (att <= 1e-4h)
        {
            continue;
        }

        half halfLambert = saturate(dot(N, L) * 0.5h + 0.5h);
        half band = QuantizeNdotL(halfLambert, steps);
        half3 lc = (half3)light.color;
        // Direct band + soft fill so night fire warms the umbra instead of only a hard edge.
        sum += albedo * lc * (band * att + att * 0.4h);
    }

    return sum;
#endif
}

half3 OilNPRLightingEx(
    Surface surfaceWS,
    half3 albedo,
    half3 ambient,
    half3 shadowTint,
    half3 shadowWarm,
    half3 specularColor,
    half specularThreshold,
    half shadeSteps,
    half paintRim,
    half shadowLift,
    half shadeLift,
    half brush,
    half shadowWobble,
    half forcedAttenuation,
    half quantize,
    float2 screenUV)
{
    half3 N = (half3)surfaceWS.normal;
    half3 other = OilNPROtherLights(surfaceWS, albedo, N, shadeSteps, screenUV);

    if (GetDirectionalLightCount() <= 0)
    {
        half3 unlit = ambient * albedo + paintRim * ambient + other;
        // Night fire: pull umbra toward warm local light instead of pure violet.
        if (any(other > 1e-4h))
        {
            half3 warmUmbra = albedo * half3(0.55h, 0.28h, 0.12h);
            unlit = lerp(unlit, max(unlit, warmUmbra * 0.65h + other * 0.35h), 0.55h);
        }

#if defined(OIL_MOBILE_PERF)
        return unlit;
#else
        return OilDitherQuantize(unlit, (half)surfaceWS.dither, quantize);
#endif
    }

    DirectionalLightData lightData = _DirectionalLightData[0];
    half3 lightColor = (half3)lightData.color.rgb;
    half3 L = (half3)lightData.directionAndMask.xyz;
    half attenuation = forcedAttenuation;
    if (forcedAttenuation < 0.0h)
    {
        ShadowData shadowData = GetShadowData(surfaceWS);
        Light light = GetDirectionalLight(0, surfaceWS, shadowData);
        lightColor = (half3)light.color;
        L = (half3)light.direction;
        attenuation = (half)light.attenuation;
    }

    // Poster bands with soft edges. Hard cascade tap still darkens, but feather the jump.
    half halfLambert = saturate(dot(N, L) * 0.5h + 0.5h);
    half steps = max(shadeSteps, 2.0h);
    half attSoft = smoothstep(0.05h, 0.55h, attenuation);
#if defined(TERRAIN_SHADOW_RECEIVER)
    // Terrain: keep readable contact umbra, slightly lifted so bands don't chalk.
    half lit = halfLambert * lerp(0.22h, 1.0h, attSoft);
#else
    half lit = halfLambert * lerp(max(shadeLift, 0.28h), 1.0h, attSoft);
#endif
    lit += (brush - 0.5h) * shadowWobble * (0.65h / steps);
#if !defined(OIL_MOBILE_PERF)
    lit += ((half)surfaceWS.dither - 0.5h) * (quantize > 0.001h ? (1.0h / steps) : 0.0h);
#endif
    half litCont = saturate(lit);
    lit = QuantizeNdotL(litCont, steps);
    // Mix a little continuous shade so oil masses stay, chalk steps don't.
    lit = lerp(lit, litCont, 0.32h);

    // lightColor is VisibleLight.finalColor (OilSkyboxTime LightPresets × intensity).
    half lightPeak = max(1e-3h, max(lightColor.r, max(lightColor.g, lightColor.b)));
    half3 litColor = albedo * (ambient * 0.45h + lightColor);
#if defined(_OIL_NO_VIOLET_UMBRA)
    half3 umbraColor = albedo * half3(0.18h, 0.20h, 0.24h) + half3(0.02h, 0.022h, 0.025h);
#else
    half3 umbraColor = albedo * half3(0.2h, 0.14h, 0.36h) + half3(0.02h, 0.012h, 0.035h);
#endif
    umbraColor = lerp(umbraColor, albedo * shadowTint, 0.35h);
    umbraColor = lerp(umbraColor, albedo * shadowWarm, 0.15h);
    umbraColor *= lerp(0.7h, 1.1h, saturate(shadowLift * 0.67h));
#if defined(OIL_MOBILE_PERF)
    // One period pull + campfire warm; skip hue/rim/paintRim/ceil.
    half3 lightHue = lightColor / lightPeak;
    umbraColor = lerp(umbraColor, albedo * lightHue * 0.2h + lightColor * 0.1h, 0.4h);
    if (any(other > 1e-4h))
    {
        umbraColor = lerp(umbraColor, albedo * half3(0.48h, 0.24h, 0.1h) + other * 0.25h, 0.5h);
    }
    return lerp(umbraColor, litColor, lit) + other;
#else
    half3 lightHue = lightColor / lightPeak;
    umbraColor = lerp(umbraColor, albedo * lightHue * 0.2h + lightColor * 0.1h, 0.4h);
    if (any(other > 1e-4h))
    {
        half warmMix = saturate(length(other) * 1.8h);
        umbraColor = lerp(umbraColor, albedo * half3(0.48h, 0.24h, 0.1h) + other * 0.25h, warmMix * 0.7h);
    }

    half3 color = lerp(umbraColor, litColor, lit) + other;
    half3 V = (half3)surfaceWS.viewDirection;
    half inv = 1.0h - saturate(dot(N, V));
    half rim = inv * inv;
    color += albedo * lerp(half3(1.0h, 0.78h, 0.48h), lightHue, 0.55h) * rim * 0.08h;
    color += paintRim * ambient * 0.15h;
    half3 ceilC = albedo * (0.85h + saturate(lightPeak) * 0.9h) + lightColor * 0.12h + other;
    color = min(color, ceilC);
    return color;
#endif
}

// Wrapper: sample cascade shadows (forcedAttenuation < 0).
half3 OilNPRLighting(
    Surface surfaceWS,
    half3 albedo,
    half3 ambient,
    half3 shadowTint,
    half3 shadowWarm,
    half3 specularColor,
    half specularThreshold,
    half shadeSteps,
    half paintRim,
    half shadowLift,
    half shadeLift,
    half brush,
    half shadowWobble,
    half quantize,
    float2 screenUV)
{
    // Always hard-tap cascade shadows (OIL_NPR_HARD_DIRECTIONAL_SHADOW).
    // OIL_MOBILE_PERF used to force attenuation=1 and wiped tree shadows on terrain —
    // keep the tap; mobile still skips soft PCF / terrain micro-spec elsewhere.
    return OilNPRLightingEx(
        surfaceWS, albedo, ambient, shadowTint, shadowWarm, specularColor,
        specularThreshold, shadeSteps, paintRim, shadowLift, shadeLift,
        brush, shadowWobble, -1.0h, quantize, screenUV);
}

#endif
