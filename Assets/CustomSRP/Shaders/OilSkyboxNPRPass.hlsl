#ifndef CUSTOM_OIL_SKYBOX_NPR_PASS_INCLUDED
#define CUSTOM_OIL_SKYBOX_NPR_PASS_INCLUDED

#include "OilPaintNoise.hlsl"

#ifndef PI
#define PI 3.141592653589793238
#endif

// OilCanvas ridges, then sun and moon discs.
// Period colors stay in the shader and blend with _Period.

struct SkyPeriodLook
{
    half3 zenith;
    half3 mid;
    half3 horizon;
    half3 ground;
    float3 sunDir;
    float3 moonDir;
    float3 sunDisc;
    float3 sunGlowColor;
    float sunSize;
    float sunIntensity;
    float sunGlow;
    float sunVisibility;
    float3 moonDisc;
    float3 moonGlowColor;
    float moonSize;
    float moonIntensity;
    float moonGlow;
    float moonVisibility;
    float starIntensity;
};

struct Attributes
{
    float4 positionOS : POSITION;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 dir : TEXCOORD0;
};

float3 DirectionFromAngles(float elevDeg, float azDeg)
{
    float el = elevDeg * (PI / 180.0);
    float az = azDeg * (PI / 180.0);
    float c = cos(el);
    return float3(sin(az) * c, sin(el), cos(az) * c);
}

float SoftVisibility(float elevation, float start, float full)
{
    return smoothstep(start, full, elevation);
}

// Same Moorea equinox anchors as OilSkyboxTime.LightPresets. Azimuth 0 = +Z north.
float3 SunDirAtIndex(int index)
{
    if (index <= 0)
    {
        return DirectionFromAngles(-1.69, 90.60);
    }

    if (index == 1)
    {
        return DirectionFromAngles(72.44, 5.96);
    }

    if (index == 2)
    {
        return DirectionFromAngles(1.73, 270.48);
    }

    return DirectionFromAngles(-72.31, 174.09);
}

