#ifndef CUSTOM_OIL_CELESTIAL_STAMP_INCLUDED
#define CUSTOM_OIL_CELESTIAL_STAMP_INCLUDED

// Water celestial reflection: stamp by camera→mirror-body hit on the water plane
// so low sun/moon sit under the sky disc on finite ocean meshes.

TEXTURE2D(_SunTex);
SAMPLER(sampler_SunTex);
TEXTURE2D(_MoonTex);
SAMPLER(sampler_MoonTex);
TEXTURE2D(_StarsTex);
SAMPLER(sampler_StarsTex);

float _StarDensity;
float _StarSize;
float _OilWaterCelestialOn;
float _OilSkyStarIntensity[4];

float2 OilCelestialHash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

float OilCelestialSoftVisibility(float elevation, float start, float full)
{
    return smoothstep(start, full, elevation);
}

float3 OilCelestialResolveSunDir()
{
    if (dot(_OilSkySunDir, _OilSkySunDir) > 1e-4)
    {
        return normalize(_OilSkySunDir);
    }

    return float3(0.0, 1.0, 0.0);
}

float3 OilCelestialResolveMoonDir()
{
    if (dot(_OilSkyMoonDir, _OilSkyMoonDir) > 1e-4)
    {
        return normalize(_OilSkyMoonDir);
    }

    return float3(0.0, 1.0, 0.0);
}

float OilCelestialStarIntensity(float period)
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
        half s0 = i0 >= 3 ? 1.0h : (i0 == 2 ? 0.15h : 0.0h);
        half s1 = i1 >= 3 ? 1.0h : (i1 == 2 ? 0.15h : 0.0h);
        return (float)lerp(s0, s1, t);
    }

    return lerp(_OilSkyStarIntensity[i0], _OilSkyStarIntensity[i1], (float)t);
}

/// Look direction from camera that hits water under bodyDir (sky direction).
/// Elev clamp keeps the foot on finite ocean when the real body sits on the horizon.
float3 OilWaterMirrorLook(float3 bodyDir)
{
    bodyDir = normalize(bodyDir);
    float elev = max(bodyDir.y, 0.075);
    float2 xz = bodyDir.xz;
    float xzLen = length(xz);
    if (xzLen < 1e-4)
    {
        return float3(0.0, -1.0, 0.0);
    }

    xz /= xzLen;
    // Reconstruct unit vector with clamped elevation (points downward onto water).
    float hy = elev;
    float hx = sqrt(max(1.0 - hy * hy, 0.0));
    return normalize(float3(xz.x * hx, -hy, xz.y * hx));
}

/// Approximate sun/moon foot on the water plane (y ≈ waterY).
bool OilWaterCelestialFoot(float3 bodyDir, float waterY, out float2 footXZ, out float pathLen)
{
    float3 cam = _WorldSpaceCameraPos;
    float3 look = OilWaterMirrorLook(bodyDir);
    if (look.y > -1e-4)
    {
        footXZ = cam.xz;
        pathLen = 0.0;
        return false;
    }

    float tHit = (waterY - cam.y) / look.y;
    if (tHit < 2.0)
    {
        footXZ = cam.xz;
        pathLen = 0.0;
        return false;
    }

    footXZ = cam.xz + look.xz * tHit;
    pathLen = length(cam.xz - footXZ);
    return pathLen > 1.0;
}

