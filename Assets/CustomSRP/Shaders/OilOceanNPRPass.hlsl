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

// 日道：从太阳倒影落点拉向相机（不是从脚边往外长）。
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
    half lit = saturate(((half)L.y + 0.18h) * 10.0h);
    half stable = saturate(((half)sunLen - 0.02h) * 14.0h);
    if (lit * stable < 0.001h)
    {
        return 0.0h;
    }

    float2 footXZ;
    float pathLen;
    if (!OilWaterCelestialFoot(L, positionWS.y, footXZ, pathLen))
    {
        return 0.0h;
    }

    float2 toCam = _WorldSpaceCameraPos.xz - footXZ;
    float2 alongDir = toCam / max(pathLen, 1e-3);
    float2 side = float2(-alongDir.y, alongDir.x);
    float2 fromFoot = positionWS.xz - footXZ;
    float along = dot(fromFoot, alongDir); // 0 = 太阳落点，pathLen = 相机
    float lat = dot(fromFoot, side);
    lat += dot(float2(normalWS.x, normalWS.z), side) * 2.4;

    float halfW = lerp(4.0, 18.0, saturate(along / max(pathLen, 1.0)));
    halfW *= lerp(1.55, 0.85, elev);
    halfW *= 6.0 / max(_SunPathWidth, 0.25);

    half lane = (half)saturate(1.0 - abs(lat) / max(halfW, 1e-3));
    lane = lane * lane * (3.0h - 2.0h * lane);
    // 从太阳落点起亮，向相机方向衰减；脚边收掉。
    half along01 = (half)saturate(along / max(pathLen, 1e-3));
    half fromSun = (1.0h - along01 * along01);
    fromSun *= (half)saturate(1.0 - along / max(pathLen * 0.97, 1.0));
    // 落点附近一小团保证日道「顶」在太阳倒影上。
    half atFoot = (half)saturate(1.0 - length(fromFoot) / max(halfW * 1.8, 4.0));
    fromSun = max(fromSun, atFoot);
    half elevGate = (half)saturate(elev * 1.8 + 0.35);
    lane *= fromSun * lit * stable * elevGate;
    [branch]
    if (lane < 1e-4h)
    {
        return 0.0h;
    }

    float u = saturate(lat / max(halfW * 2.4, 1e-3) * 0.5 + 0.5);
    float tileM = max(_SunPathWidth * 8.0, 28.0);
    float2 scrollDir = normalize(OCEAN_PAINT_SCROLL);
    float alongPaint = dot(positionWS.xz, scrollDir);
    float glintSpeed = 0.05 + max(_OilOceanWindStrength, 0.0) * 0.12;
    float v = frac(alongPaint / tileM + t * glintSpeed);
    float2 st = float2(u, v);
    float dist = length(_WorldSpaceCameraPos - positionWS);
    float glintMip = saturate((dist - 40.0) / 700.0) * 3.0;
#if defined(OIL_MOBILE_PERF)
    half3 g = (half3)SAMPLE_TEXTURE2D_LOD(_SparkleBrush, sampler_SparkleBrush, st, glintMip).rgb;
#else
    half3 g0 = (half3)SAMPLE_TEXTURE2D_LOD(_SparkleBrush, sampler_SparkleBrush, st, glintMip).rgb;
    half3 g = g0;
    [branch]
    if (dist < 380.0)
    {
        half3 g1 = (half3)SAMPLE_TEXTURE2D_LOD(
            _SparkleBrush, sampler_SparkleBrush, st + float2(0.0, 0.37), glintMip).rgb;
        g = max(g0, g1 * 0.7h);
    }
#endif
    half dab = saturate((PaintLuma(g) - 0.10h) * 2.8h);
    dab *= lerp(0.55h, 1.15h, stroke);
    dab *= (1.0h - foam * 0.85h);
    dab *= lerp(1.45h, 0.95h, (half)elev);
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