SkyPeriodLook PeriodLookAt(int index)
{
    SkyPeriodLook p;
    // Mixed oil pigments, not a primary-blue game sky. Same four anchors:
    // dawn 07:30, day 12:00, dusk 18:00, night 21:00.
    if (index <= 0)
    {
        p.zenith = half3(0.10h, 0.18h, 0.52h);
        p.mid = half3(0.34h, 0.30h, 0.46h);
        p.horizon = half3(0.86h, 0.55h, 0.28h);
        p.ground = half3(0.28h, 0.18h, 0.12h);
        p.sunDisc = float3(1.0000, 0.8200, 0.5600);
        p.sunGlowColor = float3(0.8200, 0.4200, 0.1600);
        p.sunDir = SunDirAtIndex(0);
        p.sunSize = 0.07;
        p.sunIntensity = 0.9;
        p.sunGlow = 0.32;
        p.moonDisc = float3(0.7800, 0.8200, 0.9000);
        p.moonGlowColor = float3(0.4500, 0.5200, 0.7000);
        p.moonSize = 0.05;
        p.moonIntensity = 0.12;
        p.moonGlow = 0.06;
        p.starIntensity = 0.0;
    }
    else if (index == 1)
    {
        p.zenith = half3(0.06h, 0.24h, 0.68h);
        p.mid = half3(0.26h, 0.46h, 0.70h);
        p.horizon = half3(0.50h, 0.64h, 0.74h);
        p.ground = half3(0.24h, 0.32h, 0.36h);
        p.sunDisc = float3(1.0000, 0.9600, 0.8200);
        p.sunGlowColor = float3(0.7800, 0.7400, 0.5200);
        p.sunDir = SunDirAtIndex(1);
        p.sunSize = 0.055;
        p.sunIntensity = 1.0;
        p.sunGlow = 0.16;
        p.moonDisc = float3(0.0, 0.0, 0.0);
        p.moonGlowColor = float3(0.0, 0.0, 0.0);
        p.moonSize = 0.05;
        p.moonIntensity = 0.0;
        p.moonGlow = 0.0;
        p.starIntensity = 0.0;
    }
    else if (index == 2)
    {
        p.zenith = half3(0.05h, 0.06h, 0.16h);
        p.mid = half3(0.28h, 0.12h, 0.14h);
        p.horizon = half3(0.72h, 0.28h, 0.12h);
        p.ground = half3(0.16h, 0.07h, 0.06h);
        p.sunDisc = float3(1.0000, 0.6200, 0.3200);
        p.sunGlowColor = float3(0.7000, 0.2800, 0.1000);
        p.sunDir = SunDirAtIndex(2);
        p.sunSize = 0.08;
        p.sunIntensity = 0.75;
        p.sunGlow = 0.38;
        p.moonDisc = float3(0.7000, 0.7400, 0.8600);
        p.moonGlowColor = float3(0.3500, 0.4000, 0.5800);
        p.moonSize = 0.055;
        p.moonIntensity = 0.45;
        p.moonGlow = 0.14;
        p.starIntensity = 0.12;
    }
    else
    {
        p.zenith = half3(0.07h, 0.05h, 0.18h);
        p.mid = half3(0.11h, 0.07h, 0.22h);
        p.horizon = half3(0.16h, 0.10h, 0.22h);
        p.ground = half3(0.05h, 0.04h, 0.09h);
        p.sunDisc = float3(0.4000, 0.4500, 0.5800);
        p.sunGlowColor = float3(0.0800, 0.0900, 0.1200);
        p.sunDir = SunDirAtIndex(3);
        p.sunSize = 0.05;
        p.sunIntensity = 0.08;
        p.sunGlow = 0.02;
        p.moonDisc = float3(0.6200, 0.6800, 0.8200);
        p.moonGlowColor = float3(0.2800, 0.3400, 0.5200);
        p.moonSize = 0.07;
        p.moonIntensity = 0.85;
        p.moonGlow = 0.22;
        p.starIntensity = 0.58;
    }

    int moonIndex = index + 2;
    if (moonIndex > 3)
    {
        moonIndex -= 4;
    }

    p.moonDir = SunDirAtIndex(moonIndex);
    p.sunVisibility = SoftVisibility(p.sunDir.y, -0.05, 0.12);
    p.moonVisibility = SoftVisibility(p.moonDir.y, -0.02, 0.15);
    return p;
}

SkyPeriodLook LerpPeriodLook(SkyPeriodLook a, SkyPeriodLook b, float t)
{
    SkyPeriodLook p;
    p.zenith = lerp(a.zenith, b.zenith, t);
    p.mid = lerp(a.mid, b.mid, t);
    p.horizon = lerp(a.horizon, b.horizon, t);
    p.ground = lerp(a.ground, b.ground, t);
    p.sunDir = normalize(lerp(a.sunDir, b.sunDir, t));
    p.moonDir = normalize(lerp(a.moonDir, b.moonDir, t));
    p.sunDisc = lerp(a.sunDisc, b.sunDisc, t);
    p.sunGlowColor = lerp(a.sunGlowColor, b.sunGlowColor, t);
    p.sunSize = lerp(a.sunSize, b.sunSize, t);
    p.sunIntensity = lerp(a.sunIntensity, b.sunIntensity, t);
    p.sunGlow = lerp(a.sunGlow, b.sunGlow, t);
    p.moonDisc = lerp(a.moonDisc, b.moonDisc, t);
    p.moonGlowColor = lerp(a.moonGlowColor, b.moonGlowColor, t);
    p.moonSize = lerp(a.moonSize, b.moonSize, t);
    p.moonIntensity = lerp(a.moonIntensity, b.moonIntensity, t);
    p.moonGlow = lerp(a.moonGlow, b.moonGlow, t);
    p.starIntensity = lerp(a.starIntensity, b.starIntensity, t);
    p.sunVisibility = SoftVisibility(p.sunDir.y, -0.05, 0.12);
    p.moonVisibility = SoftVisibility(p.moonDir.y, -0.02, 0.15);
    return p;
}

