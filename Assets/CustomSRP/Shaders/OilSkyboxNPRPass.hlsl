#ifndef CUSTOM_OIL_SKYBOX_NPR_PASS_INCLUDED
#define CUSTOM_OIL_SKYBOX_NPR_PASS_INCLUDED

#include "OilPaintNoise.hlsl"

#ifndef PI
#define PI 3.141592653589793238
#endif

// OilCanvas ridges, then sun and moon discs.
// Period colors come from C# OilSkyLookTables via global arrays (fallback: Camera_1).

float4 _OilSkyZenith[4];
float4 _OilSkyMid[4];
float4 _OilSkyHorizon[4];
float4 _OilSkyGround[4];
float4 _OilSkySunDisc[4];
float4 _OilSkySunGlow[4];
float4 _OilSkyMoonDisc[4];
float4 _OilSkyMoonGlow[4];
float4 _OilSkySunParams[4];  // size, intensity, glow, unused
float4 _OilSkyMoonParams[4]; // size, intensity, glow, unused
float _OilSkyStarIntensity[4];
float _OilSkyLookReady;
// Canonical sunrise weight (0–1). Same curve as main light / ambient; not baked into anchor 0.
float _OilSkySunrise;

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

float SoftVisibility(float elevation, float start, float full)
{
    return smoothstep(start, full, elevation);
}

// Boluo Cozy orbit (OilSkyboxTime.SunDirectionFromDayT / MoonDirectionFromDayT).
// parent Euler(0, yaw, pitch) * child Euler(localX,0,0); bodyDir = -forward.
float3 CozyRotateForward(float yawDeg, float pitchDeg, float localXDeg)
{
    float yaw = yawDeg * (PI / 180.0);
    float pitch = pitchDeg * (PI / 180.0);
    float lx = localXDeg * (PI / 180.0);
    // child: rotate +Z around X by localX
    float3 localFwd = float3(0.0, -sin(lx), cos(lx));
    // pivot: Z then X? Unity Euler is ZXY. For Euler(0,yaw,pitch) with x=0:
    // apply pitch (Z) then yaw (Y).
    float cp = cos(pitch);
    float sp = sin(pitch);
    float3 afterPitch = float3(
        localFwd.x * cp - localFwd.y * sp,
        localFwd.x * sp + localFwd.y * cp,
        localFwd.z);
    float cy = cos(yaw);
    float sy = sin(yaw);
    float3 fwd = float3(
        afterPitch.x * cy + afterPitch.z * sy,
        afterPitch.y,
        -afterPitch.x * sy + afterPitch.z * cy);
    return -normalize(fwd);
}

float PeriodToHours(float period)
{
    float p = period - floor(period * 0.25) * 4.0;
    return p * 6.0 + 6.0;
}

float3 CozySunDirFromPeriod(float period)
{
    float dayT = frac(PeriodToHours(period) / 24.0);
    float localX = dayT * 360.0 - 90.0;
    return CozyRotateForward(308.0, 0.0, localX);
}

float3 CozyMoonDirFromPeriod(float period)
{
    float dayT = frac(PeriodToHours(period) / 24.0);
    // SatRotationAtDay0 = -0.5/28*360; orbitOffset=-95
    float sat0 = -0.5 / 28.0 * 360.0;
    float localX = 360.0 * dayT + sat0 - 95.0 - 90.0;
    return CozyRotateForward(308.0 + 11.5, 0.0 - 30.0, localX);
}

float3 ResolveSkySunDir(float period)
{
    if (dot(_OilSkySunDir, _OilSkySunDir) > 1e-4)
    {
        return normalize(_OilSkySunDir);
    }

    return CozySunDirFromPeriod(period);
}

float3 ResolveSkyMoonDir(float period)
{
    if (dot(_OilSkyMoonDir, _OilSkyMoonDir) > 1e-4)
    {
        return normalize(_OilSkyMoonDir);
    }

    return CozyMoonDirFromPeriod(period);
}

