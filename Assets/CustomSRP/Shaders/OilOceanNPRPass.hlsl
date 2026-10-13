#ifndef CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED
#define CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED

#define OIL_SHADE_BAND 0.82h
#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"

TEXTURE2D(_SparkleBrush);
SAMPLER(sampler_SparkleBrush);
TEXTURE2D(_FoamMap);
SAMPLER(sampler_FoamMap);

// OilOceanSurface writes this. Mesh is a rest plane (Boluo WaterGrid).
// Two world-space swells, wavelength ~2 km, so a ~500 m grid can hold them.
// Short paint ripples stay in the fragment.
float _OilOceanWaveHeight;

// Boluo's lane is tuned for a few hundred metres. Moorea is viewed from kilometres out.
static const float kSunPathScale = 16.0;

float2 OilSunRotate(float2 p, float ang)
{
    float s, c;
    sincos(ang, s, c);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

half OilSampleSunStroke(float2 uv)
{
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
    {
        return 0.0h;
    }

    return (half)SAMPLE_TEXTURE2D(_SparkleBrush, sampler_SparkleBrush, uv).r;
}

// One StarBrush dab in a world cell. keep drops whole cells before the tap.
half OilSunStamp(float2 st, float cells, float seed, float keep)
{
    float2 cell = floor(st * cells);
    float h = OilHash21(cell + seed);
    if (h < keep)
    {
        return 0.0h;
    }

    float2 uv = frac(st * cells) - 0.5;
    float h1 = OilHash21(cell + seed + 4.7);
    float h2 = OilHash21(cell + seed + 8.3);
    uv += (float2(h, h1) - 0.5) * 0.34;
    uv = OilSunRotate(uv, (h2 - 0.5) * 0.14);
    uv.x *= lerp(0.72, 1.08, h);
    uv.y *= lerp(1.55, 2.35, h1);
    half brush = saturate((OilSampleSunStroke(uv + 0.5) - 0.22h) * 2.6h);
    return brush * (half)lerp(0.62, 1.0, h1);
}

// Camera-to-sun column. Near is narrow, far opens, noon tightens.
void OilSunLane(
    float2 worldXZ, half3 normalWS, float2 sunH, float elev, float along,
    out half column, out half halo)
{
    float2 side = float2(-sunH.y, sunH.x);
    float2 fromCam = worldXZ - _WorldSpaceCameraPos.xz;
    float halfW = lerp(3.5, 16.0, saturate(along / (280.0 * kSunPathScale)));
    halfW *= lerp(1.05, 0.68, elev);
    halfW *= 6.0 / max(_SunPathWidth, 0.25);
    float snake = dot(float2(normalWS.x, normalWS.z), side) * halfW * 0.45;
    float lat = abs(dot(fromCam, side) + snake);
    float x = saturate(1.0 - lat / max(halfW, 1e-3));
    x = x * x * (3.0 - 2.0 * x);
    float y = saturate(1.0 - lat / max(halfW * 2.55, 1e-3));
    half near = (half)saturate((along - 6.0 * kSunPathScale) / (40.0 * kSunPathScale));
    column = (half)x * near;
    halo = (half)(y * y * lerp(1.0, 0.42, elev)) * near;
}

// Low sun warms the water toward the sun. Noon leaves the blue alone.
half3 OilSunGoldWash(half3 albedo, float2 worldXZ, half3 normalWS, half3 viewDir)
{
    if (GetDirectionalLightCount() <= 0)
    {
        return albedo;
    }

    half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
    float elev = saturate(L.y);
    half lowSun = saturate((1.0h - (half)elev * 2.05h) * saturate(((half)L.y + 0.02h) * 8.0h));
    if (lowSun < 0.001h)
    {
        return albedo;
    }

    float2 sunXZ = float2(L.x, L.z);
    float sunLen = length(sunXZ);
    float2 toFrag = worldXZ - _WorldSpaceCameraPos.xz;
    float toLen = length(toFrag);
    half toward = 0.0h;
    if (sunLen > 0.05 && toLen > 1.0)
    {
        toward = saturate((half)dot(toFrag / toLen, sunXZ / sunLen));
    }

    half inv = 1.0h - saturate(dot(normalWS, viewDir));
    half grazing = inv * sqrt(inv);
    half wash = lowSun * toward * lerp(0.46h, 1.0h, grazing);
    half3 gold = half3(0.96h, 0.70h, 0.36h);
    half3 warm = saturate(lerp(albedo * half3(1.18h, 0.92h, 0.58h), gold, 0.42h));
    return lerp(albedo, warm, wash * 0.58h);
}

// Sun lane times three StarBrush stamps. Samples stay inside the column.
half3 OilSunPath(float3 positionWS, half3 normalWS, half stroke, half shade, half foam)
{
    if (GetDirectionalLightCount() <= 0)
    {
        return half3(0.0h, 0.0h, 0.0h);
    }

    half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
    half lit = saturate(((half)L.y + 0.06h) * 30.0h);
    float2 sunXZ = float2(L.x, L.z);
    float sunLen = length(sunXZ);
    half stable = saturate(((half)sunLen - 0.05h) * 20.0h);
    if (lit * stable < 0.001h)
    {
        return half3(0.0h, 0.0h, 0.0h);
    }

    float2 sunH = sunXZ / max(sunLen, 0.0001);
    float along = dot(positionWS.xz - _WorldSpaceCameraPos.xz, sunH);
    float elev = saturate(L.y);
    half column, halo;
    OilSunLane(positionWS.xz, normalWS, sunH, elev, along, column, halo);
    if (column + halo < 0.001h)
    {
        return half3(0.0h, 0.0h, 0.0h);
    }

    float2 side = float2(-sunH.y, sunH.x);
    float2 st = float2(dot(positionWS.xz, side), dot(positionWS.xz, sunH));
#if defined(OIL_MOBILE_PERF)
    // One StarBrush stamp — keeps oil sun lane, cuts two texture taps over the sea.
    float dab = OilSunStamp(st, 0.16 / kSunPathScale, 0.0, 0.38) * (column + halo * 0.45);
    dab *= lerp(0.62, 1.12, (float)stroke);
#else
    float dab = OilSunStamp(st, 0.09 / kSunPathScale, 31.0, 0.26) * column;
    dab += OilSunStamp(st, 0.16 / kSunPathScale, 0.0, 0.38) * column * 0.88;
    dab += OilSunStamp(st, 0.34 / kSunPathScale, 7.3, 0.62) * halo * 0.55;
    dab *= lerp(0.52, 1.18, (float)stroke);
#endif

    float dist = length(_WorldSpaceCameraPos - positionWS);
    dab *= saturate((1180.0 * kSunPathScale - dist) / (860.0 * kSunPathScale));
    dab *= saturate(elev * 2.6 + 0.18) * lit * stable;
    dab *= shade * (1.0h - foam);
    if (dab < 1e-4)
    {
        return half3(0.0h, 0.0h, 0.0h);
    }

    half3 lightColor = (half3)_DirectionalLightData[0].color.rgb;
    half peak = max(1e-3h, max(lightColor.r, max(lightColor.g, lightColor.b)));
    // Keep period key chroma (dusk orange / night cool); cream only softens highlights.
    half3 cream = half3(1.04h, 0.95h, 0.72h);
    half3 tint = saturate(lerp(lightColor / peak, cream, 0.22h));
    tint *= (half3)_GlintColor.rgb;
    return tint * (half)dab * saturate((half)_SunPathStrength);
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

// Meters of water over the terrain bed. The overlook looks across the sea, so a
// view-ray depth stays huge even in the shallows and the sliders never move.
// The heightmap column is the shoreline itself: 0 on the waterline, ~13 m offshore.
float WaterColumnMeters(float2 worldXZ)
{
    float bed = SampleShoreHeight01(worldXZ) * max(_ShoreHeightScale, 1.0);
    return max((float)_ShoreWaterLevel - bed, 0.0);
}

// FoamMap 只打碎边；带宽由 coast edge 决定（已验证有信号）。
void SampleShoreBrush(
    float2 worldXZ, float2 normalXZ, float gradient, float warp01,
    out half mask, out half wash)
{
    float2 flow = float2(0.82, 0.57);
    float tile = max(_IntersectionTiling, 0.0001);
    float2 uv = (worldXZ + normalXZ * _IntersectionDistortion) * tile;
    uv += flow * (_Time.y * _IntersectionSpeed * tile);
    uv += (warp01 - 0.5) * 0.08;
    half luma = (half)SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, uv).r;
    half g = saturate((half)gradient);
    half noise = lerp(0.85h, 1.0h, luma);
    mask = saturate(g * noise * saturate((half)_FoamStrength));
    wash = saturate(g * 0.55h * saturate((half)_FoamStrength));
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

// Center plus one offset. Two height taps.
void SampleCoast(float2 worldXZ, float reach, out half water, out half edge)
{
    water = CoastWaterMask(worldXZ);
    float r = clamp(reach, 2.0, 28.0);
    half e = abs(water - CoastWaterMask(worldXZ + float2(r, r * 0.65)));
    edge = saturate(e) * water;
}

// 固定探针米数的水线白边。edge 差分在运行时已证明有信号（沙色 wash 同源）。
// 不用 |c-0.5|/|∇c|：软羽化岸上 ∇≈0 会整条灭沫。
half CoastFoamRim(float2 worldXZ, float foamProbeM, half water)
{
    half wMask;
    half foamEdge;
    SampleCoast(worldXZ, foamProbeM, wMask, foamEdge);
    // 放大弱 edge，再用 smoothstep 收成窄带（始终差不多宽）。
    half e = saturate(foamEdge * 4.0h);
    half rim = smoothstep(0.08h, 0.42h, e);
    rim *= 1.0h - smoothstep(0.72h, 1.0h, e) * 0.15h;
    return rim * water * saturate(wMask);
}

Varyings OilOceanNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionWS = TransformObjectToWorld(input.positionOS);
    float2 xz = positionWS.xz;
    float t = _Time.y;
    // Same layout as Boluo ApplyWaves: two directions, height plus a little chop.
    // k ≈ 2π / 2000 m and 2π / 1400 m — coarser than that and the crest facets.
    float2 dir0 = float2(0.82, 0.57);
    float2 dir1 = float2(-0.45, 0.89);
    float k0 = 0.00314;
    float k1 = 0.00449;
    float phase0 = k0 * dot(dir0, xz) + t * 0.35;
    float phase1 = k1 * dot(dir1, xz) - t * 0.22;
    float s0 = sin(phase0);
    float c0 = cos(phase0);
    float s1 = sin(phase1);
    float c1 = cos(phase1);
    float amp = max(_OilOceanWaveHeight, 0.0);
    float steep = 0.12 * amp;
    positionWS.y += (s0 + s1 * 0.35) * amp;
    positionWS.xz += dir0 * (steep * c0) + dir1 * (steep * 0.35 * c1);
    float dhx = (dir0.x * k0 * c0 + dir1.x * k1 * 0.35 * c1) * amp;
    float dhz = (dir0.y * k0 * c0 + dir1.y * k1 * 0.35 * c1) * amp;
    output.positionWS = positionWS;
    output.positionCS_SS = TransformWorldToHClip(positionWS);
    output.normalWS = normalize(float3(-dhx, 1.0, -dhz));
    output.worldXZ = xz;
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
    half3 geoN = normalize((half3)input.normalWS);
    half3 normalWS = normalize(lerp(geoN, waveN, 0.85h));
    // Dawn and dusk sit on the horizon. The ripple then crosses the toon steps
    // and the grid reads as bands. Noon keeps the ripple.
    half sunY = 1.0h;
    if (GetDirectionalLightCount() > 0)
    {
        sunY = (half)_DirectionalLightData[0].directionAndMask.y;
    }

    half day = smoothstep(0.08h, 0.35h, saturate(sunY));
    half3 lightN = normalize(lerp(half3(0.0h, 1.0h, 0.0h), normalWS, day));
    // Overlook: wave NdotL quantize bands read as huge shade disks on a flat sea.
    // Keep object-style NPR near camera; flatten the lighting normal when far.
    float camDist = length(_WorldSpaceCameraPos - input.positionWS);
    half overlook = (half)smoothstep(120.0, 420.0, camDist);
    lightN = normalize(lerp(lightN, half3(0.0h, 1.0h, 0.0h), overlook * 0.92h));
    half shadeBrush = lerp(0.5h, crest, day);
    shadeBrush = lerp(shadeBrush, 0.5h, overlook);
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
    half ndotV = saturate(dot(normalWS, viewDir));
    half fresnelInv = 1.0h - ndotV;
    half fresnel = fresnelInv * fresnelInv * fresnelInv;

    // 基础配色：天顶 #80B5E2 · 沙土 #B9A07C
    half3 skyCold = lerp(
        half3(0.50h, 0.71h, 0.89h),
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        0.45h);
    half3 sandWarm = half3(0.725h, 0.627h, 0.486h);
    half3 albedo = (half3)_BaseColor.rgb;
    albedo = lerp(albedo * half3(0.86h, 0.94h, 1.06h), albedo * half3(1.04h, 0.98h, 0.9h), crest);

    half relief = saturate((half)_PaintRelief);
    // Camera-relative paint UV so the OilOcean texture follows the camera when moving,
    // instead of sliding off the screen. A gentle horizontal scroll keeps the surface alive
    // and aligns the stroke read with the horizon.
    float2 paintUV = input.worldXZ - _WorldSpaceCameraPos.xz;
    paintUV += _Time.y * float2(0.12, 0.03);
    half3 paint = SamplePaintSeamless(paintUV, _PaintTile);
    half stroke = PaintStroke(PaintLuma(paint), (half)_PaintContrast);
    albedo = lerp(albedo, paint, relief);

    float column = WaterColumnMeters(input.worldXZ);
#if defined(OIL_MOBILE_PERF)
    float columnN = OilValueNoise(input.worldXZ * 0.012);
#else
    float columnN = OilFbm(input.worldXZ * 0.012);
#endif
    float columnSoft = max(column + (columnN - 0.5) * 1.2, 0.0);
    // Shallow shelf: clear near the bank, opaque offshore. Overlook keeps a
    // softer shelf so the coast still reads without a full-screen shade disk.
    float clearDepth = max(_DepthVertical, 0.4) * lerp(4.0, 10.0, (float)overlook);
    half glass = (half)(1.0 - smoothstep(0.15, clearDepth, columnSoft)) * water;
    glass *= lerp(1.0h, 0.55h, overlook);

    albedo = lerp(albedo, skyCold, fresnel * 0.55h);
    albedo = OilSunGoldWash(albedo, input.worldXZ, normalWS, viewDir);
    // Sand wash on the shelf; ease off where refraction already shows the bed.
    albedo = lerp(
        albedo,
        sandWarm,
        edge * (1.0h - fresnel) * lerp(0.55h, 0.22h, glass));

    // 岸沫：固定 ~6m 探针的 coast edge（与沙色同源信号），收成窄白带。
    float foamProbeM = clamp(_IntersectionLength * 11.0, 5.0, 8.0);
    half lipGrad = CoastFoamRim(input.worldXZ, foamProbeM, water);
    // 兜底：主 edge（ShoreFoamWidth）也有信号时不让沫整条消失。
    lipGrad = max(lipGrad, smoothstep(0.12h, 0.45h, edge) * water * 0.9h);
    half alpha = (half)_BaseColor.a * water;

    Surface surface;
    surface.position = input.positionWS;
    surface.normal = (float3)lightN;
    surface.interpolatedNormal = input.normalWS;
    surface.viewDirection = (float3)viewDir;
    surface.depth = -TransformWorldToView(input.positionWS).z;
    surface.color = (float3)albedo;
    surface.alpha = (float)alpha;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.smoothness = 0.0;
    surface.fresnelStrength = 0.0;
    surface.dither = InterleavedGradientNoise(input.positionCS_SS.xy, 0);
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

    // Copy Depth 已开：始终采 Depth，岸沫贴礁石/岸线（对齐河）。
    Fragment fragment = GetFragment(input.positionCS_SS);
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
        shadeBrush,
        (half)_ShadowWobble,
        1.0h,
        0.0h,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, relief);
#if !defined(OIL_MOBILE_PERF)
    color = OilWeaveTint(color, input.worldXZ * 0.08);
    color += OilSunPath(input.positionWS, normalWS, stroke, 1.0h, 0.0h);
#endif

    // 礁石短 Depth 边；不盖过水线沫。
    float viewDepth = max(fragment.bufferDepth - fragment.depth, 0.0);
    half depthRim = 0.0h;
    if (fragment.bufferDepth > 1e-3 && viewDepth < 6.0)
    {
        depthRim = (half)pow(saturate(1.0 - smoothstep(0.01, 0.35, viewDepth)), 2.5);
        depthRim *= (half)(1.0 - saturate((fragment.depth - fragment.bufferDepth) * 4.0));
        depthRim *= (1.0h - glass * 0.85h) * water;
    }
    half shoreGrad = max(lipGrad, depthRim * (1.0h - lipGrad * 0.7h));

    half shoreMask;
    half shoreWash;
    SampleShoreBrush(
        input.worldXZ, float2(normalWS.x, normalWS.z), shoreGrad, columnN,
        shoreMask, shoreWash);

    half shoreClear = lerp(0.26h, 0.48h, overlook);
    alpha = lerp(alpha, shoreClear, glass);

#if !defined(OIL_MOBILE_PERF)
    float2 refractOffset = float2(normalWS.x, normalWS.z) * (_RefractionStrength * 0.05);
    float2 edgeUv = saturate(min(fragment.screenUV, 1.0 - fragment.screenUV) * 8.0);
    refractOffset *= edgeUv.x * edgeUv.y;
    float rawDepth = SAMPLE_DEPTH_TEXTURE_LOD(
        _CameraDepthTexture, sampler_point_clamp, fragment.screenUV + refractOffset, 0);
    float sceneEye = LinearEyeDepth(rawDepth, _ZBufferParams);
    float inFront = saturate((fragment.depth - sceneEye) * 4.0);
    half3 sceneCol = (half3)GetBufferColor(fragment, refractOffset).rgb;
    color = lerp(color, sceneCol, glass * (half)(1.0 - inFront) * lerp(0.7h, 0.4h, overlook));
#endif

    color = (half3)ApplyOilFog((float3)color, surface.depth, stroke, input.positionWS);

    // 半透钛白岸沫 —— 强度足够可见，但不做整片浅滩实心白。
    half3 whiteFoam = half3(0.96h, 0.98h, 1.0h);
    half3 shoreCol = lerp(color, whiteFoam, 0.85h);
    color = lerp(color, shoreCol, shoreWash * 0.35h);
    color = lerp(color, shoreCol, shoreMask * 0.9h);
    alpha = saturate(alpha + shoreMask * 0.35h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