SkyPeriodLook EvaluatePeriodLook(float period)
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

    return LerpPeriodLook(PeriodLookAt(i0), PeriodLookAt(i1), period - floor(period));
}

// Canvas ridges run along +U. U follows yaw so the strokes lie along the horizon,
// V follows elevation so the rows stack upward. Integer turns hide the yaw seam.
// Higher _BrushScale repeats the tile more often.
half CanvasStroke(float yaw, float elev)
{
    float scale = max(_BrushScale, 0.5);
    float turns = max(round(scale * _OilCanvas_ST.x), 1.0);
    float2 uv = float2(
        yaw * (turns / (2.0 * PI)) + _OilCanvas_ST.z,
        elev * (scale * 0.16) * _OilCanvas_ST.y + _OilCanvas_ST.w);

    float2 f = frac(uv);
    float2 edge = min(f, 1.0 - f);
    half w = (half)smoothstep(0.0, 0.16, min(edge.x, edge.y));
    half a = (half)SAMPLE_TEXTURE2D(_OilCanvas, sampler_OilCanvas, uv).r;
    half b = (half)SAMPLE_TEXTURE2D(_OilCanvas, sampler_OilCanvas, uv + 0.5).r;
    half canvas = lerp(b, a, w);
    half contrast = max((half)_BrushContrast, 0.5h);
    return saturate((canvas - 0.5h) * contrast + 0.5h);
}

half3 PaintSky(float y, half stroke, SkyPeriodLook look)
{
    half strength = saturate((half)_BrushStrength);
    float above = saturate(y);
    // Horizon haze holds. Zenith color only gathers overhead.
    half3 col = lerp((half3)look.horizon, (half3)look.mid, smoothstep(0.0, 0.40, above));
    col = lerp(col, (half3)look.zenith, smoothstep(0.24, 0.96, above));

    float below = smoothstep(0.02, -0.32, y);
    col = lerp(col, (half3)look.ground, below);

    float shift = (stroke - 0.5h) * strength * 0.42h;
    half3 carried = lerp((half3)look.horizon, (half3)look.zenith, saturate(above + shift));
    col = lerp(col, carried, 0.72h * strength);

    half depth = max(saturate((half)_BrushRelief), 0.85h) * strength;
    half3 groove = col * half3(0.48h, 0.58h, 0.78h);
    half3 ridge = min(col * half3(1.22h, 1.10h, 0.96h) + 0.035h, half3(1.15h, 1.15h, 1.15h));
    half3 painted = lerp(groove, ridge, smoothstep(0.12h, 0.88h, stroke));
    return lerp(col, painted, depth);
}

half3 PaintAir(half3 sky, float3 viewDir, SkyPeriodLook look)
{
    float3 sunDir = normalize(look.sunDir);
    float sunDot = dot(viewDir, sunDir);
    half sunAir = (half)smoothstep(0.2, 0.92, sunDot) * (half)look.sunGlow * (half)look.sunVisibility;
    sky = lerp(sky, lerp(sky, (half3)look.sunGlowColor, 0.5h), saturate(sunAir * 0.4h));

    float3 moonDir = normalize(look.moonDir);
    float moonDot = dot(viewDir, moonDir);
    half moonFacing = saturate(moonDot);
    half moonSq = moonFacing * moonFacing;
    half moonAir = moonSq * moonSq * moonFacing * (half)look.moonGlow * (half)look.moonVisibility;
    sky = lerp(sky, lerp(sky, (half3)look.moonGlowColor, 0.35h), saturate(moonAir));
    return sky;
}

float2 Hash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

