#ifndef CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED
#define CUSTOM_OIL_OCEAN_NPR_PASS_INCLUDED

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
TEXTURE2D(_IntersectionNoise);
SAMPLER(sampler_IntersectionNoise);
TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);

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

    // 车道沿相机→太阳（光痕从太阳底下伸出）；V 滚动跟 Monet 纹理，不跟太阳。
    float along = dot(fromCam, sunH);
    float lat = dot(fromCam, side);
    lat += dot(float2(normalWS.x, normalWS.z), side) * 2.4;

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

    // U：侧向进竖列中心；V：与 OceanMonetPaintUv 同向滚动（+t 对齐 st += SCROLL * t）。
    float u = saturate(lat / max(halfW * 2.4, 1e-3) * 0.5 + 0.5);
    float tileM = max(_SunPathWidth * 8.0, 28.0);
    float2 scrollDir = normalize(OCEAN_PAINT_SCROLL);
    float alongPaint = dot(positionWS.xz, scrollDir);
    float glintSpeed = 0.05 + max(_OilOceanWindStrength, 0.0) * 0.12;
    float v = frac(alongPaint / tileM + t * glintSpeed);
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
    // Rest-plane eye depth：岸沫用静止面深度，避免 Gerstner 上下抖 rim。
    float restEyeDepth : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// 对齐 OilRiverShore：Depth 窗 × IntersectionNoise × Shore Clip（无 Foam 笔刷贴图）。
// 抗闪：UV 只用空间 warp（不用 bump/涟漪法线）；时间滚动放慢；clip 窗加宽。
void SampleShoreBrush(
    float2 worldXZ, float gradient, float warp01,
    out half mask, out half wash)
{
    half g = saturate((half)gradient);
    half strength = saturate((half)_FoamStrength);
    if (g * strength < 1e-4h)
    {
        mask = 0.0h;
        wash = 0.0h;
        return;
    }

    float t = _Time.y;
    // 空间扰动固定在世界 XZ，避免法线动画把碎边拖成闪烁。
    float2 offsetVector = float2(warp01 - 0.5, warp01 - 0.5);
    float2 interUv = worldXZ + offsetVector * _IntersectionDistortion;
    // 原 -t 滚动过快 + 硬 clip → 沫缘逐帧开合；降速并双样混合压闪。
    float speed = max(_IntersectionSpeed, 0.0) * 0.35;
    float2 interTime = float2(t * 0.08, -t * 0.22) * speed;
    float dist = saturate((float)g / max(_IntersectionFalloff, 1e-4));
    float2 nUV = interUv * max(_IntersectionTiling, 0.02);
    half n0 = (half)SAMPLE_TEXTURE2D(
        _IntersectionNoise, sampler_IntersectionNoise, nUV + interTime).r;
#if defined(OIL_MOBILE_PERF)
    half noise = n0;
#else
    half n1 = (half)SAMPLE_TEXTURE2D(
        _IntersectionNoise, sampler_IntersectionNoise,
        nUV * 1.37 + interTime * float2(-0.61, 0.47) + 0.17).r;
    half noise = max(n0, n1 * 0.85h);
#endif
    noise = saturate(noise * (half)(dist * 1.15) + (half)(dist * 0.42));

    half softLo = (half)max(_IntersectionClipping - 0.28, 0.0);
    half softHi = (half)min(_IntersectionClipping + 0.32, 1.0);
    mask = smoothstep(softLo, softHi, noise)
        * strength
        * (half)saturate((float)g * 1.15);
    // 浅水 wash：同河，只给一点，不作奶白渐变。
    wash = saturate(g * g * g) * strength * 0.22h;
}

half3 SineWaveNormal(float2 worldXZ, out half crest)
{
    float2 p = worldXZ * 0.045 + _Time.y * float2(0.35, -0.22);
    half s0 = (half)sin(p.x * 1.6 + p.y * 0.45);
    half s1 = (half)sin(p.y * 1.35 - p.x * 0.7 + _Time.y * 0.8);
    crest = s0 * 0.5h + 0.5h;
    return normalize(half3(s0 * 0.16h, 1.0h, s1 * 0.12h));
}

