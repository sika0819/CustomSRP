#ifndef CUSTOM_OIL_NPR_LIGHTING_INCLUDED
#define CUSTOM_OIL_NPR_LIGHTING_INCLUDED

// Fixed-tap oil filter (not float[] PCF — GLES/half break). Soft cascade always —
// OilNPR has no _SOFT_CASCADE_BLEND multi_compile; dither cascade reads as stripes.
#define OIL_NPR_HARD_DIRECTIONAL_SHADOW
#define _SOFT_CASCADE_BLEND

#include "../ShaderLibrary/Surface.hlsl"
#include "../ShaderLibrary/Shadows.hlsl"
#include "../ShaderLibrary/Light.hlsl"
// Oil NPR must NOT include BRDF.hlsl — Lit owns the metallic BRDF path.
// Shared shade/posterize helpers only (no GetBRDF / DirectBRDF).
#include "../ShaderLibrary/OilBrdfCommon.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "OilTreeCrossBillboard.hlsl"
#include "OilVegetationLighting.hlsl"

TEXTURECUBE(unity_SpecCube0);
SAMPLER(samplerunity_SpecCube0);

#ifndef OIL_NPR_MIN_REFLECTIVITY
#define OIL_NPR_MIN_REFLECTIVITY 0.04h
#endif

// Real IBL: SH9 directional ambient sampled by surface normal (天顶→地平渐变).
// Vegetation uses OilVegetationAmbient; _OilPeriodAmbient declared in OilVegetationLighting.

half3 OilSampleSHDirectional(half3 n)
{
    return OilVegSampleSH(n);
}

// Returns directional SH ambient keyed to surfaceWS.normal.
// Weights (0.45 lit / 1.0 foliage / 0.15 rim) are applied at call sites
// so each lighting region can tune independently.
half3 OilNPRAmbient(Surface surfaceWS)
{
    return OilSampleSHDirectional((half3)surfaceWS.normal);
}

// Compat wrapper — shared implementation in OilBrdfCommon.
half QuantizeNdotL(half ndotL, half steps)
{
    return OilQuantizeNdotL(ndotL, steps);
}

// Lightweight metal remap for NPR only (not Lit GetBRDF).
half OilNPROneMinusReflectivity(half metallic)
{
    half range = 1.0h - OIL_NPR_MIN_REFLECTIVITY;
    return range - saturate(metallic) * range;
}

half3 OilNPRDiffuseAlbedo(half3 albedo, half metallic)
{
    return albedo * OilNPROneMinusReflectivity(metallic);
}

half3 OilNPRSpecF0(half3 albedo, half3 specularColor, half metallic)
{
    half m = saturate(metallic);
    half3 dielectric = lerp(half3(OIL_NPR_MIN_REFLECTIVITY, OIL_NPR_MIN_REFLECTIVITY, OIL_NPR_MIN_REFLECTIVITY), specularColor, 1.0h - m);
    return lerp(dielectric, albedo, m);
}

// forcedAttenuation < 0 samples the camera cascade. A large flat receiver
// otherwise draws that sphere as a disk that slides with the camera.

// Port of URP Simple Lit's LightingSpecular (Blinn-Phong).
// smoothness: 0→2 (very rough), 1→512 (mirror-like).
// URP Simple Lit uses exp2(10*s+1): 2→2048. We halve the exponent for a softer spec.
half OilNPRSpecularTerm(half3 N, half3 H, half smoothness)
{
    half NdotH = saturate(dot(N, H));
    // Gentler exponent curve for the oil-paint feel.
    half shininess = exp2(5.0h * smoothness + 1.0h);
    return pow(NdotH, shininess);
}

// Additive Blinn-Phong specular, capped so NPR painterly feel stays dominant.
// specularThreshold > 0 steps the lobe into oil glint masses (River/Waterfall UI).
half3 OilNPRLightSpecular(
    half3 lightColor,
    half3 L,
    half3 N,
    half3 V,
    half3 specColor,
    half smoothness,
    half attenuation,
    half specularThreshold)
{
    if (smoothness < 0.01h || any(specColor <= 0.0h))
    {
        return 0.0h;
    }

    half3 H = normalize(L + V);
    half spec = OilNPRSpecularTerm(N, H, smoothness);
    if (specularThreshold > 0.001h)
    {
        half edge = max(0.04h, 1.0h - specularThreshold);
        half stepped = smoothstep(specularThreshold - edge, specularThreshold + edge * 0.25h, spec);
        spec = lerp(spec, stepped * spec, 0.85h);
    }

    // Clamp specular strength so it doesn't overpower the NPR bands.
    return lightColor * specColor * spec * attenuation;
}