// Small oil dabs from Stars_Oil 4x4 atlas. Each cell stamps ONE tile, never the whole sheet.
// Density / size: material _StarDensity / _StarSize (OilSkyboxTime syncs them).
// One cell, one stamp. The disc is inset so it does not need the eight neighbors.
half3 PaintStars(half3 sky, float3 viewDir, float yaw, float pitch, float starIntensity)
{
    if (starIntensity < 0.001)
    {
        return sky;
    }

    float horizon = smoothstep(0.02, 0.22, viewDir.y);
    if (horizon < 0.001)
    {
        return sky;
    }

    float density = max(_StarDensity, 0.05);
    float sizeScale = max(_StarSize, 0.05);
    float densX = 28.0 * density;
    float densY = 14.0 * density;
    float fill = saturate(0.38 * sqrt(density));

    const float kTiles = 4.0;
    const float kTile = 0.25;
    const float kInset = 0.08;

    float2 cell = floor(float2(
        (yaw * 0.15915494 + 0.5) * densX,
        (pitch * 0.31830989 + 0.5) * densY));
    float2 rnd = Hash22(cell + float2(17.1, 9.7));
    if (rnd.x > fill)
    {
        return sky;
    }

    float2 jitter = Hash22(cell + float2(3.1, 5.9));
    float2 starGrid = cell + jitter * 0.5 + 0.25;
    float starYaw = (starGrid.x / densX - 0.5) * 6.2831853;
    float starPitch = (starGrid.y / densY - 0.5) * 3.14159265;
    float cp = cos(starPitch);
    float3 starDir = float3(sin(starYaw) * cp, sin(starPitch), cos(starYaw) * cp);
    if (starDir.y < 0.04)
    {
        return sky;
    }

    float size = lerp(0.007, 0.014, rnd.y) * sizeScale;
    if (dot(viewDir, starDir) < 1.0 - size)
    {
        return sky;
    }

    float3 upRef = abs(starDir.y) > 0.92 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
    float3 right = normalize(cross(upRef, starDir));
    float3 up = cross(starDir, right);
    float2 offset = float2(dot(viewDir, right), dot(viewDir, up)) / max(size, 1e-4);
    offset = float2(offset.x * 1.35 + offset.y * 0.28, offset.y * 0.62);
    float r = length(offset);
    if (r > 1.0)
    {
        return sky;
    }

    float2 uvLocal = offset * 0.5 + 0.5;
    uvLocal = uvLocal * (1.0 - 2.0 * kInset) + kInset;
    float tileId = floor(Hash22(cell + float2(8.4, 1.7)).x * 16.0);
    float tileX = tileId - floor(tileId / kTiles) * kTiles;
    float tileY = floor(tileId / kTiles);
    float2 uv = float2(tileX, tileY) * kTile + uvLocal * kTile;

    half4 s = SAMPLE_TEXTURE2D_LOD(_StarsTex, sampler_StarsTex, uv, 0);
    half cover = s.a * (half)(starIntensity * horizon);
    if (cover < 0.004h)
    {
        return sky;
    }

    float tintPick = Hash22(cell + float2(2.2, 8.8)).x;
    half3 tint = tintPick > 0.5
        ? half3(1.08h, 0.96h, 0.7h)
        : half3(0.74h, 0.86h, 1.14h);
    return lerp(sky, s.rgb * tint, saturate(cover * 0.72h));
}

