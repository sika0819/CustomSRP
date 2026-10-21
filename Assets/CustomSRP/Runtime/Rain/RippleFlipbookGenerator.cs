using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Generates stand-in flipbook atlases at runtime so the rain effects work
    /// before any art is supplied. Assign real textures in OilRainSettings to
    /// override these.
    ///
    /// Ripples: RG holds the normal XY of an expanding concentric ring.
    /// Splash:  A channel holds a soft expanding-then-dissolving crown.
    /// </summary>
    public static class RippleFlipbookGenerator
    {
        const int kCell = 256;

        /// <summary>Expanding ring normals. RG = normal.xy, encoded 0..1.</summary>
        public static Texture2D Generate(int columns, int rows, int frames)
        {
            columns = Mathf.Max(1, columns);
            rows = Mathf.Max(1, rows);
            frames = Mathf.Max(1, frames);

            int w = columns * kCell;
            int h = rows * kCell;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "OilRainRippleFlipbook_Generated",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
                anisoLevel = 0,
            };

            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(128, 128, 0, 255);

            for (int f = 0; f < frames; f++)
            {
                float t = (f + 0.5f) / frames;
                float radius = Mathf.Lerp(0.08f, 0.46f, t);
                // Ring thins and fades as it expands.
                float width = Mathf.Lerp(0.20f, 0.07f, t);
                float amp = (1f - t) * (1f - t);

                int cellIndex = f % (columns * rows);
                int cx = cellIndex % columns;
                int cy = cellIndex / columns;

                for (int y = 0; y < kCell; y++)
                {
                    for (int x = 0; x < kCell; x++)
                    {
                        float u = (x + 0.5f) / kCell;
                        float v = (y + 0.5f) / kCell;
                        float dx = u - 0.5f;
                        float dy = v - 0.5f;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);

                        // Signed falloff across the ring gives the normal direction.
                        float ring = 1f - Mathf.Clamp01(Mathf.Abs(d - radius) / width);
                        ring = ring * ring * (3f - 2f * ring); // smoothstep

                        float nx = 0f, ny = 0f;
                        if (d > 1e-4f)
                        {
                            nx = (dx / d) * ring * amp;
                            ny = (dy / d) * ring * amp;
                        }

                        int ax = cx * kCell + x;
                        int ay = cy * kCell + y;
                        px[ay * w + ax] = new Color32(
                            (byte)(Mathf.Clamp01(nx * 0.5f + 0.5f) * 255f),
                            (byte)(Mathf.Clamp01(ny * 0.5f + 0.5f) * 255f),
                            0, 255);
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Splash crown. A channel = coverage; edges are cut hard to limit overdraw.</summary>
        public static Texture2D GenerateSplash(int columns, int rows, int frames)
        {
            columns = Mathf.Max(1, columns);
            rows = Mathf.Max(1, rows);
            frames = Mathf.Max(1, frames);

            int w = columns * kCell;
            int h = rows * kCell;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "OilRainSplashFlipbook_Generated",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
                anisoLevel = 0,
            };

            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);

            for (int f = 0; f < frames; f++)
            {
                float t = (f + 0.5f) / frames;
                // Burst outward fast, then dissolve.
                float radius = Mathf.Lerp(0.10f, 0.44f, Mathf.Sqrt(t));
                float fade = 1f - t;
                float crown = Mathf.Lerp(0.35f, 0.95f, t);

                int cellIndex = f % (columns * rows);
                int cx = cellIndex % columns;
                int cy = cellIndex / columns;

                for (int y = 0; y < kCell; y++)
                {
                    for (int x = 0; x < kCell; x++)
                    {
                        float u = (x + 0.5f) / kCell;
                        float v = (y + 0.5f) / kCell;
                        float dx = u - 0.5f;
                        float dy = v - 0.5f;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);

                        // Annulus: a splash is a ring of water, not a disc.
                        float band = 1f - Mathf.Clamp01(Mathf.Abs(d - radius) / crown);
                        band = band * band * (3f - 2f * band);

                        byte a = (byte)(Mathf.Clamp01(band * fade) * 255f);

                        int ax = cx * kCell + x;
                        int ay = cy * kCell + y;
                        px[ay * w + ax] = new Color32(255, 255, 255, a);
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}