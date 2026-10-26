#ifndef CUSTOM_LIGHTING_INCLUDED
#define CUSTOM_LIGHTING_INCLUDED

#include "OilBrdfCommon.hlsl"

bool RenderingLayersOverlap(Surface surface, Light light)
{
    return (surface.renderingLayerMask & light.renderingLayerMask) != 0;
}

float3 IncomingLight(Surface surface, Light light)
{
    // Half-Lambert → oil shade bands; keep attenuation raw (no hash warp —
    // world-noise on att made self-shadow read as concentric rings).
    float ndotL = saturate(dot(surface.normal, light.direction));
    float halfLambert = ndotL * 0.5 + 0.5;
    float band = (float)OilQuantizeNdotL((half)halfLambert, OilBrdfShadeSteps());
    // More continuous mix — strong poster steps read as concentric rings on spheres.
    float lit = lerp(band, halfLambert, 0.42) * light.attenuation;
    return lit * light.color;
}

float3 GetLighting(Surface surface, BRDF brdf, Light light)
{
    return IncomingLight(surface, light) * DirectBRDF(surface, brdf, light);
}

float3 GetLighting(Fragment fragment, Surface surfaceWS, BRDF brdf, GI gi)
{
    ShadowData shadowData = GetShadowData(surfaceWS);
    float3 color = IndirectBRDF(surfaceWS, brdf, gi.diffuse, gi.specular);
    for (int i = 0; i < GetDirectionalLightCount(); i++)
    {
        Light light = GetDirectionalLight(i, surfaceWS, shadowData);
        if (RenderingLayersOverlap(surfaceWS, light))
        {
            color += GetLighting(surfaceWS, brdf, light);
        }
    }

    ForwardPlusTile tile = GetForwardPlusTile(fragment.screenUV);
    int lastLightIndex = tile.GetLastLightIndexInTile();
    for (int j = tile.GetFirstLightIndexInTile(); j <= lastLightIndex; j++)
    {
        Light light = GetOtherLight(
            tile.GetLightIndex(j), surfaceWS, shadowData);
        if (RenderingLayersOverlap(surfaceWS, light))
        {
            color += GetLighting(surfaceWS, brdf, light);
        }
    }

    return color;
}

#endif
