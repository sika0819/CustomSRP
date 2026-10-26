#ifndef CUSTOM_OIL_WATER_REFLECT_INCLUDED
#define CUSTOM_OIL_WATER_REFLECT_INCLUDED

#include "../ShaderLibrary/OilBrdfCommon.hlsl"

// ReflectionRT — low-res oil glaze (planar cam matches main FOV/aspect).
TEXTURE2D(_OilPlanarReflectTex);
SAMPLER(sampler_OilPlanarReflectTex);
float4 _OilPlanarReflectTex_TexelSize;
float _OilPlanarReflectOn;

float _OilSkyPeriod;
float _OilSkyLookReady;
float3 _OilSkySunDir;
float3 _OilSkyMoonDir;
float4 _OilSkyZenith[4];
float4 _OilSkyMid[4];
float4 _OilSkyHorizon[4];
float4 _OilSkyGround[4];
float4 _OilSkySunDisc[4];
float4 _OilSkySunGlow[4];
float4 _OilSkyMoonDisc[4];
float4 _OilSkyMoonGlow[4];
float4 _OilSkySunParams[4];
float4 _OilSkyMoonParams[4];
float _OilSkySunrise;

half OilWaterNightWeight(float period)
{
    if (period < 0.0 || period > 4.0)
    {
        period = 1.0;
    }
    period = period - floor(period * 0.25) * 4.0;
    float d = min(abs(period - 3.0), min(abs(period + 1.0), abs(period - 7.0)));
    return saturate(1.0 - d * 1.15);
}

// Keep in sync with OilSkyboxNPRPass.hlsl PeriodLookFallback — same Camera_1 anchors
// so water analytic sky matches the skybox before C# pushes _OilSkyLookReady.
void OilWaterPeriodLookFallback(
    int index,
    out half3 zenith,
    out half3 mid,
    out half3 horizon,
    out half3 ground,
    out half3 sunDisc,
    out half3 sunGlow,
    out half3 moonDisc,
    out half3 moonGlow,
    out half sunSize,
    out half sunIntensity,
    out half sunGlowAmt,
    out half moonSize,
    out half moonIntensity,
    out half moonGlowAmt)
{
    if (index <= 0)
    {
        zenith = half3(0.0078h, 0.1451h, 0.7412h);
        mid = half3(0.9098h, 0.7686h, 0.6902h);
        horizon = half3(1.0000h, 0.7373h, 0.0000h);
        ground = half3(0.40h, 0.36h, 0.26h);
        sunDisc = half3(1.0000h, 0.9725h, 0.9098h);
        sunGlow = half3(1.0000h, 0.8235h, 0.3922h);
        moonDisc = half3(0.7800h, 0.8200h, 0.9000h);
        moonGlow = half3(0.4500h, 0.5200h, 0.7000h);
        sunSize = 0.07h;
        sunIntensity = 0.9h;
        sunGlowAmt = 0.24h;
        moonSize = 0.05h;
        moonIntensity = 0.12h;
        moonGlowAmt = 0.06h;
    }
    else if (index == 1)
    {
        // Camera_1 noon — keep in sync with OilSkyboxNPRPass PeriodLookFallback.
        zenith = half3(0.0685h, 0.4793h, 1.0000h);
        mid = half3(0.2739h, 0.5619h, 0.9154h);
        horizon = half3(0.4793h, 0.6445h, 0.8308h);
        ground = half3(0.8392h, 0.7686h, 0.7020h);
        sunDisc = half3(1.0000h, 0.9804h, 0.8235h);
        sunGlow = half3(1.0000h, 0.9804h, 0.8235h);
        moonDisc = half3(0.0h, 0.0h, 0.0h);
        moonGlow = half3(0.0h, 0.0h, 0.0h);
        sunSize = 0.055h;
        sunIntensity = 1.0h;
        sunGlowAmt = 0.16h;
        moonSize = 0.05h;
        moonIntensity = 0.0h;
        moonGlowAmt = 0.0h;
    }
    else if (index == 2)
    {
        zenith = half3(0.2157h, 0.2588h, 0.4314h);
        mid = half3(0.459h, 0.437h, 0.473h);
        horizon = half3(0.9098h, 0.7686h, 0.5490h);
        ground = half3(0.22h, 0.16h, 0.14h);
        sunDisc = half3(1.0000h, 0.7843h, 0.4157h);
        sunGlow = half3(1.0000h, 0.7843h, 0.4157h);
        moonDisc = half3(0.7000h, 0.7400h, 0.8600h);
        moonGlow = half3(0.3500h, 0.4000h, 0.5800h);
        sunSize = 0.08h;
        sunIntensity = 0.75h;
        sunGlowAmt = 0.28h;
        moonSize = 0.055h;
        moonIntensity = 0.45h;
        moonGlowAmt = 0.14h;
    }
    else
    {
        // Night — was the startup blue bug: old path always used day zenith (0.07,0.48,1).
        zenith = half3(0.0118h, 0.0706h, 0.3216h);
        mid = half3(0.0059h, 0.0608h, 0.3118h);
        horizon = half3(0.0000h, 0.0510h, 0.3020h);
        ground = half3(0.0000h, 0.0306h, 0.1812h);
        sunDisc = half3(0.4000h, 0.4500h, 0.5800h);
        sunGlow = half3(0.0800h, 0.0900h, 0.1200h);
        moonDisc = half3(0.6200h, 0.6800h, 0.8200h);
        moonGlow = half3(0.2800h, 0.3400h, 0.5200h);
        sunSize = 0.05h;
        sunIntensity = 0.08h;
        sunGlowAmt = 0.02h;
        moonSize = 0.07h;
        moonIntensity = 0.85h;
        moonGlowAmt = 0.22h;
    }
}

