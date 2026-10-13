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

half QuantizeNdotL(half ndotL, half steps)
{
    steps = max(steps, 2.0h);
    return floor(ndotL * steps) / (steps - 1.0h);
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

// Forward+ Point/Spot：油画分带 + 暖色本地填充（篝火等）。
half3 OilNPROtherLights(
    Surface surfaceWS,
    half3 albedo,
    half3 N,
    half shadeSteps,
    float2 screenUV)
{
    if (GetOtherLightCount() <= 0)
    {
        return 0.0h;
    }

    ShadowData shadowData = GetShadowData(surfaceWS);
    ForwardPlusTile tile = GetForwardPlusTile(screenUV);
    int lastLightIndex = tile.GetLastLightIndexInTile();
    half steps = max(shadeSteps * 0.5h, 2.0h);
    half3 sum = 0.0h;
    for (int j = tile.GetFirstLightIndexInTile(); j <= lastLightIndex; j++)
    {
        Light light = GetOtherLight(tile.GetLightIndex(j), surfaceWS, shadowData);
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
    half3 V = (half3)surfaceWS.viewDirection;
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

        return OilDitherQuantize(unlit, (half)surfaceWS.dither, quantize);
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

    // Four poster bands. The shadow tap only picks a darker band; it is not a soft penumbra.
    half halfLambert = saturate(dot(N, L) * 0.5h + 0.5h);
    half steps = max(shadeSteps, 2.0h);
#if defined(TERRAIN_SHADOW_RECEIVER)
    // Terrain: attenuation must read as clear umbra (object contact), not a soft shadeLift nudge.
    half lit = halfLambert * lerp(0.12h, 1.0h, attenuation);
#else
    half lit = halfLambert * lerp(max(shadeLift, 0.2h), 1.0h, attenuation);
#endif
    lit += (brush - 0.5h) * shadowWobble * (0.65h / steps);
    lit += ((half)surfaceWS.dither - 0.5h) * (quantize > 0.001h ? (1.0h / steps) : 0.0h);
    lit = QuantizeNdotL(saturate(lit), steps);

    // lightColor is VisibleLight.finalColor (OilSkyboxTime LightPresets × intensity).
    // Do not force a warm RGB multiply — that erases dawn gold / dusk orange / night blue.
    half lightPeak = max(1e-3h, max(lightColor.r, max(lightColor.g, lightColor.b)));
    half3 lightHue = lightColor / lightPeak;
    half3 litColor = albedo * (ambient * 0.45h + lightColor);
#if defined(_OIL_NO_VIOLET_UMBRA)
    // Water planes live in heavy shadow; the hardcoded violet below tinted everything magenta.
    // Replace with a neutral cool gray that lets shadowTint / shadowWarm carry the chroma.
    half3 umbraColor = albedo * half3(0.18h, 0.20h, 0.24h) + half3(0.02h, 0.022h, 0.025h);
#else
    half3 umbraColor = albedo * half3(0.2h, 0.14h, 0.36h) + half3(0.02h, 0.012h, 0.035h);
#endif
    umbraColor = lerp(umbraColor, albedo * shadowTint, 0.35h);
    umbraColor = lerp(umbraColor, albedo * shadowWarm, 0.15h);
    umbraColor *= lerp(0.7h, 1.1h, saturate(shadowLift * 0.67h));
    // Dim / cool keys pull the umbra toward the period light (night blue-black).
    umbraColor = lerp(umbraColor, albedo * lightHue * 0.2h + lightColor * 0.1h, 0.4h);
    // Campfire / lamps warm the night umbra where other lights reach.
    if (any(other > 1e-4h))
    {
        half warmMix = saturate(length(other) * 1.8h);
        umbraColor = lerp(umbraColor, albedo * half3(0.48h, 0.24h, 0.1h) + other * 0.25h, warmMix * 0.7h);
    }

    half3 color = lerp(umbraColor, litColor, lit) + other;

    half inv = 1.0h - saturate(dot(N, V));
    half rim = inv * inv;
    color += albedo * lerp(half3(1.0h, 0.78h, 0.48h), lightHue, 0.55h) * rim * 0.08h;
    color += paintRim * ambient * 0.15h;
    // Ceiling scales with key energy so noon stays brightest and night stays dark.
    half3 ceilC = albedo * (0.85h + saturate(lightPeak) * 0.9h) + lightColor * 0.12h + other;
    color = min(color, ceilC);
    return color;
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
