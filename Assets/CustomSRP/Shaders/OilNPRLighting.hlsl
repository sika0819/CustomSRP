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
half3 PainterlyUmbra(
    half3 albedo,
    half3 ambient,
    half3 coolTint,
    half3 warmTint,
    half brush,
    half lift)
{
    // Umbra is a cool violet pigment. Warm paint only breaks the stroke, it does not gray it out.
    half3 violet = half3(0.50h, 0.38h, 0.76h);
    half3 cool = albedo * lerp(coolTint, violet, 0.62h);
    half3 warm = albedo * lerp(warmTint, violet, 0.28h);
    half3 pigment = lerp(warm, cool, saturate(0.64h + brush * 0.36h));

    pigment *= lerp(0.9h, 1.12h, brush);

    half3 wash = ambient * albedo;
    half3 umbra = wash * 1.05h + pigment * max(lift, 0.8h);
    umbra = max(umbra, albedo * violet * 0.42h);
    return umbra;
}

// forcedAttenuation < 0 samples the camera cascade. A large flat receiver
// otherwise draws that sphere as a disk that slides with the camera.
#ifndef OIL_SHADE_BAND
#define OIL_SHADE_BAND 0.28h
#endif

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
    half quantize)
{
    if (GetDirectionalLightCount() <= 0)
    {
        half3 unlit = ambient * albedo + paintRim * ambient;
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

    half3 N = (half3)surfaceWS.normal;
    half3 V = (half3)surfaceWS.viewDirection;

    // Four poster bands. The shadow tap only picks a darker band; it is not a soft penumbra.
    half halfLambert = saturate(dot(N, L) * 0.5h + 0.5h);
    half steps = max(shadeSteps, 2.0h);
    half lit = halfLambert * lerp(max(shadeLift, 0.2h), 1.0h, attenuation);
    lit += (brush - 0.5h) * shadowWobble * (0.65h / steps);
    lit += ((half)surfaceWS.dither - 0.5h) * (quantize > 0.001h ? (1.0h / steps) : 0.0h);
    lit = QuantizeNdotL(saturate(lit), steps);

    half3 litColor = albedo * (ambient * 0.45h + lightColor * half3(1.05h, 0.9h, 0.62h));
    half3 umbraColor = albedo * half3(0.2h, 0.14h, 0.36h) + half3(0.02h, 0.012h, 0.035h);
    umbraColor = lerp(umbraColor, albedo * shadowTint, 0.35h);
    umbraColor = lerp(umbraColor, albedo * shadowWarm, 0.15h);
    umbraColor *= lerp(0.7h, 1.1h, saturate(shadowLift * 0.67h));
    half3 color = lerp(umbraColor, litColor, lit);

    half inv = 1.0h - saturate(dot(N, V));
    half rim = inv * inv;
    color += albedo * half3(1.0h, 0.78h, 0.48h) * rim * 0.08h;
    color += paintRim * ambient * 0.15h;
    color = min(color, albedo * 1.25h + half3(0.06h, 0.05h, 0.04h));
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
    half quantize)
{
    return OilNPRLightingEx(
        surfaceWS, albedo, ambient, shadowTint, shadowWarm, specularColor,
        specularThreshold, shadeSteps, paintRim, shadowLift, shadeLift,
        brush, shadowWobble, -1.0h, quantize);
}

#endif