SkyPeriodLook PeriodLookFallback(int index)
{
    // Camera_1 / BoluoLookPalette hard fallback when C# has not pushed look globals yet.
    SkyPeriodLook p;
    if (index <= 0)
    {
        // Match Canonical dawn: blue mid (#3E5CAA), gold only on horizon.
        p.zenith = half3(0.0078h, 0.1451h, 0.7412h);
        p.mid = half3(0.243h, 0.361h, 0.667h);
        p.horizon = half3(0.878h, 0.627h, 0.157h);
        p.ground = half3(0.40h, 0.36h, 0.26h);
        p.sunDisc = float3(1.0000, 0.9725, 0.9098);
        p.sunGlowColor = float3(1.0000, 0.8235, 0.3922);
        p.sunSize = 0.07;
        p.sunIntensity = 0.9;
        p.sunGlow = 0.24;
        p.moonDisc = float3(0.8784, 0.9098, 0.9490);
        p.moonGlowColor = float3(0.7843, 0.8471, 0.9255);
        p.moonSize = 0.05;
        p.moonIntensity = 0.12;
        p.moonGlow = 0.06;
        p.starIntensity = 0.0;
    }
    else if (index == 1)
    {
        // Canonical noon: slate #4A78B4 / horizon #7E98B4. Intensities unchanged.
        p.zenith = half3(0.2902h, 0.4706h, 0.7059h);
        p.mid = half3(0.3758h, 0.5233h, 0.7059h);
        p.horizon = half3(0.4941h, 0.5961h, 0.7059h);
        p.ground = half3(0.8392h, 0.7686h, 0.7020h);
        p.sunDisc = float3(0.9020, 0.9216, 0.9098);
        p.sunGlowColor = float3(0.9020, 0.9216, 0.9098);
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
        // Match Canonical dusk: #2C3A58 zenith, muted warm horizon #A07858.
        p.zenith = half3(0.1725h, 0.2275h, 0.3451h);
        p.mid = half3(0.3636h, 0.3296h, 0.3451h);
        p.horizon = half3(0.6275h, 0.4706h, 0.3451h);
        p.ground = half3(0.22h, 0.16h, 0.14h);
        p.sunDisc = float3(0.7843, 0.7059, 0.6039);
        p.sunGlowColor = float3(0.7843, 0.7059, 0.6039);
        p.sunSize = 0.08;
        p.sunIntensity = 0.75;
        p.sunGlow = 0.28;
        p.moonDisc = float3(0.88, 0.92, 0.96);
        p.moonGlowColor = float3(0.68, 0.76, 0.88);
        p.moonSize = 0.055;
        p.moonIntensity = 0.45;
        p.moonGlow = 0.22;
        p.starIntensity = 0.15;
    }
    else
    {
        // Displays #2F4071 after PaintSky. Moon disc stays near-white.
        p.zenith = half3(0.03258h, 0.05934h, 0.18565h);
        p.mid = half3(0.03258h, 0.05934h, 0.18565h);
        p.horizon = half3(0.03258h, 0.05934h, 0.18565h);
        p.ground = half3(0.0039h, 0.0157h, 0.0471h);
        p.sunDisc = float3(0.3451, 0.5020, 0.5333);
        p.sunGlowColor = float3(0.0627, 0.1098, 0.1333);
        p.sunSize = 0.05;
        p.sunIntensity = 0.08;
        p.sunGlow = 0.02;
        // Cool-white halo — softer than sun glare.
        p.moonDisc = float3(0.92, 0.94, 0.98);
        p.moonGlowColor = float3(0.6275, 0.7843, 0.8000);
        p.moonSize = 0.07;
        p.moonIntensity = 0.85;
        p.moonGlow = 0.32;
        p.starIntensity = 1.0;
    }

    p.sunDir = float3(0, 1, 0);
    p.moonDir = float3(0, 1, 0);
    p.sunVisibility = 1.0;
    p.moonVisibility = 1.0;
    return p;
}

