using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Per-preset sky / ambient / light / fog 4-anchor tables, ported from Boluo Cozy
    /// AtmosphereProfiles (Camera_1 / Camera_5 / 3 / 7(1) / 8 / 10) + BoluoLookPalette.
    /// Anchors: 0 dawn@06 · 1 day@12 · 2 dusk@18 · 3 night@00.
    /// </summary>
    public enum OilSkyLookId
    {
        Canonical = 0,
        CoolFocus = 1,
        WarmAfternoon = 2,
        CyanMorning = 3,
        WarmNoon = 4,
        NightCold = 5,
    }

    public static class OilSkyLookTables
    {
        public const int PeriodCount = 4;
        public const int LookCount = 6;

        public struct SkyAnchor
        {
            public Color zenith;
            public Color mid;
            public Color horizon;
            public Color ground;
            public Color sunDisc;
            public Color sunGlow;
            public Color moonDisc;
            public Color moonGlow;
            public float sunSize;
            public float sunIntensity;
            public float sunGlowAmt;
            public float moonSize;
            public float moonIntensity;
            public float moonGlowAmt;
            public float starIntensity;
        }

        /// <summary>Cozy ambientLightZenith / Horizon / Multiplier.</summary>
        public struct AmbientAnchor
        {
            public Color zenith;
            public Color horizon;
            public float mul;
        }

        public struct LightAnchor
        {
            public Color color;
            public float intensity;
            public Color fogColor;
            /// <summary>Cozy fogDensityMultiplier at this anchor.</summary>
            public float fogDensityMul;
        }

        public struct LookTable
        {
            public SkyAnchor[] sky;
            public AmbientAnchor[] ambient;
            public LightAnchor[] light;
        }

        static readonly LookTable[] Tables = BuildAll();

        public static LookTable Get(OilSkyLookId id)
        {
            int i = (int)id;
            if (i < 0 || i >= Tables.Length)
                i = 0;
            return Tables[i];
        }

        public static OilSkyboxTime.SkyLookPreset SkyLookAt(OilSkyLookId id, int periodIndex)
        {
            SkyAnchor a = Get(id).sky[periodIndex & 3];
            return new OilSkyboxTime.SkyLookPreset
            {
                zenith = a.zenith,
                mid = a.mid,
                horizon = a.horizon,
                ground = a.ground,
                sunSize = a.sunSize,
                sunIntensity = a.sunIntensity,
                sunGlow = a.sunGlowAmt,
                moonSize = a.moonSize,
                moonIntensity = a.moonIntensity,
                moonGlow = a.moonGlowAmt,
                starIntensity = a.starIntensity,
            };
        }

        // boluo-main Camera_1 @ 06:13（PresetsScheme_1 清晨起床）。
        public static readonly Color SunriseSkyZenith = Hex(0x01, 0x28, 0xCD);
        public static readonly Color SunriseSkyMid = Hex(0xD4, 0x8C, 0x68); // 暖霞中层（地平金 × 粉云）
        public static readonly Color SunriseSkyHorizon = Hex(0xF6, 0xC0, 0x1F);
        public static readonly Color SunriseSunDisc = Hex(0xFF, 0x8A, 0x5F);
        public static readonly Color SunriseSun = Hex(0xFF, 0xB4, 0x2A);
        public static readonly Color SunriseFog = Hex(0x97, 0x50, 0x28);
        public static readonly Color SunriseCloud = Hex(0xBC, 0x72, 0x73);
        public static readonly Color SunriseAmbZenith = Hex(0x51, 0x86, 0xB0);
        public static readonly Color SunriseAmbHorizon = Hex(0xC0, 0x6D, 0x31);
        /// <summary>Camera_1 ambientLightMultiplier @ 06:13.</summary>
        public const float SunriseAmbientMul = 0.977f;
        /// <summary>Main-light intensity at sunrise (NPR; Cozy sunlight HDR clamped).</summary>
        public const float SunriseLightIntensity = 0.62f;

        /// <summary>
        /// boluo-main 清晨起床带。峰值约 6:13，8:15 前收回。
        /// </summary>
        public static float CanonicalSunriseWeight(OilSkyLookId id, float hours)
        {
            if (id != OilSkyLookId.Canonical)
                return 0f;
            hours = Mathf.Repeat(hours, 24f);
            float rise = Mathf.SmoothStep(5.2f, 6.15f, hours);
            float fall = 1f - Mathf.SmoothStep(7.05f, 8.15f, hours);
            return Mathf.Clamp01(rise * fall);
        }

        public static void EvaluateLight(OilSkyLookId id, float period, out Color color, out float intensity)
        {
            SampleLight(id, period, out LightAnchor a, out LightAnchor b, out float t);
            color = Color.Lerp(a.color, b.color, t);
            intensity = Mathf.Lerp(a.intensity, b.intensity, t);
            float w = CanonicalSunriseWeight(id, OilSkyboxTime.PeriodToHours(period));
            if (w > 0.001f)
            {
                color = Color.Lerp(color, SunriseSun, w);
                intensity = Mathf.Lerp(intensity, SunriseLightIntensity, w);
            }
        }

        /// <summary>
        /// Raw + softened fog tint (BoluoDistanceFog). Night clock (21–06) snaps to night fog.
        /// </summary>
        public static void EvaluateFog(OilSkyLookId id, float period, out Color fogColor, float foggyT = 0f)
        {
            float hours = OilSkyboxTime.PeriodToHours(period);
            Color raw;
            if (hours >= 21f || hours < 6f)
                raw = Get(id).light[3].fogColor;
            else
            {
                SampleLight(id, period, out LightAnchor a, out LightAnchor b, out float t);
                raw = Color.Lerp(a.fogColor, b.fogColor, t);
            }

            fogColor = OilDistanceFog.Soften(raw, period, foggyT);
            float sunrise = CanonicalSunriseWeight(id, hours);
            if (sunrise > 0.001f)
                fogColor = Color.Lerp(fogColor, SunriseFog, sunrise);
        }

        /// <summary>
        /// Fog density scale relative to that look's noon fogDensityMul (noon → 1).
        /// Dawn/dusk thicker, night varies per profile.
        /// </summary>
        public static float EvaluateFogDensityScale(OilSkyLookId id, float period)
        {
            SampleLight(id, period, out LightAnchor a, out LightAnchor b, out float t);
            float mul = Mathf.Lerp(a.fogDensityMul, b.fogDensityMul, t);
            float noon = Mathf.Max(0.05f, Get(id).light[1].fogDensityMul);
            return Mathf.Clamp(mul / noon, 0.35f, 3.5f);
        }

        /// <summary>
        /// Cozy Trilight channels: zenith×mul / horizon×horizonScale×mul / horizon×gray×….
        /// oilFill = equator×0.62 + sky×0.38 for NPR _OilPeriodAmbient.
        /// </summary>
        public static void EvaluateAmbient(
            OilSkyLookId id,
            float period,
            float cumulus,
            out Color sky,
            out Color equator,
            out Color ground,
            out Color oilFill)
        {
            SampleAmbient(id, period, out AmbientAnchor a, out AmbientAnchor b, out float t);
            Color zenith = Color.Lerp(a.zenith, b.zenith, t);
            Color horizon = Color.Lerp(a.horizon, b.horizon, t);
            float mul = Mathf.Lerp(a.mul, b.mul, t);
            float horizonScale = 1f - Mathf.Clamp01(cumulus) * 0.5f;

            sky = zenith * mul;
            equator = horizon * horizonScale * mul;
            ground = horizon * new Color(0.5f, 0.5f, 0.5f, 1f) * horizonScale * mul;
            float sunrise = CanonicalSunriseWeight(id, OilSkyboxTime.PeriodToHours(period));
            if (sunrise > 0.001f)
            {
                float ambMul = Mathf.Lerp(mul, SunriseAmbientMul, sunrise);
                sky = Color.Lerp(sky, SunriseAmbZenith * ambMul, sunrise);
                equator = Color.Lerp(equator, SunriseAmbHorizon * horizonScale * ambMul, sunrise);
                ground = Color.Lerp(
                    ground,
                    SunriseAmbHorizon * new Color(0.5f, 0.5f, 0.5f, 1f) * horizonScale * ambMul,
                    sunrise);
            }
            oilFill = new Color(
                equator.r * 0.62f + sky.r * 0.38f,
                equator.g * 0.62f + sky.g * 0.38f,
                equator.b * 0.62f + sky.b * 0.38f,
                1f);
        }

        static void SampleLight(OilSkyLookId id, float period, out LightAnchor a, out LightAnchor b, out float t)
        {
            float wrapped = Mathf.Repeat(period, 4f);
            int i0 = Mathf.FloorToInt(wrapped) % 4;
            int i1 = (i0 + 1) % 4;
            t = wrapped - Mathf.Floor(wrapped);
            LookTable table = Get(id);
            a = table.light[i0];
            b = table.light[i1];
        }

        static void SampleAmbient(OilSkyLookId id, float period, out AmbientAnchor a, out AmbientAnchor b, out float t)
        {
            float wrapped = Mathf.Repeat(period, 4f);
            int i0 = Mathf.FloorToInt(wrapped) % 4;
            int i1 = (i0 + 1) % 4;
            t = wrapped - Mathf.Floor(wrapped);
            LookTable table = Get(id);
            a = table.ambient[i0];
            b = table.ambient[i1];
        }

        static Color Hex(byte r, byte g, byte b) =>
            new Color(r / 255f, g / 255f, b / 255f, 1f);

        static Color MidOf(Color zenith, Color horizon) =>
            Color.Lerp(zenith, horizon, 0.42f);

        static LookTable[] BuildAll() => new[]
        {
            BuildCanonical(),
            BuildCoolFocus(),
            BuildWarmAfternoon(),
            BuildCyanMorning(),
            BuildWarmNoon(),
            BuildNightCold(),
        };

        static LookTable BuildCanonical()
        {
            var dawnZ = Hex(0x02, 0x25, 0xBD);
            var dawnH = Hex(0xFF, 0xBC, 0x00);
            var dawnM = Hex(0xE8, 0xC4, 0xB0);
            var dayZ = Hex(0x11, 0x7A, 0xFF);
            var dayH = Hex(0xB8, 0xD1, 0xEB);
            var dayG = Hex(0xD6, 0xC4, 0xB3);
            var duskZ = Hex(0x37, 0x42, 0x6E);
            var duskH = Hex(0xE8, 0xC4, 0x8C);
            // zhanweitu4 night — muted slate / teal-navy (not royal #031252).
            var nightZ = Hex(0x38, 0x4A, 0x6B);
            var nightH = Hex(0x11, 0x2A, 0x46);

            return new LookTable
            {
                sky = new[]
                {
                    Sky(dawnZ, dawnM, dawnH, Hex(0x66, 0x5C, 0x52),
                        Hex(0xFF, 0xF8, 0xE8), Hex(0xFF, 0xD2, 0x64),
                        Hex(0xC7, 0xD1, 0xE6), Hex(0x73, 0x85, 0xB3),
                        0.07f, 0.9f, 0.24f, 0.05f, 0.12f, 0.06f, 0f),
                    Sky(dayZ, MidOf(dayZ, dayH), dayH, dayG,
                        Hex(0xFF, 0xFA, 0xD2), Hex(0xFF, 0xFA, 0xD2),
                        Color.black, Color.black,
                        0.055f, 1f, 0.16f, 0.05f, 0f, 0f, 0f),
                    Sky(duskZ, MidOf(duskZ, duskH), duskH, Hex(0x38, 0x29, 0x24),
                        Hex(0xFF, 0xC8, 0x6A), Hex(0xFF, 0xC8, 0x6A),
                        Hex(0xB3, 0xBD, 0xDB), Hex(0x59, 0x66, 0x94),
                        0.08f, 0.75f, 0.28f, 0.055f, 0.45f, 0.14f, 0.12f),
                    Sky(nightZ, MidOf(nightZ, nightH), nightH, Hex(0x08, 0x14, 0x1C),
                        Hex(0x6A, 0x78, 0x92), Hex(0x12, 0x18, 0x22),
                        Hex(0x9A, 0xAC, 0xC4), Hex(0x3A, 0x4E, 0x66),
                        0.05f, 0.08f, 0.02f, 0.07f, 0.85f, 0.22f, 0.58f),
                },
                // Live Camera_1 ambient gradients @ 06/12/18/00.
                ambient = new[]
                {
                    Amb(Hex(0x47, 0x7A, 0xB3), Hex(0xAD, 0x61, 0x3D), 0.96f),
                    Amb(Hex(0x97, 0xB5, 0x7B), Hex(0x7B, 0xB5, 0xA9), 1.00f),
                    Amb(Hex(0x78, 0x8B, 0xCF), Hex(0x6E, 0x48, 0x36), 0.95f),
                    Amb(Hex(0x1E, 0x32, 0x48), Hex(0x0C, 0x1E, 0x28), 0.46f),
                },
                // fogColor5 raw from Camera_1 (Soften → MorningFog / Day haze / NightDistanceFog).
                light = new[]
                {
                    Light(Hex(0xFF, 0xD2, 0x64), 0.47f, Hex(0xA4, 0x9D, 0x94), 1.50f),
                    Light(Hex(0xFF, 0xFA, 0xD2), 1.5f, Hex(0xB8, 0xC2, 0xD1), 0.50f),
                    Light(Hex(0xFF, 0xC8, 0x6A), 0.57f, Hex(0xB3, 0xA7, 0xAE), 1.25f),
                    Light(Hex(0x3A, 0x4E, 0x66), 0.16f, Hex(0x2A, 0x32, 0x3A), 1.05f),
                },
            };
        }

        static LookTable BuildCoolFocus()
        {
            var dawnZ = Hex(0x06, 0x18, 0x29);
            var dawnH = Hex(0x3A, 0x62, 0x73);
            var dayZ = Hex(0x46, 0x6E, 0xC8);
            var dayH = Hex(0x70, 0xAF, 0xD5);
            var duskZ = Hex(0x03, 0x08, 0x23);
            var duskH = Hex(0x1A, 0x2B, 0x61);
            // Align night with zhanweitu4 slate-teal (CoolFocus stays cooler than Canonical).
            var nightZ = Hex(0x2A, 0x3C, 0x52);
            var nightH = Hex(0x0A, 0x22, 0x34);

            return new LookTable
            {
                sky = new[]
                {
                    Sky(dawnZ, MidOf(dawnZ, dawnH), dawnH, Hex(0x2A, 0x38, 0x40),
                        Hex(0xF0, 0xD8, 0xC8), Hex(0x8E, 0x52, 0x44),
                        Hex(0x92, 0xB0, 0xEC), Hex(0x5A, 0x70, 0xA8),
                        0.065f, 0.75f, 0.18f, 0.05f, 0.2f, 0.08f, 0f),
                    Sky(dayZ, MidOf(dayZ, dayH), dayH, Hex(0x9B, 0xB0, 0xB8),
                        Hex(0xFF, 0xFA, 0xBD), Hex(0xFF, 0xFA, 0xBD),
                        Color.black, Color.black,
                        0.055f, 1f, 0.14f, 0.05f, 0f, 0f, 0f),
                    Sky(duskZ, MidOf(duskZ, duskH), duskH, Hex(0x12, 0x18, 0x28),
                        Hex(0xFF, 0x79, 0x58), Hex(0xFF, 0x79, 0x58),
                        Hex(0x92, 0xB0, 0xEC), Hex(0x4A, 0x59, 0x78),
                        0.075f, 0.65f, 0.22f, 0.06f, 0.55f, 0.16f, 0.15f),
                    Sky(nightZ, MidOf(nightZ, nightH), nightH, Hex(0x06, 0x10, 0x16),
                        Hex(0x40, 0x48, 0x58), Hex(0x10, 0x14, 0x1C),
                        Hex(0x8A, 0xA0, 0xBC), Hex(0x3A, 0x4E, 0x66),
                        0.05f, 0.06f, 0.02f, 0.075f, 0.9f, 0.24f, 0.55f),
                },
                ambient = new[]
                {
                    Amb(Hex(0x69, 0xA1, 0xAB), Hex(0x84, 0x58, 0x6F), 0.96f),
                    Amb(Hex(0x6D, 0xA3, 0xAD), Hex(0x7B, 0xAD, 0xB4), 1.00f),
                    Amb(Hex(0x76, 0x9E, 0xBB), Hex(0x83, 0x5D, 0x53), 0.95f),
                    Amb(Hex(0x14, 0x28, 0x32), Hex(0x0A, 0x1C, 0x24), 0.48f),
                },
                light = new[]
                {
                    Light(Hex(0x8E, 0x52, 0x44), 0.38f, Hex(0x3A, 0x62, 0x74), 1.08f),
                    Light(Hex(0xFF, 0xFA, 0xBD), 1.35f, Hex(0x6C, 0xBB, 0xEC), 0.99f),
                    Light(Hex(0xFF, 0x79, 0x58), 0.48f, Hex(0x11, 0x1C, 0x40), 0.88f),
                    Light(Hex(0x4A, 0x62, 0x78), 0.18f, Hex(0x0A, 0x19, 0x24), 0.92f),
                },
            };
        }

        static LookTable BuildWarmAfternoon()
        {
            var dawnZ = Hex(0x02, 0x25, 0xBD);
            var dawnH = Hex(0xFC, 0xBE, 0x0B);
            var dayZ = Hex(0x27, 0x49, 0xF7);
            var dayH = Hex(0x00, 0x41, 0xFF);
            var duskZ = Hex(0xB5, 0xAE, 0x75);
            var duskH = Hex(0xFF, 0xEF, 0xAB);
            var nightZ = Hex(0x38, 0x4A, 0x6B);
            var nightH = Hex(0x11, 0x2A, 0x46);

            return new LookTable
            {
                sky = new[]
                {
                    Sky(dawnZ, Hex(0xE8, 0xC4, 0xB0), dawnH, Hex(0x66, 0x5C, 0x52),
                        Hex(0xFF, 0xF8, 0xE8), Hex(0xDE, 0x8D, 0x20),
                        Hex(0xC7, 0xD1, 0xE6), Hex(0x73, 0x85, 0xB3),
                        0.07f, 0.9f, 0.24f, 0.05f, 0.12f, 0.06f, 0f),
                    Sky(dayZ, MidOf(dayZ, dayH), dayH, Hex(0xD6, 0xC4, 0xB3),
                        Hex(0xFF, 0xFF, 0xCE), Hex(0xFF, 0xFF, 0xCE),
                        Color.black, Color.black,
                        0.055f, 1.05f, 0.16f, 0.05f, 0f, 0f, 0f),
                    Sky(duskZ, MidOf(duskZ, duskH), duskH, Hex(0x6A, 0x58, 0x3A),
                        Hex(0xFF, 0xFF, 0xA3), Hex(0xFF, 0xA4, 0x58),
                        Hex(0xB3, 0xBD, 0xDB), Hex(0x59, 0x66, 0x94),
                        0.085f, 0.82f, 0.3f, 0.055f, 0.35f, 0.12f, 0.08f),
                    Sky(nightZ, MidOf(nightZ, nightH), nightH, Hex(0x08, 0x14, 0x1C),
                        Hex(0x6A, 0x78, 0x92), Hex(0x12, 0x18, 0x22),
                        Hex(0x9A, 0xAC, 0xC4), Hex(0x3A, 0x4E, 0x66),
                        0.05f, 0.08f, 0.02f, 0.07f, 0.85f, 0.22f, 0.58f),
                },
                ambient = new[]
                {
                    Amb(Hex(0x47, 0x7A, 0xB3), Hex(0xAD, 0x61, 0x3D), 0.96f),
                    Amb(Hex(0x6B, 0xA2, 0xAC), Hex(0x7B, 0xB5, 0xB3), 1.00f),
                    // 17:00 warm afternoon — horizon pulls orange like Atmosphere 3 @17.
                    Amb(Hex(0x6D, 0xA3, 0xAD), Hex(0xB5, 0x5E, 0x24), 0.95f),
                    Amb(Hex(0x1E, 0x32, 0x48), Hex(0x0C, 0x1E, 0x28), 0.46f),
                },
                light = new[]
                {
                    Light(Hex(0xDE, 0x8D, 0x20), 0.47f, Hex(0x8D, 0x67, 0x5A), 1.37f),
                    Light(Hex(0xFF, 0xFF, 0xCE), 1.45f, Hex(0x6C, 0x99, 0xDB), 0.51f),
                    // Gold haze at 17:00 (Atmosphere 3 fogColor5 sample), not 18:00 slate.
                    Light(Hex(0xFF, 0xFF, 0xA3), 0.72f, Hex(0xC4, 0xB1, 0x66), 1.13f),
                    Light(Hex(0x3A, 0x4E, 0x66), 0.16f, Hex(0x2A, 0x32, 0x3A), 0.50f),
                },
            };
        }

        static LookTable BuildCyanMorning()
        {
            var dawnZ = Hex(0x00, 0xA7, 0xFF);
            var dawnH = Hex(0xAF, 0xCB, 0xEC);
            var dayZ = Hex(0x1C, 0x74, 0xFF);
            var dayH = Hex(0x00, 0x41, 0xFF);
            var duskZ = Hex(0x37, 0x42, 0x6E);
            var duskH = Hex(0xE8, 0xC4, 0x8C);
            var nightZ = Hex(0x38, 0x4A, 0x6B);
            var nightH = Hex(0x11, 0x2A, 0x46);

            return new LookTable
            {
                sky = new[]
                {
                    Sky(dawnZ, MidOf(dawnZ, dawnH), dawnH, Hex(0x5A, 0x6E, 0x78),
                        Hex(0xFA, 0xF5, 0xC8), Hex(0xDE, 0x8D, 0x20),
                        Hex(0xC7, 0xD1, 0xE6), Hex(0x73, 0x85, 0xB3),
                        0.07f, 0.95f, 0.22f, 0.05f, 0.1f, 0.05f, 0f),
                    Sky(dayZ, MidOf(dayZ, dayH), dayH, Hex(0xD6, 0xC4, 0xB3),
                        Hex(0xFF, 0xFF, 0xCE), Hex(0xFF, 0xFF, 0xCE),
                        Color.black, Color.black,
                        0.055f, 1.1f, 0.16f, 0.05f, 0f, 0f, 0f),
                    Sky(duskZ, MidOf(duskZ, duskH), duskH, Hex(0x38, 0x29, 0x24),
                        Hex(0xFF, 0xC8, 0x6A), Hex(0xF7, 0x91, 0x45),
                        Hex(0xB3, 0xBD, 0xDB), Hex(0x59, 0x66, 0x94),
                        0.08f, 0.75f, 0.28f, 0.055f, 0.45f, 0.14f, 0.12f),
                    Sky(nightZ, MidOf(nightZ, nightH), nightH, Hex(0x08, 0x14, 0x1C),
                        Hex(0x6A, 0x78, 0x92), Hex(0x12, 0x18, 0x22),
                        Hex(0x9A, 0xAC, 0xC4), Hex(0x3A, 0x4E, 0x66),
                        0.05f, 0.08f, 0.02f, 0.07f, 0.85f, 0.22f, 0.58f),
                },
                ambient = new[]
                {
                    Amb(Hex(0x47, 0x7A, 0xB3), Hex(0xAD, 0x61, 0x3D), 0.96f),
                    Amb(Hex(0x6B, 0xA2, 0xAC), Hex(0x7B, 0xB5, 0xB3), 1.00f),
                    Amb(Hex(0x78, 0x8B, 0xCF), Hex(0x6E, 0x48, 0x36), 0.95f),
                    Amb(Hex(0x1E, 0x32, 0x48), Hex(0x0C, 0x1E, 0x28), 0.46f),
                },
                light = new[]
                {
                    // Soften pulls orange-wall #85482A / cyan #40A0FE toward MorningFog.
                    Light(Hex(0xFA, 0xF5, 0xC8), 0.55f, Hex(0x40, 0xA0, 0xFE), 1.52f),
                    Light(Hex(0xFF, 0xFF, 0xCE), 1.5f, Hex(0x6C, 0x99, 0xDB), 1.13f),
                    Light(Hex(0xF7, 0x91, 0x45), 0.57f, Hex(0x41, 0x38, 0x4A), 1.31f),
                    Light(Hex(0x3A, 0x4E, 0x66), 0.16f, Hex(0x2A, 0x32, 0x3A), 1.05f),
                },
            };
        }

        static LookTable BuildWarmNoon()
        {
            var dawnZ = Hex(0x02, 0x25, 0xBD);
            var dawnH = Hex(0xFC, 0xBE, 0x0B);
            var dayZ = Hex(0x01, 0x4F, 0xFF);
            var dayH = Hex(0x7A, 0xA4, 0xD4);
            var duskZ = Hex(0x37, 0x42, 0x6E);
            var duskH = Hex(0xE8, 0xC4, 0x8C);
            var nightZ = Hex(0x38, 0x4A, 0x6B);
            var nightH = Hex(0x11, 0x2A, 0x46);

            return new LookTable
            {
                sky = new[]
                {
                    Sky(dawnZ, Hex(0xE8, 0xC4, 0xB0), dawnH, Hex(0x66, 0x5C, 0x52),
                        Hex(0xFF, 0xF8, 0xE8), Hex(0xDE, 0x8D, 0x20),
                        Hex(0xC7, 0xD1, 0xE6), Hex(0x73, 0x85, 0xB3),
                        0.07f, 0.9f, 0.24f, 0.05f, 0.12f, 0.06f, 0f),
                    Sky(dayZ, MidOf(dayZ, dayH), dayH, Hex(0xD6, 0xC4, 0xB3),
                        Hex(0xFF, 0xEF, 0xCD), Hex(0xFF, 0xEF, 0xCD),
                        Color.black, Color.black,
                        0.055f, 1.15f, 0.18f, 0.05f, 0f, 0f, 0f),
                    Sky(duskZ, MidOf(duskZ, duskH), duskH, Hex(0x38, 0x29, 0x24),
                        Hex(0xFF, 0xC8, 0x6A), Hex(0xF7, 0x91, 0x45),
                        Hex(0xB3, 0xBD, 0xDB), Hex(0x59, 0x66, 0x94),
                        0.08f, 0.75f, 0.28f, 0.055f, 0.45f, 0.14f, 0.12f),
                    Sky(nightZ, MidOf(nightZ, nightH), nightH, Hex(0x08, 0x14, 0x1C),
                        Hex(0x6A, 0x78, 0x92), Hex(0x12, 0x18, 0x22),
                        Hex(0x9A, 0xAC, 0xC4), Hex(0x3A, 0x4E, 0x66),
                        0.05f, 0.08f, 0.02f, 0.07f, 0.85f, 0.22f, 0.58f),
                },
                ambient = new[]
                {
                    Amb(Hex(0x47, 0x7A, 0xB3), Hex(0xAD, 0x61, 0x3D), 0.96f),
                    // Atmosphere 8 noon — deep blue zenith + warm sand horizon.
                    Amb(Hex(0x00, 0x4F, 0xFF), Hex(0xB0, 0x92, 0x5F), 1.00f),
                    Amb(Hex(0x78, 0x8B, 0xCF), Hex(0x6E, 0x48, 0x36), 0.95f),
                    Amb(Hex(0x1E, 0x32, 0x48), Hex(0x0C, 0x1E, 0x28), 0.46f),
                },
                light = new[]
                {
                    Light(Hex(0xDE, 0x8D, 0x20), 0.47f, Hex(0x85, 0x48, 0x2A), 1.52f),
                    Light(Hex(0xFF, 0xEF, 0xCD), 1.55f, Hex(0x6C, 0x99, 0xDB), 1.13f),
                    Light(Hex(0xF7, 0x91, 0x45), 0.57f, Hex(0x41, 0x38, 0x4A), 1.31f),
                    Light(Hex(0x3A, 0x4E, 0x66), 0.16f, Hex(0x2A, 0x32, 0x3A), 1.05f),
                },
            };
        }

        static LookTable BuildNightCold()
        {
            LookTable canon = BuildCanonical();
            // NightCold: cooler/darker than Canonical, still zhanweitu4 teal-navy (no royal).
            var nightZ = Hex(0x2E, 0x3E, 0x58);
            var nightH = Hex(0x0A, 0x20, 0x38);
            canon.sky[3] = Sky(nightZ, MidOf(nightZ, nightH), nightH, Hex(0x06, 0x10, 0x18),
                Hex(0x40, 0x48, 0x58), Hex(0x10, 0x14, 0x1C),
                Hex(0xA0, 0xB4, 0xCC), Hex(0x3A, 0x52, 0x6E),
                0.05f, 0.06f, 0.02f, 0.075f, 0.95f, 0.26f, 0.62f);
            canon.sky[2] = Sky(Hex(0x1A, 0x2C, 0x42), MidOf(Hex(0x1A, 0x2C, 0x42), Hex(0x0A, 0x20, 0x38)),
                Hex(0x0A, 0x20, 0x38), Hex(0x0C, 0x14, 0x1E),
                Hex(0x80, 0x90, 0xB0), Hex(0x40, 0x50, 0x70),
                Hex(0x4A, 0x62, 0x7E), Hex(0x3A, 0x52, 0x6E),
                0.06f, 0.2f, 0.08f, 0.065f, 0.7f, 0.2f, 0.35f);
            // Atmosphere 10 ambient — desaturated teal night for campfire contrast.
            canon.ambient[0] = Amb(Hex(0x3F, 0x6B, 0x98), Hex(0xAD, 0x61, 0x3D), 0.96f);
            canon.ambient[1] = Amb(Hex(0x6B, 0xA2, 0xAC), Hex(0x7B, 0xB5, 0xB3), 1.00f);
            canon.ambient[2] = Amb(Hex(0x76, 0x87, 0xC7), Hex(0x68, 0x4F, 0x69), 0.95f);
            canon.ambient[3] = Amb(Hex(0x16, 0x2A, 0x3A), Hex(0x0A, 0x1A, 0x26), 0.44f);
            canon.light[2] = Light(Hex(0x4A, 0x62, 0x7E), 0.24f, Hex(0xAE, 0xA3, 0xA6), 1.13f);
            canon.light[3] = Light(Hex(0x3A, 0x4E, 0x66), 0.18f, Hex(0x2A, 0x32, 0x3A), 0.55f);
            return canon;
        }

        static SkyAnchor Sky(
            Color zenith, Color mid, Color horizon, Color ground,
            Color sunDisc, Color sunGlow, Color moonDisc, Color moonGlow,
            float sunSize, float sunIntensity, float sunGlowAmt,
            float moonSize, float moonIntensity, float moonGlowAmt, float starIntensity)
        {
            return new SkyAnchor
            {
                zenith = zenith,
                mid = mid,
                horizon = horizon,
                ground = ground,
                sunDisc = sunDisc,
                sunGlow = sunGlow,
                moonDisc = moonDisc,
                moonGlow = moonGlow,
                sunSize = sunSize,
                sunIntensity = sunIntensity,
                sunGlowAmt = sunGlowAmt,
                moonSize = moonSize,
                moonIntensity = moonIntensity,
                moonGlowAmt = moonGlowAmt,
                starIntensity = starIntensity,
            };
        }

        static AmbientAnchor Amb(Color zenith, Color horizon, float mul) =>
            new AmbientAnchor { zenith = zenith, horizon = horizon, mul = mul };

        static LightAnchor Light(Color color, float intensity, Color fog, float fogDensityMul) =>
            new LightAnchor
            {
                color = color,
                intensity = intensity,
                fogColor = fog,
                fogDensityMul = fogDensityMul,
            };
    }
}