// Stamp disc centered on the camera→mirrorLook alignment (under the sky body).
// Wave/stroke warp the UV only — foot stays under the body, silhouette shimmers.
half3 OilWaterStampBodyOnSurface(
    Texture2D tex,
    SamplerState samp,
    float3 positionWS,
    float3 bodyDir,
    float size,
    float intensity,
    float visibility,
    float glow,
    float3 discColor,
    float3 glowColor,
    half3 sky,
    half3 waterTint,
    half glazeAmt,
    half impasto,
    half3 normalWS,
    half stroke,
    half distortAmt)
{
    if (glazeAmt < 0.01h || visibility < 0.001 || intensity < 0.05)
    {
        return sky;
    }

    float3 cam = _WorldSpaceCameraPos;
    float3 toP = positionWS - cam;
    float dist = length(toP);
    if (dist < 0.5)
    {
        return sky;
    }

    toP /= dist;
    float3 mirrorLook = OilWaterMirrorLook(bodyDir);
    // Modest horizon enlarge only — previous 1.85× made the disc a blob.
    float elev = saturate(bodyDir.y);
    size *= lerp(1.18, 1.0, elev);
    float facing = dot(toP, mirrorLook);
    float glowReach = size * 4.0;
    if (facing < 1.0 - glowReach)
    {
        return sky;
    }

    float3 upRef = abs(mirrorLook.y) > 0.95 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
    float3 right = normalize(cross(upRef, mirrorLook));
    float3 up = cross(mirrorLook, right);
    float2 uv = float2(dot(toP, right), dot(toP, up)) / max(size, 1e-4);
    uv = uv * 0.5 + 0.5;

    // Water twist — 横 (across) strong, 纵 (along) faint（相对相机朝向）.
    float warpAmp = (0.045 + (float)distortAmt * 0.55) * lerp(1.15, 0.85, elev);
#if defined(OIL_MOBILE_PERF)
    warpAmp *= 0.75;
#endif
    float2 nFlat = float2(normalWS.x, normalWS.z);
    float3 lookH = float3(toP.x, 0.0, toP.z);
    float lookLen = length(lookH);
    lookH = lookLen > 1e-4 ? lookH / lookLen : float3(0.0, 0.0, 1.0);
    float3 sideH = cross(float3(0.0, 1.0, 0.0), lookH);
    float across = dot(nFlat, sideH.xz);
    float along = dot(nFlat, lookH.xz);
    float2 acrossUV = float2(dot(sideH, right), dot(sideH, up));
    float aLen = length(acrossUV);
    acrossUV = aLen > 1e-4 ? acrossUV / aLen : float2(1.0, 0.0);
    float2 alongUV = float2(-acrossUV.y, acrossUV.x);
    float2 waveWarp = acrossUV * (across * warpAmp)
        + alongUV * (along * warpAmp * 0.22);
    float strokeW = (float)(stroke - 0.5h);
    float2 strokeWarp = acrossUV * (strokeW * warpAmp * 0.65)
        + alongUV * (strokeW * warpAmp * 0.12);
    float shiverA = sin(positionWS.x * 0.55 + positionWS.z * 0.31 + _Time.y * 1.7);
    float shiverL = cos(positionWS.z * 0.48 - positionWS.x * 0.27 + _Time.y * 1.3);
    float2 shiver = acrossUV * (shiverA * warpAmp * 0.40)
        + alongUV * (shiverL * warpAmp * 0.10);
    uv += waveWarp + strokeWarp + shiver;

    float2 centered = uv - 0.5;
    float r = length(centered) * 2.0;
    if (r > 2.2)
    {
        return sky;
    }

    half4 c = 0.0h;
    bool inDisc = r <= 1.25;
    bool inAtlas = all(uv > 0.002) && all(uv < 0.998);
    if (inDisc && inAtlas)
    {
        c = SAMPLE_TEXTURE2D_LOD(tex, samp, uv, 0.0);
    }

    half coverA = smoothstep(0.10h, 0.48h, c.a);
    half coverR = 1.0h - smoothstep(0.50h, 1.20h, (half)r);
    half coverage = coverA * coverR * glazeAmt;
    // Sun punchy; moon = pale cool veil (impasto≈0.55).
    coverage *= impasto > 0.5h ? 0.95h : 0.30h;
    coverage *= lerp(1.25h, 1.05h, (half)elev);
    if (impasto <= 0.5h)
    {
        coverage *= lerp(0.75h, 0.58h, (half)elev);
    }
    // Wave breaks coverage at the rim — reads as water refraction shred.
    half waveBreak = (half)saturate(length(nFlat) * 3.2);
    coverage *= lerp(1.0h, 0.78h, waveBreak * saturate(distortAmt / 0.25h));
    if (coverage < 0.01h)
    {
        return sky;
    }

    half3 paint = c.rgb * (half3)discColor;
    if (impasto > 0.5h)
    {
        paint = paint * lerp(1.25h, 1.85h, saturate((half)intensity));
        paint = lerp(paint, waterTint, 0.04h);
    }
    else
    {
        // 浅 + 冷：抬亮、压暖、偏青银。
        half3 coolMoon = half3(0.70h, 0.84h, 1.0h);
        paint = saturate(paint * 1.05h);
        paint = lerp(paint, coolMoon, 0.55h);
        paint = lerp(paint, waterTint, 0.16h);
        half luma = max(paint.r, max(paint.g, paint.b));
        paint = lerp(paint, paint * (0.92h / max(luma, 1e-3h)), 0.25h); // 略去饱和，更浅
        paint = paint * half3(0.88h, 0.96h, 1.08h);
    }

    half3 stamp = lerp(sky, paint, saturate(coverage));
    // Sun core additive punch only (does not grow radius).
    if (impasto > 0.5h)
    {
        half core = coverR * coverA * glazeAmt;
        stamp = stamp + paint * core * 0.55h;
    }

    const half glowOut = 1.85h;
    half glowBand = smoothstep(0.50h, 0.98h, (half)r)
        * (1.0h - smoothstep(1.05h, glowOut, (half)r));
#if defined(OIL_MOBILE_PERF)
    half glowW = impasto > 0.5h ? 0.38h : 0.08h;
#else
    half glowW = impasto > 0.5h ? 0.48h : 0.11h;
#endif
    half glowMask = glowBand * (half)glow * glazeAmt * glowW;
    half3 glowTint = lerp((half3)glowColor, paint, 0.45h);
    if (impasto <= 0.5h)
    {
        glowTint = lerp(glowTint, half3(0.55h, 0.72h, 0.95h), 0.55h);
    }
    if (impasto > 0.5h)
    {
        stamp = stamp + glowTint * saturate(glowMask);
    }
    else
    {
        stamp = lerp(stamp, glowTint, saturate(glowMask * 0.28h));
    }

    return stamp;
}