SkyPeriodLook PeriodLookAt(int index)
{
    index = clamp(index, 0, 3);
    if (_OilSkyLookReady < 0.5)
    {
        return PeriodLookFallback(index);
    }

    SkyPeriodLook p;
    p.zenith = (half3)_OilSkyZenith[index].rgb;
    p.mid = (half3)_OilSkyMid[index].rgb;
    p.horizon = (half3)_OilSkyHorizon[index].rgb;
    p.ground = (half3)_OilSkyGround[index].rgb;
    p.sunDisc = _OilSkySunDisc[index].rgb;
    p.sunGlowColor = _OilSkySunGlow[index].rgb;
    p.moonDisc = _OilSkyMoonDisc[index].rgb;
    p.moonGlowColor = _OilSkyMoonGlow[index].rgb;
    p.sunSize = _OilSkySunParams[index].x;
    p.sunIntensity = _OilSkySunParams[index].y;
    p.sunGlow = _OilSkySunParams[index].z;
    p.moonSize = _OilSkyMoonParams[index].x;
    p.moonIntensity = _OilSkyMoonParams[index].y;
    p.moonGlow = _OilSkyMoonParams[index].z;
    p.starIntensity = _OilSkyStarIntensity[index];
    p.sunDir = float3(0, 1, 0);
    p.moonDir = float3(0, 1, 0);
    p.sunVisibility = 1.0;
    p.moonVisibility = 1.0;
    return p;
}

SkyPeriodLook LerpPeriodLook(SkyPeriodLook a, SkyPeriodLook b, float t)
{
    SkyPeriodLook p;
    p.zenith = lerp(a.zenith, b.zenith, t);
    p.mid = lerp(a.mid, b.mid, t);
    p.horizon = lerp(a.horizon, b.horizon, t);
    p.ground = lerp(a.ground, b.ground, t);
    p.sunDir = a.sunDir;
    p.moonDir = a.moonDir;
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
    p.sunVisibility = 1.0;
    p.moonVisibility = 1.0;
    return p;
}

// OilSkyLookTables sunrise pigments (byte/255). Weight matches CanonicalSunriseWeight.
void ApplyOilSkySunrise(inout SkyPeriodLook p)
{
    float w = saturate(_OilSkySunrise);
    if (w < 0.001)
    {
        return;
    }

    // 0x0128CD / 0x4868B8 / 0xE0A028 — mid stays blue; gold only on horizon.
    p.zenith = lerp(p.zenith, half3(1.0 / 255.0, 40.0 / 255.0, 205.0 / 255.0), w);
    p.mid = lerp(p.mid, half3(72.0 / 255.0, 104.0 / 255.0, 184.0 / 255.0), w);
    p.horizon = lerp(p.horizon, half3(224.0 / 255.0, 160.0 / 255.0, 40.0 / 255.0), w);
    p.sunDisc = lerp(p.sunDisc, float3(255.0 / 255.0, 138.0 / 255.0, 95.0 / 255.0), w);
    p.sunGlowColor = lerp(p.sunGlowColor, float3(240.0 / 255.0, 168.0 / 255.0, 58.0 / 255.0), w);
    p.sunIntensity = lerp(p.sunIntensity, 0.72, w);
    p.sunGlow = lerp(p.sunGlow, 0.16, w);
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

    SkyPeriodLook p = LerpPeriodLook(PeriodLookAt(i0), PeriodLookAt(i1), period - floor(period));
    ApplyOilSkySunrise(p);
    // Disc directions: C# globals (Cozy orbit) or shader fallback.
    p.sunDir = ResolveSkySunDir(period);
    p.moonDir = ResolveSkyMoonDir(period);
    p.sunVisibility = SoftVisibility(p.sunDir.y, -0.05, 0.12);
    p.moonVisibility = SoftVisibility(p.moonDir.y, -0.02, 0.15);
    return p;
}

