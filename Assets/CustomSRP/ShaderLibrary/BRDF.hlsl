#ifndef CUSTOM_BRDF_INCLUDED
#define CUSTOM_BRDF_INCLUDED

#include "OilBrdfCommon.hlsl"

#define MIN_REFLECTIVITY 0.04

struct BRDF
{
    float3 diffuse;
    float3 specular;
    float roughness;
    float perceptualRoughness;
    float fresnel;
};

float OneMinusReflectivity(float metallic)
{
    float range = 1.0 - MIN_REFLECTIVITY;
    return range - metallic * range;
}

BRDF GetBRDF(Surface surface, bool applyAlphaToDiffuse = false)
{
    BRDF brdf;
    float oneMinusReflectivity = OneMinusReflectivity(surface.metallic);
    brdf.diffuse = surface.color * oneMinusReflectivity;
    if (applyAlphaToDiffuse)
    {
        brdf.diffuse *= surface.alpha;
    }

    brdf.specular = lerp(MIN_REFLECTIVITY, surface.color, surface.metallic);
    brdf.perceptualRoughness =
        PerceptualSmoothnessToPerceptualRoughness(surface.smoothness);
    brdf.roughness = PerceptualRoughnessToRoughness(brdf.perceptualRoughness);
    brdf.fresnel = saturate(surface.smoothness + 1.0 - oneMinusReflectivity);
    return brdf;
}

float SpecularStrength(Surface surface, BRDF brdf, Light light)
{
    float3 h = SafeNormalize(light.direction + surface.viewDirection);
    float nh2 = Square(saturate(dot(surface.normal, h)));
    float lh2 = Square(saturate(dot(light.direction, h)));
    float r2 = Square(brdf.roughness);
    float d2 = Square(nh2 * (r2 - 1.0) + 1.00001);
    float normalization = brdf.roughness * 4.0 + 2.0;
    return r2 / (d2 * max(0.1, lh2) * normalization);
}

// Oil-paint direct specular: soft Blinn-Phong masses + brush-tilted H.
// GGX kept only as a faint metal hint so chrome doesn't go fully NPR-flat.
float OilDirectSpecular(Surface surface, BRDF brdf, Light light)
{
    float3 h = SafeNormalize(light.direction + surface.viewDirection);
    h = OilBrdfWarpReflectDir(h, surface.position, brdf.perceptualRoughness);

    half specOil = OilBrdfOilSpecularTerm(
        (half3)surface.normal, (half3)h, (half)surface.smoothness);
    specOil = OilBrdfStepSpecMass(specOil, (half)surface.smoothness);

    // Soft 3-band glaze so highlights read as paint dabs, not a photo hot spot.
    half specQ = OilQuantizeNdotL(saturate(specOil), OilBrdfSpecSteps());
    specOil = lerp(specOil, specQ, 0.7h);

    float metal = saturate(surface.metallic);
#if defined(OIL_MOBILE_PERF)
    float pbrHint = saturate(SpecularStrength(surface, brdf, light) * 0.15) * metal;
    return (float)specOil * lerp(0.85, 1.15, metal) + pbrHint * 0.25;
#else
    float pbrHint = saturate(SpecularStrength(surface, brdf, light) * 0.12) * metal;
    return (float)specOil * lerp(0.9, 1.2, metal) + pbrHint * 0.35;
#endif
}

float3 DirectBRDF(Surface surface, BRDF brdf, Light light)
{
    float spec = OilDirectSpecular(surface, brdf, light);
    // Warm dielectric glaze; metals keep albedo-tinted F0 from GetBRDF.
    float3 specCol = brdf.specular;
    float3 warm = specCol * float3(1.06, 1.0, 0.9);
    specCol = lerp(warm, specCol, saturate(surface.metallic));
    return spec * specCol + brdf.diffuse;
}

float3 IndirectBRDF(Surface surface, BRDF brdf, float3 diffuse, float3 specular)
{
    float ndotV = saturate(dot(surface.normal, surface.viewDirection));
    float fresnelStrength = surface.fresnelStrength * Pow4(1.0 - ndotV);
    // Soft glaze masses instead of sharp Schlick chrome rim.
    float fresnelQ = (float)OilQuantizeNdotL((half)fresnelStrength, 3.0h);
    fresnelStrength = lerp(fresnelStrength, fresnelQ, 0.55) * 0.7;

    float3 envSpec = (float3)OilStylizeEnvSpec((half3)specular);
    float3 fMix = lerp(brdf.specular, brdf.fresnel, fresnelStrength);
    // Dielectrics quieter; metals keep more of the oil SpecCube.
    float oilAtten = lerp(0.52, 1.0, saturate(surface.metallic));
    float3 reflection = envSpec * fMix * oilAtten;
    reflection /= brdf.roughness * brdf.roughness + 1.0;
    return (diffuse * brdf.diffuse + reflection) * surface.occlusion;
}

#endif
