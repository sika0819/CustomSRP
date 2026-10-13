#ifndef CUSTOM_FOG_INCLUDED
#define CUSTOM_FOG_INCLUDED

// Clearance is 1 when the fragment is clear and 0 when it is fully fogged.
// _CustomFogMode: 0 linear, 1 exponential, 2 exponential squared.
float _CustomFogEnabled;
float _CustomFogMode;
float4 _CustomFogColor;
float4 _CustomFogParams;
float _CustomFogSkyPower;
// OilAtmosphereDriver Foggy weather weight in [0,1]. Extra glaze only — base density stays in FogClearance.
float _FogDensity;

// Dedicated sky/cloud fog dye — set by OilSkyboxTime. Does not rely on
// _CustomFogEnabled (CreateSkyboxRendererList can miss SetupPass globals).
float4 _OilAtmosphereFogColor; // linear rgb
float _OilAtmosphereFogAmount; // 0..1 master

// Soft OilCanvas weave used as the fog dry-brush (not OilBrush dabs).
TEXTURE2D(_OilFogCanvas);
SAMPLER(sampler_OilFogCanvas);
float4 _OilFogCanvas_ST;
float _OilFogCanvasEnable;

float FogClearance(float viewZ)
{
    float z = max(viewZ, 0.0);
    if (_CustomFogMode < 0.5)
    {
        return saturate(z * _CustomFogParams.z + _CustomFogParams.w);
    }

    if (_CustomFogMode < 1.5)
    {
        return saturate(exp2(-_CustomFogParams.y * z));
    }

    float d = _CustomFogParams.x * z;
    return saturate(exp2(-d * d));
}

float3 ApplyFog(float3 color, float viewZ, float coverage)
{
    if (_CustomFogEnabled < 0.5)
    {
        return color;
    }

    return lerp(_CustomFogColor.rgb * coverage, color, FogClearance(viewZ));
}

float3 ApplyFog(float3 color, float viewZ)
{
    return ApplyFog(color, viewZ, 1.0);
}

float OilFoggyBoost()
{
    return 1.0 + saturate(_FogDensity) * 0.35;
}

// Authored ExpSquared density (~0.0002..0.002) + Foggy weather → 0..1 concentration.
float AtmosphereConcentration()
{
    float dens = 0.001;
    if (_CustomFogMode >= 1.5)
    {
        dens = _CustomFogParams.x * (1.0 / 1.20112240879);
    }
    else if (_CustomFogMode >= 0.5)
    {
        dens = _CustomFogParams.y * (1.0 / 1.44269504089);
    }

    // 0.0002 → low band, 0.001 → mid, 0.002+ → climbs high.
    float n = saturate((dens - 0.00015) / 0.00185);
    n = saturate(n + saturate(_FogDensity) * 0.4);
    return n;
}

// Fog cover height on the sky dome. Density raises the ceiling; max = full skybox cover.
float AtmosphereFogCover(float3 dir, float concentration)
{
    // View-Y ceiling: sparse haze hugs the horizon; max concentration blankets zenith too.
    float fogCeil = lerp(0.06, 1.0, concentration);
    float above = dir.y;
    if (above <= 0.0)
    {
        return 1.0;
    }

    // At full concentration, cover the entire dome (including zenith).
    if (concentration >= 0.995)
    {
        return 1.0;
    }

    // 1 at horizon / below, 0 above the density ceiling.
    float cover = 1.0 - smoothstep(0.0, max(fogCeil, 1e-3), saturate(above));
    // skyPower: higher = sharper top edge (thinner feather).
    float feather = saturate(2.0 / max(_CustomFogSkyPower, 0.5));
    cover = pow(saturate(cover), lerp(0.65, 1.35, 1.0 - feather));
    return saturate(cover);
}

// Sky fog cover on the painted base — height from density.
float3 ApplySkyFog(float3 color, float3 viewDirWS)
{
    if (_CustomFogEnabled < 0.5)
    {
        return color;
    }

    float lenSq = dot(viewDirWS, viewDirWS);
    float3 dir = lenSq > 1e-8 ? viewDirWS * rsqrt(lenSq) : float3(0.0, 1.0, 0.0);
    float concentration = AtmosphereConcentration();
    float cover = AtmosphereFogCover(dir, concentration);
    float opacity = lerp(0.50, 1.0, concentration);
    return lerp(color, _CustomFogColor.rgb, saturate(cover * opacity));
}

// Crush OilBrush dabs / high-contrast strokes into a soft mid-gray grain.
half SoftFogStroke(half stroke)
{
    return saturate(lerp(0.5h, stroke, 0.2h));
}

// Dual-octave OilCanvas weave. Large tiles + soft blend — dry-brush scumble, not holes.
half SampleOilFogCanvasWS(float3 positionWS)
{
#if defined(SHADER_STAGE_FRAGMENT)
    if (_OilFogCanvasEnable < 0.5)
    {
        return 0.5h;
    }

    float2 scale = max(_OilFogCanvas_ST.xy, float2(1e-4, 1e-4));
    float2 uv = positionWS.xz * scale + _OilFogCanvas_ST.zw;
    half a = (half)SAMPLE_TEXTURE2D(_OilFogCanvas, sampler_OilFogCanvas, uv).r;
    half b = (half)SAMPLE_TEXTURE2D(
        _OilFogCanvas, sampler_OilFogCanvas, uv * 1.618 + float2(0.31, 0.17)).r;
    half raw = saturate(a * 0.62h + b * 0.38h);
    return lerp(0.5h, raw, 0.38h);
#else
    return 0.5h;
#endif
}