// Canvas ridges run along +U. U follows yaw so the strokes lie along the horizon,
// V follows elevation so the rows stack upward. Integer turns hide the yaw seam.
// Higher _BrushScale repeats the tile more often.
void CanvasStrokeUV(float yaw, float elev, out float2 uv, out half seam)
{
    float scale = max(_BrushScale, 0.5);
    float turns = max(round(scale * _OilCanvas_ST.x), 1.0);
    uv = float2(
        yaw * (turns / (2.0 * PI)) + _OilCanvas_ST.z,
        elev * (scale * 0.16) * _OilCanvas_ST.y + _OilCanvas_ST.w);
#if defined(OIL_MOBILE_PERF)
    seam = 1.0h;
#else
    float2 f = frac(uv);
    float2 edge = min(f, 1.0 - f);
    seam = (half)smoothstep(0.0, 0.16, min(edge.x, edge.y));
#endif
}

half CanvasStroke(float yaw, float elev, out float2 uv, out half seam)
{
    CanvasStrokeUV(yaw, elev, uv, seam);
#if defined(OIL_MOBILE_PERF)
    // Same one tap. Negative bias undoes the extra mip from renderScale 0.375–0.5.
    half canvas = (half)SAMPLE_TEXTURE2D_BIAS(_OilCanvas, sampler_OilCanvas, uv, -1.25).r;
#else
    half a = (half)SAMPLE_TEXTURE2D(_OilCanvas, sampler_OilCanvas, uv).r;
    half b = (half)SAMPLE_TEXTURE2D(_OilCanvas, sampler_OilCanvas, uv + 0.5).r;
    half canvas = lerp(b, a, seam);
#endif
    half contrast = max((half)_BrushContrast, 0.5h);
    return saturate((canvas - 0.5h) * contrast + 0.5h);
}

// Same UV and seam blend as the height, so the lit flank sits on the painted ridge.
float3 SampleCanvasNormal(float2 uv, half seam)
{
    float relief = saturate(_BrushRelief);
#if defined(OIL_MOBILE_PERF)
    return DecodeNormal(
        SAMPLE_TEXTURE2D_BIAS(_OilCanvasNormal, sampler_OilCanvasNormal, uv, -1.25), relief);
#else
    float3 a = DecodeNormal(SAMPLE_TEXTURE2D(_OilCanvasNormal, sampler_OilCanvasNormal, uv), relief);
    float3 b = DecodeNormal(
        SAMPLE_TEXTURE2D(_OilCanvasNormal, sampler_OilCanvasNormal, uv + 0.5), relief);
    return normalize(lerp(b, a, (float)seam));
#endif
}

// Clock morning 06:00–~08:30 (period near 0). Poster zeros _OilSkySunrise, so peach stain
// stays off — but stroke groove/shadow still need this or they fall back to cool blue-black.
half MorningClockWeight()
{
    float wrapped = _Period - floor(_Period * 0.25) * 4.0;
    return (half)(1.0 - smoothstep(0.40, 0.85, wrapped));
}

// Peach stain over dome pigments. Requires live _OilSkySunrise (Canonical only).
// Poster ColorOverride pins sunrise to 0 so authored zhanweitu colors stay put.
half MorningDomeWeight()
{
    float sunrise = saturate(_OilSkySunrise);
    if (sunrise < 0.001)
    {
        return 0.0h;
    }

    half byClock = MorningClockWeight();
    half bySunrise = (half)smoothstep(0.05, 0.25, sunrise);
    return max(byClock, bySunrise);
}