half3 OilWaterPaintStars(
    half3 sky,
    float3 rayDir,
    float yaw,
    float pitch,
    float starIntensity,
    half3 waterTint,
    half glazeAmt)
{
    if (starIntensity < 0.001 || glazeAmt < 0.01h)
    {
        return sky;
    }

    float horizon = smoothstep(0.04, 0.28, rayDir.y);
    if (horizon < 0.001)
    {
        return sky;
    }

    float density = max(_StarDensity, 0.05);
    float sizeScale = max(_StarSize, 0.05) * 1.35;
#if defined(OIL_MOBILE_PERF)
    float densX = 18.0 * density;
    float densY = 9.0 * density;
    float fill = saturate(0.22 * sqrt(density));
#else
    float densX = 28.0 * density;
    float densY = 14.0 * density;
    float fill = saturate(0.30 * sqrt(density));
#endif

    const float kTiles = 4.0;
    const float kTile = 0.25;
    const float kInset = 0.08;

    float2 cell = floor(float2(
        (yaw * 0.15915494 + 0.5) * densX,
        (pitch * 0.31830989 + 0.5) * densY));
    float2 rnd = OilCelestialHash22(cell + float2(17.1, 9.7));
    if (rnd.x > fill)
    {
        return sky;
    }

    float2 jitter = OilCelestialHash22(cell + float2(3.1, 5.9));
    float2 starGrid = cell + jitter * 0.5 + 0.25;
    float starYaw = (starGrid.x / densX - 0.5) * 6.2831853;
    float starPitch = (starGrid.y / densY - 0.5) * 3.14159265;
    float cp = cos(starPitch);
    float3 starDir = float3(sin(starYaw) * cp, sin(starPitch), cos(starYaw) * cp);
    if (starDir.y < 0.06)
    {
        return sky;
    }

    float size = lerp(0.010, 0.020, rnd.y) * sizeScale;
    float ang = 1.0 - saturate(dot(rayDir, starDir));
    if (ang > size)
    {
        return sky;
    }

    float3 upRef = abs(starDir.y) > 0.92 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
    float3 right = normalize(cross(upRef, starDir));
    float3 up = cross(starDir, right);
    float2 offset = float2(dot(rayDir, right), dot(rayDir, up)) / max(size, 1e-4);
    float r = length(offset);
    if (r > 1.0)
    {
        return sky;
    }

    float2 uvLocal = offset * 0.5 + 0.5;
    uvLocal = uvLocal * (1.0 - 2.0 * kInset) + kInset;
    float tileId = floor(OilCelestialHash22(cell + float2(8.4, 1.7)).x * 16.0);
    float tileX = tileId - floor(tileId / kTiles) * kTiles;
    float tileY = floor(tileId / kTiles);
    float2 uv = float2(tileX, tileY) * kTile + uvLocal * kTile;

    half4 s = SAMPLE_TEXTURE2D_LOD(_StarsTex, sampler_StarsTex, uv, 0.0);
    half soft = (1.0h - (half)smoothstep(0.35, 1.0, r)) * smoothstep(0.08h, 0.45h, s.a);
    soft *= glazeAmt * (half)horizon * saturate((half)starIntensity) * 0.42h;
    if (soft < 0.01h)
    {
        return sky;
    }

    float tintPick = OilCelestialHash22(cell + float2(2.2, 8.8)).x;
    half3 tint = tintPick > 0.5
        ? half3(1.02h, 0.96h, 0.82h)
        : half3(0.78h, 0.88h, 1.06h);
    half3 spark = lerp(s.rgb * tint, waterTint, 0.35h);
    return sky + spark * soft;
}

