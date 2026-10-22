using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Island HUD music-playing equalizer — Painter2D + MarkDirtyRepaint only.
    /// Design space matches HudIcon (108×108, baseline y=74, bars at x 31/49/67).
    /// </summary>
    public sealed class HudEqualizerBars : VisualElement
    {
        static readonly Color SideColor = Color.white;
        static readonly Color CenterColor = new Color(166f / 255f, 1f, 0f, 1f);

        const float DesignSize = 108f;
        const float BarWidth = 10f;
        const float Baseline = 74f;
        const float LeftX = 31f;
        const float CenterX = 49f;
        const float RightX = 67f;
        const float LeftMinH = 10f;
        const float LeftMaxH = 32f;
        const float CenterMinH = 14f;
        const float CenterMaxH = 48f;
        const float RightMinH = 10f;
        const float RightMaxH = 40f;

        float _phaseTime;

        public HudEqualizerBars()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
            style.position = Position.Absolute;
            style.left = 0f;
            style.top = 0f;
            style.right = 0f;
            style.bottom = 0f;
            style.width = Length.Percent(100);
            style.height = Length.Percent(100);
        }

        /// <summary>Advance wave phase (seconds) and repaint the bars only.</summary>
        public void Tick(float phaseTime)
        {
            _phaseTime = phaseTime;
            MarkDirtyRepaint();
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            float w = contentRect.width;
            float s = w > 1f ? w / DesignSize : 1f;
            Painter2D p = ctx.painter2D;
            DrawBar(p, LeftX, LeftMinH, LeftMaxH, _phaseTime * 5.2f + 0.4f, SideColor, s);
            DrawBar(p, CenterX, CenterMinH, CenterMaxH, _phaseTime * 6.7f + 1.8f, CenterColor, s);
            DrawBar(p, RightX, RightMinH, RightMaxH, _phaseTime * 4.4f + 3.1f, SideColor, s);
        }

        static void DrawBar(
            Painter2D p,
            float xDesign,
            float minH,
            float maxH,
            float phase,
            Color color,
            float s)
        {
            float t = 0.5f + 0.5f * Mathf.Sin(phase);
            float h = Mathf.Lerp(minH, maxH, t) * s;
            if (h < 1f)
                return;

            float x = xDesign * s;
            float bw = BarWidth * s;
            float y = Baseline * s - h;
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + bw, y));
            p.LineTo(new Vector2(x + bw, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }
    }
}