// Online PaintSky — OilCanvas groove/ridge IS the底纹. Do not strip this.
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
    // Cool groove (0.48, 0.58, 0.78) turns morning poster mauve into blue-black strokes.
    half morningClock = MorningClockWeight();
    half3 grooveMul = lerp(half3(0.48h, 0.58h, 0.78h), half3(0.78h, 0.68h, 0.58h), morningClock);
    half3 groove = col * grooveMul;
    // Absolute +0.035 chalks a dark night dome toward gray. Day luma keeps the full
    // ridge; blue-black night (luma ~0.06) gets almost none.
    half skyLuma = dot(col, half3(0.30h, 0.59h, 0.11h));
    half ridgeLift = 0.035h * smoothstep(0.05h, 0.28h, skyLuma);
    // Full ridge contrast is the 厚涂底纹. Cap HDR with pigment, never flatten relief.
    half3 ridge = min(col * half3(1.22h, 1.10h, 0.96h) + ridgeLift, half3(1.15h, 1.15h, 1.15h));
    half3 painted = lerp(groove, ridge, smoothstep(0.12h, 0.88h, stroke));
    half3 sky = lerp(col, painted, depth);
    // Upper dome was zenith #0128CD. Stain peach over the painted canvas —
    // do not replace groove/ridge (full lerp wiped the impasto).
    // Gated on live sunrise — poster keeps authored dome colors.
    half morning = MorningDomeWeight();
    if (morning > 0.001h)
    {
        half3 peach = half3(0.86h, 0.72h, 0.62h);
        half peachL = max(dot(peach, half3(0.30h, 0.59h, 0.11h)), 1e-3h);
        half paintL = max(dot(sky, half3(0.30h, 0.59h, 0.11h)), 1e-3h);
        // Same OilCanvas value, peach chroma — ridge/groove contrast stays.
        half3 warmed = (peach / peachL) * paintL;
        half above = smoothstep(0.02h, 0.20h, saturate((half)y));
        sky = lerp(sky, lerp(sky, warmed, 0.45h), morning * above);
    }
    return sky;
}

// Directional impasto. Baked OilCanvas normal, lit by the sun (moon at night).
// Only the tilt (xy) shades the dome — a steep Z must not dim the whole sky.
half3 PaintSkyCanvasBump(
    half3 sky, float2 uv, half seam, half strength, float3 viewDir, float3 sunDir, float3 moonDir)
{
    half cover = saturate(strength);
    if (cover < 0.02h || _BrushRelief < 0.02)
    {
        return sky;
    }

    float3 nTS = SampleCanvasNormal(uv, seam);
    float3 N = viewDir;
    float3 T = float3(N.z, 0.0, -N.x);
    float tLen2 = dot(T, T);
    T = tLen2 > 1e-6 ? T * rsqrt(tLen2) : float3(1.0, 0.0, 0.0);
    float3 B = cross(N, T);

    float3 L = sunDir;
    if (dot(L, L) < 1e-4 || L.y < 0.02)
    {
        L = (dot(moonDir, moonDir) > 1e-4 && moonDir.y > 0.02)
            ? moonDir
            : float3(0.22, 0.90, 0.18);
    }

    L = normalize(L);
    float flank = dot(nTS.xy, float2(dot(L, T), dot(L, B)));
    // Slightly shallower than 1.65 / 0.66. Noon flanks stay cool.
    // Cool morning shadow (0.78, 0.84, 1) paints peach strokes blue-black — warm by clock,
    // even when poster zeros _OilSkySunrise (MorningDomeWeight stays 0).
    half morning = max(MorningDomeWeight(), MorningClockWeight());
    // Morning needs a touch more flank punch — peach stain lowers chroma contrast.
    half tshade = saturate(0.5h + (half)flank * lerp(1.35h, 1.55h, morning));
    half3 shadow = lerp(half3(0.78h, 0.84h, 1.00h), half3(0.92h, 0.78h, 0.62h), morning);
    half3 light = half3(1.22h, 1.08h, 0.96h);
    half3 mid = 0.5h * (shadow + light);
    half3 lit = sky * (lerp(shadow, light, tshade) / mid);
    half day = saturate((half)sunDir.y * 3.0h);
    half glint = pow(saturate((half)flank), 1.5h) * lerp(0.12h, 0.16h, morning);
    half3 glintCol = lerp(half3(0.75h, 0.82h, 1.0h), half3(1.0h, 0.84h, 0.52h), day);
    lit += glint * glintCol;
    return lerp(sky, max(lit, 0.0h), cover);
}