half3 OilNPRLightSpecular(
    half3 lightColor,
    half3 L,
    half3 N,
    half3 V,
    half3 specColor,
    half smoothness,
    half attenuation)
{
    return OilNPRLightSpecular(
        lightColor, L, N, V, specColor, smoothness, attenuation, 0.0h);
}

// Fresnel × (SH or SpecCube) oil environment; mobile stays SH-only.
half3 OilNPREnvironment(Surface surfaceWS, half3 albedo, half3 ambient)
{
    half metal = (half)saturate(surfaceWS.metallic);
    half smooth = (half)saturate(surfaceWS.smoothness);
    half3 N = (half3)surfaceWS.normal;
    half3 V = (half3)surfaceWS.viewDirection;
    half ndotV = saturate(dot(N, V));
    half fresnel = Pow4(1.0h - ndotV);
    fresnel *= saturate(smooth * 0.85h + metal);
    if (fresnel < 1e-4h && metal < 0.01h)
    {
        return 0.0h;
    }

    half3 f0 = OilNPRSpecF0(
        albedo,
        half3(OIL_NPR_MIN_REFLECTIVITY, OIL_NPR_MIN_REFLECTIVITY, OIL_NPR_MIN_REFLECTIVITY),
        metal);
    half3 env = ambient;
#if !defined(OIL_MOBILE_PERF)
    if (metal > 0.05h || smooth > 0.35h)
    {
        float perceptualRoughness =
            PerceptualSmoothnessToPerceptualRoughness(surfaceWS.smoothness);
        float3 uvw = reflect(-surfaceWS.viewDirection, surfaceWS.normal);
        uvw = OilBrdfWarpReflectDir(uvw, surfaceWS.position, perceptualRoughness);
        float mip = PerceptualRoughnessToMipmapLevel(perceptualRoughness);
        mip += OilBrdfEnvMipBias(perceptualRoughness);
        float4 environment = SAMPLE_TEXTURECUBE_LOD(
            unity_SpecCube0, samplerunity_SpecCube0, uvw, mip);
        env = (half3)DecodeHDREnvironment(environment, unity_SpecCube0_HDR);
        env = OilStylizeEnvSpec(env);
    }
#endif
    return env * f0 * max(fresnel, metal * 0.25h) * (0.4h + 0.6h * metal);
}

// Soft blob Other light for Oil NPR (TerrainOilNPR / OilNPR / grass share this).
//
// RCA: TerrainLit "soft" rim is mostly other-shadow PCF, NOT Unity Square×Square/r².
// That Unity geom still paints a chalk isocontour on flat ground; mobile skips the
// atlas so the disk edge is pure hard att. Fix = one cheap smooth falloff (no
// multi-tap, no 1/r² rings, no QuantizeNdotL). Desktop still multiplies soft PCF.
Light GetOilSoftOtherLight(
    int index,
    Surface surfaceWS
#if !defined(OIL_MOBILE_PERF)
    , ShadowData shadowData
#endif
)
{
    OtherLightData data = _OtherLightData[index];
    Light light;
    light.color = data.color.rgb;
    float3 position = data.position.xyz;
    float3 ray = position - surfaceWS.position;
    light.direction = normalize(ray);
    float distanceSqr = max(dot(ray, ray), 0.00001);
    light.renderingLayerMask = asuint(data.directionAndMask.w);

    // tNorm: 0 at center → 1 at Unity range. Finish fade before range so the
    // Forward+ / Light.range hard-zero never shows as a 1px rim.
    float tNorm = sqrt(distanceSqr * data.position.w);
    // Tighter toe than 0.12→0.88 — wide soft + high gain washed night sand white.
    half rangeAtt = 1.0h - smoothstep(0.05h, 0.72h, (half)tNorm);
    // Near InverseSquare energy (not flat blob). 0.035 was ~7× too soft at 5–10 m.
    half core = rcp(1.0h + (half)distanceSqr * 0.11h);

    float3 spotDirection = data.directionAndMask.xyz;
    float spotLin = saturate(
        dot(spotDirection, light.direction) * data.spotAngle.x + data.spotAngle.y);
    half spotAtt = smoothstep(0.0h, 1.0h, (half)spotLin);

    // Light.color already carries intensity — no 2.55× dump onto night ground.
    half att = spotAtt * rangeAtt * core;

#if !defined(OIL_MOBILE_PERF)
    OtherShadowData otherShadowData = GetOtherShadowData(data.shadowData);
    otherShadowData.lightPositionWS = position;
    otherShadowData.lightDirectionWS = light.direction;
    otherShadowData.spotDirectionWS = spotDirection;
    otherShadowData.strength *= shadowData.strength;
    att *= (half)GetOtherShadowAttenuation(
        otherShadowData, shadowData, surfaceWS);
#endif

    light.attenuation = att;
    return light;
}

