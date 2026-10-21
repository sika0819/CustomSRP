#ifndef CUSTOM_OIL_WATER_REFLECT_INCLUDED
#define CUSTOM_OIL_WATER_REFLECT_INCLUDED

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
        zenith = half3(0.07h, 0.48h, 1.0h);
        mid = half3(0.45h, 0.62h, 0.86h);
        horizon = half3(0.55h, 0.72h, 0.90h);
        ground = half3(0.28h, 0.32h, 0.28h);
        sunDisc = half3(1.0h, 0.72h, 0.37h);
        sunGlow = half3(1.0h, 0.84h, 0.45h);
        moonDisc = half3(0.78h, 0.84h, 0.95h);
        moonGlow = half3(0.45h, 0.52h, 0.72h);
        sunSize = 0.08h;
        sunIntensity = 1.0h;
        sunGlowAmt = 0.35h;
        moonSize = 0.06h;
        moonIntensity = 0.0h;
        moonGlowAmt = 0.0h;
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
}

half3 OilWaterSkyColor(float y, half stroke, half3 zenith, half3 mid, half3 horizon, half3 ground)
{
    float above = saturate(y * 0.85 + 0.12);
    half3 col = lerp(horizon, mid, smoothstep(0.0, 0.28, above));
    col = lerp(col, zenith, smoothstep(0.16, 0.82, above));
    col = lerp(col, ground, smoothstep(0.02, -0.28, y));
    half shift = (stroke - 0.5h) * 0.28h;
    half3 carried = lerp(horizon, zenith, saturate((half)above + shift));
    return lerp(col, carried, 0.55h);
}

half3 OilWaterBodyGlint(half3 sky, float3 rayDir, float3 bodyDir, half size, half intensity, half glowAmt, half3 disc, half3 glow)
{
    if (intensity < 0.05h || dot(bodyDir, bodyDir) < 1e-4)
    {
        return sky;
    }

    bodyDir = normalize(bodyDir);
    half vis = (half)smoothstep(-0.04, 0.12, bodyDir.y);
    if (vis < 0.001h)
    {
        return sky;
    }

    half facing = (half)dot(rayDir, bodyDir);
    half discR = saturate((facing - (1.0h - size * 3.2h)) / max(size * 3.2h, 0.02h));
    discR = discR * discR;
    half glowR = saturate((facing - (1.0h - size * 9.0h)) / max(size * 9.0h, 0.04h));
    sky = lerp(sky, glow, glowR * glowAmt * vis * 0.55h);
    return lerp(sky, disc, discR * vis);
}

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
    sky = OilWaterBodyGlint(sky, rayDir, _OilSkySunDir, sunSize, sunIntensity, sunGlowAmt, sunDisc, sunGlow);
    sky = OilWaterBodyGlint(sky, rayDir, _OilSkyMoonDir, moonSize, moonIntensity, moonGlowAmt, moonDisc, moonGlow);
    return sky;
}

half3 ApplyOilWaterReflection(
    half3 color,
    float3 positionWS,
    half3 normalWS,
    half3 viewDir,
    half stroke,
    half openWater,
    float2 screenUV)
{
    half strength = (half)_ReflectStrength * saturate(openWater);
    if (strength < 0.01h)
    {
        return color;
    }

    half facing = saturate(dot(viewDir, half3(0.0h, 1.0h, 0.0h)));
    half fresnel = saturate(1.0h - facing);
    fresnel = fresnel * fresnel * (3.0h - 2.0h * fresnel);
    // Soft glaze — never a hard mirror, even at grazing angles.
    half mix = strength * lerp(0.2h, 0.7h, fresnel);
    mix *= lerp(0.9h, 1.0h, stroke);

    float3 n = normalize(lerp(float3(0.0, 1.0, 0.0), (float3)normalWS, 0.35));
    float3 rayDir = reflect(-(float3)viewDir, n);
    half3 reflectCol = OilWaterAnalyticSky(rayDir, stroke);

    // 全时段把倒影色往水体本色调，避免 horizon/sun 把水面贴成偏白。
    // 不动 mix / fresnel / _ReflectStrength（倒影可见度由它们控制）。
    {
        half3 waterTint = lerp(
            (half3)_BaseColor.rgb,
            (half3)_ShallowColor.rgb,
            saturate(fresnel * 1.4h + 0.25h));
        // 视线越正（fresnel 小）越压向深水本色；掠射保留更多天光（不破坏远处夕阳感）。
        half keep = lerp(0.30h, 0.65h, fresnel);
        reflectCol = lerp(waterTint, reflectCol, keep);
    }

    if (_OilPlanarReflectOn > 0.5)
    {
        float2 uv = screenUV;
        uv += float2(n.x, n.z) * 0.035;
        uv += float2(stroke - 0.5h, 0.5h - stroke) * 0.01h;
        float2 edge = saturate(min(uv, 1.0 - uv) * 6.0);
        half edgeFade = (half)(edge.x * edge.y);
        if (edgeFade > 0.001h)
        {
            // Low-res RT + wide 5-tap smear at mip0 (no auto-mips — those were black).
            float2 texel = max(_OilPlanarReflectTex_TexelSize.xy, float2(1.0 / 160.0, 1.0 / 160.0));
            texel *= 4.5;
            half3 planar = (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv, 0.0).rgb;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv + float2(texel.x, 0.0), 0.0).rgb;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv - float2(texel.x, 0.0), 0.0).rgb;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv + float2(0.0, texel.y), 0.0).rgb;
            planar += (half3)SAMPLE_TEXTURE2D_LOD(
                _OilPlanarReflectTex, sampler_OilPlanarReflectTex, uv - float2(0.0, texel.y), 0.0).rgb;
            planar *= 0.2h;
            reflectCol = lerp(reflectCol, planar, edgeFade);
        }
    }

    return lerp(color, reflectCol, saturate(mix));
}

#endif