// Shared celestial halo: ring from disc rim → 1.85× disc radius (sun & moon).
half3 PaintCelestialHalo(
    half3 sky,
    float3 viewDir,
    float3 bodyDir,
    float bodySize,
    float glow,
    float visibility,
    float3 glowColor,
    half mixAmt,
    half allowHdr)
{
    half amt = (half)glow * (half)visibility;
    if (amt < 0.01h)
    {
        return sky;
    }

    bodyDir = normalize(bodyDir);
    float discR = max(bodySize, 1e-4);
    float haloR = discR * 1.85;
    float ang = acos(clamp(dot(viewDir, bodyDir), -1.0, 1.0));
    float3 upRef = abs(bodyDir.y) > 0.95 ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
    float3 t = normalize(cross(upRef, bodyDir));
    float3 b = cross(bodyDir, t);
    float2 angUv = float2(dot(viewDir, t), dot(viewDir, b));
    float n = (OilFbm(angUv * 9.0 + bodyDir.xz * 2.1) - 0.5) * discR * 0.18;

    half ring = (half)smoothstep(discR * 0.78 + n, discR * 0.98 + n, ang)
        * (1.0h - (half)smoothstep(discR * 1.05 + n, haloR + n, ang));
    half3 tint = (half3)glowColor;
    if (allowHdr > 0.5h)
    {
        // Sun: push halo above 1 so bloom/HDR reads as bright oil glare.
        tint = tint * (1.35h + amt * 0.85h);
        sky = sky + tint * saturate(ring * amt * mixAmt);
    }
    else
    {
        // Moon glow in the look table is a dark teal, so a lerp never reads as light.
        // Additive, cooler and weaker than the sun's HDR ring.
        half3 cool = half3(0.55h, 0.70h, 1.02h);
        sky = sky + cool * saturate(ring * amt * mixAmt);
    }
    return sky;
}

half3 PaintAir(half3 sky, float3 viewDir, SkyPeriodLook look)
{
    // Sun/moon 1.85× halos are applied after fog (PaintCelestialHalo) so they stay visible.
    return sky;
}

float2 Hash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

// Small oil dabs from Stars_Oil 4x4 atlas. Each cell stamps ONE tile, never the whole sheet.
// Density / size: material _StarDensity / _StarSize (authored on OilSkybox.mat).
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
#if defined(OIL_MOBILE_PERF)
    float densX = 18.0 * density;
    float densY = 9.0 * density;
    float fill = saturate(0.28 * sqrt(density));
#else
    float densX = 28.0 * density;
    float densY = 14.0 * density;
    float fill = saturate(0.38 * sqrt(density));
