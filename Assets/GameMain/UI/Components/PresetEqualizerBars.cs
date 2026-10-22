using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Mesh equalizer for preset Playing chrome — MarkDirtyRepaint only, no layout style writes.
    /// Layout mirrors Boluo state_Playing (78×78, three bars, baseline y=59).
    /// </summary>
    public sealed class PresetEqualizerBars : VisualElement
    {
        static readonly Color SideColor = Color.white;
        static readonly Color CenterColor = new Color(166f / 255f, 1f, 0f, 1f);

        const float HostSize = 78f;
        const float BarWidth = 10f;
        const float BarLeftX = 16f;
        const float BarCenterX = 34f;
        const float BarRightX = 52f;
        const float Baseline = 59f;
        const float LeftBaseH = 20f;
        const float CenterBaseH = 45f;
        const float RightBaseH = 30f;
        const float LeftPhase = 0f;
        const float CenterPhase = 2.094395f;
        const float RightPhase = 4.18879f;
        const float LeftMin = 0.28f;
        const float LeftMax = 1f;
        const float CenterMin = 0.16f;
        const float CenterMax = 0.95f;
        const float RightMin = 0.24f;
        const float RightMax = 1f;

        float _phaseTime;

        public PresetEqualizerBars()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
            style.width = HostSize;
            style.height = HostSize;
            style.position = Position.Absolute;
            style.left = 0f;
            style.top = 0f;
        }

        /// <summary>Advance wave phase (radians) and repaint the bars only.</summary>
        public void Tick(float phaseTime)
        {
            _phaseTime = phaseTime;
            MarkDirtyRepaint();
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            Painter2D p = ctx.painter2D;
            DrawBar(p, BarLeftX, LeftBaseH, LeftPhase, LeftMin, LeftMax, SideColor);
            DrawBar(p, BarCenterX, CenterBaseH, CenterPhase, CenterMin, CenterMax, CenterColor);
            DrawBar(p, BarRightX, RightBaseH, RightPhase, RightMin, RightMax, SideColor);
        }

        void DrawBar(
            Painter2D p,
            float x,
            float baseHeight,
            float phase,
            float minScale,
            float maxScale,
            Color color)
        {
            float wave = (Mathf.Sin(_phaseTime + phase) + 1f) * 0.5f;
            float h = baseHeight * Mathf.Lerp(minScale, maxScale, wave);
            if (h < 1f)
                return;

            float y = Baseline - h;
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + BarWidth, y));
            p.LineTo(new Vector2(x + BarWidth, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }
    }
}