// Forward+ Point/Spot soft fill. Water: OIL_NPR_NO_OTHER_LIGHTS (no disk on ocean).
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
    // Campfire only; no other-shadow atlas taps (FireController also sets Shadows=None).
    last = first;
#endif
    // shadeSteps intentionally unused: QuantizeNdotL × radial att = chalk rings on ground.
    half metal = (half)saturate(surfaceWS.metallic);
    half3 diffuseAlbedo = OilNPRDiffuseAlbedo(albedo, metal);
    half3 sum = 0.0h;
#if !defined(OIL_MOBILE_PERF)
    half smooth = (half)saturate(surfaceWS.smoothness);
    half3 specF0 = OilNPRSpecF0(albedo, half3(0.04h, 0.04h, 0.04h), metal);
    half3 V = (half3)surfaceWS.viewDirection;
    ShadowData shadowData = GetShadowData(surfaceWS);
#endif
    for (int j = first; j <= last; j++)
    {
#if defined(OIL_MOBILE_PERF)
        Light light = GetOilSoftOtherLight(tile.GetLightIndex(j), surfaceWS);
#else
        Light light = GetOilSoftOtherLight(
            tile.GetLightIndex(j), surfaceWS, shadowData);
#endif
        if ((surfaceWS.renderingLayerMask & light.renderingLayerMask) == 0u)
        {
            continue;
        }

        half3 L = (half3)light.direction;
        half att = (half)light.attenuation;
        if (att <= 1e-5h)
        {
            continue;
        }

        // Continuous half-Lambert only (no poster bands → no concentric rings).
        half shade = saturate(dot(N, L) * 0.5h + 0.5h);
        half3 lc = (half3)light.color;
#if !defined(OIL_MOBILE_PERF)
        // Att toe: collapse chroma so warm fill → cool night isn't a hue cliff.
        half luma = max(lc.r, max(lc.g, lc.b));
        half3 lcDim = half3(luma, luma * 0.90h, luma * 0.78h) * 0.30h;
        half chromaKeep = smoothstep(0.0h, 0.45h, att);
        lc = lerp(lcDim, lc, chromaKeep);
#endif
        sum += diffuseAlbedo * lc * (shade * att);
#if !defined(OIL_MOBILE_PERF)
        if (metal > 0.01h || smooth > 0.01h)
        {
            sum += OilNPRLightSpecular(lc, L, N, V, specF0, smooth, att);
        }
#endif
    }

    return sum;
#endif
}

