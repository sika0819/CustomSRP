using GameFramework.Event;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class WeatherForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        protected override bool HidesBottomHudIcons => true;

        /// <summary>Boluo InGameTianQiView <c>tianqitanchuang</c> anchoredY (bottom pivot).</summary>
        protected override float SheetBottomOffset => 120f;

        const float KnobAnimSeconds = 0.5f;

        static readonly float[] LevelMarginLeft = { -403.5f, -184.5f, 37.5f, 259.5f };
        static readonly string[] LevelNames = { "关闭", "小", "中", "大" };
        static readonly string[] WeatherButtons = { "fengButton", "yuButton", "wuButton" };
        static readonly string[] LevelHits = { "closeLevel", "levelSmall", "levelMedium", "levelLarge" };
        static readonly IslandWeatherKind[] WeatherKinds =
        {
            IslandWeatherKind.Wind,
            IslandWeatherKind.Rain,
            IslandWeatherKind.Foggy,
        };

        int _editingIndex;
        int _knobLevel;
        bool _dragging;
        bool _animating;
        bool _applying;
        int _dragPointer = -1;
        float _dragStartLocalX;
        float _dragStartMargin;
        float _animFrom;
        float _animTo;

        VisualElement _knob;
        VisualElement _knobHighlight;
        Label _knobLabel;
        VisualElement _track;
        EventCallback<PointerDownEvent> _knobDown;
        EventCallback<PointerMoveEvent> _knobMove;
        EventCallback<PointerUpEvent> _knobUp;
        EventCallback<ClickEvent> _trackClick;
        IVisualElementScheduledItem _knobAnim;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            UnbindKnobPointer();

            _knob = Root.Q("dragHandle");
            _knobHighlight = _knob?.Q("highlight");
            _knobLabel = _knob?.Q<Label>("label");
            if (_knob != null)
            {
                _knob.pickingMode = PickingMode.Position;
                _knob.usageHints = UsageHints.DynamicTransform;
                _knob.BringToFront();
            }

            for (int i = 0; i < LevelHits.Length; i++)
            {
                VisualElement hit = Root.Q(LevelHits[i]);
                if (hit == null)
                    continue;
                hit.style.display = DisplayStyle.Flex;
                HideChrome(hit);
                int level = i;
                OnClick(LevelHits[i], () => CommitLevel(level));
            }

            for (int i = 0; i < WeatherButtons.Length; i++)
            {
                int index = i;
                OnClick(WeatherButtons[i], () => SelectTab(index));
            }

            BindKnobPointer();
            BindTrack();

            if (GameEntry.Event != null)
                GameEntry.Event.Subscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);

            AlignFromService();
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            if (GameEntry.Event != null)
                GameEntry.Event.Unsubscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);

            UnbindKnobPointer();
            UnbindTrack();
            StopKnobAnim();
            _dragging = false;
            _applying = false;
            base.OnClose(isShutdown, userData);
        }

        void OnWeatherChanged(object sender, GameEventArgs e)
        {
            if (_applying || e is not WeatherChangedEventArgs)
                return;
            AlignFromService();
        }

        void AlignFromService()
        {
            _dragging = false;
            StopKnobAnim();
            WeatherService weather = GameEntry.Weather;
            if (weather == null
                || weather.Kind == IslandWeatherKind.Cloud
                || weather.Kind == IslandWeatherKind.Clear)
            {
                _editingIndex = 0;
                _knobLevel = 0;
            }
            else
            {
                _editingIndex = IndexOf(weather.Kind);
                _knobLevel = Mathf.Clamp(weather.Level, 0, 3);
            }

            RefreshTabs();
            PlaceKnobInstant(_knobLevel);
            ShowKnobLabel();
        }

        void SelectTab(int index)
        {
            if (_dragging)
                return;

            _editingIndex = Mathf.Clamp(index, 0, WeatherKinds.Length - 1);
            StopKnobAnim();
            _knobLevel = DisplayLevelForEditing();
            RefreshTabs();
            PlaceKnobInstant(_knobLevel);
            ShowKnobLabel();
        }

        int DisplayLevelForEditing()
        {
            WeatherService weather = GameEntry.Weather;
            if (weather != null && weather.Kind == WeatherKinds[_editingIndex])
                return Mathf.Clamp(weather.Level, 0, 3);
            return 0;
        }

        void CommitLevel(int level)
        {
            if (!CheckInput())
                return;
            ApplyCommittedLevel(level);
        }

        void SnapToDisplayLevel()
        {
            StopKnobAnim();
            _knobLevel = DisplayLevelForEditing();
            PlaceKnobInstant(_knobLevel);
            ShowKnobLabel();
        }

        void ApplyCommittedLevel(int level)
        {
            _knobLevel = Mathf.Clamp(level, 0, 3);
            HideKnobLabel();
            BeginKnobAnim(_knobLevel);

            WeatherService weather = GameEntry.Weather;
            if (weather == null || _editingIndex < 0 || _editingIndex >= WeatherKinds.Length)
                return;

            _applying = true;
            try
            {
                weather.SetWeather(WeatherKinds[_editingIndex], _knobLevel, animate: true);
                weather.BeginPanelTransitionLock();
            }
            finally
            {
                _applying = false;
            }
        }

        bool CheckInput()
        {
            if (_dragging || _animating)
                return false;

            WeatherService weather = GameEntry.Weather;
            if (weather != null && weather.IsWeatherBusy)
            {
                ShowToast();
                return false;
            }

            return true;
        }

        void RefreshTabs()
        {
            VisualElement tab = Root.Q("tab");
            for (int i = 0; i < WeatherButtons.Length; i++)
            {
                Button button = Root.Q<Button>(WeatherButtons[i]);
                if (button == null)
                    continue;

                bool on = i == _editingIndex;
                button.style.opacity = on ? 1f : 0.3f;
                button.style.scale = new Scale(Vector3.one);
                if (!on || tab == null)
                    continue;

                void PlaceTab()
                {
                    if (button.layout.width <= 1f)
                        return;
                    float center = button.layout.x + button.layout.width * 0.5f;
                    tab.style.left = center - 21f;
                    tab.style.marginLeft = 0;
                }

                PlaceTab();
                button.schedule.Execute(PlaceTab).StartingIn(0);
                button.schedule.Execute(PlaceTab).StartingIn(50);
            }
        }

        void PlaceKnobInstant(int level)
        {
            SetKnobMargin(LevelMarginLeft[Mathf.Clamp(level, 0, 3)]);
        }

        void StopKnobAnim()
        {
            _knobAnim?.Pause();
            _knobAnim = null;
            _animating = false;
        }

        void BeginKnobAnim(int level)
        {
            StopKnobAnim();
            _animFrom = _knob != null ? _knob.resolvedStyle.marginLeft : LevelMarginLeft[level];
            _animTo = LevelMarginLeft[level];
            if (_knob == null || Mathf.Abs(_animFrom - _animTo) <= 0.5f)
            {
                SetKnobMargin(_animTo);
                ShowKnobLabel();
                return;
            }

            _animating = true;
            float start = Time.unscaledTime;
            _knobAnim = _knob.schedule.Execute(() =>
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / KnobAnimSeconds);
                SetKnobMargin(Mathf.Lerp(_animFrom, _animTo, Mathf.SmoothStep(0f, 1f, t)));
                if (t < 1f)
                    return;

                _animating = false;
                SetKnobMargin(_animTo);
                ShowKnobLabel();
                _knobAnim?.Pause();
                _knobAnim = null;
            }).Every(16);
        }

        void SetKnobMargin(float margin)
        {
            if (_knob == null)
                return;
            _knob.style.marginLeft = margin;
            // Stay above the stationary hit at 关闭, under the description.
            VisualElement description = Root != null ? Root.Q("description") : null;
            if (description != null && description.parent == _knob.parent)
                _knob.PlaceBehind(description);
        }

        void ShowKnobLabel()
        {
            if (_knobHighlight != null)
                _knobHighlight.style.display = DisplayStyle.Flex;
            if (_knobLabel == null)
                return;
            _knobLabel.text = LevelNames[Mathf.Clamp(_knobLevel, 0, LevelNames.Length - 1)];
            _knobLabel.style.display = DisplayStyle.Flex;
        }

        void HideKnobLabel()
        {
            if (_knobHighlight != null)
                _knobHighlight.style.display = DisplayStyle.Flex;
            if (_knobLabel != null)
                _knobLabel.style.display = DisplayStyle.None;
        }

        static void HideChrome(VisualElement knob)
        {
            VisualElement highlight = knob.Q("highlight");
            Label label = knob.Q<Label>("label");
            if (highlight != null)
                highlight.style.display = DisplayStyle.None;
            if (label != null)
                label.style.display = DisplayStyle.None;
        }

        void BindKnobPointer()
        {
            if (_knob == null)
                return;

            _knobDown = OnKnobDown;
            _knobMove = OnKnobMove;
            _knobUp = OnKnobUp;
            _knob.RegisterCallback(_knobDown);
            _knob.RegisterCallback(_knobMove);
            _knob.RegisterCallback(_knobUp);
        }

        void UnbindKnobPointer()
        {
            if (_knob == null)
                return;
            if (_dragPointer >= 0 && _knob.HasPointerCapture(_dragPointer))
                _knob.ReleasePointer(_dragPointer);
            if (_knobDown != null)
                _knob.UnregisterCallback(_knobDown);
            if (_knobMove != null)
                _knob.UnregisterCallback(_knobMove);
            if (_knobUp != null)
                _knob.UnregisterCallback(_knobUp);
            _knobDown = null;
            _knobMove = null;
            _knobUp = null;
            _dragPointer = -1;
        }

        void BindTrack()
        {
            _track = Root.Q("levelBackground");
            if (_track == null)
                return;

            _track.pickingMode = PickingMode.Position;
            _trackClick = OnTrackClick;
            _track.RegisterCallback(_trackClick);
        }

        void UnbindTrack()
        {
            if (_track != null && _trackClick != null)
                _track.UnregisterCallback(_trackClick);
            _track = null;
            _trackClick = null;
        }

        void OnTrackClick(ClickEvent evt)
        {
            if (_track == null || _dragging)
                return;

            float localX = _track.WorldToLocal(evt.position).x;
            CommitLevel(LevelFromTrackLocalX(localX));
        }

        int LevelFromTrackLocalX(float localX)
        {
            float trackLeft = _track != null ? _track.resolvedStyle.marginLeft : -433.5f;
            float knobWidth = _knob != null ? _knob.resolvedStyle.width : 144f;
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < LevelMarginLeft.Length; i++)
            {
                float center = LevelMarginLeft[i] + knobWidth * 0.5f - trackLeft;
                float dist = Mathf.Abs(localX - center);
                if (dist >= bestDist)
                    continue;
                bestDist = dist;
                best = i;
            }

            return best;
        }

        void OnKnobDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || _knob == null)
                return;
            if (!CheckInput())
                return;

            VisualElement space = _knob.parent ?? _knob;
            _dragging = true;
            _dragPointer = evt.pointerId;
            _dragStartLocalX = space.WorldToLocal(evt.position).x;
            _dragStartMargin = _knob.resolvedStyle.marginLeft;
            HideKnobLabel();
            _knob.CapturePointer(evt.pointerId);
        }

        void OnKnobMove(PointerMoveEvent evt)
        {
            if (!_dragging || evt.pointerId != _dragPointer || _knob == null)
                return;

            VisualElement space = _knob.parent ?? _knob;
            float x = space.WorldToLocal(evt.position).x;
            float margin = _dragStartMargin + (x - _dragStartLocalX);
            margin = Mathf.Clamp(margin, LevelMarginLeft[0], LevelMarginLeft[LevelMarginLeft.Length - 1]);
            SetKnobMargin(margin);
        }

        void OnKnobUp(PointerUpEvent evt)
        {
            if (!_dragging || evt.pointerId != _dragPointer || _knob == null)
                return;

            if (_knob.HasPointerCapture(evt.pointerId))
                _knob.ReleasePointer(evt.pointerId);
            _dragging = false;
            _dragPointer = -1;
            int level = NearestLevel(_knob.resolvedStyle.marginLeft);
            if (!CheckInput())
            {
                SnapToDisplayLevel();
                return;
            }

            ApplyCommittedLevel(level);
        }

        static int NearestLevel(float margin)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < LevelMarginLeft.Length; i++)
            {
                float dist = Mathf.Abs(margin - LevelMarginLeft[i]);
                if (dist >= bestDist)
                    continue;
                bestDist = dist;
                best = i;
            }

            return best;
        }

        static int IndexOf(IslandWeatherKind kind)
        {
            for (int i = 0; i < WeatherKinds.Length; i++)
            {
                if (WeatherKinds[i] == kind)
                    return i;
            }

            return 0;
        }

        void ShowToast()
        {
            UiTipToast.ShowMessage("天气正在改变,稍后再操作", anchoredY: 0f);
        }
    }
}