// Dual-layer bump：层A/B 非等比笔触交叉 + 对向滚动 → 一节一节交错，非单条长横线。
// _BumpTiling.zw / _BumpScroll.zw 为 0 时自动轴交换 tiling、对向 scroll。
half3 OilOceanBumpNormal(float2 worldXZ, float2 paintSt, half3 baseN)
{
    float tile = max(_BumpTile, 20.0);
    float2 tilingA = max(_BumpTiling.xy, float2(0.01, 0.01));
    float2 scrollA = _BumpScroll.xy;
    float t = _Time.y;
    // 同 Monet：竖笔贴图经 worldXZ.yx 转横。
    float2 uvA = (worldXZ.yx / tile) * tilingA + scrollA * t;
#if defined(OIL_MOBILE_PERF)
    // Gmj / mobile floor: single bump sample (desktop keeps dual-layer cross).
    half3 nA = (half3)DecodeNormal(
        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvA), _BumpScale);
    half2 offset = half2(nA.x, nA.y);
#else
    float2 tilingB = _BumpTiling.zw;
    if (tilingB.x + tilingB.y < 1e-4)
    {
        tilingB = tilingA.yx;
    }
    tilingB = max(tilingB, float2(0.01, 0.01));
    float2 scrollB = _BumpScroll.zw;
    if (abs(scrollB.x) + abs(scrollB.y) < 1e-6)
    {
        scrollB = -scrollA;
    }
    // 层B 换周期 + 相位，避免两层锁成网格。
    float2 uvB = (worldXZ.yx / (tile * 1.37)) * tilingB + scrollB * t + float2(0.37, 0.19);
    half3 nA = (half3)DecodeNormal(
        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvA), _BumpScale);
    half3 nB = (half3)DecodeNormal(
        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvB), _BumpScale);
    half crossW = saturate((half)_BumpCross);
    half2 offset = (half2(nA.x, nA.y) + half2(nB.x, nB.y) * crossW)
        / max(1.0h + crossW, 1.0h);
    // Paint relief as supplemental chop when bump is flat / unset.
    half3 paintC = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt).rgb;
    half3 paintDx = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt + float2(0.02, 0.0)).rgb;
    half3 paintDz = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt + float2(0.0, 0.02)).rgb;
    half h0 = PaintLuma(paintC);
    half hx = PaintLuma(paintDx) - h0;
    half hz = PaintLuma(paintDz) - h0;
    offset += half2(hx, hz) * (half)_BumpScale * 1.8h;
#endif
    // Horizontal sea: tangent ≈ X, bitangent ≈ Z.
    half3 bumpN = normalize(half3(offset.x, 1.0h, offset.y));
    return normalize(lerp(baseN, bumpN, saturate((half)_BumpScale)));
}

float OceanColumnFromDepth(Fragment fragment)
{
    if (fragment.bufferDepth < 1e-3)
    {
        return 80.0;
    }

    return max(fragment.bufferDepth - fragment.depth, 0.0);
}

// 岸缘 depth 用 5-tap 最近点（线性眼深 min），压住 point 采样在沙/天边界的像素闪。
float OilOceanStableSceneEyeDepth(float2 screenUV)
{
    float2 texel = _CameraBufferSize.xy;
    float d0 = LinearEyeDepth(
        SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, sampler_point_clamp, screenUV, 0),
        _ZBufferParams);
    float d1 = LinearEyeDepth(
        SAMPLE_DEPTH_TEXTURE_LOD(
            _CameraDepthTexture, sampler_point_clamp, screenUV + float2(texel.x, 0.0), 0),
        _ZBufferParams);
    float d2 = LinearEyeDepth(
        SAMPLE_DEPTH_TEXTURE_LOD(
            _CameraDepthTexture, sampler_point_clamp, screenUV - float2(texel.x, 0.0), 0),
        _ZBufferParams);
    float d3 = LinearEyeDepth(
        SAMPLE_DEPTH_TEXTURE_LOD(
            _CameraDepthTexture, sampler_point_clamp, screenUV + float2(0.0, texel.y), 0),
        _ZBufferParams);
    float d4 = LinearEyeDepth(
        SAMPLE_DEPTH_TEXTURE_LOD(
            _CameraDepthTexture, sampler_point_clamp, screenUV - float2(0.0, texel.y), 0),
        _ZBufferParams);
    return min(d0, min(min(d1, d2), min(d3, d4)));
}

