#ifndef CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED
#define CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED

#define OIL_SHADE_BAND 0.82h
#define OIL_NPR_NO_OTHER_LIGHTS
#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "OilWaterReflect.hlsl"
#include "OilRainRippleFlipbook.hlsl"

// Wave propagation directions: wind=0 → pure oscillation in place,
// wind>0 → waves travel along these directions.
#define OCEAN_WAVE_DIR0 float2(0.82, 0.57)
#define OCEAN_WAVE_DIR1 float2(-0.45, 0.89)

// 与河一致：单列竖带光痕贴图（非横笔印章平铺）。
TEXTURE2D(_SparkleBrush);
SAMPLER(sampler_SparkleBrush);
TEXTURE2D(_FoamMap);
SAMPLER(sampler_FoamMap);

// OilOceanSurface writes this. Mesh is a rest plane (Boluo WaterGrid).
float _OilOceanWaveHeight;

// 风力强度：与草/云风力系统同步，风天气时 > 0。
float _OilOceanWindStrength;

// 天空太阳优先（清晨起床），否则方向光。 _OilSkySunDir 在 OilWaterReflect.hlsl。
bool OilOceanResolveSun(out float3 L)
{
    float3 sky = _OilSkySunDir;
    float skyLen = length(sky);
    if (skyLen > 0.05 && sky.y > -0.08)
    {
        L = sky / skyLen;
        return true;
    }

    if (GetDirectionalLightCount() <= 0)
    {
        L = float3(0.0, 1.0, 0.0);
        return false;
    }

    L = _DirectionalLightData[0].directionAndMask.xyz;
    return length(L) > 0.05;
}

