using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Port of Boluo <c>BoluoDistanceFog.Soften</c> for Custom SRP ExpSquared fog.
    /// Desaturates orange-wall / cool-blue fogColor5, pulls dawn toward MorningFog,
    /// dusk toward TwilightFog, night toward NightDistanceFog.
    /// </summary>
    public static class OilDistanceFog
    {
        public static readonly Color MorningFog = Hex(0xD1, 0xC7, 0xB8);
        // Deeper mauve — chalk #C4B6C0 milked dusk skyboxes to lavender-white.
        public static readonly Color TwilightFog = Hex(0x7A, 0x68, 0x78);
        public static readonly Color NightDistanceFog = Hex(0x36, 0x36, 0x3C);

        static Color Hex(byte r, byte g, byte b) =>
            new Color(r / 255f, g / 255f, b / 255f, 1f);

        /// <summary>
        /// Soften a raw look-table fog tint.
        /// <paramref name="period"/> is OilSkyboxTime period (0–4).
        /// <paramref name="foggyT"/> is Foggy weather weight 0–1 (OilAtmosphereDriver).
        /// </summary>
        public static Color Soften(Color src, float period, float foggyT = 0f)
        {
            float hours = OilSkyboxTime.PeriodToHours(period);
            float dayPct = hours / 24f;
            float dawn = EvaluateDawnWeight(dayPct);
            float dusk = EvaluateDuskWeight(dayPct);
            float sunY = OilSkyboxTime.SunDirectionAt(period).y;
            float glow = Mathf.Max(dawn, dusk);
            bool night = glow < 0.08f && sunY < 0.04f;

            Color.RGBToHSV(src, out float h, out float s, out float v);
            float orangeWall = Mathf.Clamp01((s - 0.22f) / 0.50f)
                * Mathf.Clamp01((src.r - Mathf.Min(src.g, src.b)) * 2.1f);
            // Keep dusk chroma/value — orange-wall crush + V lift was chalking sunset haze.
            float duskKeep = 1f - dusk * 0.85f;
            s = Mathf.Lerp(s, s * 0.32f, orangeWall * duskKeep);
            v = Mathf.Lerp(v, Mathf.Max(v, 0.58f), orangeWall * 0.55f * duskKeep);

            float coolTint = 0f;
            if (!night)
            {
                coolTint = Mathf.Clamp01((src.b - (src.r + src.g) * 0.5f) * 4.8f);
                s = Mathf.Lerp(s, s * 0.32f, coolTint);
                v = Mathf.Lerp(v, Mathf.Max(v, 0.62f), coolTint * 0.35f);
            }
            else
            {
                s *= 0.12f;
                v = Mathf.Lerp(v, 0.22f, 0.55f);
            }

            Color softened = Color.HSVToRGB(h, Mathf.Clamp01(s), Mathf.Clamp01(v));
            softened.a = src.a;
            // Cool→MorningFog is for dawn/dusk haze walls. At noon (period≈1) keep blue-gray
            // so the skybox dome stays blue instead of milking to beige chalk.
            float midday = Mathf.Clamp01(1f - Mathf.Abs(period - 1f) / 0.85f);
            if (!night && coolTint > 0.01f)
                softened = Color.Lerp(
                    softened, MorningFog, coolTint * 0.82f * (1f - midday * 0.92f));

            // Soft mauve wash only — 0.78× chalk TwilightFog erased dusk indigo/amber.
            if (!night && dusk > 0.01f)
                softened = Color.Lerp(softened, TwilightFog, dusk * 0.34f);
            // Dawn: warm mist toward MorningFog. 0.28 left haze too blue; 0.88 chalked the dome.
            else if (dawn > 0.01f)
                softened = Color.Lerp(softened, MorningFog, dawn * 0.58f);

            if (night)
            {
                float nightT = Mathf.Clamp01(-sunY * 1.6f + 0.35f);
                softened = Color.Lerp(softened, NightDistanceFog, nightT * 0.82f);
            }

            foggyT = Mathf.Clamp01(foggyT);
            if (foggyT > 0.001f)
            {
                float srcKeep = night
                    ? 0.04f
                    : Mathf.Lerp(0.16f, 0.05f, Mathf.Max(dawn, coolTint));
                Color foggyCol = Color.Lerp(src, softened, 1f - srcKeep);
                if (dawn > 0.01f)
                    foggyCol = Color.Lerp(foggyCol, MorningFog, dawn * 0.62f);
                else if (night)
                    foggyCol = Color.Lerp(foggyCol, NightDistanceFog, 0.55f);
                else if (coolTint > 0.01f)
                    foggyCol = Color.Lerp(foggyCol, MorningFog, coolTint * 0.45f);
                return Color.Lerp(softened, foggyCol, foggyT);
            }

            return softened;
        }

        /// <summary>
        /// Dusk band around 18:00 (Evening−45 → Evening+120 min), matching Boluo DuskSunsetGlow.
        /// </summary>
        public static float EvaluateDuskWeight(float dayFraction)
        {
            const float evening = 18f * 60f;
            return PeakBand(dayFraction * 24f * 60f, evening - 45f, evening + 120f, evening);
        }

        /// <summary>
        /// Dawn band around 06:00 (Morning−90 → Morning+120). Always on for fog soften
        /// (Boluo restricts warm sky glow to preset 1; fog still benefits from MorningFog pull).
        /// </summary>
        public static float EvaluateDawnWeight(float dayFraction)
        {
            const float morning = 6f * 60f;
            return PeakBand(dayFraction * 24f * 60f, morning - 90f, morning + 120f, morning);
        }

        static float PeakBand(float minutes, float start, float end, float peak)
        {
            minutes = Mathf.Repeat(minutes, 24f * 60f);
            if (end <= start)
                return 0f;
            if (minutes < start || minutes >= end)
                return 0f;
            if (minutes <= peak)
            {
                float span = Mathf.Max(1e-3f, peak - start);
                return Mathf.SmoothStep(0f, 1f, (minutes - start) / span);
            }

            float outSpan = Mathf.Max(1e-3f, end - peak);
            return Mathf.SmoothStep(1f, 0f, (minutes - peak) / outSpan);
        }
    }
}
