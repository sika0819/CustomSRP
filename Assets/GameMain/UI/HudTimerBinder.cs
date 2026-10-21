using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// HUD 定时图标：idle/active 切换 + 进度环 + dial 旋转。
    /// 对齐旧工程 HudDownTimeBinder（满圈=3600s，跟原生 SyncDownTime 进度）。
    /// </summary>
    public sealed class HudTimerBinder
    {
        // UITK Painter2D：0°=+X；从 -90° 顺时针 = 顶点，对齐 uGUI FillOrigin=Top + Clockwise。
        const float StartAngleDeg = -90f;
        // timer-fill.png 在 108px 上约 3~4px 环宽。
        const float RingThickness = 0.04f;
        static readonly Color RingColor = new Color(0.62f, 0.62f, 0.62f, 1f);

        VisualElement _idle;
        VisualElement _active;
        VisualElement _fill;
        VisualElement _dialSpin;
        float _fillRatio;
        bool _bound;
        bool _drawing;

        public void Bind(VisualElement timerBackground)
        {
            Unbind();
            if (timerBackground == null)
                return;

            _idle = timerBackground.Q("timerIdle");
            _active = timerBackground.Q("timerActive");
            _fill = timerBackground.Q("timerFill");
            _dialSpin = timerBackground.Q("dialSpin");

            if (_dialSpin != null)
            {
                _dialSpin.usageHints = UsageHints.DynamicTransform;
                _dialSpin.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
            }

            if (_fill != null)
            {
                // 径向裁剪由 generateVisualContent 画；清掉整图 background。
                _fill.style.backgroundImage = StyleKeyword.None;
                _fill.generateVisualContent += OnGenerateFill;
                _drawing = true;
            }

            _bound = true;
            Apply(0f);
        }

        public void Unbind()
        {
            if (_drawing && _fill != null)
                _fill.generateVisualContent -= OnGenerateFill;
            _drawing = false;
            _idle = null;
            _active = null;
            _fill = null;
            _dialSpin = null;
            _bound = false;
            _fillRatio = 0f;
        }

        /// <summary>remainingSeconds≤0 → idle；否则 active，按 remaining/3600 画环。</summary>
        public void Apply(int remainingSeconds, float fillRatio)
        {
            if (!_bound)
                return;

            if (remainingSeconds <= 0)
            {
                SetActive(false);
                _fillRatio = 0f;
                if (_dialSpin != null)
                    _dialSpin.style.rotate = new Rotate(Angle.Degrees(0f));
                _fill?.MarkDirtyRepaint();
                return;
            }

            SetActive(true);
            ApplyProgress(Mathf.Clamp01(fillRatio));
        }

        public void Apply(float fillRatio)
        {
            if (!_bound)
                return;
            if (fillRatio <= 0f)
            {
                Apply(0, 0f);
                return;
            }

            SetActive(true);
            ApplyProgress(Mathf.Clamp01(fillRatio));
        }

        void ApplyProgress(float remainingRatio)
        {
            _fillRatio = remainingRatio;
            // 旧工程：elapsedAngle = (1 - remainingRatio) * 360，RectTransform +Z = 逆时针。
            // UITK 正角为顺时针，取负以对齐针尖落在剩余弧终点。
            float elapsed = (1f - remainingRatio) * 360f;
            if (_dialSpin != null)
                _dialSpin.style.rotate = new Rotate(Angle.Degrees(-elapsed));
            _fill?.MarkDirtyRepaint();
        }

        void SetActive(bool running)
        {
            if (_idle != null)
                _idle.style.display = running ? DisplayStyle.None : DisplayStyle.Flex;
            if (_active != null)
                _active.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OnGenerateFill(MeshGenerationContext ctx)
        {
            if (_fillRatio <= 0.0001f)
                return;

            Rect rect = _fill.contentRect;
            float size = Mathf.Min(rect.width, rect.height);
            if (size < 1f)
                return;

            float lineWidth = Mathf.Max(1f, RingThickness * size);
            float radius = size * 0.5f - lineWidth * 0.5f - 1f;
            if (radius < 1f)
                return;

            Vector2 center = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
            float sweep = Mathf.Clamp01(_fillRatio) * 360f;

            Painter2D painter = ctx.painter2D;
            painter.lineCap = LineCap.Butt;
            painter.lineWidth = lineWidth;
            painter.strokeColor = RingColor;
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
