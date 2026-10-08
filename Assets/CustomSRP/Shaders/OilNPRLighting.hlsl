#ifndef CUSTOM_OIL_NPR_LIGHTING_INCLUDED
#define CUSTOM_OIL_NPR_LIGHTING_INCLUDED

// Single-tap shadow for GLES3 / half fragment shaders (PCF float[] vs min16float[] breaks compile).
#define OIL_NPR_HARD_DIRECTIONAL_SHADOW

#include "../ShaderLibrary/Surface.hlsl"
#include "../ShaderLibrary/Shadows.hlsl"
#include "../ShaderLibrary/Light.hlsl"

half QuantizeNdotL(half ndotL, half steps)
{
    steps = max(steps, 2.0h);
    return floor(ndotL * steps) / (steps - 1.0h);
}

// Soft lit amount with brush-warped threshold — broken paint edge, not binary black.
half PainterlyLitAmount(half attenuation, half brush, half wobble)
{
    half warped = attenuation + (brush - 0.5h) * wobble * 1.5h;
    return smoothstep(0.2h, 0.72h, warped);
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
    // Cool stroke / warm gap — both stay chromatic and lifted.
    half3 cool = albedo * coolTint;
    half3 warm = albedo * warmTint;
    half3 pigment = lerp(warm, cool, saturate(brush));

    // Brush pressure: thicker ink vs thinner wash inside the shadow.
    pigment *= lerp(0.82h, 1.18h, brush);

    // Always keep a strong ambient wash so umbra never reads as dead black.
    half3 wash = ambient * albedo;
    half3 umbra = wash * 0.9h + pigment * max(lift, 0.65h);
    return umbra;
}

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
    half shadowWobble)
{
    if (GetDirectionalLightCount() <= 0)
    {
        return ambient * albedo + paintRim * ambient;
    }

    ShadowData shadowData = GetShadowData(surfaceWS);
    Light light = GetDirectionalLight(0, surfaceWS, shadowData);

    half3 lightColor = (half3)light.color;
    half3 N = (half3)surfaceWS.normal;
    half3 L = (half3)light.direction;
    half3 V = (half3)surfaceWS.viewDirection;
    half3 H = normalize(L + V);

    half ndotL = saturate(dot(N, L));
    half diffuse = QuantizeNdotL(ndotL, shadeSteps);
    diffuse = lerp(shadeLift, 1.0h, diffuse);

    half litAmount = PainterlyLitAmount(
        (half)light.attenuation, brush, shadowWobble);

    half3 litColor = albedo * (ambient + lightColor * diffuse);
    half3 umbraColor = PainterlyUmbra(
        albedo, ambient, shadowTint, shadowWarm, brush, shadowLift);

    // Extra stroke marks only in shadow: dark ribs / light scumbles.
    half inShadow = 1.0h - litAmount;
    half3 strokeDark = albedo * shadowTint * 0.7h;
    half3 strokeLight = albedo * lerp(shadowWarm, ambient, 0.35h) * 1.15h;
    half3 strokeLayer = lerp(strokeDark, strokeLight, brush);
    umbraColor = lerp(umbraColor, strokeLayer, inShadow * 0.45h);

    half3 color = lerp(umbraColor, litColor, litAmount);

    half ndotH = saturate(dot(N, H));
    half specWidth = max(1.0h - specularThreshold, 0.08h);
    half spec = saturate((ndotH - specularThreshold) / specWidth);
    color += specularColor * lightColor * spec * diffuse * litAmount * 0.25h;

    color += paintRim * (ambient + lightColor * litAmount * 0.2h);
    return color;
}

#endif