// surfaceEye：用静止海面眼深（restEyeDepth），不跟波浪上下抖。
half OilOceanDepthFoamRim(float surfaceEye, float sceneEye, float foamReachM)
{
    if (sceneEye < 1e-3)
    {
        return 0.0h;
    }

    float viewDepth = max(sceneEye - surfaceEye, 0.0);
    float reach = max(foamReachM, 0.35);
    if (viewDepth > reach * 2.5)
    {
        return 0.0h;
    }

    // 更宽的 smoothstep，降低阈值附近的二值跳变。
    half rim = (half)pow(saturate(1.0 - smoothstep(0.04, reach * 1.15, viewDepth)), 1.45);
    rim *= (half)(1.0 - saturate((surfaceEye - sceneEye) * 3.0));
    return rim;
}

Varyings OilOceanNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionWS = TransformObjectToWorld(input.positionOS);
    float2 xz = positionWS.xz;
    // 岸沫 depth 锚定静止面，位移后再算 clip。
    output.restEyeDepth = -TransformWorldToView(positionWS).z;
    float t = _Time.y;
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
    half water = 1.0h;

    float t = _Time.y;
    half crest;
    half3 waveN = SineWaveNormal(input.worldXZ, crest);
    half3 geoN = normalize((half3)input.normalWS);
    half3 normalWS = normalize(lerp(geoN, waveN, 0.55h));

    float2 paintSt = OceanMonetPaintUv(
        input.worldXZ, t, _PaintTile, _StrokeBands);
    normalWS = OilOceanBumpNormal(input.worldXZ, paintSt, normalWS);
    normalWS = ApplyRainRippleNormal(normalWS, input.positionWS);

    // 光照仍 flat，避免 cascade 圆影；bump 只喂反射/折射/glint。
    half3 lightN = half3(0.0h, 1.0h, 0.0h);
    float camDist = length(_WorldSpaceCameraPos - input.positionWS);
    half overlook = (half)smoothstep(120.0, 420.0, camDist);
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));

    half3 albedo = lerp(_ShallowColor.rgb, _BaseColor.rgb, 0.45h);
    albedo *= (half)_AlbedoBoost;

    half relief = saturate((half)_PaintRelief);
    half3 paint;
    {
#if defined(OIL_MOBILE_PERF)
        paint = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt).rgb;
#else
        float2 f = frac(paintSt);
        float2 e = min(f, 1.0 - f);
        half w = (half)smoothstep(0.0, 0.14, min(e.x, e.y));
        half3 c0 = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt).rgb;
        half3 c1 = (half3)SAMPLE_TEXTURE2D(_PaintMap, sampler_PaintMap, paintSt + 0.5).rgb;
        paint = lerp(c1, c0, w);
#endif
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
#if defined(OIL_MOBILE_PERF)
    // 移动端：单样 + 静止面深度已压掉波浪闪；省 4 次 depth tap。
    float shoreSceneEye = fragment.bufferDepth;
#else
    float shoreSceneEye = OilOceanStableSceneEyeDepth(fragment.screenUV);