void OilWaterSkyAnchors(
    float period,
    out half3 zenith,
    out half3 mid,
    out half3 horizon,
    out half3 ground,
    out half3 sunDisc,
    out half3 sunGlow,
    out half3 moonDisc,
    out half3 moonGlow,
    out half sunSize,
    out half sunIntensity,
    out half sunGlowAmt,
    out half moonSize,
    out half moonIntensity,
    out half moonGlowAmt)
{
    period = period - floor(period * 0.25) * 4.0;
    int i0 = (int)floor(period);
    if (i0 > 3)
    {
        i0 = 0;
    }
    int i1 = i0 + 1;
    if (i1 > 3)
    {
        i1 = 0;
    }
    half t = (half)(period - floor(period));

    if (_OilSkyLookReady < 0.5)
    {
        half3 z0, m0, h0, g0, sd0, sg0, md0, mg0;
        half ss0, si0, sga0, ms0, mi0, mga0;
        half3 z1, m1, h1, g1, sd1, sg1, md1, mg1;
        half ss1, si1, sga1, ms1, mi1, mga1;
        OilWaterPeriodLookFallback(i0, z0, m0, h0, g0, sd0, sg0, md0, mg0, ss0, si0, sga0, ms0, mi0, mga0);
        OilWaterPeriodLookFallback(i1, z1, m1, h1, g1, sd1, sg1, md1, mg1, ss1, si1, sga1, ms1, mi1, mga1);
        zenith = lerp(z0, z1, t);
        mid = lerp(m0, m1, t);
        horizon = lerp(h0, h1, t);
        ground = lerp(g0, g1, t);
        sunDisc = lerp(sd0, sd1, t);
        sunGlow = lerp(sg0, sg1, t);
        moonDisc = lerp(md0, md1, t);
        moonGlow = lerp(mg0, mg1, t);
        sunSize = lerp(ss0, ss1, t);
        sunIntensity = lerp(si0, si1, t);
        sunGlowAmt = lerp(sga0, sga1, t);
        moonSize = lerp(ms0, ms1, t);
        moonIntensity = lerp(mi0, mi1, t);
        moonGlowAmt = lerp(mga0, mga1, t);
        return;
    }

    zenith = lerp((half3)_OilSkyZenith[i0].rgb, (half3)_OilSkyZenith[i1].rgb, t);
    mid = lerp((half3)_OilSkyMid[i0].rgb, (half3)_OilSkyMid[i1].rgb, t);
    horizon = lerp((half3)_OilSkyHorizon[i0].rgb, (half3)_OilSkyHorizon[i1].rgb, t);
    ground = lerp((half3)_OilSkyGround[i0].rgb, (half3)_OilSkyGround[i1].rgb, t);
    sunDisc = lerp((half3)_OilSkySunDisc[i0].rgb, (half3)_OilSkySunDisc[i1].rgb, t);
    sunGlow = lerp((half3)_OilSkySunGlow[i0].rgb, (half3)_OilSkySunGlow[i1].rgb, t);
    moonDisc = lerp((half3)_OilSkyMoonDisc[i0].rgb, (half3)_OilSkyMoonDisc[i1].rgb, t);
    moonGlow = lerp((half3)_OilSkyMoonGlow[i0].rgb, (half3)_OilSkyMoonGlow[i1].rgb, t);
    sunSize = lerp((half)_OilSkySunParams[i0].x, (half)_OilSkySunParams[i1].x, t);
    sunIntensity = lerp((half)_OilSkySunParams[i0].y, (half)_OilSkySunParams[i1].y, t);
    sunGlowAmt = lerp((half)_OilSkySunParams[i0].z, (half)_OilSkySunParams[i1].z, t);
    moonSize = lerp((half)_OilSkyMoonParams[i0].x, (half)_OilSkyMoonParams[i1].x, t);
    moonIntensity = lerp((half)_OilSkyMoonParams[i0].y, (half)_OilSkyMoonParams[i1].y, t);
    moonGlowAmt = lerp((half)_OilSkyMoonParams[i0].z, (half)_OilSkyMoonParams[i1].z, t);

    // Same sunrise weight as the skybox, so reflections don't keep the old dawn-anchor fade.
    half sunrise = (half)saturate(_OilSkySunrise);
    if (sunrise > 0.001h)
    {
        zenith = lerp(zenith, half3(1.0 / 255.0, 40.0 / 255.0, 205.0 / 255.0), sunrise);
        mid = lerp(mid, half3(72.0 / 255.0, 104.0 / 255.0, 184.0 / 255.0), sunrise);
        horizon = lerp(horizon, half3(224.0 / 255.0, 160.0 / 255.0, 40.0 / 255.0), sunrise);
        sunDisc = lerp(sunDisc, half3(1.0, 138.0 / 255.0, 95.0 / 255.0), sunrise);
        sunGlow = lerp(sunGlow, half3(240.0 / 255.0, 168.0 / 255.0, 58.0 / 255.0), sunrise);
        sunIntensity = lerp(sunIntensity, 0.72h, sunrise);
        sunGlowAmt = lerp(sunGlowAmt, 0.16h, sunrise);
    }
}