// 对齐 OilRiverShore：Depth 窗 × IntersectionNoise × crest 碎边（无 Foam 笔刷贴图）。
// 抗闪：UV 跟 crest/短波法线（同河），不用涟漪；时间滚动放慢；clip 贴浪唇。
void SampleShoreBrush(
    float2 worldXZ, float gradient, float crest, half2 waveXZ,
    out half mask, out half wash)
{
    half g = saturate((half)gradient);
    half strength = saturate((half)_FoamStrength);
    // 外海 gradient≈0。LOD 采样才能让这个分支在整片海里真正不取贴图。
    [branch]
    if (g * strength < 1e-4h)
    {
        mask = 0.0h;
        wash = 0.0h;
        return;
    }

    float t = _Time.y;
    // 同河：crest + 短波切向把碎沫拉到浪花边缘，而不是浅滩中心填白。
    float2 offsetVector = saturate(float2(crest, crest) + (float2)waveXZ);
    float2 interUv = worldXZ + offsetVector * _IntersectionDistortion;
    // 原 -t 滚动过快 + 硬 clip → 沫缘逐帧开合；降速并双样混合压闪。
    float speed = max(_IntersectionSpeed, 0.0) * 0.35;
    float2 interTime = float2(t * 0.08, -t * 0.22) * speed;
    float dist = saturate((float)g / max(_IntersectionFalloff, 1e-4));
    float2 nUV = interUv * max(_IntersectionTiling, 0.02);
    float shoreMip = saturate(distance(_WorldSpaceCameraPos.xz, worldXZ) / 350.0) * 3.0;
    half n0 = (half)SAMPLE_TEXTURE2D_LOD(
        _IntersectionNoise, sampler_IntersectionNoise, nUV + interTime, shoreMip).r;
#if defined(OIL_MOBILE_PERF)
    half noise = n0;
#else
    half n1 = (half)SAMPLE_TEXTURE2D_LOD(
        _IntersectionNoise, sampler_IntersectionNoise,
        nUV * 1.37 + interTime * float2(-0.61, 0.47) + 0.17, shoreMip).r;
    half noise = max(n0, n1 * 0.85h);
#endif
    // 少填实心带，碎沫更贴 depth 交线。
    noise = saturate(noise * (half)(dist * 1.35) + (half)(dist * 0.28));

    half softLo = (half)max(_IntersectionClipping - 0.16, 0.0);
    half softHi = (half)min(_IntersectionClipping + 0.20, 1.0);
    mask = smoothstep(softLo, softHi, noise)
        * strength
        * (half)saturate((float)g * 1.25);
    // 浅水 wash：同河，只给一点，不作奶白渐变。
    wash = saturate(g * g * g) * strength * 0.08h;
}

half3 SineWaveNormal(float2 worldXZ, out half crest)
{
    float2 p = worldXZ * 0.045 + _Time.y * float2(0.35, -0.22);
    half s0 = (half)sin(p.x * 1.6 + p.y * 0.45);
    half s1 = (half)sin(p.y * 1.35 - p.x * 0.7 + _Time.y * 0.8);
    crest = s0 * 0.5h + 0.5h;
    return normalize(half3(s0 * 0.16h, 1.0h, s1 * 0.12h));
}

// 单层波浪法线。层 B（_BumpTiling.zw / _BumpScroll.zw）不再采样。
half3 OilOceanBumpNormal(float2 worldXZ, half3 baseN)
{
    float tile = max(_BumpTile, 20.0);
    float2 tilingA = max(_BumpTiling.xy, float2(0.01, 0.01));
    float2 scrollA = _BumpScroll.xy;
    // 同 Monet 底纹：world XZ.
    float2 uvA = (worldXZ / tile) * tilingA + scrollA * _Time.y;
    half3 nA = (half3)DecodeNormal(
        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvA), _BumpScale);
    // Horizontal sea: tangent ≈ X, bitangent ≈ Z.
    half3 bumpN = normalize(half3(nA.x, 1.0h, nA.y));
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