#endif
    half lipGrad = OilOceanDepthFoamRim(input.restEyeDepth, shoreSceneEye, foamReach);
    lipGrad = saturate(lipGrad * 1.35h);
    half edge = lipGrad;

    half night = OilWaterNightWeight(_OilSkyPeriod);
    half3 sandWarm = lerp(half3(0.68h, 0.58h, 0.42h), half3(0.14h, 0.12h, 0.11h), night);
    albedo = lerp(albedo, sandWarm, edge * lerp(0.28h, 0.12h, glass));
    albedo = lerp(albedo, _ShallowColor.rgb, edge * 0.18h);

    half deepA = (half)_BaseColor.a;
    half shallowA = (half)_ShallowColor.a;
    half alpha = lerp(deepA, shallowA, glass * 0.65h) * water;
    alpha = lerp(alpha, min(shallowA, 0.85h) * water, glass * glass * 0.45h);
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
        half3(0.36h, 0.42h, 0.52h),
        half3(0.50h, 0.44h, 0.36h),
        half3(0.0h, 0.0h, 0.0h),
        0.0h,
        max((half)_ShadeSteps, 3.0h),
        0.0h,
        stroke,
        1.0h,
        0.0h,
        surface.smoothness,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, relief * 0.45h);
#if !defined(OIL_MOBILE_PERF)
    color = OilWeaveTint(color, paintSt * 6.0);
#endif

    half depthRim = OilOceanDepthFoamRim(input.restEyeDepth, shoreSceneEye, 0.55);
    depthRim *= (1.0h - glass * 0.7h) * water;
    half shoreGrad = max(lipGrad, depthRim * (1.0h - lipGrad * 0.55h));

    half shoreMask;
    half shoreWash;
    SampleShoreBrush(input.worldXZ, shoreGrad, columnN, shoreMask, shoreWash);

#if !defined(OIL_MOBILE_PERF)
    float2 refractOffset = float2(normalWS.x, normalWS.z) * (_RefractionStrength * 0.045);
    float2 edgeUv = saturate(min(fragment.screenUV, 1.0 - fragment.screenUV) * 8.0);
    refractOffset *= edgeUv.x * edgeUv.y;
    float rawDepth = SAMPLE_DEPTH_TEXTURE_LOD(
        _CameraDepthTexture, sampler_point_clamp, fragment.screenUV + refractOffset, 0);
    float sceneEye = LinearEyeDepth(rawDepth, _ZBufferParams);
    float inFront = saturate((fragment.depth - sceneEye) * 4.0);
    half3 sceneCol = (half3)GetBufferColor(fragment, refractOffset).rgb;
    half refractMix = glass * (half)(1.0 - inFront) * lerp(0.5h, 0.25h, overlook);
    half3 refractTint = lerp(half3(0.22h, 0.20h, 0.38h), half3(0.10h, 0.08h, 0.22h), night);
    sceneCol *= refractTint;
    color = lerp(color, sceneCol, refractMix);
#endif

#if defined(OIL_MOBILE_PERF)
    color = (half3)ApplyOilFog((float3)color, surface.depth, stroke);
#else
    color = (half3)ApplyOilFog((float3)color, surface.depth, stroke, input.positionWS);
#endif

    color = ApplyOilWaterReflection(
        color,
        input.positionWS,
        normalWS,
        viewDir,
        stroke,
        (1.0h - shoreMask) * (1.0h - shoreWash),
        fragment.screenUV,
        (half)_ReflectDistort,
        (half)_ReflectSteps);

    // 深蓝紫叠色；夜晚压浅滩暖色。
    half tintAmt = (half)_TintStrength * lerp(1.0h, 0.45h, night);
    half3 waterTint = lerp((half3)_TintDeep.rgb, (half3)_TintShallow.rgb, glass);
    color = lerp(color, color * waterTint, tintAmt);

    // 对齐河：雾后叠一道太阳光痕（竖列 Glint + 世界车道），再画岸沫。
    half glint = OilOceanSunGlint(input.positionWS, normalWS, t, stroke, shoreMask);
    color += (half3)_GlintColor.rgb * glint;

    // 钛白岸沫：直接靠向 _FoamColor，避免深蓝紫水体把沫染灰。
    half3 foamCol = (half3)_FoamColor.rgb;
    half foamNight = lerp(1.0h, 0.45h, night);
    half foamMix = saturate(shoreWash * 0.42h + shoreMask * 0.92h) * foamNight;
    color = lerp(color, foamCol, foamMix);
    alpha = saturate(alpha + shoreMask * 0.35h + relief * 0.06h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