half3 OilWaterSkyColor(float y, half stroke, half3 zenith, half3 mid, half3 horizon, half3 ground)
{
    // 水面釉：地平别跟天空一样爆亮，掠射只取偏中层的色带。
    horizon = lerp(mid, horizon, 0.45h);
    float above = saturate(y * 0.85 + 0.12);
    half3 col = lerp(horizon, mid, smoothstep(0.0, 0.32, above));
    col = lerp(col, zenith, smoothstep(0.20, 0.88, above));
    col = lerp(col, ground, smoothstep(0.02, -0.28, y));
    half shift = (stroke - 0.5h) * 0.22h;
    half3 carried = lerp(horizon, mid, saturate((half)above + shift));
    return lerp(col, carried, 0.40h);
}

// 倒影天空：只叠色带，不跟太阳/月亮方向。
// 太阳光痕只走 OilOceanSunGlint / OilRiverSunGlint（太阳→相机车道）。
half3 OilWaterAnalyticSky(float3 rayDir, half stroke)
{
    half3 zenith, mid, horizon, ground, sunDisc, sunGlow, moonDisc, moonGlow;
    half sunSize, sunIntensity, sunGlowAmt, moonSize, moonIntensity, moonGlowAmt;
    OilWaterSkyAnchors(
        _OilSkyPeriod,
        zenith, mid, horizon, ground,
        sunDisc, sunGlow, moonDisc, moonGlow,
        sunSize, sunIntensity, sunGlowAmt,
        moonSize, moonIntensity, moonGlowAmt);

    half3 sky = OilWaterSkyColor(rayDir.y, stroke, zenith, mid, horizon, ground);
    // 峰值再压一档，避免中午/日出色带把釉面刷成粉笔。
    half peak = max(sky.r, max(sky.g, sky.b));
    sky *= lerp(1.0h, 0.70h, saturate((peak - 0.55h) / 0.45h));
    return sky;
}

