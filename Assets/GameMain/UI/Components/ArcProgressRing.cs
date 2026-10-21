using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// UI Toolkit circular download ring — mirrors Boluo ArcProgressBar on InGameYuSheView Fill
    /// (360°, start at top, lime fore / translucent white track).
    /// </summary>
    public class ArcProgressRing : VisualElement
    {
        // Boluo InGameYuSheView Fill / ArcProgressBar serialized defaults.
        static readonly Color DefaultFore = new Color(0.7f, 1f, 0.1f, 1f);
        static readonly Color DefaultTrack = new Color(1f, 1f, 1f, 0.29803923f);
        const float DefaultThickness = 0.138f;
        // UITK: 0° = +X; Clockwise from -90° = top, matching Boluo startAngle=90 + decreasing atan2.
        const float StartAngleDeg = -90f;

        float _fillAmount;
        float _peakFill;
        Color _foreColor = DefaultFore;
        Color _trackColor = DefaultTrack;
        float _thickness = DefaultThickness;

        public ArcProgressRing()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
        }

        /// <summary>Progress 0~1. Never decreases unless <see cref="ResetProgress"/> (Boluo Downloading rule).</summary>
        public float FillAmount
        {
            get => _fillAmount;
            set
            {
                float p = Mathf.Clamp01(value);
                if (p < _peakFill)
                    p = _peakFill;
                if (Mathf.Approximately(p, _fillAmount))
                    return;
                _fillAmount = p;
                _peakFill = p;
                MarkDirtyRepaint();
            }
        }

        public Color ForeColor
        {
            get => _foreColor;
            set
            {
                _foreColor = value;
                MarkDirtyRepaint();
            }
        }

        public Color TrackColor
        {
            get => _trackColor;
            set
            {
                _trackColor = value;
                MarkDirtyRepaint();
            }
        }

        public void ResetProgress()
        {
            _fillAmount = 0f;
            _peakFill = 0f;
            MarkDirtyRepaint();
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            Rect rect = contentRect;
            float size = Mathf.Min(rect.width, rect.height);
            if (size < 1f)
                return;

            float lineWidth = Mathf.Max(1f, _thickness * size);
            float radius = size * 0.5f - lineWidth * 0.5f - 1f;
            if (radius < 1f)
                return;

            Vector2 center = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
            Painter2D painter = ctx.painter2D;
            painter.lineCap = LineCap.Round;
            painter.lineWidth = lineWidth;

            painter.strokeColor = _trackColor;
            painter.BeginPath();
            painter.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            painter.Stroke();

            if (_fillAmount <= 0.0001f)
                return;

            float sweep = Mathf.Clamp01(_fillAmount) * 360f;
            painter.strokeColor = _foreColor;
            painter.BeginPath();
            painter.Arc(
                center,
                radius,
                Angle.Degrees(StartAngleDeg),
                Angle.Degrees(StartAngleDeg + sweep),
                ArcDirection.Clockwise);
            painter.Stroke();
        }
    }
}
