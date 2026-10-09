#ifndef CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED
#define CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED

#define OIL_SHADE_BAND 0.82h
#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"

TEXTURECUBE(unity_SpecCube0);
SAMPLER(samplerunity_SpecCube0);

// OilOceanSurface writes this. The mesh stays a rest plane; the swell is a vertex sine.
float _OilOceanWaveHeight;

// One blurred cubemap tap. Fresnel mixes it in. No roughness, no BRDF.
half3 OilFakeReflect(half3 normalWS, half3 viewDir, half3 fallback)
{
    half3 r = reflect(-viewDir, normalWS);
    half4 raw = SAMPLE_TEXTURECUBE_LOD(
        unity_SpecCube0, samplerunity_SpecCube0, (float3)r, 5);
    half3 env = (half3)raw.rgb * (half)unity_SpecCube0_HDR.x * max(raw.a, 0.2h);
    return max(env, fallback);
}

struct Attributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    float3 positionWS : VAR_POSITION;
    float3 normalWS : VAR_NORMAL;
    float2 worldXZ : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// Center plus one offset. Two height taps.
void SampleCoast(float2 worldXZ, float reach, out half water, out half edge)
{
    water = CoastWaterMask(worldXZ);
    float r = clamp(reach, 8.0, 28.0);
    half e = abs(water - CoastWaterMask(worldXZ + float2(r, r * 0.65)));
    edge = saturate(e) * water;
}

// Two time-scrolled sines, written as a normal. Crest is the same wave, for color only.
half3 SineWaveNormal(float2 worldXZ, out half crest)
{
    float2 p = worldXZ * 0.045 + _Time.y * float2(0.35, -0.22);
    half s0 = (half)sin(p.x * 1.6 + p.y * 0.45);
    half s1 = (half)sin(p.y * 1.35 - p.x * 0.7 + _Time.y * 0.8);
    crest = s0 * 0.5h + 0.5h;
    return normalize(half3(s0 * 0.16h, 1.0h, s1 * 0.12h));
}

// One height tap up-sun. Enough for a painted island shadow, not a ray march.
half IslandPaintShadow(float2 worldXZ)
{
#if !defined(_RECEIVE_SHADOWS)
    return 1.0h;
#else
    if (GetDirectionalLightCount() <= 0 || _ShoreMapStrength <= 0.001)
    {
        return 1.0h;
    }

    half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
    float2 sunXZ = float2(L.x, L.z);
    float sunLen = length(sunXZ);
    if (sunLen < 0.04)
    {
        return 1.0h;
    }

    float2 towardSun = sunXZ / sunLen;
    float slope = max(L.y, 0.12) / sunLen;
    float water = (float)_ShoreWaterLevel;
    float bed = SampleShoreHeight01(worldXZ + towardSun * 140.0) * max(_ShoreHeightScale, 1.0);
    half hit = (half)smoothstep(water + 140.0 * slope + 6.0, water + 140.0 * slope + 22.0, bed);
    return 1.0h - hit;
#endif
}

Varyings OilOceanNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionWS = TransformObjectToWorld(input.positionOS);
    float2 xz = positionWS.xz;
    float t = _Time.y;
    float phase0 = dot(xz, float2(0.00264, 0.00182)) + t * 0.35;
    float phase1 = dot(xz, float2(-0.00155, 0.00241)) - t * 0.22;
    float s0 = sin(phase0);
    float s1 = sin(phase1);
    float amp = max(_OilOceanWaveHeight, 0.0);
    positionWS.y += (s0 + s1 * 0.35) * amp;
    float dhx = (cos(phase0) * 0.00264 + cos(phase1) * -0.00155 * 0.35) * amp;
    float dhz = (cos(phase0) * 0.00182 + cos(phase1) * 0.00241 * 0.35) * amp;
    output.positionWS = positionWS;
    output.positionCS_SS = TransformWorldToHClip(positionWS);
    output.normalWS = normalize(float3(-dhx, 1.0, -dhz));
    output.worldXZ = positionWS.xz;
    return output;
}

half4 OilOceanNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    half water;
    half edge;
    SampleCoast(input.worldXZ, _ShoreFoamWidth, water, edge);

    half crest;
    half3 waveN = SineWaveNormal(input.worldXZ, crest);
    half3 normalWS = normalize(lerp(normalize((half3)input.normalWS), waveN, 0.85h));
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
    half ndotV = saturate(dot(normalWS, viewDir));
    half fresnelInv = 1.0h - ndotV;
    half fresnel = fresnelInv * fresnelInv * fresnelInv;

    half3 skyCold = lerp(
        half3(0.45h, 0.62h, 0.85h),
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        0.45h);
    half3 sandWarm = half3(0.74h, 0.62h, 0.42h);
    half3 albedo = (half3)_BaseColor.rgb;
    albedo = lerp(albedo * half3(0.86h, 0.94h, 1.06h), albedo * half3(1.04h, 0.98h, 0.9h), crest);

    half relief = saturate((half)_PaintRelief);
    half3 paint = SamplePaintSeamless(input.worldXZ, _PaintTile);
    half stroke = PaintStroke(PaintLuma(paint), (half)_PaintContrast);
    albedo = lerp(albedo, paint, relief);

    half3 reflected = OilFakeReflect(normalWS, viewDir, skyCold);
    albedo = lerp(albedo, reflected, fresnel * 0.55h);
    albedo = lerp(albedo, sandWarm, edge * (1.0h - fresnel) * 0.55h);

    half foam = smoothstep(0.35h, 0.85h, edge) * water * saturate((half)_FoamStrength) * 0.55h;
    half alpha = (half)_BaseColor.a * water;
    half shade = IslandPaintShadow(input.worldXZ);

    Surface surface;
    surface.position = input.positionWS;
    surface.normal = (float3)normalWS;
    surface.interpolatedNormal = input.normalWS;
    surface.viewDirection = (float3)viewDir;
    surface.depth = -TransformWorldToView(input.positionWS).z;
    surface.color = (float3)albedo;
    surface.alpha = (float)alpha;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.smoothness = 0.0;
    surface.fresnelStrength = 0.0;
    surface.dither = InterleavedGradientNoise(GetFragment(input.positionCS_SS).positionSS, 0);
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

    half3 color = OilNPRLightingEx(
        surface,
        albedo,
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        (half3)_ShadowTint.rgb,
        (half3)_ShadowWarm.rgb,
        (half3)_SpecularColor.rgb,
        (half)_SpecularThreshold,
        max((half)_ShadeSteps, 3.0h),
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        crest,
        (half)_ShadowWobble,
        shade,
        0.0h);

    if (GetDirectionalLightCount() > 0)
    {
        half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
        half3 H = normalize(L + viewDir);
        half glint = saturate(dot(normalWS, H));
        glint *= glint;
        glint *= glint;
        glint *= glint;
        glint *= glint;
        color += (half3)_GlintColor.rgb * glint * shade * (1.0h - foam) * saturate((half)_SunPathStrength);
    }

    color = ImpastoDeviation(color, stroke, relief);
    color = lerp(color, (half3)_FoamColor.rgb, foam);
    color = OilWeaveTint(color, input.worldXZ * 0.08);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