// Compat alias — shared OilPosterizeLuma lives in OilBrdfCommon.
half3 OilWaterPosterizeLuma(half3 c, half steps)
{
    return OilPosterizeLuma(c, steps);
}

half3 ApplyOilWaterReflection(
    half3 color,
    float3 positionWS,
    half3 normalWS,
    half3 viewDir,
    half stroke,
    half openWater,
    float2 screenUV,
    half distort,
    half reflectSteps)
{
    // strength 用入参路径的材质常量；俯视时也要能明显拉开 0↔1（旧 nadir×0.2 体感「没反应」）。
    half strength = (half)_ReflectStrength * saturate(openWater);
    if (strength < 0.01h)
    {
        return color;
    }

    half facing = saturate(dot(viewDir, half3(0.0h, 1.0h, 0.0h)));
    half fresnel = saturate(1.0h - facing);
    fresnel = fresnel * fresnel * (3.0h - 2.0h * fresnel);
    // Soft glaze — 连续 mix；nadir 也给足，Reflection Glaze 滑条才跟手。
    half mix = strength * lerp(0.48h, 0.82h, fresnel);
    mix *= lerp(0.9h, 1.0h, stroke);

    // 侧缘略压，挡住掠射发白；别压死，否则山倒影跟着没。
    float2 frame = saturate(min(screenUV, 1.0 - screenUV) * 5.0);
    half inFrame = saturate(frame.x * frame.y);
    half sideFrame = saturate(min(screenUV.x, 1.0 - screenUV.x) * 5.5);
    inFrame *= lerp(0.65h, 1.0h, sideFrame);
    mix *= lerp(0.55h, 1.0h, inFrame);

    // 解析天空仍用偏平法线；UV 扭曲另走 waveN（见下）——混在一起会让 Distort≈noop。
    float3 n = normalize(lerp(float3(0.0, 1.0, 0.0), (float3)normalWS, 0.28));
    float3 rayDir = reflect(-(float3)viewDir, n);
    // 解析天空：色带 only（无太阳盘）；太阳光痕另走 glint。
    half3 reflectCol = OilWaterAnalyticSky(rayDir, stroke);

    // 解析天空略往水体收；过亮压 chalk。平面山体倒影叠在后面，不受 keep 冲掉。
    half3 waterTint = lerp(
        (half3)_BaseColor.rgb,
        (half3)_ShallowColor.rgb,
        saturate(fresnel * 1.3h + 0.25h));
    {
        half rb = (waterTint.r + waterTint.b) * 0.5h;
        waterTint.g = lerp(waterTint.g, rb, 0.35h);
        half keep = lerp(0.42h, 0.68h, fresnel);
        keep *= lerp(0.75h, 1.0h, inFrame);

        half reflL = max(reflectCol.r, max(reflectCol.g, reflectCol.b));
        half reflC = reflL - min(reflectCol.r, min(reflectCol.g, reflectCol.b));
        half chalk = saturate((reflL - 0.62h) * 2.8h) * saturate(1.0h - reflC * 6.0h);
        half hot = saturate((reflL - 0.72h) * 4.0h);
        chalk = saturate(max(chalk, hot * 0.55h));
        half sunrise = (half)saturate(_OilSkySunrise);
        chalk = saturate(chalk * lerp(0.7h, 1.1h, sunrise));
        reflectCol = lerp(reflectCol, waterTint, chalk * 0.5h);
        reflectCol *= lerp(1.0h, 0.78h, chalk);
        reflectCol = lerp(waterTint, reflectCol, keep);
    }

    // Cap = OilOceanNPR _ReflectDistort Range max。
    half distortAmt = min(max(distort, 0.0h), 0.35h) * lerp(0.55h, 1.0h, inFrame);

    if (_OilPlanarReflectOn > 0.5)
    {
        float2 uv = screenUV;
        // 波浪法线直接进 UV（0.85），再加笔触底噪——平静处也能糊掉低分辨率锯齿。
        float3 nWarp = normalize(lerp(float3(0.0, 1.0, 0.0), (float3)normalWS, 0.85));
        float3 nFlat = float3(nWarp.x, 0.0, nWarp.z);
        float3 lookH = float3(viewDir.x, 0.0, viewDir.z);
        float lookLen = length(lookH);
        lookH = lookLen > 1e-4 ? lookH / lookLen : float3(0.0, 0.0, 1.0);
        float3 rightH = cross(float3(0.0, 1.0, 0.0), lookH);
        float rightLen = length(rightH);
        rightH = rightLen > 1e-4 ? rightH / rightLen : float3(1.0, 0.0, 0.0);
        // 主扭曲在 U（横）：acrossReflect → 左右涟漪；V 只留很淡的沿倒影起伏。
        float alongReflect = dot(nFlat, lookH);
        float acrossReflect = dot(nFlat, rightH);
        float2 warp = float2(acrossReflect, alongReflect * 0.28) * ((float)distortAmt * 2.4);
        float strokeWobble = (float)(stroke - 0.5h);
        warp += float2(strokeWobble, strokeWobble * 0.25) * (float)distortAmt;
        uv += warp;
        float2 edge = saturate(min(uv, 1.0 - uv) * 6.0);
        half edgeFade = (half)(edge.x * edge.y);
        if (edgeFade > 0.001h)
        {
#if defined(OIL_MOBILE_PERF)
            half3 planar = (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv, 0.0).rgb;
#else
            // smear 横宽竖窄，跟主扭曲轴向一致。
            float2 texel = max(_OilPlanarReflectTex_TexelSize.xy, float2(1.0 / 160.0, 1.0 / 160.0));
            float smear = 2.0 + (float)distortAmt * 18.0;
            texel *= float2(smear, smear * 0.4);
            half3 planar = (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv, 0.0).rgb;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv + float2(texel.x, 0.0), 0.0).rgb;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv - float2(texel.x, 0.0), 0.0).rgb;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv + float2(0.0, texel.y), 0.0).rgb * 0.55h;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv - float2(0.0, texel.y), 0.0).rgb * 0.55h;
            planar *= 1.0h / 4.1h;
#endif
            // 植被溢绿削一档；山体轮廓保留。
            half maxRB = max(planar.r, planar.b);
            half gPush = saturate((planar.g - maxRB) * 2.4h);
            planar.g = lerp(planar.g, maxRB, gPush * 0.72h);
            planar.b = saturate(planar.b + gPush * 0.12h);
            // 平面相机倒影（山/岸）拉满 edgeFade，不再 ×0.32 冲没。
            reflectCol = lerp(reflectCol, planar, edgeFade);
        }
    }

    // 极轻柔化即可；强 posterize / 阶梯 mix 会在釉面上拉条带。
    // reflectSteps 仍接材质，但只作很淡的 luma 分档。
    half steps = max(reflectSteps, 2.0h);
    half3 posterized = OilWaterPosterizeLuma(reflectCol, steps);
    reflectCol = lerp(reflectCol, posterized, 0.18h);
    return lerp(color, reflectCol, saturate(mix));
}

#endif