// 对齐 OilRiverSunGlint：相机→太阳一道车道 + 竖列 Glint 采样；海面尺度更大、跟天空太阳。
half OilOceanSunGlint(float3 positionWS, half3 normalWS, float t, half stroke, half foam)
{
    float3 L;
    if (!OilOceanResolveSun(L))
    {
        return 0.0h;
    }

    float elev = saturate(L.y);
    float2 sunXZ = float2(L.x, L.z);
    float sunLen = length(sunXZ);
    // 清晨 7°（L.y≈0.12）仍亮；略宽于河的 lit 门。
    half lit = saturate(((half)L.y + 0.12h) * 14.0h);
    half stable = saturate(((half)sunLen - 0.02h) * 14.0h);
    if (lit * stable < 0.001h)
    {
        return 0.0h;
    }

    float2 sunH = sunXZ / max(sunLen, 1e-4);
    float2 side = float2(-sunH.y, sunH.x);
    float2 fromCam = positionWS.xz - _WorldSpaceCameraPos.xz;

    // 沿波向滚动：风向决定光痕走向，滚动速度随风力缩放。
    float2 waveH = OCEAN_WAVE_DIR0;
    float2 waveSide = float2(-waveH.y, waveH.x);
    float along = dot(fromCam, waveH);
    float lat = dot(fromCam, waveSide);
    lat += dot(float2(normalWS.x, normalWS.z), waveSide) * 2.4;

    // 海面车道比河宽；_SunPathWidth 仍作窄度（默认 4.5 → ×1.33）。
    float halfW = lerp(3.5, 16.0, saturate(along / 220.0));
    halfW *= lerp(1.45, 0.78, elev);
    halfW *= 6.0 / max(_SunPathWidth, 0.25);

    half lane = (half)saturate(1.0 - abs(lat) / max(halfW, 1e-3));
    lane = lane * lane * (3.0h - 2.0h * lane);
    // 近距起带：黎明略近，避免太阳底下一段空。
    float nearStart = lerp(1.5, 4.0, elev);
    float nearRange = lerp(14.0, 28.0, elev);
    half near = (half)saturate((along - nearStart) / max(nearRange, 1e-3));
    half elevGate = (half)saturate(elev * 2.2 + 0.28);
    lane *= near * lit * stable * elevGate;
    if (lane < 1e-4h)
    {
        return 0.0h;
    }

    // U：侧向进竖列中心；V：沿光迹滚动（速度随风力缩放）。
    float u = saturate(lat / max(halfW * 2.4, 1e-3) * 0.5 + 0.5);
    float tileM = max(_SunPathWidth * 8.0, 28.0);
    float glintSpeed = 0.05 + max(_OilOceanWindStrength, 0.0) * 0.12;
    float v = frac(along / tileM - t * glintSpeed);
    float2 st = float2(u, v);
#if defined(OIL_MOBILE_PERF)
    half3 g = (half3)SAMPLE_TEXTURE2D(_SparkleBrush, sampler_SparkleBrush, st).rgb;
#else
    half3 g0 = (half3)SAMPLE_TEXTURE2D(_SparkleBrush, sampler_SparkleBrush, st).rgb;
    half3 g1 = (half3)SAMPLE_TEXTURE2D(
        _SparkleBrush, sampler_SparkleBrush, st + float2(0.0, 0.37)).rgb;
    half3 g = max(g0, g1 * 0.7h);
#endif
    half dab = saturate((PaintLuma(g) - 0.12h) * 2.6h);
    dab *= lerp(0.55h, 1.15h, stroke);
    dab *= (1.0h - foam * 0.85h);
    // 低太阳略加强；远距不灭。
    dab *= lerp(1.35h, 0.9h, (half)elev);
    float dist = length(_WorldSpaceCameraPos - positionWS);
    dab *= (half)saturate((4800.0 - dist) / 2800.0);
    return dab * lane * (half)max(_SunPathStrength, 0.0);
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
    float2 waveDir0 : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// FoamMap 打碎边；带宽由场景 Depth 浅滩决定（无 TerrainHeight）。
void SampleShoreBrush(
    float2 worldXZ, float2 normalXZ, float gradient, float warp01,
    out half mask, out half wash)
{
    float2 flow = float2(0.82, 0.57);
    float tile = max(_IntersectionTiling, 0.0001);
    float2 uv = (worldXZ + normalXZ * _IntersectionDistortion) * tile;
    uv += flow * (_Time.y * _IntersectionSpeed * tile);
    uv += (warp01 - 0.5) * 0.08;
    half3 foamRgb = (half3)SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, uv).rgb;
    half luma = max(foamRgb.r, max(foamRgb.g, foamRgb.b));
    half g = saturate((half)gradient);
    half noise = lerp(0.72h, 1.15h, luma);
    half strength = saturate((half)_FoamStrength);
    mask = saturate(g * noise * strength);
    wash = saturate(g * 0.7h * strength);
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

// 相机 Depth：浅滩 column 小 → glass；远海 / 天空无床 → 当深水。
float OceanColumnFromDepth(Fragment fragment)
{
    if (fragment.bufferDepth < 1e-3)
    {
        return 80.0;
    }

    return max(fragment.bufferDepth - fragment.depth, 0.0);
}

half OilOceanDepthFoamRim(Fragment fragment, float foamReachM)
{
    float viewDepth = OceanColumnFromDepth(fragment);
    float reach = max(foamReachM, 0.35);
    if (fragment.bufferDepth < 1e-3 || viewDepth > reach * 2.5)
    {
        return 0.0h;
    }

    half rim = (half)pow(saturate(1.0 - smoothstep(0.02, reach, viewDepth)), 1.8);
    rim *= (half)(1.0 - saturate((fragment.depth - fragment.bufferDepth) * 4.0));
    return rim;
}

Varyings OilOceanNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionWS = TransformObjectToWorld(input.positionOS);
    float2 xz = positionWS.xz;
    float t = _Time.y;
    // Wind-controlled drift: wind=0 → spatial term drops out, pure time oscillation (原地动).
    // Wind>0 → spatial term resumes, waves travel along the wave direction.
    float k0 = 0.00314;
    float k1 = 0.00449;
    float windDrift = max(_OilOceanWindStrength, 0.0);
    float spatial0 = k0 * dot(OCEAN_WAVE_DIR0, xz);
    float spatial1 = k1 * dot(OCEAN_WAVE_DIR1, xz);
    float phase0 = spatial0 * windDrift + t * 0.35;
    float phase1 = spatial1 * windDrift - t * 0.22;
    float s0 = sin(phase0);
    float c0 = cos(phase0);
    float s1 = sin(phase1);
    float c1 = cos(phase1);
    // 风力增强波浪幅度：风天气时波涛更汹涌。
    float windBoost = 1.0 + windDrift * 0.8;
    float amp = max(_OilOceanWaveHeight, 0.0) * windBoost;
    float steep = 0.12 * amp;
    positionWS.y += (s0 + s1 * 0.35) * amp;
    positionWS.xz += OCEAN_WAVE_DIR0 * (steep * c0) + OCEAN_WAVE_DIR1 * (steep * 0.35 * c1);
    float dhx = (OCEAN_WAVE_DIR0.x * k0 * c0 + OCEAN_WAVE_DIR1.x * k1 * 0.35 * c1) * amp;
    float dhz = (OCEAN_WAVE_DIR0.y * k0 * c0 + OCEAN_WAVE_DIR1.y * k1 * 0.35 * c1) * amp;
    output.positionWS = positionWS;
    output.positionCS_SS = TransformWorldToHClip(positionWS);
    output.normalWS = normalize(float3(-dhx, 1.0, -dhz));
    output.worldXZ = xz;
    output.waveDir0 = OCEAN_WAVE_DIR0;
    return output;
}