half3 StampBody(
    Texture2D tex,
    SamplerState samp,
    float3 viewDir,
    float3 bodyDir,
    float size,
    float intensity,
    float visibility,
    float glow,
    float3 discColor,
    float3 glowColor,
    half3 sky,
    half impasto)
{
    if (visibility < 0.001 || intensity < 0.001)
    {
        return sky;
    }

    bodyDir = normalize(bodyDir);
    float facing = dot(viewDir, bodyDir);
    if (facing < 1.0 - size * 2.4)
    {
        return sky;
    }

    float3 upRef = abs(bodyDir.y) > 0.95 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
    float3 right = normalize(cross(upRef, bodyDir));
    float3 up = cross(bodyDir, right);
    float2 uv = float2(dot(viewDir, right), dot(viewDir, up)) / max(size, 1e-4);
    uv = uv * 0.5 + 0.5;
    // Gradients before the disc test. A return on only some pixels of a
    // quad blows up implicit mip derivatives and smears the square border.
    float2 uvDx = ddx(uv);
    float2 uvDy = ddy(uv);
    float r = length(uv - 0.5) * 2.0;
    if (r > 1.35 || any(uv < 0.0) || any(uv > 1.0))
    {
        return sky;
    }

    // Paint ends near r=0.9. Past that, only the square texture border is left.
    half4 c = 0.0;
    if (r <= 0.98)
    {
        // A wide sine shove turns the round stamp into an egg. Keep only a shiver.
        float2 sampleUv = uv + float2(
            sin(uv.y * 17.0 + uv.x * 5.0),
            sin(uv.x * 13.0 - uv.y * 4.0)) * 0.006 * (float)impasto;
        sampleUv = clamp(sampleUv, 0.001, 0.999);
        c = SAMPLE_TEXTURE2D_GRAD(tex, samp, sampleUv, uvDx, uvDy);
    }
    half coverage = smoothstep(0.08h, 0.55h, c.a) * (half)visibility * saturate((half)intensity);
    half3 paint = c.rgb / max(c.a, 1e-3h);
    half wobble = saturate(sin(uv.x * 9.0 + uv.y * 7.0) * 0.5h + 0.5h);
    half3 thick = lerp(half3(1.0h, 0.45h, 0.22h), half3(1.0h, 0.88h, 0.35h), wobble);
    thick = lerp(thick, half3(1.0h, 0.58h, 0.72h), saturate(sin(uv.y * 11.0) * 0.5h + 0.5h) * 0.4h);
    paint = saturate(paint * lerp((half3)discColor, thick, impasto * 0.22h));
    // Rim only. A center-filled glow out to r=1.15 was the soft pancake around the disc.
    half glowBand = smoothstep(0.55h, 0.82h, (half)r) * (1.0h - smoothstep(0.82h, 1.02h, (half)r));
    half glowMask = glowBand * (half)(glow * visibility) * (1.0h - coverage);
    half3 glowTint = lerp(sky, (half3)glowColor, 0.45h);
    half3 stamp = lerp(sky, paint, coverage);
    return lerp(stamp, glowTint, saturate(glowMask * 0.35h));
}

Varyings OilSkyboxNPRVertex(Attributes input)
{
    Varyings output;
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.dir = input.positionOS.xyz;
    return output;
}

half4 OilSkyboxNPRFragment(Varyings input) : SV_TARGET
{
    float3 viewDir = normalize(input.dir);
    float yaw = atan2(viewDir.x, viewDir.z);
    float pitch = asin(clamp(viewDir.y, -1.0, 1.0));
    SkyPeriodLook look = EvaluatePeriodLook(_Period);
    half3 sky = PaintAir(PaintSky(viewDir.y, CanvasStroke(yaw, pitch), look), viewDir, look);
    sky = PaintStars(sky, viewDir, yaw, pitch, look.starIntensity);
    sky = StampBody(
        _SunTex, sampler_SunTex, viewDir, look.sunDir,
        look.sunSize, look.sunIntensity, look.sunVisibility, look.sunGlow,
        look.sunDisc, look.sunGlowColor, sky, 1.0h);
    sky = StampBody(
        _MoonTex, sampler_MoonTex, viewDir, look.moonDir,
        look.moonSize, look.moonIntensity, look.moonVisibility, look.moonGlow,
        look.moonDisc, look.moonGlowColor, sky, 0.0h);
    sky = OilWeaveTint(sky, viewDir.xz * 6.0);
    return half4(saturate(sky), 1.0h);
}

#endif
