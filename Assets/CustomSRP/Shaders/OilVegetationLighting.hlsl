#ifndef CUSTOM_OIL_VEGETATION_LIGHTING_INCLUDED
#define CUSTOM_OIL_VEGETATION_LIGHTING_INCLUDED

// Shared lit term for near leaves / far tree cross-cards / terrain grass.
// Formula (Boluo EnvFoliage / EnvGrass): albedo * (N·L * sun * shadow + SH).
//
// Dark-scene unify: when main light is dim, ambient pulls toward zenith SH so
// grass / leaves / billboards share night fill. NightKeep lifts crowns with
// period oilFill + moon chroma (BoluoNightKeepFoliage) so they don't crush black.
//
// Dusk: half-Lambert wrap keeps low sun on up-facing grass; moon cool is gated
// so warm oilFill periods (黄昏) don't turn blades teal/grey.

#ifndef OIL_PERIOD_AMBIENT_DECLARED
#define OIL_PERIOD_AMBIENT_DECLARED
float4 _OilPeriodAmbient;
#endif

half3 OilVegSampleSH(half3 n)
{
    float4 coefficients[7];
    coefficients[0] = unity_SHAr;
    coefficients[1] = unity_SHAg;
    coefficients[2] = unity_SHAb;
    coefficients[3] = unity_SHBr;
    coefficients[4] = unity_SHBg;
    coefficients[5] = unity_SHBb;
    coefficients[6] = unity_SHC;
    return (half3)max(0.0, SampleSH9(coefficients, n));
}

half OilVegMainLightLuma(half3 lightColor)
{
    return saturate(dot(lightColor, half3(0.2126h, 0.7152h, 0.0722h)));
}

half OilVegPeriodLuma()
{
    half3 period = (half3)_OilPeriodAmbient.rgb;
    return dot(period, half3(0.2126h, 0.7152h, 0.0722h));
}

// 1 when oilFill is warm (dusk/dawn horizon) → suppress moon-blue cool on foliage.
half OilVegPeriodWarmth()
{
    half3 period = (half3)_OilPeriodAmbient.rgb;
    return saturate((period.r - period.b) * 3.5h);
}

// 0 day → 1 full night. Uses main-light luma and oilFill (dial already night).
half OilVegetationNightAmount(half lightLum)
{
    half fromLight = saturate(1.0h - lightLum * 4.0h);
    half periodLum = OilVegPeriodLuma();
    half fromPeriod = 0.0h;
    if (periodLum > 0.02h && periodLum < 0.30h)
        fromPeriod = saturate((0.30h - periodLum) / 0.24h);
    return max(fromLight, fromPeriod);
}

half3 OilVegMoonChroma()
{
    // Night foliage moon: cooler than Camera_1 (0.58,0.68,1) so crowns read slight blue.
    half3 moon = half3(0.48h, 0.62h, 1.0h);
    half srcL = max(dot(moon, half3(0.2126h, 0.7152h, 0.0722h)), 1e-3h);
    return moon / srcL;
}

// How hard to pull ambient toward moon blue. Delayed past dusk; killed by warm oilFill.
half OilVegMoonCoolAmount(half unify, half nightAmt)
{
    half late = saturate((unify - 0.42h) / 0.58h);
    half night = saturate((nightAmt - 0.35h) / 0.65h);
    half cool = max(late, night);
    return cool * (1.0h - OilVegPeriodWarmth());
}

// lightLum 0 = night → ambient ≈ zenith + period floor; 1 = day → N-keyed SH.
half3 OilVegetationAmbient(half3 n, half lightLum)
{
    half3 shN = OilVegSampleSH(normalize(n));
    half3 shUp = OilVegSampleSH(half3(0.0h, 1.0h, 0.0h));
    half unify = saturate(1.0h - lightLum * 3.5h);
    half3 amb = lerp(shN, lerp(shN, shUp, 0.72h), unify);

    // SH probe can be near-black at night; light oilFill floor only (avoid night glow).
    half3 period = (half3)_OilPeriodAmbient.rgb;
    half periodLum = OilVegPeriodLuma();
    if (periodLum > 0.02h)
        amb = lerp(amb, max(amb, period), unify * 0.35h);

    // Night only: cool fill toward moon. Dusk warm oilFill keeps amber grass/leaves.
    half moonCool = OilVegMoonCoolAmount(unify, OilVegetationNightAmount(lightLum));
    if (moonCool > 0.01h)
    {
        half ambLum = max(dot(amb, half3(0.2126h, 0.7152h, 0.0722h)), 1e-3h);
        half3 cool = OilVegMoonChroma() * ambLum;
        amb = lerp(amb, cool, moonCool * 0.32h);
    }
    return amb;
}

// Lighting normal for vegetation: in dark, bias toward up so far BB matches grass fill.
half3 OilVegetationLitNormal(half3 n, half lightLum)
{
    half3 up = half3(0.0h, 1.0h, 0.0h);
    half keepDetail = saturate(0.18h + lightLum * 2.6h);
    return normalize(lerp(up, normalize(n), keepDetail));
}

half3 OilVegetationDirect(
    half3 albedo,
    half3 n,
    half3 lightColor,
    half3 L,
    half attenuation,
    half lightLum)
{
    half3 N = OilVegetationLitNormal(n, lightLum);
    half3 ambient = OilVegetationAmbient(N, lightLum);
    // Half-Lambert: dusk low sun still warms up-facing grass (matches terrain NPR wrap).
    half ndotl = saturate(dot(N, L) * 0.5h + 0.5h);
    half att = saturate(attenuation);
    // Match OilNPR day/night umbra: residual key at noon, harder moon cast at night.
    half nightAmt = OilVegetationNightAmount(lightLum);
    half shadeFloor = lerp(0.28h, 0.04h, nightAmt);
    half attPaint = lerp(shadeFloor, 1.0h, smoothstep(0.05h, 0.55h, att));
    half ambScale = lerp(1.0h, lerp(1.0h, 0.58h, nightAmt), 1.0h - att);
    return albedo * (lightColor * ndotl * attPaint + ambient * ambScale);
}

// BoluoNightKeepFoliage: soft night floor only — keep crowns from crushing, not glowing.
half3 OilVegetationNightKeep(half3 lit, half3 albedo, half3 n, half lightLum)
{
    half nightAmt = OilVegetationNightAmount(lightLum);
    if (nightAmt < 0.01h)
        return lit;

    half3 N = OilVegetationLitNormal(n, lightLum);
    half3 fill = OilVegetationAmbient(N, lightLum);
    half3 period = (half3)_OilPeriodAmbient.rgb;
    fill = max(fill, period * 0.45h);

    // Mild luminance floor (period night oilFill ≈ 0.05); old 0.11–0.18 washed foliage.
    half fillLum = max(dot(fill, half3(0.2126h, 0.7152h, 0.0722h)), 1e-3h);
    half wantLum = max(fillLum, lerp(0.035h, 0.055h, nightAmt));
    fill *= wantLum / fillLum;

    // Extra cool only after dusk; warm oilFill periods keep amber NightKeep.
    half moonCool = OilVegMoonCoolAmount(
        saturate(1.0h - lightLum * 3.5h), nightAmt);
    half3 moon = OilVegMoonChroma();
    fill = lerp(fill, moon * wantLum, moonCool * 0.38h);

    half3 env = albedo * (fill + moon * (0.055h * moonCool));
    half3 lifted = max(lit, env);
    return saturate(lerp(lit, lifted, nightAmt * 0.55h));
}

#endif