half4 OilOceanNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    Fragment fragment = GetFragment(input.positionCS_SS);
    // 海网格本身即水面；岸线靠相机 Depth（对齐河），无 TerrainHeight。
    half water = 1.0h;

    float t = _Time.y;
    half crest;
    half3 waveN = SineWaveNormal(input.worldXZ, crest);
    half3 geoN = normalize((half3)input.normalWS);
    // 对齐河：波纹法线混合更弱，光照用法线朝上，避免圆影盘。
    half3 normalWS = normalize(lerp(geoN, waveN, 0.55h));
    // 雨滴涟漪：法线序列帧直接扰动法线，光照/折射/高光都会跟着动。
    normalWS = ApplyRainRippleNormal(normalWS, input.positionWS);
    half3 lightN = half3(0.0h, 1.0h, 0.0h);
    float camDist = length(_WorldSpaceCameraPos - input.positionWS);
    half overlook = (half)smoothstep(120.0, 420.0, camDist);
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));

    // 对齐河：深/浅水色 + Monet 笔触主导 albedo。
    half3 albedo = lerp(_ShallowColor.rgb, _BaseColor.rgb, 0.45h);
    albedo *= (half)_AlbedoBoost;

    half relief = saturate((half)_PaintRelief);
    float2 paintSt = OceanMonetPaintUv(
        input.worldXZ, t, _PaintTile, _StrokeBands);
    half3 paint;
    {
        float2 f = frac(paintSt);
        float2 e = min(f, 1.0 - f);
        half w = (half)smoothstep(0.0, 0.14, min(e.x, e.y));
        half3 c0 = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt).rgb;
        half3 c1 = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt + 0.5).rgb;
        paint = lerp(c1, c0, w);
    }
    half stroke = PaintStroke(PaintLuma(paint), (half)_PaintContrast);
    albedo = lerp(albedo, paint, relief);
    albedo = ImpastoDeviation(albedo, stroke, relief * 0.55h);

    float column = OceanColumnFromDepth(fragment);
#if defined(OIL_MOBILE_PERF)
    float columnN = OilValueNoise(input.worldXZ * 0.012);
#else
    float columnN = OilFbm(input.worldXZ * 0.012);
#endif
    float columnSoft = max(column + (columnN - 0.5) * 1.2, 0.0);
    float clearDepth = max(_DepthVertical, 0.4) * lerp(1.6, 2.8, (float)overlook);
    half glass = (half)(1.0 - smoothstep(0.08, clearDepth, columnSoft)) * water;
    glass *= lerp(0.9h, 0.4h, overlook);
    glass = saturate(glass);

    float foamReach = clamp(_ShoreFoamWidth * 0.045, 0.35, 1.4);
    half lipGrad = OilOceanDepthFoamRim(fragment, foamReach);
    lipGrad = saturate(lipGrad * 1.35h);
    half edge = lipGrad;

    // 夜晚浅滩不再铺暖沙，避免近岸比夜色海面亮一截。
    half night = OilWaterNightWeight(_OilSkyPeriod);
    half3 sandWarm = lerp(half3(0.68h, 0.58h, 0.42h), half3(0.14h, 0.12h, 0.11h), night);
    albedo = lerp(albedo, sandWarm, edge * lerp(0.28h, 0.12h, glass));
    albedo = lerp(albedo, _ShallowColor.rgb, edge * 0.18h);

    half deepA = (half)_BaseColor.a;
    half shallowA = (half)_ShallowColor.a;
    half alpha = lerp(deepA, shallowA, glass * 0.65h) * water;
    alpha = lerp(alpha, min(shallowA, 0.85h) * water, glass * glass * 0.45h);
    // 夜晚少透出浅滩底色，折射带跟着海面一起沉下去。
    alpha = lerp(alpha, deepA * water, night * glass * 0.8h);

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

    half3 color = OilNPRLightingEx(
        surface,
        albedo,
        OilNPRAmbient(surface),
        (half3)_SpecularColor.rgb,
        (half)_SpecularThreshold,
        max((half)_ShadeSteps, 3.0h),
        0.0h,
        stroke,
        0.0h,
        1.0h,
        0.0h,
        surface.smoothness,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, relief * 0.45h);