#endif

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
    // Keep a circular stamp in the star's tangent plane (no X stretch / Y squash).
    float2 offset = float2(dot(viewDir, right), dot(viewDir, up)) / max(size, 1e-4);
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
    // Opaque dab only — no alpha lerp, no intensity-as-glass. Soft tex fringe stays out.
    if (s.a < 0.5h)
    {
        return sky;
    }

    // One warm tint. The cool pick was replacing Stars_Oil yellow with blue-white.
    half3 tint = half3(1.04h, 0.97h, 0.84h);
    // starIntensity from C# look table: day=0, dusk fade-in, night=1.
    return lerp(sky, s.rgb * tint, saturate(starIntensity));
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
    half impasto,
    float2 positionSS)
{
    // On/off only. Dawn sun sits at y≈0 → SoftVisibility≈0.29; a 0.5 gate hid it.
    // Partial visibility must NOT scale coverage (that was the glass look).
    if (visibility < 0.001 || intensity < 0.05)
    {
        return sky;
    }

    bodyDir = normalize(bodyDir);
    float facing = dot(viewDir, bodyDir);
    // Cull pad covers disc + 1.85× circular halo (angular, not the texture square).
    float glowReach = size * 3.8;
    if (facing < 1.0 - glowReach)
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
    float2 centered = uv - 0.5;
    float r = length(centered) * 2.0;
    // Circular cull only — never open the UV square past the disc (Sun_Oil
    // touches mid-edges at alpha 255; clamp-to-edge there paints a white box).
    float rimPad = 0.10 * (float)impasto;
    if (r > 1.85 + rimPad)
    {
        return sky;
    }

    float2 radialDir = centered / max(length(centered), 1e-4);
    float ang = atan2(centered.y, centered.x);
    // Multi-frequency angular tooth — stronger toward the rim (sun via impasto).
    float rimAmt = saturate((r - 0.48) / 0.50);
    float angN = OilFbm(float2(ang * 1.65, ang * 2.55 + 4.2));
    float angN2 = OilValueNoise(float2(ang * 5.1 + 1.3, ang * 3.7));
    float rJitter = ((angN - 0.5) * 0.11 + (angN2 - 0.5) * 0.06) * rimAmt * (float)impasto;
    // Radial UV warp: jagged silhouette without stretching the center.
    float2 warpedUv = uv - radialDir * (rJitter * 0.5);
    float rEff = r + rJitter;

    // Sample only inside the circle AND inside the atlas. Never clamp UV from
    // outside onto the opaque mid-edge texels (that was the white square).
    half4 c = 0.0h;
    bool inDisc = rEff <= 1.02;
    bool inAtlas = all(warpedUv > 0.002) && all(warpedUv < 0.998);
    if (inDisc && inAtlas)
    {
#if defined(OIL_MOBILE_PERF)
        c = SAMPLE_TEXTURE2D(tex, samp, warpedUv);
#else
        // Rim shiver + cheap radial blur (3 taps) — soft oil edge, not a geometric disc.
        float shiver = lerp(0.006, 0.018, rimAmt) * (float)impasto;
        float2 sampleUv = warpedUv + float2(
            sin(warpedUv.y * 17.0 + warpedUv.x * 5.0),
            sin(warpedUv.x * 13.0 - warpedUv.y * 4.0)) * shiver;
        if (all(sampleUv > 0.002) && all(sampleUv < 0.998))
        {
            c = SAMPLE_TEXTURE2D_GRAD(tex, samp, sampleUv, uvDx, uvDy);
            // Sun only. The same blur on the moon averages Moon_Oil brush strokes away.
            if (impasto > 0.75h && rimAmt > 0.15)
            {
                float blurAmt = 0.014 * rimAmt;
                float2 uIn = sampleUv - radialDir * blurAmt;
                float2 uOut = sampleUv + radialDir * blurAmt;
                half4 cIn = (all(uIn > 0.002) && all(uIn < 0.998))
                    ? SAMPLE_TEXTURE2D_GRAD(tex, samp, uIn, uvDx, uvDy) : c;
                half4 cOut = (all(uOut > 0.002) && all(uOut < 0.998))
                    ? SAMPLE_TEXTURE2D_GRAD(tex, samp, uOut, uvDx, uvDy) : c;
                c = (c * 0.45h + cIn * 0.30h + cOut * 0.25h);
            }
        }
#endif
    }
    // SoftVisibility / intensity already gated above — never scale body by intensity.
    // Noise + screen dither on coverage: impressionist ragged rim, not a perfect disc.
    half edgeN = (half)OilValueNoise(uv * 28.0 + float2(rEff * 3.1, rEff * 1.7)) - 0.5h;
    half dither = (half)InterleavedGradientNoise(positionSS, 0) - 0.5h;
    half coverage = smoothstep(
        0.28h, 0.68h,
        c.a + edgeN * lerp(0.14h, 0.34h, impasto) + dither * 0.14h * impasto);
    // Hard circular gate — kills square mid-edge alpha (texture disc fills to r≈1).
    coverage *= 1.0h - smoothstep(0.90h, 1.0h, (half)rEff);
    // Sun impasto is 1. Moon is 0.55: graded oil, not the raw high-key stamp.
    bool sunLamp = impasto > 0.75h;
    half3 paint = sunLamp ? c.rgb : OilMoonAlbedo(c.rgb);
    if (sunLamp)
    {
        half wobble = saturate(sin(uv.x * 9.0 + uv.y * 7.0) * 0.5h + 0.5h);
        half3 thick = lerp(half3(1.0h, 0.45h, 0.22h), half3(1.0h, 0.88h, 0.35h), wobble);
        thick = lerp(thick, half3(1.0h, 0.58h, 0.72h), saturate(sin(uv.y * 11.0) * 0.5h + 0.5h) * 0.4h);
        paint = paint * lerp((half3)discColor, thick, impasto * 0.22h);
        half hdr = max((half)intensity, 1.0h) * 1.45h;
        paint = paint * hdr;
    }
    // Circular rim glow to 1.85× (no texture) — PaintCelestialHalo also covers this.
    const half glowOut = 1.85h;
    half glowBand = smoothstep(0.78h, 0.98h, (half)rEff)
        * (1.0h - smoothstep(1.05h, glowOut, (half)rEff));
    glowBand *= (half)lerp(1.0, 0.82 + angN * 0.36, (float)impasto);
    half glowMask = glowBand * (half)glow * (1.0h - coverage);
    half3 glowTint = (half3)glowColor;
    if (sunLamp)
    {
        glowTint = glowTint * (1.25h + (half)intensity * 0.55h);
        half3 stamp = lerp(sky, paint, coverage);
        return stamp + glowTint * saturate(glowMask * 0.55h);
    }
    // Cool rim, lighter than the sun's HDR fringe so the disc edge is not a hard cut.
    half3 stamp = lerp(sky, paint, coverage);
    half3 cool = half3(0.48h, 0.64h, 0.96h);
    return stamp + cool * saturate(glowMask * 0.35h);
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
    float2 canvasUv;
    half canvasSeam;
    half stroke = CanvasStroke(yaw, pitch, canvasUv, canvasSeam);
    // Online path: paint底纹 → weave → fog → then normal-lit 凹凸.
    half3 sky = PaintAir(PaintSky(viewDir.y, stroke, look), viewDir, look);
    sky = OilWeaveTint(sky, viewDir.xz * 6.0);
    sky = (half3)ApplyOilSkyFog((float3)sky, viewDir, stroke);
    sky = PaintSkyCanvasBump(
        sky, canvasUv, canvasSeam, saturate((half)_BrushStrength),
        viewDir, look.sunDir, look.moonDir);
    sky = PaintStars(sky, viewDir, yaw, pitch, look.starIntensity);
    float2 positionSS = input.positionCS.xy;
    sky = StampBody(
        _SunTex, sampler_SunTex, viewDir, look.sunDir,
        look.sunSize, look.sunIntensity, look.sunVisibility, look.sunGlow,
        look.sunDisc, look.sunGlowColor, sky, 1.0h, positionSS);
    // 0.55 keeps a soft rim. Color is Moon_Oil itself — not the sun HDR lamp.
    sky = StampBody(
        _MoonTex, sampler_MoonTex, viewDir, look.moonDir,
        look.moonSize, look.moonIntensity, look.moonVisibility, look.moonGlow,
        look.moonDisc, look.moonGlowColor, sky, 0.55h, positionSS);
    sky = (half3)ApplyOilCelestialFog((float3)sky, viewDir, stroke);
    // Sun HDR ring. Moon uses the same radius at a higher mix because its
    // authored glow is ~0.12 and must stay visible, still under the sun.
    sky = PaintCelestialHalo(
        sky, viewDir, look.sunDir, look.sunSize,
        look.sunGlow, look.sunVisibility, look.sunGlowColor, 0.55h, 1.0h);
    sky = PaintCelestialHalo(
        sky, viewDir, look.moonDir, look.moonSize,
        look.moonGlow, look.moonVisibility, look.moonGlowColor, 1.15h, 0.0h);
    // Allow sun HDR >1 for bloom; clamp only negatives.
    return half4(max(sky, 0.0h), 1.0h);
}

#endif
