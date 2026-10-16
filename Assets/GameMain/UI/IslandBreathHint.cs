using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Idle immersive breath glyph: sine scale/alpha, 4s half period / 8s full cycle.
    /// </summary>
    public sealed class IslandBreathHint
    {
        const float BreathMinScale = 0.5f;
        const float BreathMaxScale = 0.9f;
        const float BreathHalfPeriod = 4f;
        const float AlphaMin = 0.08f;
        const float AlphaMax = 1f;
        const float RefSize = 200f;
        const float RefAnchoredY = 642f;
        const float RefH = 2436f;

        VisualElement _el;
        float _phaseRadians;
        bool _shown;

        public bool IsShown => _shown && _el != null && _el.resolvedStyle.display != DisplayStyle.None;

        public void Bind(VisualElement breathHint)
        {
            _el = breathHint;
            if (_el == null)
                return;
            _el.pickingMode = PickingMode.Ignore;
            _el.style.display = DisplayStyle.None;
            ApplyLayout();
        }

        public void ApplyLayout()
        {
            if (_el == null)
                return;

            VisualElement parent = _el.parent;
            float h = parent != null ? parent.resolvedStyle.height : RefH;
            if (h < 1f) h = RefH;

            // Fixed design size; PanelSettings scales physically (same as IslandMainHudLayout).
            float size = RefSize;
            float yFromBottom = RefAnchoredY * (h / RefH);
            _el.style.width = size;
            _el.style.height = size;
            _el.style.left = StyleKeyword.Auto;
            _el.style.right = StyleKeyword.Auto;
            _el.style.bottom = yFromBottom - size * 0.5f;
            _el.style.top = StyleKeyword.Auto;
            _el.style.alignSelf = Align.Center;
            // Absolute bottom-center via left 50% + translate.
            _el.style.position = Position.Absolute;
            _el.style.left = Length.Percent(50);
            _el.style.marginLeft = -size * 0.5f;
            _el.style.bottom = yFromBottom - size * 0.5f;
        }

        public void Show()
        {
            if (_el == null)
                return;
            _el.BringToFront();
            _phaseRadians = 0f;
            ApplyBreath(0f);
            _el.style.display = DisplayStyle.Flex;
            _shown = true;
        }

        public void Hide()
        {
            _shown = false;
            if (_el != null)
                _el.style.display = DisplayStyle.None;
            ResetVisualToBase();
        }

        public void Tick()
        {
            if (!_shown || _el == null)
                return;

            float half = Mathf.Max(0.01f, BreathHalfPeriod);
            _phaseRadians += (Mathf.PI / half) * Time.unscaledDeltaTime;
            if (_phaseRadians > Mathf.PI * 2f)
                _phaseRadians -= Mathf.PI * 2f;

            float t = (Mathf.Sin(_phaseRadians - Mathf.PI * 0.5f) + 1f) * 0.5f;
            ApplyBreath(t);
        }

        void ApplyBreath(float t01)
        {
            if (_el == null)
                return;
            float s = Mathf.Lerp(BreathMinScale, BreathMaxScale, t01);
            _el.style.scale = new Scale(Vector3.one * s);
            float a = Mathf.Lerp(AlphaMin, AlphaMax, t01);
            _el.style.opacity = a;
        }

        void ResetVisualToBase()
        {
            if (_el == null)
                return;
            _el.style.scale = new Scale(Vector3.one);
            _el.style.opacity = 1f;
        }
    }
}
