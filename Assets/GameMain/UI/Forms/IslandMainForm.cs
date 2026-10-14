using Game.Native;
using GameFramework.Event;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class IslandMainForm : GameFormLogic
    {
        const float TimeToastSeconds = 1.6f;
        const float TimeToastFadeSeconds = 0.35f;

        // Boluo YuSheLable timing (fade in after hold, then fade out).
        const float PresetVeilInSeconds = 0.35f;
        const float PresetVeilHoldSeconds = 0.15f;
        const float PresetVeilOutSeconds = 0.55f;
        // Boluo YuSheLable: fadeIn@2s, fadeOut@5s, each 0.6s.
        const float PresetLabelDelaySeconds = 2f;
        const float PresetLabelFadeSeconds = 0.6f;
        const float PresetLabelHoldSeconds = 2.4f;

        VisualElement _timeHand;
        VisualElement _timeDial;
        VisualElement _timeLabel;
        VisualElement _timeIcon1;
        VisualElement _timeIcon2;
        VisualElement _musicPlaying;
        Label _timeLabelText;
        Texture2D _texLabelMorning;
        Texture2D _texLabelNoon;
        Texture2D _texLabelEvening;
        Texture2D _texLabelNight;
        Texture2D _texIconMorningEvening;
        Texture2D _texIconNoon;
        Texture2D _texIconNight;
        Texture2D _texArrowMorning;
        Texture2D _texArrowEvening;
        Texture2D _texArrowNight;
        IVisualElementScheduledItem _toastHide;
        float _toastAlpha;
        float _toastFadeFrom;
        float _toastFadeTo;
        float _toastFadeDuration;
        float _toastFadeElapsed = -1f;
        bool _toastVisible;
        IslandVirtualJoystick _joystick;
        readonly IslandLocomotionInput _locomotion = new IslandLocomotionInput();
        readonly IslandIdleImmersive _idle = new IslandIdleImmersive();
        readonly IslandMainHudLayout _hudLayout = new IslandMainHudLayout();
        bool _layoutHooked;

        VisualElement _presetVeil;
        Label _presetLabel;
        IVisualElementScheduledItem _presetLabelHide;
        float _veilAlpha;
        float _veilFadeFrom;
        float _veilFadeTo;
        float _veilFadeDuration;
        float _veilFadeElapsed = -1f;
        float _presetLabelAlpha;
        float _presetLabelFadeFrom;
        float _presetLabelFadeTo;
        float _presetLabelFadeDuration;
        float _presetLabelFadeElapsed = -1f;
        bool _presetTransitionBusy;
        int _transitionPresetId;
        int _queuedPresetId;
        float _veilHoldElapsed = -1f;
        enum VeilPhase { None, FadingIn, Holding, FadingOut }
        VeilPhase _veilPhase;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            OnClick("weatherBackground", () => UiAssets.OpenPanel(UiAssets.WeatherForm));
            OnClick("musicBackground", () => UiAssets.OpenPanel(UiAssets.MusicForm));
            OnClick("volumeBackground", () => UiAssets.OpenPanel(UiAssets.VolumeForm));
            OnClick("presetIcon", () => UiAssets.OpenPanel(UiAssets.PresetForm));
            // Hybrid HUD：原生个人中心 / 定时 / 健康；Editor 走 NativeEditorHud 模拟。
            OnClick("profile", () => NativeBridgeFacade.HudProfile());
            OnClick("timerBackground", () => NativeBridgeFacade.HudTimer());
            OnClick("defaultState2", () => NativeBridgeFacade.HudHealth());
            OnClick("timeBackground2", CycleTimePeriod);

            _timeHand = Root.Q("timeHand");
            if (_timeHand != null)
            {
                _timeHand.usageHints = UsageHints.DynamicTransform;
                // Pivot at dial center so Period snaps (左/上/右/下) stay cardinal.
                _timeHand.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
            }

            _timeDial = Root.Q("timeDial");
            _timeLabel = Root.Q("timeLabel");
            _timeIcon1 = Root.Q("timeIcon1");
            _timeIcon2 = Root.Q("timeIcon2");
            EnsureTimeDialTextures();
            EnsureTimeLabelText();
            HideTimeToastImmediate();
            EnsurePresetChrome();
            ResetPresetChrome();

            VisualElement musicIcon = Root.Q("musicBackground");
            _musicPlaying = musicIcon?.Q("musicPlaying");

            VisualElement pauseHint = Root.Q("pauseHint");
            if (pauseHint != null)
                pauseHint.style.opacity = 0;

            VisualElement formRoot = Root.Q("root") ?? Root;
            _joystick?.Dispose();
            _joystick = IslandVirtualJoystick.Attach(formRoot);
            _locomotion.Bind(_joystick);
            _hudLayout.Bind(formRoot);

            _idle.Bind(Root.Q("hudHeader"), Root.Q("hudIcons"), Root.Q("breathHint"));

            if (!_layoutHooked)
            {
                ScreenLayout.Changed += OnScreenLayoutChanged;
                _layoutHooked = true;
            }

            // Layout may not be resolved on first frame.
            Root.schedule.Execute(ApplyInputLayout).StartingIn(0);
            Root.schedule.Execute(ApplyInputLayout).StartingIn(50);

            if (GameEntry.Event != null)
            {
                GameEntry.Event.Subscribe(TimeOfDayChangedEventArgs.EventId, OnTimeChanged);
                GameEntry.Event.Subscribe(PresetTransitionRequestEventArgs.EventId, OnPresetTransitionRequest);
            }

            // Cold-start: wall clock → Period → four cardinal dial snaps (Boluo InitClockHandFromTimeType).
            GameEntry.Weather?.ApplyLocalClockIfNeeded();
            SyncDial();
            Root.schedule.Execute(SyncDial).StartingIn(0);
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            if (_layoutHooked)
            {
                ScreenLayout.Changed -= OnScreenLayoutChanged;
                _layoutHooked = false;
            }

            _toastHide?.Pause();
            _toastHide = null;
            _presetLabelHide?.Pause();
            _presetLabelHide = null;
            _idle.Dispose();
            _locomotion.ForceEnd();
            _joystick?.Dispose();
            _joystick = null;

            if (GameEntry.Event != null)
            {
                GameEntry.Event.Unsubscribe(TimeOfDayChangedEventArgs.EventId, OnTimeChanged);
                GameEntry.Event.Unsubscribe(PresetTransitionRequestEventArgs.EventId, OnPresetTransitionRequest);
            }

            base.OnClose(isShutdown, userData);
        }

        public override void OnUpdate(float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(elapseSeconds, realElapseSeconds);
            SyncDial();
            SyncMusicPlaying();
            TickTimeToastFade(elapseSeconds);
            TickPresetVeil(elapseSeconds);
            TickPresetLabelFade(elapseSeconds);
            _locomotion.Tick();
            _idle.Tick(WasPointerPressedThisFrame(), _locomotion.IsMoveInputActive);
        }

        void CycleTimePeriod()
        {
            WeatherService weather = GameEntry.Weather;
            if (weather == null || weather.IsTimeTransitioning)
                return;

            IslandTimePeriod next = weather.Period switch
            {
                IslandTimePeriod.Morning => IslandTimePeriod.Noon,
                IslandTimePeriod.Noon => IslandTimePeriod.Evening,
                IslandTimePeriod.Evening => IslandTimePeriod.Night,
                _ => IslandTimePeriod.Morning,
            };
            // Sky / light SmoothStep over 2s (Boluo IETimeLerp); dial snaps immediately.
            weather.SetPeriod(next, animate: true);
            ShowTimeToast(next);
        }

        void SyncMusicPlaying()
        {
            if (_musicPlaying == null)
                return;

            bool playing = GameEntry.Music != null && GameEntry.Music.IsPlaying;
            _musicPlaying.style.display = playing ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OnScreenLayoutChanged()
        {
            _locomotion.ForceEnd();
            ApplyInputLayout();
        }

        void ApplyInputLayout()
        {
            _hudLayout.Apply();
            _locomotion.ApplyLayoutScale();
            _idle.ApplyLayout();
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused)
                _idle.ExitOnReturnFromForeground();
        }

        void OnApplicationFocus(bool focused)
        {
            if (focused)
                _idle.ExitOnReturnFromForeground();
        }

        static bool WasPointerPressedThisFrame()
        {
            if (Input.touchCount > 0)
                return Input.GetTouch(0).phase == TouchPhase.Began;
            return Input.GetMouseButtonDown(0);
        }

        void OnTimeChanged(object sender, GameEventArgs e) => SyncDial();

        void OnPresetTransitionRequest(object sender, GameEventArgs e)
        {
            if (e is not PresetTransitionRequestEventArgs args)
                return;

            if (_presetTransitionBusy)
            {
                _queuedPresetId = args.PresetId;
                return;
            }

            BeginPresetTransition(args.PresetId);
        }

        void BeginPresetTransition(int presetId)
        {
            _presetTransitionBusy = true;
            _transitionPresetId = presetId;
            _queuedPresetId = 0;
            _idle.ExitOnReturnFromForeground();
            HidePresetLabelImmediate();
            HideTimeToastImmediate();

            if (_presetVeil != null)
            {
                _presetVeil.style.display = DisplayStyle.Flex;
                _presetVeil.pickingMode = PickingMode.Position;
                _presetVeil.BringToFront();
            }

            _veilPhase = VeilPhase.FadingIn;
            StartVeilFade(_veilAlpha, 1f, PresetVeilInSeconds);
        }

        void EnsurePresetChrome()
        {
            VisualElement formRoot = Root.Q("root") ?? Root;
            _presetVeil = Root.Q("presetVeil");
            if (_presetVeil == null)
            {
                _presetVeil = new VisualElement { name = "presetVeil" };
                _presetVeil.AddToClassList("game-island-main-form__preset-veil");
                formRoot.Add(_presetVeil);
            }

            _presetLabel = Root.Q<Label>("presetLabel");
            if (_presetLabel == null)
            {
                _presetLabel = new Label
                {
                    name = "presetLabel",
                    pickingMode = PickingMode.Ignore,
                };
                _presetLabel.AddToClassList("game-island-main-form__preset-label");
                formRoot.Add(_presetLabel);
            }
        }

        void ResetPresetChrome()
        {
            _veilPhase = VeilPhase.None;
            _veilFadeElapsed = -1f;
            _veilAlpha = 0f;
            ApplyVeilOpacity(0f);
            if (_presetVeil != null)
            {
                _presetVeil.style.display = DisplayStyle.None;
                _presetVeil.pickingMode = PickingMode.Ignore;
            }

            HidePresetLabelImmediate();
            _presetTransitionBusy = false;
            _transitionPresetId = 0;
            _queuedPresetId = 0;
            _veilHoldElapsed = -1f;
        }

        void TickPresetVeil(float elapseSeconds)
        {
            if (_veilPhase == VeilPhase.None)
                return;

            if (_veilPhase == VeilPhase.Holding)
            {
                _veilHoldElapsed += elapseSeconds;
                if (_veilHoldElapsed < PresetVeilHoldSeconds)
                    return;

                _veilHoldElapsed = -1f;
                _veilPhase = VeilPhase.FadingOut;
                StartVeilFade(_veilAlpha, 0f, PresetVeilOutSeconds);
                SchedulePresetLabelAfterReveal();
                return;
            }

            if (_veilFadeElapsed < 0f)
                return;

            _veilFadeElapsed += elapseSeconds;
            float t = Mathf.Clamp01(_veilFadeElapsed / _veilFadeDuration);
            _veilAlpha = Mathf.Lerp(_veilFadeFrom, _veilFadeTo, t);
            ApplyVeilOpacity(_veilAlpha);
            if (t < 1f)
                return;

            _veilFadeElapsed = -1f;

            if (_veilPhase == VeilPhase.FadingIn)
            {
                GameEntry.Presets?.Apply(_transitionPresetId, teleportPlayer: true);
                _veilPhase = VeilPhase.Holding;
                _veilHoldElapsed = 0f;
                return;
            }

            if (_veilPhase == VeilPhase.FadingOut)
            {
                if (_presetVeil != null)
                {
                    _presetVeil.style.display = DisplayStyle.None;
                    _presetVeil.pickingMode = PickingMode.Ignore;
                }

                _veilPhase = VeilPhase.None;
                _presetTransitionBusy = false;
                if (_queuedPresetId > 0)
                {
                    int next = _queuedPresetId;
                    _queuedPresetId = 0;
                    BeginPresetTransition(next);
                }
            }
        }

        void StartVeilFade(float from, float to, float duration)
        {
            _veilFadeFrom = from;
            _veilFadeTo = to;
            _veilFadeDuration = Mathf.Max(0.01f, duration);
            _veilFadeElapsed = 0f;
            if (_presetVeil != null && to > 0f)
                _presetVeil.style.display = DisplayStyle.Flex;
        }

        void ApplyVeilOpacity(float alpha)
        {
            if (_presetVeil != null)
                _presetVeil.style.opacity = alpha;
        }

        void SchedulePresetLabelAfterReveal()
        {
            IslandPresetAsset preset = GameEntry.Presets?.GetById(GameEntry.Presets.CurrentId);
            string text = preset != null ? preset.Description : null;
            if (string.IsNullOrEmpty(text))
                text = preset != null ? preset.DisplayName : null;
            if (string.IsNullOrEmpty(text) || _presetLabel == null)
                return;

            text = text.Replace("\r\n", "\n");
            _presetLabel.text = text;
            _presetLabel.style.display = DisplayStyle.Flex;
            _presetLabel.BringToFront();
            if (_presetVeil != null)
                _presetVeil.BringToFront();

            _presetLabelHide?.Pause();
            _presetLabelHide = Root.schedule.Execute(() =>
            {
                StartPresetLabelFade(_presetLabelAlpha, 1f, PresetLabelFadeSeconds);
                _presetLabelHide = Root.schedule.Execute(() =>
                {
                    StartPresetLabelFade(_presetLabelAlpha, 0f, PresetLabelFadeSeconds);
                }).StartingIn((long)(PresetLabelHoldSeconds * 1000f));
            }).StartingIn((long)(PresetLabelDelaySeconds * 1000f));
        }

        void HidePresetLabelImmediate()
        {
            _presetLabelHide?.Pause();
            _presetLabelHide = null;
            _presetLabelFadeElapsed = -1f;
            _presetLabelAlpha = 0f;
            if (_presetLabel != null)
            {
                _presetLabel.style.opacity = 0f;
                _presetLabel.style.display = DisplayStyle.None;
                _presetLabel.text = string.Empty;
            }
        }

        void StartPresetLabelFade(float from, float to, float duration)
        {
            _presetLabelFadeFrom = from;
            _presetLabelFadeTo = to;
            _presetLabelFadeDuration = Mathf.Max(0.01f, duration);
            _presetLabelFadeElapsed = 0f;
            if (_presetLabel != null && to > 0f)
                _presetLabel.style.display = DisplayStyle.Flex;
        }

        void TickPresetLabelFade(float elapseSeconds)
        {
            if (_presetLabelFadeElapsed < 0f)
                return;

            _presetLabelFadeElapsed += elapseSeconds;
            float t = Mathf.Clamp01(_presetLabelFadeElapsed / _presetLabelFadeDuration);
            _presetLabelAlpha = Mathf.Lerp(_presetLabelFadeFrom, _presetLabelFadeTo, t);
            if (_presetLabel != null)
                _presetLabel.style.opacity = _presetLabelAlpha;

            if (t < 1f)
                return;

            _presetLabelFadeElapsed = -1f;
            if (_presetLabelAlpha <= 0.001f && _presetLabel != null)
                _presetLabel.style.display = DisplayStyle.None;
        }

        void SyncDial()
        {
            if (_timeHand == null || GameEntry.Weather == null)
                return;
            // Four cardinal snaps only — match Boluo HudTimeDialController InitClockHandFromTimeType.
            float degrees = GameEntry.Weather.DialAngleDegrees;
            _timeHand.style.rotate = new Rotate(Angle.Degrees(degrees));
        }

        void EnsureTimeDialTextures()
        {
            if (_texLabelMorning != null)
                return;

            _texLabelMorning = Resources.Load<Texture2D>("UI/TimeDial/qingchent");
            _texLabelNoon = Resources.Load<Texture2D>("UI/TimeDial/zhengwut");
            _texLabelEvening = Resources.Load<Texture2D>("UI/TimeDial/huanghunt");
            _texLabelNight = Resources.Load<Texture2D>("UI/TimeDial/yewant");
            _texIconMorningEvening = Resources.Load<Texture2D>("UI/TimeDial/huanghuntaiyang");
            _texIconNoon = Resources.Load<Texture2D>("UI/TimeDial/zhengwuicon");
            _texIconNight = Resources.Load<Texture2D>("UI/TimeDial/yewanyueliang");
            _texArrowMorning = Resources.Load<Texture2D>("UI/TimeDial/qingchenjiantou");
            _texArrowEvening = Resources.Load<Texture2D>("UI/TimeDial/huanghunjiantou");
            _texArrowNight = Resources.Load<Texture2D>("UI/TimeDial/yewanzzz");
        }

        void EnsureTimeLabelText()
        {
            if (_timeLabel == null)
                return;

            _timeLabelText = _timeLabel.Q<Label>("timeLabelText");
            if (_timeLabelText != null)
            {
                _timeLabelText.style.display = DisplayStyle.None;
                return;
            }

            _timeLabelText = new Label
            {
                name = "timeLabelText",
                pickingMode = PickingMode.Ignore,
            };
            _timeLabelText.AddToClassList("game-island-main-form__time-label-text");
            _timeLabelText.style.display = DisplayStyle.None;
            _timeLabel.Add(_timeLabelText);
        }

        static void SetBackground(VisualElement el, Texture2D tex)
        {
            if (el == null)
                return;
            el.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.None;
        }

        void ShowTimeToast(IslandTimePeriod period)
        {
            if (_timeDial == null)
                return;

            ApplyToastContent(period);
            _toastHide?.Pause();
            _toastVisible = true;
            StartToastFade(_toastAlpha, 1f, TimeToastFadeSeconds);
            _toastHide = Root.schedule.Execute(() =>
            {
                StartToastFade(_toastAlpha, 0f, TimeToastFadeSeconds);
            }).StartingIn((long)(TimeToastSeconds * 1000f));
        }

        void HideTimeToastImmediate()
        {
            _toastHide?.Pause();
            _toastHide = null;
            _toastVisible = false;
            _toastFadeElapsed = -1f;
            _toastAlpha = 0f;
            ApplyToastOpacity(0f);
            if (_timeDial != null)
                _timeDial.style.display = DisplayStyle.None;
        }

        void ApplyToastContent(IslandTimePeriod period)
        {
            EnsureTimeDialTextures();

            // Boluo HudTimeDialController.PlayEffect / ApplyTimeSlotStaticUI sprite map.
            Texture2D label = period switch
            {
                IslandTimePeriod.Morning => _texLabelMorning,
                IslandTimePeriod.Noon => _texLabelNoon,
                IslandTimePeriod.Evening => _texLabelEvening,
                IslandTimePeriod.Night => _texLabelNight,
                _ => _texLabelNoon,
            };
            Texture2D icon1 = period switch
            {
                IslandTimePeriod.Noon => _texIconNoon,
                IslandTimePeriod.Night => _texIconNight,
                _ => _texIconMorningEvening,
            };
            Texture2D icon2 = period switch
            {
                IslandTimePeriod.Morning => _texArrowMorning,
                IslandTimePeriod.Evening => _texArrowEvening,
                IslandTimePeriod.Night => _texArrowNight,
                _ => null,
            };

            SetBackground(_timeLabel, label);
            SetBackground(_timeIcon1, icon1);
            SetBackground(_timeIcon2, icon2);
            if (_timeIcon2 != null)
            {
                _timeIcon2.style.rotate = StyleKeyword.Null;
                _timeIcon2.style.display = icon2 != null ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_timeLabelText != null)
                _timeLabelText.style.display = DisplayStyle.None;

            if (_timeDial != null)
                _timeDial.style.display = DisplayStyle.Flex;
        }

        void StartToastFade(float from, float to, float duration)
        {
            _toastFadeFrom = from;
            _toastFadeTo = to;
            _toastFadeDuration = Mathf.Max(0.01f, duration);
            _toastFadeElapsed = 0f;
            if (_timeDial != null && to > 0f)
                _timeDial.style.display = DisplayStyle.Flex;
        }

        void TickTimeToastFade(float elapseSeconds)
        {
            if (_toastFadeElapsed < 0f)
                return;

            _toastFadeElapsed += elapseSeconds;
            float t = Mathf.Clamp01(_toastFadeElapsed / _toastFadeDuration);
            _toastAlpha = Mathf.Lerp(_toastFadeFrom, _toastFadeTo, t);
            ApplyToastOpacity(_toastAlpha);

            if (t < 1f)
                return;

            _toastFadeElapsed = -1f;
            if (_toastAlpha <= 0.001f)
            {
                _toastVisible = false;
                if (_timeDial != null)
                    _timeDial.style.display = DisplayStyle.None;
            }
        }

        void ApplyToastOpacity(float alpha)
        {
            if (_timeDial != null)
                _timeDial.style.opacity = alpha;
        }
    }
}