half SampleOilFogCanvasDir(float3 viewDirWS)
{
#if defined(SHADER_STAGE_FRAGMENT)
    if (_OilFogCanvasEnable < 0.5)
    {
        return 0.5h;
    }

    float3 dir = normalize(viewDirWS);
    float yaw = atan2(dir.x, dir.z);
    float elev = asin(clamp(dir.y, -1.0, 1.0));
    float2 scale = max(_OilFogCanvas_ST.xy, float2(1e-4, 1e-4));
    // Horizon-aligned weave: soft large strokes along the band.
    float2 uv = float2(yaw * scale.x * 2.2 + _OilFogCanvas_ST.z, elev * scale.y * 3.5 + _OilFogCanvas_ST.w);
    half a = (half)SAMPLE_TEXTURE2D(_OilFogCanvas, sampler_OilFogCanvas, uv).r;
    half b = (half)SAMPLE_TEXTURE2D(
        _OilFogCanvas, sampler_OilFogCanvas, uv * 1.618 + float2(0.31, 0.17)).r;
    half raw = saturate(a * 0.62h + b * 0.38h);
    return lerp(0.5h, raw, 0.32h);
#else
    return 0.5h;
#endif
}

half OilFogBrush(half materialStroke, half canvasStroke)
{
    half soft = SoftFogStroke(materialStroke);
    if (_OilFogCanvasEnable < 0.5)
    {
        return soft;
    }

    // Canvas is the fog brush; material stroke only a whisper.
    return saturate(lerp(canvasStroke, soft, 0.12h));
}

// Soft canvas scumble — value grain only. Keep the authored fog hue
// (dawn ochre / dusk red / night #36363C). No warm/cool tear ridges.
float3 OilFogPigment(float3 fogCol, half stroke, float fogAmt)
{
    half grain = stroke - 0.5h;
    fogCol *= 1.0 + (float)grain * fogAmt * 0.05;
    return fogCol;
}

float3 ApplyOilFogCore(float3 color, float viewZ, half stroke)
{
    if (_CustomFogEnabled < 0.5)
    {
        return color;
    }

    float clearance = FogClearance(viewZ);
    float fogAmt = 1.0 - clearance;
    // Soft density wobble — do not tear holes in the glaze.
    half w = stroke - 0.5h;
    fogAmt = saturate(fogAmt + (float)w * fogAmt * 0.12);
    fogAmt = saturate(fogAmt * 0.92 * OilFoggyBoost());
    float3 fogCol = OilFogPigment(_CustomFogColor.rgb, stroke, fogAmt);
    return lerp(color, fogCol, fogAmt);
}

float3 ApplyOilFog(float3 color, float viewZ, half stroke)
{
    return ApplyOilFogCore(color, viewZ, SoftFogStroke(stroke));
}

float3 ApplyOilFog(float3 color, float viewZ, half stroke, float3 positionWS)
{
    half canvas = SampleOilFogCanvasWS(positionWS);
    return ApplyOilFogCore(color, viewZ, OilFogBrush(stroke, canvas));
}

float3 AtmosphereFogColor(half stroke, float fogAmt)
{
    float3 fogCol = _OilAtmosphereFogAmount > 1e-4
        ? _OilAtmosphereFogColor.rgb
        : _CustomFogColor.rgb;
    return OilFogPigment(fogCol, SoftFogStroke(stroke), fogAmt);
}

// Fog glaze ON TOP of the painted sky base. Height from density; opacity from concentration.
float3 ApplyOilSkyFog(float3 color, float3 viewDirWS, half stroke)
{
    float master = saturate(_OilAtmosphereFogAmount);
    if (master < 1e-4 && _CustomFogEnabled < 0.5)
    {
        return color;
    }

    float lenSq = dot(viewDirWS, viewDirWS);
    float3 dir = lenSq > 1e-8 ? viewDirWS * rsqrt(lenSq) : float3(0.0, 1.0, 0.0);
    float concentration = AtmosphereConcentration();
    float cover = AtmosphereFogCover(dir, concentration);
    float amount = master > 1e-4 ? master : 1.0;
    // How solid the glaze is — max density fully blankets the painted base.
    float opacity = lerp(0.50, 1.0, concentration) * amount;
    float fogAmt = saturate(cover * opacity);
    float3 fogCol = AtmosphereFogColor(stroke, fogAmt);
    // Cover the skybox base (PaintSky), do not replace zenith when cover is 0.
    return lerp(color, fogCol, fogAmt);
}

// Opaque cloud cards: same density-height fog glaze on RGB. Never touch alpha.
float3 ApplyOilCloudFog(float3 color, float3 viewDirWS, half stroke)
{
    float master = saturate(_OilAtmosphereFogAmount);
    if (master < 1e-4 && _CustomFogEnabled < 0.5)
    {
        return color;
    }

    float lenSq = dot(viewDirWS, viewDirWS);
    float3 dir = lenSq > 1e-8 ? viewDirWS * rsqrt(lenSq) : float3(0.0, 1.0, 0.0);
    float concentration = AtmosphereConcentration();
    float cover = AtmosphereFogCover(dir, concentration);
    float amount = master > 1e-4 ? master : 1.0;
    // Light glaze only — heavy fog wash erased oil impasto on the cloud brush.
    float opacity = lerp(0.10, 0.32, concentration) * amount;
    float fogAmt = saturate(cover * opacity);
    float3 fogCol = AtmosphereFogColor(SoftFogStroke(stroke), fogAmt);
    return lerp(color, fogCol, fogAmt);
}

#endif