// Core lighting with specular support.
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
    half brush,
    half forcedAttenuation,
    half quantize,
    half smoothness,
    float2 screenUV)
{
    half metal = (half)saturate(surfaceWS.metallic);
    half3 diffuseAlbedo = OilNPRDiffuseAlbedo(albedo, metal);
    half3 specF0 = OilNPRSpecF0(albedo, specularColor, metal);
    half3 N = (half3)surfaceWS.normal;
    half3 other = OilNPROtherLights(surfaceWS, albedo, N, shadeSteps, screenUV);
    half3 env = OilNPREnvironment(surfaceWS, albedo, ambient);

    if (GetDirectionalLightCount() <= 0)
    {
        half3 unlit = ambient * diffuseAlbedo + paintRim * ambient + other + env;
        // Soft fire pull — keep local, don't re-lift a wide soft pool into white.
        half fireW = smoothstep(0.02h, 0.18h, length(other));
        if (fireW > 1e-4h)
        {
            half3 warmUmbra = diffuseAlbedo * half3(0.40h, 0.20h, 0.08h);
            unlit = lerp(
                unlit,
                max(unlit, warmUmbra * 0.45h + other * 0.25h),
                fireW * 0.35h);
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
    // Same nightAmt as foliage (light luma + oilFill period) so a brighter moon
    // still gets fill/key treatment — pure lightLum*4 collapses once intensity rises.
    half lightLumEarly = OilVegMainLightLuma(lightColor);
    half nightAmt = OilVegetationNightAmount(lightLumEarly);
#if defined(OIL_NPR_NO_OTHER_LIGHTS) || defined(OIL_WATER_GREY_UMBRA)
    // Ocean / River: no cascade shadow → umbra dead code; shadeLift stays day value.
    half attSoft = smoothstep(0.05h, 0.55h, attenuation);
    half lit = halfLambert * lerp(0.28h, 1.0h, attSoft);
#elif defined(TERRAIN_SHADOW_RECEIVER)
    // Terrain cast: day keeps a soft floor (noon blobs weren't charcoal); night drops hard.
    half attSoft = smoothstep(0.02h, 0.38h, attenuation);
    half shadeLift = lerp(0.18h, 0.02h, nightAmt);
    half lit = halfLambert * lerp(shadeLift, 1.0h, attSoft);
#else
    // Day shadeLift higher → softer noon umbra; night lower → moon cast reads darker.
    half attSoft = smoothstep(0.05h, 0.55h, attenuation);
    half shadeLift = lerp(0.40h, 0.08h, nightAmt);
    half lit = halfLambert * lerp(shadeLift, 1.0h, attSoft);
#endif
#if !defined(OIL_MOBILE_PERF)
    lit += ((half)surfaceWS.dither - 0.5h) * (quantize > 0.001h ? (1.0h / steps) : 0.0h);
#endif
    half litCont = saturate(lit);
    lit = QuantizeNdotL(litCont, steps);
    // Mix a little continuous shade so oil masses stay, chalk steps don't.
    lit = lerp(lit, litCont, 0.32h);

    // lightColor is VisibleLight.finalColor (OilSkyboxTime LightPresets × intensity).
    half lightPeak = max(1e-3h, max(lightColor.r, max(lightColor.g, lightColor.b)));
    // Night: SH often near-black while foliage NightKeep stays readable — floor with oilFill
    // and punch moon key so lit−umbra (= key) has absolute contrast on terrain.
    half3 amb = ambient;
    half3 keyColor = lightColor;
    half ambW = 0.45h;
#if defined(TERRAIN_SHADOW_RECEIVER)
    // Day key compressed — noon light≈1.5 × bright sand chalked LDR (no tonemap).
    // Night still punches moon key; ambW stays modest so midtones don't milk.
    keyColor = lightColor * 0.68h;
    ambW = 0.40h;
#endif
    if (nightAmt > 0.01h)
    {
        half3 period = (half3)_OilPeriodAmbient.rgb;
        amb = max(amb, period * lerp(0.40h, 0.65h, nightAmt));
#if defined(TERRAIN_SHADOW_RECEIVER)
        // Lit ground keeps fill; umbra uses a lower weight below so cast reads darker.
        ambW = lerp(0.40h, 0.68h, nightAmt);
        keyColor = lightColor * lerp(0.68h, 2.05h, nightAmt);
#else
        ambW = lerp(0.45h, 0.60h, nightAmt);
        keyColor = lightColor * lerp(1.0h, 1.65h, nightAmt);
#endif
    }
    half3 ambTerm = amb * ambW;
    half3 litColor = diffuseAlbedo * (ambTerm + keyColor);
#if defined(TERRAIN_SHADOW_RECEIVER)
    // Soft highlight ceiling — preserve pigment, avoid chalk flats on lit slopes.
    litColor = min(litColor, diffuseAlbedo * 1.12h + half3(0.04h, 0.035h, 0.028h));
#endif
#if defined(OIL_NPR_NO_OTHER_LIGHTS) || defined(OIL_WATER_GREY_UMBRA)
    // Water (ocean / river / waterfall): grey shade bands — no cool teal→magenta night.
    half3 umbraColor = diffuseAlbedo * half3(0.18h, 0.20h, 0.24h) + half3(0.02h, 0.022h, 0.025h);
#elif defined(TERRAIN_SHADOW_RECEIVER)
    // Terrain cast umbra: still cooler than props, but day pigment lifted (was too deep at noon).
    half3 pigment = half3(0.12h, 0.16h, 0.26h);
    half3 baseUmbra = diffuseAlbedo * pigment + half3(0.012h, 0.014h, 0.022h);
    half3 tinted = lerp(diffuseAlbedo * shadowTint * 0.55h, diffuseAlbedo * shadowWarm * 0.5h, saturate(brush));
    half3 umbraColor = lerp(baseUmbra, tinted, 0.40h);
#else
    // Teal-blue pigment + call-site cool/warm tint. Avoid magenta (night rocks went purple).
    half3 pigment = half3(0.22h, 0.28h, 0.38h);
    half3 baseUmbra = diffuseAlbedo * pigment + half3(0.022h, 0.026h, 0.040h);
    half3 tinted = lerp(diffuseAlbedo * shadowTint, diffuseAlbedo * shadowWarm, saturate(brush));
    half3 umbraColor = lerp(baseUmbra, tinted, 0.55h);
#endif
    // Mild light-hue pull so noon warmth does not dirty the cool umbra.
    // Terrain: weaker pull — was re-lifting cast umbra after the deeper pigment.
    half3 lightHue = lightColor / lightPeak;
#if defined(TERRAIN_SHADOW_RECEIVER)
    umbraColor = lerp(umbraColor, diffuseAlbedo * lightHue * 0.2h + lightColor * 0.1h, 0.08h);
#else
    umbraColor = lerp(umbraColor, diffuseAlbedo * lightHue * 0.2h + lightColor * 0.1h, 0.18h);
#endif
    // Night: cut ambient fill + tighten vs litColor so moon cast isn't washed out
    // (user: 白天阴影太深，夜晚阴影太浅).
    if (nightAmt > 0.01h)
    {
#if defined(OIL_NPR_NO_OTHER_LIGHTS) || defined(OIL_WATER_GREY_UMBRA)
        half3 nightUmbra = diffuseAlbedo * (amb * 0.22h) * half3(0.88h, 0.92h, 1.0h);
        umbraColor = lerp(umbraColor, nightUmbra, nightAmt * 0.85h);
        umbraColor = min(umbraColor, litColor * lerp(1.0h, 0.62h, nightAmt));
#elif defined(TERRAIN_SHADOW_RECEIVER)
        half3 nightUmbra = diffuseAlbedo * (amb * 0.14h) * half3(0.62h, 0.72h, 0.92h);
        umbraColor = lerp(umbraColor, nightUmbra, nightAmt * 0.95h);
        umbraColor = min(umbraColor, litColor * lerp(1.0h, 0.30h, nightAmt));
#else
        half3 nightUmbra = diffuseAlbedo * (amb * 0.20h) * half3(0.70h, 0.80h, 0.98h);
        umbraColor = lerp(umbraColor, nightUmbra, nightAmt * 0.90h);
        umbraColor = min(umbraColor, litColor * lerp(1.0h, 0.45h, nightAmt));
#endif
    }
#if defined(OIL_MOBILE_PERF)
    {
        // Narrower/softer than 0→0.12×0.5 — wide soft other was bleaching night umbra.
        half warmMix = smoothstep(0.02h, 0.20h, length(other));
        umbraColor = lerp(
            umbraColor,
            diffuseAlbedo * half3(0.40h, 0.20h, 0.08h) + other * 0.18h,
            warmMix * 0.32h);
    }
    // Most env mats ship Smoothness=0 — skip view normalize + Blinn path.
    half3 spec = 0.0h;
    if (smoothness >= 0.01h && any(specF0 > 0.0h))
    {
        half3 V = normalize((half3)(_WorldSpaceCameraPos - surfaceWS.position));
        spec = OilNPRLightSpecular(
            lightColor, L, N, V, specF0, smoothness, attenuation, specularThreshold);
    }
    half3 colorM = lerp(umbraColor, litColor, lit) + other + spec + env;
    colorM += paintRim * ambient * 0.15h;
    return colorM;
#else
    {
        half warmMix = smoothstep(0.02h, 0.20h, length(other));
        umbraColor = lerp(
            umbraColor,
            diffuseAlbedo * half3(0.40h, 0.20h, 0.08h) + other * 0.18h,
            warmMix * 0.40h);
    }

    half3 color = lerp(umbraColor, litColor, lit) + other + env;
    half3 V = (half3)surfaceWS.viewDirection;
    half inv = 1.0h - saturate(dot(N, V));
    half rim = inv * inv;
    color += diffuseAlbedo * lerp(half3(1.0h, 0.78h, 0.48h), lightHue, 0.55h) * rim * 0.08h;
    color += paintRim * ambient * 0.15h;
    half3 ceilC = diffuseAlbedo * (0.85h + saturate(lightPeak) * 0.9h) + lightColor * 0.12h + other;
    color = min(color, ceilC);
    // Blinn-Phong specular from main directional light (metal F0).
    half3 mainSpec = OilNPRLightSpecular(
        lightColor, L, N, V, specF0, smoothness, attenuation, specularThreshold);
    color += mainSpec;
    return color;
#endif
}

// Boluo EnvFoliage: shared OilVegetationDirect + local other lights.
half3 OilFoliageLighting(Surface surfaceWS, half3 albedo, half shadeSteps, float2 screenUV)
{
    half3 N = normalize((half3)surfaceWS.normal);
    half3 lightColor = 0.0h;
    half3 L = half3(0.0h, 1.0h, 0.0h);
    half att = 0.0h;
    half lightLum = 0.0h;
    if (GetDirectionalLightCount() > 0)
    {
        ShadowData shadowData = GetShadowData(surfaceWS);
        Light light = GetDirectionalLight(0, surfaceWS, shadowData);
        lightColor = (half3)light.color;
        L = (half3)light.direction;
        att = (half)light.attenuation;
        lightLum = OilVegMainLightLuma(lightColor);
    }

    half3 lit = OilVegetationDirect(albedo, N, lightColor, L, att, lightLum);
    lit += OilNPROtherLights(surfaceWS, albedo, N, shadeSteps, screenUV);
    return OilVegetationNightKeep(lit, albedo, N, lightLum);
}

// Far cross-card: same OilFoliageLighting as near leaves (BoluoUnity LitAlbedoObject).
// Dark: OilVegetationLitNormal biases toward up → ambient matches grass zenith fill.
half3 OilTreeCrossBillboardShade(
    half3 albedo,
    half3 bakedNormalEnc,
    half normalStrength,
    Surface surfaceWS,
    float2 screenUV)
{
    half strength = saturate(normalStrength);
    half3 bakedOS = OilTreeCrossBillboardDecodeObjectNormal(bakedNormalEnc);
    half3 bakedWS = (half3)TransformObjectToWorldNormal((float3)bakedOS);
    half3 n = normalize(lerp(half3(0.0h, 1.0h, 0.0h), bakedWS, strength));

    Surface s = surfaceWS;
    s.normal = (float3)n;
    s.interpolatedNormal = (float3)n;
    return OilFoliageLighting(s, albedo, 2.0h, screenUV);
}

// Wrapper: sample cascade shadows (forcedAttenuation < 0).
// Ambient is sampled via OilNPRAmbient so callers can't flatten the IBL gradient.
half3 OilNPRLighting(
    Surface surfaceWS,
    half3 albedo,
    half3 shadowTint,
    half3 shadowWarm,
    half3 specularColor,
    half specularThreshold,
    half shadeSteps,
    half paintRim,
    half brush,
    half quantize,
    half smoothness,
    float2 screenUV)
{
    half3 ambient = OilNPRAmbient(surfaceWS);
    return OilNPRLightingEx(
        surfaceWS, albedo, ambient, shadowTint, shadowWarm, specularColor,
        specularThreshold, shadeSteps, paintRim,
        brush, -1.0h, quantize, smoothness, screenUV);
}

#endif