half3 OilWaterStampCelestials(
    half3 sky,
    float3 rayDir,
    float3 positionWS,
    half3 sunDisc,
    half3 sunGlow,
    half3 moonDisc,
    half3 moonGlow,
    half sunSize,
    half sunIntensity,
    half sunGlowAmt,
    half moonSize,
    half moonIntensity,
    half moonGlowAmt,
    half3 waterTint,
    half glazeAmt,
    half3 normalWS,
    half stroke,
    half distortAmt)
{
    if (_OilWaterCelestialOn < 0.5 || glazeAmt < 0.01h)
    {
        return sky;
    }

    float3 sunDir = OilCelestialResolveSunDir();
    float3 moonDir = OilCelestialResolveMoonDir();
    // Water: keep discs readable on the horizon (skybox SoftVisibility was too shy).
    float sunVis = OilCelestialSoftVisibility(sunDir.y, -0.18, 0.06);
    float moonVis = OilCelestialSoftVisibility(moonDir.y, -0.12, 0.08);

#if !defined(OIL_MOBILE_PERF)
    float starIntensity = OilCelestialStarIntensity(_OilSkyPeriod);
    float yaw = atan2(rayDir.x, rayDir.z);
    float pitch = asin(clamp(rayDir.y, -1.0, 1.0));
    sky = OilWaterPaintStars(sky, rayDir, yaw, pitch, starIntensity, waterTint, glazeAmt);
#endif

    sky = OilWaterStampBodyOnSurface(
        _SunTex, sampler_SunTex, positionWS, sunDir,
        (float)sunSize, (float)sunIntensity, sunVis, (float)sunGlowAmt,
        (float3)sunDisc, (float3)sunGlow, sky, waterTint, glazeAmt, 1.0h,
        normalWS, stroke, distortAmt);
    sky = OilWaterStampBodyOnSurface(
        _MoonTex, sampler_MoonTex, positionWS, moonDir,
        (float)moonSize, (float)moonIntensity, moonVis, (float)moonGlowAmt,
        (float3)moonDisc, (float3)moonGlow, sky, waterTint, glazeAmt, 0.55h,
        normalWS, stroke, distortAmt);
    return sky;
}

#endif
