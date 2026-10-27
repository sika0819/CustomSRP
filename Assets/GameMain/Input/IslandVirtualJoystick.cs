using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Self-contained UI Toolkit virtual joystick.
    /// Builds its own hit + paint layers in code (does not rely on UXML glyph nodes).
    /// Tap empty screen → knob; drag past dead zone → ring faces move dir.
    /// Stick: +X right, +Y screen-up (forward).
    /// </summary>
    public sealed class IslandVirtualJoystick
    {
        // Match Boluo GameControl.prefab: fanwei 444, drag 180 (portrait design @ 1125 short edge).
        const float DeadZoneDesign = 14f;
        const float RingDesign = 444f;
        const float KnobDesign = 180f;
        const float RimMarginDesign = 18f;
        const float RingInnerRadiusFactor = 0.878f;
        /// <summary>Portrait design short edge; joystick scales so physical throw stays stable across orientation.</summary>
        const float RefShortEdge = 1125f;

        VisualElement _host;
        VisualElement _hit;
        VisualElement _paint;
        VisualElement _ring;
        VisualElement _knob;

        bool _active;
        Vector2 _origin;
        Vector2 _offset;
        int _pointerId = -1;
        float _ringSize = RingDesign;
        float _knobSize = KnobDesign;
        float _deadZone = DeadZoneDesign;
        float _maxDrag = 48f;

        public bool IsActive => _active;

        /// <summary>Attach under Island Main form root. Returns null if host is null.</summary>
        public static IslandVirtualJoystick Attach(VisualElement formRoot)
        {
            if (formRoot == null)
                return null;
            var joy = new IslandVirtualJoystick();
            joy.Build(formRoot);
            return joy;
        }

        public void Dispose()
        {
            ForceEnd();
            if (_hit != null)
            {
                _hit.UnregisterCallback<PointerDownEvent>(OnDown);
                _hit.UnregisterCallback<PointerMoveEvent>(OnMove);
                _hit.UnregisterCallback<PointerUpEvent>(OnUp);
                _hit.UnregisterCallback<PointerCancelEvent>(OnCancel);
            }

            _paint?.RemoveFromHierarchy();
            _hit?.RemoveFromHierarchy();
            _host = null;
            _hit = null;
            _paint = null;
            _ring = null;
            _knob = null;
        }

        public void ApplyLayoutScale()
        {
            // Short-edge relative (common floating-stick): Match Height landscape enlarges
            // panel short edge → scale up design px so on-screen throw ≈ portrait.
            float shortEdge = ResolvePanelShortEdge();
            float s = shortEdge > 1f ? shortEdge / RefShortEdge : 1f;
            _ringSize = RingDesign * s;
            _knobSize = KnobDesign * s;
            _deadZone = DeadZoneDesign * s;
            float rim = RimMarginDesign * s;
            float ringInnerR = _ringSize * 0.5f * RingInnerRadiusFactor;
            float knobR = _knobSize * 0.5f;
            _maxDrag = Mathf.Max(_deadZone + 1f, ringInnerR - knobR - rim);
            ApplyGlyphSize(_ring, _ringSize);
            ApplyGlyphSize(_knob, _knobSize);
            if (!_active)
                HideGlyphs();
        }

        float ResolvePanelShortEdge()
        {
            VisualElement el = _host != null ? _host : _paint;
            if (el != null)
            {
                float w = el.resolvedStyle.width;
                float h = el.resolvedStyle.height;
                if (w > 1f && h > 1f)
                    return Mathf.Min(w, h);
            }

            // Match Height fallback: panel height = ref 2436, width follows aspect.
            const float RefH = 2436f;
            float sh = Mathf.Max(1f, Screen.height);
            float panelW = Screen.width * RefH / sh;
            return Mathf.Min(panelW, RefH);
        }

        public void TickFrame(bool allowBegin)
        {
            if (!allowBegin && _active)
                ForceEnd();
        }

        public bool TryGetMove(out Vector2 direction, out float speedRatio)
        {
            direction = Vector2.zero;
            speedRatio = 0f;
            if (!_active)
                return false;

            float dis = _offset.magnitude;
            if (dis <= _deadZone)
                return true;

            // Remap past dead zone → 0..1 (full throw = full walk), independent of resolution.
            float span = Mathf.Max(1e-3f, _maxDrag - _deadZone);
            speedRatio = Mathf.Clamp01((dis - _deadZone) / span);

            // Panel +Y is down → game +Y is screen-up.
            direction = new Vector2(_offset.x, -_offset.y) / dis;
            return true;
        }

        public void ForceEnd()
        {
            if (_active && _hit != null && _pointerId >= 0 && _hit.HasPointerCapture(_pointerId))
                _hit.ReleasePointer(_pointerId);
            End();
        }

        public bool OwnsPointer(int fingerId) =>
            _active && _pointerId == fingerId;

        void Build(VisualElement formRoot)
        {
            _host = formRoot;

            // Hide legacy UXML joystick nodes so they cannot fight this control.
            HideLegacy(formRoot, "joystickRoot");
            HideLegacy(formRoot, "joystickVisual");
            HideLegacy(formRoot, "fanwei");
            HideLegacy(formRoot, "knob");

            _hit = new VisualElement { name = "uitkJoystickHit" };
            StretchFull(_hit);
            _hit.pickingMode = PickingMode.Position;
            _hit.style.backgroundColor = Color.clear;

            _paint = new VisualElement { name = "uitkJoystickPaint" };
            StretchFull(_paint);
            _paint.pickingMode = PickingMode.Ignore;
            _paint.style.overflow = Overflow.Visible;

            _ring = MakeCircle("uitkJoystickRing", new Color(1f, 1f, 1f, 0.28f));
            _knob = MakeCircle("uitkJoystickKnob", new Color(1f, 1f, 1f, 0.85f));
            TrySprite(_ring, "UI/Joystick/ring");
            TrySprite(_knob, "UI/Joystick/knob");
            _paint.Add(_ring);
            _paint.Add(_knob);

            // Hit under HUD for picking empty space; paint above everything so glyphs show.
            formRoot.Insert(0, _hit);
            formRoot.Add(_paint);
            _paint.BringToFront();

            _hit.RegisterCallback<PointerDownEvent>(OnDown);
            _hit.RegisterCallback<PointerMoveEvent>(OnMove);
            _hit.RegisterCallback<PointerUpEvent>(OnUp);
            _hit.RegisterCallback<PointerCancelEvent>(OnCancel);

            ApplyLayoutScale();
            HideGlyphs();
            formRoot.schedule.Execute(ApplyLayoutScale).StartingIn(0);
            formRoot.schedule.Execute(ApplyLayoutScale).StartingIn(50);
        }

        static void HideLegacy(VisualElement root, string name)
        {
            VisualElement el = root.Q(name);
            if (el == null)
                return;
            el.pickingMode = PickingMode.Ignore;
            el.style.display = DisplayStyle.None;
        }

        static void StretchFull(VisualElement el)
        {
            el.style.position = Position.Absolute;
            el.style.left = 0;
            el.style.top = 0;
            el.style.right = 0;
            el.style.bottom = 0;
        }

        static VisualElement MakeCircle(string name, Color fill)
        {
            var el = new VisualElement { name = name };
            el.pickingMode = PickingMode.Ignore;
            el.style.position = Position.Absolute;
            el.style.backgroundColor = fill;
            el.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
            el.style.overflow = Overflow.Visible;
            el.style.flexShrink = 0;
            el.style.display = DisplayStyle.None;
            return el;
        }

        static void TrySprite(VisualElement el, string resourcesPath)
        {
            Sprite sprite = Resources.Load<Sprite>(resourcesPath);
            if (sprite != null)
            {
                el.style.backgroundImage = new StyleBackground(sprite);
                el.style.backgroundColor = Color.clear;
                return;
            }

            Texture2D tex = Resources.Load<Texture2D>(resourcesPath);
            if (tex != null)
            {
                el.style.backgroundImage = new StyleBackground(tex);
                el.style.backgroundColor = Color.clear;
            }
        }

        static void ApplyGlyphSize(VisualElement el, float size)
        {
            if (el == null)
                return;
            float px = Mathf.Max(24f, size);
            var len = new Length(px, LengthUnit.Pixel);
            el.style.width = len;
            el.style.height = len;
            el.style.minWidth = len;
            el.style.minHeight = len;
            el.style.maxWidth = len;
            el.style.maxHeight = len;
            float radius = px * 0.5f;
            el.style.borderTopLeftRadius = radius;
            el.style.borderTopRightRadius = radius;
            el.style.borderBottomLeftRadius = radius;
            el.style.borderBottomRightRadius = radius;
        }

        void OnDown(PointerDownEvent evt)
        {
            if (_active || evt.button != 0)
                return;
            if (HudOverlayGate.BlocksLocomotion())
                return;
            if (!ShouldBegin(evt))
                return;

            _pointerId = evt.pointerId;
            _origin = ToLocal(evt.position);
            _offset = Vector2.zero;
            _active = true;
            _hit.CapturePointer(_pointerId);
            _paint.BringToFront();
            ShowAt(_origin, Vector2.zero);
            evt.StopPropagation();
        }

        void OnMove(PointerMoveEvent evt)
        {
            if (!_active || evt.pointerId != _pointerId)
                return;
            Vector2 local = ToLocal(evt.position);
            Vector2 offset = local - _origin;
            float mag = offset.magnitude;
            if (mag > _maxDrag && mag > 1e-4f)
                offset *= _maxDrag / mag;
            _offset = offset;
            ShowAt(_origin, offset);
            evt.StopPropagation();
        }

        void OnUp(PointerUpEvent evt)
        {
            if (!_active || evt.pointerId != _pointerId)
                return;
            if (_hit.HasPointerCapture(_pointerId))
                _hit.ReleasePointer(_pointerId);
            End();
            evt.StopPropagation();
        }

        void OnCancel(PointerCancelEvent evt)
        {
            if (!_active || evt.pointerId != _pointerId)
                return;
            if (_hit.HasPointerCapture(_pointerId))
                _hit.ReleasePointer(_pointerId);
            End();
        }

        bool ShouldBegin(PointerDownEvent evt)
        {
            if (_hit?.panel == null)
                return false;

            VisualElement target = evt.target as VisualElement;
            if (target != null && IsOurs(target))
                return true;

            VisualElement picked = _hit.panel.Pick(evt.position);
            if (picked == null || IsOurs(picked))
                return true;
            return !IsHudControl(picked);
        }

        bool IsOurs(VisualElement el)
        {
            for (VisualElement n = el; n != null; n = n.parent)
            {
                if (n == _hit || n == _paint || n == _ring || n == _knob)
                    return true;
                if (n.name == "uitkJoystickHit" || n.name == "uitkJoystickPaint")
                    return true;
            }
            return false;
        }

        static bool IsHudControl(VisualElement el)
        {
            for (VisualElement n = el; n != null; n = n.parent)
            {
                if (n is Button)
                    return true;
                string name = n.name ?? string.Empty;
                if (name == "hudHeader" || name == "hudIcons" || name == "root"
                    || name == "uitkJoystickHit" || name == "joystickRoot")
                    continue;
                if (n.pickingMode == PickingMode.Ignore)
                    continue;
                if (name.Contains("Background")
                    || name.Contains("Icon")
                    || name == "profile"
                    || name == "presetIcon"
                    || name == "timeBackground2"
                    || name == "timeDial"
                    || name == "defaultState2"
                    || name == "timerBackground"
                    || name == "musicBackground"
                    || name == "weatherBackground"
                    || name == "volumeBackground")
                    return true;
            }
            return false;
        }

        Vector2 ToLocal(Vector2 panelPos)
        {
            return _paint != null ? _paint.WorldToLocal(panelPos) : panelPos;
        }

        void ShowAt(Vector2 origin, Vector2 offset)
        {
            ApplyGlyphSize(_ring, _ringSize);
            ApplyGlyphSize(_knob, _knobSize);

            float mag = offset.magnitude;
            Place(_knob, origin + offset, _knobSize);
            SetShown(_knob, true);

            if (mag > _deadZone)
            {
                Place(_ring, origin, _ringSize);
                float deg = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                _ring.style.rotate = new Rotate(Angle.Degrees(deg - 180f));
                SetShown(_ring, true);
            }
            else
            {
                SetShown(_ring, false);
            }

            _paint.BringToFront();
            _knob.BringToFront();
        }

        static void Place(VisualElement el, Vector2 center, float size)
        {
            el.style.left = center.x - size * 0.5f;
            el.style.top = center.y - size * 0.5f;
        }

        static void SetShown(VisualElement el, bool on)
        {
            el.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            el.style.visibility = on ? Visibility.Visible : Visibility.Hidden;
            el.style.opacity = on ? 1f : 0f;
        }

        void HideGlyphs()
        {
            SetShown(_ring, false);
            SetShown(_knob, false);
        }

        void End()
        {
            _active = false;
            _offset = Vector2.zero;
            _pointerId = -1;
            HideGlyphs();
        }
    }
}