#if !defined(OIL_MOBILE_PERF)
    color = OilWeaveTint(color, paintSt * 6.0);
#endif

    half depthRim = OilOceanDepthFoamRim(fragment, 0.55);
    depthRim *= (1.0h - glass * 0.7h) * water;
    half shoreGrad = max(lipGrad, depthRim * (1.0h - lipGrad * 0.55h));

    half shoreMask;
    half shoreWash;
    SampleShoreBrush(
        input.worldXZ, float2(normalWS.x, normalWS.z), shoreGrad, columnN,
        shoreMask, shoreWash);

#if !defined(OIL_MOBILE_PERF)
    // 浅滩保留折射（河没有场景色折射，海保留但只在 glass 窗口）。
    float2 refractOffset = float2(normalWS.x, normalWS.z) * (_RefractionStrength * 0.045);
    float2 edgeUv = saturate(min(fragment.screenUV, 1.0 - fragment.screenUV) * 8.0);
    refractOffset *= edgeUv.x * edgeUv.y;
    float rawDepth = SAMPLE_DEPTH_TEXTURE_LOD(
        _CameraDepthTexture, sampler_point_clamp, fragment.screenUV + refractOffset, 0);
    float sceneEye = LinearEyeDepth(rawDepth, _ZBufferParams);
    float inFront = saturate((fragment.depth - sceneEye) * 4.0);
    half3 sceneCol = (half3)GetBufferColor(fragment, refractOffset).rgb;
    half refractMix = glass * (half)(1.0 - inFront) * lerp(0.5h, 0.25h, overlook);
    // 折射带染色：从冷白 (0.78,0.84,0.92) 改为深海蓝绿 (0.18,0.30,0.38)，
    // 避免日光下浅滩透出来一片冷白。
    half3 refractTint = lerp(half3(0.78h, 0.84h, 0.92h), half3(0.18h, 0.30h, 0.38h), night);
    sceneCol *= refractTint;
    color = lerp(color, sceneCol, refractMix);
#endif

    color = (half3)ApplyOilFog((float3)color, surface.depth, stroke, input.positionWS);

    // Fog first, then planar / sky reflect — otherwise atmosphere washes the glaze out.
    color = ApplyOilWaterReflection(
        color,
        input.positionWS,
        normalWS,
        viewDir,
        stroke,
        (1.0h - shoreMask) * (1.0h - shoreWash),
        fragment.screenUV);

    // 对齐河：雾后叠一道太阳光痕（竖列 Glint + 世界车道），再画岸沫。
    half glint = OilOceanSunGlint(input.positionWS, normalWS, t, stroke, shoreMask);
    color += (half3)_GlintColor.rgb * glint;

    // 钛白岸沫：白天保持对比；夜晚压暗，避免和浅滩折射叠成一条白带。
    // whiteFoam 整体压暗、混合权重降一档，让岸沫偏冷灰而不是近白。
    half3 whiteFoam = half3(0.78h, 0.82h, 0.86h);
    half3 shoreCol = lerp(color, whiteFoam, 0.72h);
    half foamNight = lerp(1.0h, 0.38h, night);
    color = lerp(color, shoreCol, shoreWash * 0.36h * foamNight);
    color = lerp(color, shoreCol, shoreMask * 0.78h * foamNight);
    alpha = saturate(alpha + shoreMask * 0.35h + relief * 0.06h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
