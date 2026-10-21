#ifndef OIL_RAIN_WET_SURFACE_INCLUDED
#define OIL_RAIN_WET_SURFACE_INCLUDED

// =============================================================================
// OilRainWetSurface.hlsl
// NPR shader injection for wet ground / surface enhancement.
// Include this file AFTER the main BRDF in OilNPRPass.hlsl or OilTerrainNPRPass.hlsl,
// then call ApplyWetSurface() in the fragment shader.
// =============================================================================

// Shader variables set by OilRainGPUParticles.cs via Shader.SetGlobalFloat/Color
// These must be declared before use.
#ifndef OIL_RAIN_WET_GLOBALS
#define OIL_RAIN_WET_GLOBALS
float   _OilRainWetStrength;     // 0=disabled, 1=full wet
float   _OilRainWetSmoothnessBoost; // 0~0.3 extra smoothness
float   _OilRainWetDarkening;    // 0~0.4 extra darkening
float4  _OilRainAmbientColor;   // water / wet ambient tint
#endif

// -----------------------------------------------------------------------------
// Wet surface enhancement — call from fragment after main BRDF
// Works with NPR-style ramp lighting (OilNPRLighting.hlsl).
// -----------------------------------------------------------------------------
half3 ApplyWetSurface(half3 baseColor, half3 brdfResult, half NdotV, half roughness)
{
    half wetStrength = _OilRainWetStrength;
    if (wetStrength <= 0.0)
        return brdfResult;

    half smoothness = 1.0 - roughness;
    half wetSmoothness = saturate(smoothness + _OilRainWetSmoothnessBoost);

    // Extra specular glint on wet surfaces
    half wetSpec = pow(saturate(NdotV), lerp(4.0, 64.0, wetSmoothness));
    wetSpec *= wetSmoothness * wetSmoothness * wetStrength * 0.4;

    // Darken diffuse (wet surfaces absorb more light)
    half darkening = 1.0 - _OilRainWetDarkening * wetStrength;
    half3 darkenedBase = baseColor * darkening;

    // Ambient wet tint — water colour reflecting off surface
    half3 wetAmbient = _OilRainAmbientColor.rgb * wetStrength * 0.3;

    half3 wetBRDF = brdfResult;
    wetBRDF *= darkening;
    wetBRDF += wetAmbient;
    wetBRDF += wetSpec * 0.3; // small additive specular contribution

    return wetBRDF;
}

// -----------------------------------------------------------------------------
// Wet surface wetness mask — call from fragment.
// Returns 1 if this fragment should be considered "wet" based on world position.
// (Actual surface wetness is uniform across scene when _OilRainWetStrength > 0.)
// Extend this to sample a procedural wetness texture if needed.
// -----------------------------------------------------------------------------
half WetnessMask(float3 positionWS)
{
    // For now, full wetness everywhere rain is active.
    // Could be extended with a wetness coverage texture or distance-to-water probe.
    return _OilRainWetStrength;
}

#endif // OIL_RAIN_WET_SURFACE_INCLUDED