// 岸缘才补十字 depth（线性眼深 min），压住沙/天交界的像素闪。
// 外海 center 已深过泡沫窗且四元组深度连续，邻域 min 改不了结果，不再每像素多采 4 次全分辨率 depth。
float OilOceanStableSceneEyeDepth(
    float2 screenUV, float centerEye, float surfaceEye, float foamReachM)
{
    float reach = max(foamReachM, 0.55);
    float gap = centerEye - surfaceEye;
    float edge = abs(ddx(centerEye)) + abs(ddy(centerEye));
    [branch]
    if (centerEye > 1e-3 && gap > reach * 2.5 && edge < reach)
    {
        return centerEye;
    }

    float2 texel = _CameraBufferSize.xy;
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
    return min(centerEye, min(min(d1, d2), min(d3, d4)));
}

// 水面柱深。陆地挡在水前 → 0；无场景 depth → 大值（外海）。
float OilOceanWaterColumn(float surfaceEye, float sceneEye)
{
    if (sceneEye < 1e-3)
    {
        return 80.0;
    }

    if (sceneEye + 0.02 < surfaceEye)
    {
        return 0.0;
    }

    return max(sceneEye - surfaceEye, 0.0);
}

// 拍岸白沫贴水陆交线（column≈0，海面可见边缘），向深水收到 reach。
// 远处关掉 fwidth，避免 depth 噪声假沫点。
half OilOceanDepthFoamRim(
    float surfaceEye, float sceneEye, float foamReachM, float camDist)
{
    float column = OilOceanWaterColumn(surfaceEye, sceneEye);
    if (column > 40.0)
    {
        return 0.0h;
    }

    float reach = max(foamReachM, 0.35);
    if (column > reach)
    {
        return 0.0h;
    }

    // 交线满强度。峰值若偏进深水，边缘会空出一条没有沫的水带。
    half band = (half)(1.0 - smoothstep(0.0, reach, column));

#if defined(OIL_MOBILE_PERF)
    // No ddx/ddy edge — full-screen ocean derivatives are expensive and noisy.
    half rim = band * 0.85h;
    rim *= (half)(1.0 - saturate((surfaceEye - sceneEye) * 4.0));
    return saturate(rim);
#else
    half nearF = (half)saturate(1.0 - smoothstep(45.0, 140.0, camDist));
    float colEdge = abs(ddx(column)) + abs(ddy(column));
    half edge = (half)saturate(colEdge / max(reach * 0.2, 0.035)) * nearF;
    half rim = band * lerp(0.55h, 1.0h, edge);

    rim *= (half)(1.0 - saturate((surfaceEye - sceneEye) * 4.0));
    rim *= lerp(band, 1.0h, nearF);
    return saturate(rim);
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
    half3 foamN = normalize(lerp(geoN, waveN, 0.55h));
    half3 normalWS = foamN;
    float camDist = length(_WorldSpaceCameraPos - input.positionWS);

    float2 paintSt = OceanMonetPaintUv(
        input.worldXZ, t, _PaintTile, _StrokeBands);
#if defined(OIL_MOBILE_PERF)
    // Sine + geo normal only — bump atlas is a full-screen sample on open water.
#else
    normalWS = OilOceanBumpNormal(input.worldXZ, normalWS);
#endif
    normalWS = ApplyRainRippleNormal(normalWS, input.positionWS);

    // 光照仍 flat，避免 cascade 圆影；bump 只喂反射/折射/glint。
    half3 lightN = half3(0.0h, 1.0h, 0.0h);
    half overlook = (half)smoothstep(120.0, 420.0, camDist);
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));

    half3 albedo = lerp(_ShallowColor.rgb, _BaseColor.rgb, 0.45h);
    albedo *= (half)_AlbedoBoost;

    half relief = saturate((half)_PaintRelief);
    half3 paint;
    {
#if defined(OIL_MOBILE_PERF)
        // Same one tap. Bias keeps Monet strokes out of the half-res mip.
        paint = (half3)SAMPLE_TEXTURE2D_BIAS(_PaintMap, sampler_PaintMap, paintSt, -1.25).rgb;
#else
        float2 paintDx = ddx(paintSt);
        float2 paintDy = ddy(paintSt);
        half3 c0 = (half3)SAMPLE_TEXTURE2D_GRAD(
            _PaintMap, sampler_PaintMap, paintSt, paintDx, paintDy).rgb;
        paint = c0;
        // 半张图的接缝混合只在近处看得见。地平线上海面不再第二采。
        [branch]
        if (camDist < 320.0)
        {
            float2 f = frac(paintSt);
            float2 e = min(f, 1.0 - f);
            half w = (half)smoothstep(0.0, 0.14, min(e.x, e.y));
            half3 c1 = (half3)SAMPLE_TEXTURE2D_GRAD(
                _PaintMap, sampler_PaintMap, paintSt + 0.5, paintDx, paintDy).rgb;
            paint = lerp(c1, c0, w);
        }
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

    // 白沫从水陆交线（海 mesh 可见边缘）起，向深水收。不把峰值偏进水里留空带。
    // 必须用实际画出的浪面眼深。restEyeDepth 与位移后屏幕像素不对齐 → 沫偏到沙滩。
    float surfaceEye = fragment.depth;
    float foamReach = clamp(_ShoreFoamWidth * 0.05, 0.4, 1.6);
    // 不用邻域 min：会把浅滩往岸上扩，白沫画到沙滩。
    float shoreSceneEye = fragment.bufferDepth;
    half lipGrad = OilOceanDepthFoamRim(surfaceEye, shoreSceneEye, foamReach, camDist);
    lipGrad = saturate(lipGrad * 1.25h);
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
        half3(0.0h, 0.0h, 0.0h),
        0.0h,
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

    // 透视沙滩（glass 很高）灭实心白；礁石薄交线近景兜底。
    half depthRim = OilOceanDepthFoamRim(surfaceEye, shoreSceneEye, min(foamReach, 0.55), camDist);
    half nearOnly = (half)saturate(1.0 - smoothstep(40.0, 160.0, camDist));
    depthRim *= (1.0h - glass * 0.75h) * water * nearOnly;
    half shoreGrad = max(lipGrad, depthRim * 0.5h * (1.0h - lipGrad));
    // 透视沙滩减浓，但不整段掐死（夜晚 glass 偏高会把沫灭没）。
    shoreGrad *= lerp(1.0h, 0.45h, saturate(glass));
    half crestAmt = lerp(crest, 0.5h, overlook);
    shoreGrad *= lerp(0.9h, 1.05h, crestAmt);
    shoreGrad *= (half)saturate(1.15 - overlook * 0.55h);

    half shoreMask;
    half shoreWash;
    SampleShoreBrush(
        input.worldXZ, shoreGrad, crest, half2(foamN.x, foamN.z), shoreMask, shoreWash);
    half inFront = (half)saturate((surfaceEye - shoreSceneEye) * 4.0);
    shoreMask *= (1.0h - inFront);
    shoreWash *= (1.0h - inFront);

#if !defined(OIL_MOBILE_PERF)
    // 折射只在浅滩 glass 窗口。外海 glass≈0，不要每像素再采一张全分辨率颜色。
    [branch]
    if (glass > 0.02h)
    {
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
    }
#endif

    // 海面铺满画面。雾画布是两次世界空间采样，水上只留 stroke 软雾（与移动端同一条）。
    color = (half3)ApplyOilFog((float3)color, surface.depth, stroke);

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
#if defined(OIL_MOBILE_PERF)
    // Far ocean: lane early-out still runs ALU; skip past ~1.2km.
    [branch]
    if (camDist < 1200.0)
#endif
    {
        half glint = OilOceanSunGlint(input.positionWS, normalWS, t, stroke, shoreMask);
        // 夜：日道更浅、偏冷青；昼保持材质 _GlintColor。
        half3 glintCol = (half3)_GlintColor.rgb;
        half3 coolPath = half3(0.52h, 0.70h, 0.95h);
        glintCol = lerp(glintCol, coolPath, night * 0.72h);
        glint *= lerp(1.0h, 0.48h, night);
        color += glintCol * glint;
    }

    // 钛白岸沫：直接靠向 _FoamColor，避免深蓝紫水体把沫染灰。
    half3 foamCol = (half3)_FoamColor.rgb;
    half foamNight = lerp(1.0h, 0.7h, night);
    half foamMix = saturate(shoreWash * 0.18h + shoreMask * 0.95h) * foamNight;
    color = lerp(color, foamCol, foamMix);
    alpha = saturate(alpha + shoreMask * 0.35h + relief * 0.06h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
