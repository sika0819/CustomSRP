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

        // Boluo IEMoveTo iris timings.
        const float OutlensSeconds = 2f;
        const float VignetteStartSeconds = 0.5f;
        const float VignetteKeepSeconds = 1f;
        const float InlensSeconds = 2f;
        const float BlackHoldSeconds = 0.16f;
        const float BlackHoldLowEndSeconds = 0.08f;
        // Boluo YuSheLable: fadeIn@2s, fadeOut@5s, each 0.6s.
        const float PresetLabelDelaySeconds = 2f;
        const float PresetLabelFadeSeconds = 0.6f;
        const float PresetLabelHoldSeconds = 2.4f;

        VisualElement _timeHand;
        VisualElement _timeDial;
        VisualElement _timeLabel;
        VisualElement _timeIcon1;
        VisualElement _timeIcon2;
        VisualElement _weatherGlyph;
        VisualElement _musicGlyph;
        VisualElement _musicPlaying;
        VisualElement _eqBarLeft;
        VisualElement _eqBarCenter;
        VisualElement _eqBarRight;
        float _eqTime;
        VisualElement _volumeMusicDot;
        VisualElement _volumeEffectDot;
        VisualElement _volumeMusicFill;
        VisualElement _volumeEffectFill;
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
        // Boluo HudWeatherBinder atlas: clear + cloud / rain / fog / wind × 小中大.
        Texture2D _texWeatherClear;
        Texture2D[] _texWeatherCloud;
        Texture2D[] _texWeatherRain;
        Texture2D[] _texWeatherFog;
        Texture2D[] _texWeatherWind;
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
        readonly HudTimerBinder _timerBinder = new HudTimerBinder();
        bool _layoutHooked;

        VisualElement _presetVeil;
        VisualElement _hudHeader;
        VisualElement _hudIcons;
        Label _presetLabel;
        IVisualElementScheduledItem _presetLabelHide;
        float _presetLabelAlpha;
        float _presetLabelFadeFrom;
        float _presetLabelFadeTo;
        float _presetLabelFadeDuration;
        float _presetLabelFadeElapsed = -1f;
        bool _presetTransitionBusy;
        int _transitionPresetId;
        int _queuedPresetId;
        float _phaseElapsed;
        enum IrisPhase { None, Exiting, Holding, Entering }
        IrisPhase _irisPhase;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            OnClick("weatherBackground", () => UiAssets.OpenPanel(UiAssets.WeatherForm));
            OnClick("musicBackground", () => UiAssets.OpenPanel(UiAssets.MusicForm));
            OnClick("volumeBackground", () => UiAssets.OpenPanel(UiAssets.VolumeForm));
            OnClick("presetIcon", TryOpenPresetPanel);
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

            VisualElement weatherIcon = Root.Q("weatherBackground");
            _weatherGlyph = weatherIcon?.Q("glyph");

            VisualElement musicIcon = Root.Q("musicBackground");
            _musicGlyph = musicIcon?.Q("glyph");
            _musicPlaying = musicIcon?.Q("musicPlaying");
            _eqBarLeft = _musicPlaying?.Q("barLeft");
            _eqBarCenter = _musicPlaying?.Q("barCenter");
            _eqBarRight = _musicPlaying?.Q("barRight");

            VisualElement volumeIcon = Root.Q("volumeBackground");
            _volumeMusicDot = volumeIcon?.Q("musicvolumeyuan");
            _volumeEffectDot = volumeIcon?.Q("effectvolumeyuan");
            _volumeMusicFill = volumeIcon?.Q("volumeTrack1");
            _volumeEffectFill = volumeIcon?.Q("volumeTrack2");
            SyncVolumeIcon();
            SyncWeatherIcon();

            VisualElement pauseHint = Root.Q("pauseHint");
            if (pauseHint != null)
                pauseHint.style.opacity = 0;

            VisualElement formRoot = Root.Q("root") ?? Root;
            _joystick?.Dispose();
            _joystick = IslandVirtualJoystick.Attach(formRoot);
            _locomotion.Bind(_joystick);
            _hudLayout.Bind(formRoot);
            _timerBinder.Bind(Root.Q("timerBackground"));
            SyncTimerIcon();

            _hudHeader = Root.Q("hudHeader");
            _hudIcons = Root.Q("hudIcons");
            _idle.Bind(_hudHeader, _hudIcons, Root.Q("breathHint"));

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
                GameEntry.Event.Subscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
                GameEntry.Event.Subscribe(PresetTransitionRequestEventArgs.EventId, OnPresetTransitionRequest);
                GameEntry.Event.Subscribe(NativeTimerEventArgs.EventId, OnNativeTimer);
            }

            // Cold-start: wall clock → Period → four cardinal dial snaps (Boluo InitClockHandFromTimeType).
            GameEntry.Weather?.ApplyLocalClockIfNeeded();
            SyncDial();
            SyncWeatherIcon();
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
            _timerBinder.Unbind();
            AbortPresetTransitionCleanup();
            _locomotion.ForceEnd();
            _joystick?.Dispose();
            _joystick = null;

            if (GameEntry.Event != null)
            {
                GameEntry.Event.Unsubscribe(TimeOfDayChangedEventArgs.EventId, OnTimeChanged);
                GameEntry.Event.Unsubscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
                GameEntry.Event.Unsubscribe(PresetTransitionRequestEventArgs.EventId, OnPresetTransitionRequest);
                GameEntry.Event.Unsubscribe(NativeTimerEventArgs.EventId, OnNativeTimer);
            }

            base.OnClose(isShutdown, userData);
        }

        public override void OnUpdate(float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(elapseSeconds, realElapseSeconds);
            SyncDial();
            SyncMusicPlaying(realElapseSeconds);
            SyncVolumeIcon();
            SyncTimerIcon();
            TickTimeToastFade(elapseSeconds);
            TickPresetIris(elapseSeconds);
            TickPresetLabelFade(elapseSeconds);
            _locomotion.Tick();
            bool pointerPressed = WasPointerPressedThisFrame();
            _idle.Tick(pointerPressed, _locomotion.IsMoveInputActive);
            if (pointerPressed)
            {
                // 点场景 = 可靠的「我回前台了」信号：宿主壳里 OnApplicationFocus 可能不触发，
                // 音频被打断时靠这一下把音乐 / 音效拉回来。
                AudioAppLifecycle.OnSceneInteraction();
            }
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

        void SyncMusicPlaying(float dt)
        {
            bool playing = GameEntry.Music != null && GameEntry.Music.IsPlaying;
            if (_musicPlaying != null)
                _musicPlaying.style.display = playing ? DisplayStyle.Flex : DisplayStyle.None;
            if (_musicGlyph != null)
                _musicGlyph.style.display = playing ? DisplayStyle.None : DisplayStyle.Flex;
            if (playing)
                TickMusicEqualizer(dt);
        }

        // Design space (108px icon): bars share baseline y=74 and bounce upward.
        const float EqBaseline = 74f;
        const float EqBarWidth = 10f;

        void TickMusicEqualizer(float dt)
        {
            _eqTime += dt;
            float s = 1f;
            if (_musicPlaying != null)
            {
                float w = _musicPlaying.resolvedStyle.width;
                if (w > 1f)
                    s = w / 108f;
            }

            PlaceEqBar(_eqBarLeft, 31f, 10f, 32f, _eqTime * 5.2f + 0.4f, s);
            PlaceEqBar(_eqBarCenter, 49f, 14f, 48f, _eqTime * 6.7f + 1.8f, s);
            PlaceEqBar(_eqBarRight, 67f, 10f, 40f, _eqTime * 4.4f + 3.1f, s);
        }

        static void PlaceEqBar(VisualElement bar, float left, float minH, float maxH, float phase, float s)
        {
            if (bar == null)
                return;
            float t = 0.5f + 0.5f * Mathf.Sin(phase);
            float h = Mathf.Lerp(minH, maxH, t) * s;
            bar.style.left = left * s;
            bar.style.width = EqBarWidth * s;
            bar.style.height = h;
            bar.style.top = EqBaseline * s - h;
        }

        /// <summary>
        /// Boluo HudVolumeBinder: dots travel x 24→62, horizontal fills are 60px * volume.
        /// Track 1 / music dot follow music; track 2 / effect dot follow effects.
        /// </summary>
        void SyncVolumeIcon()
        {
            MusicPlaylist music = GameEntry.Music;
            float musicVolume = music != null ? Mathf.Clamp01(music.MusicVolume) : 1f;
            float effectVolume = music != null ? Mathf.Clamp01(music.EffectVolume) : 1f;
            PlaceVolumeMark(_volumeMusicDot, _volumeMusicFill, musicVolume);
            PlaceVolumeMark(_volumeEffectDot, _volumeEffectFill, effectVolume);
        }

        static void PlaceVolumeMark(VisualElement dot, VisualElement fill, float value)
        {
            const float minX = 24f;
            const float maxX = 62f;
            const float trackWidth = 60f;
            if (dot != null)
                dot.style.left = minX + (maxX - minX) * value;
            if (fill != null)
                fill.style.width = trackWidth * value;
        }

        void OnScreenLayoutChanged()
        {
            _locomotion.ForceEnd();
            ApplyInputLayout();
        }

        void ApplyInputLayout()
        {
            _hudLayout.Apply();
            SyncVolumeIcon();
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

        void OnWeatherChanged(object sender, GameEventArgs e) => SyncWeatherIcon();

        void OnNativeTimer(object sender, GameEventArgs e) => SyncTimerIcon();

        /// <summary>
        /// 跟 NativeTimerService（原生 TimerProgress / Editor NativeEditorHud 推送）画定时环。
        /// </summary>
        void SyncTimerIcon()
        {
            _timerBinder.Apply(NativeTimerService.LastRemaining, NativeTimerService.FillRatio);
        }

        /// <summary>
        /// Boluo HudWeatherBinder.Refresh: level 0 → 晴天；否则按 Kind 取 小/中/大 图标。
        /// </summary>
        void SyncWeatherIcon()
        {
            if (_weatherGlyph == null)
                return;

            EnsureWeatherIconTextures();
            WeatherService weather = GameEntry.Weather;
            IslandWeatherKind kind = weather != null ? weather.Kind : IslandWeatherKind.Cloud;
            int level = weather != null ? Mathf.Clamp(weather.Level, 0, 3) : 0;
            SetBackground(_weatherGlyph, ResolveWeatherIcon(kind, level));
        }

        Texture2D ResolveWeatherIcon(IslandWeatherKind kind, int level)
        {
            if (level <= 0 || kind == IslandWeatherKind.Clear)
                return _texWeatherClear;

            int idx = Mathf.Clamp(level - 1, 0, 2);
            return kind switch
            {
                IslandWeatherKind.Cloud => PickWeatherTier(_texWeatherCloud, idx),
                IslandWeatherKind.Rain => PickWeatherTier(_texWeatherRain, idx),
                IslandWeatherKind.Foggy => PickWeatherTier(_texWeatherFog, idx),
                IslandWeatherKind.Wind => PickWeatherTier(_texWeatherWind, idx),
                _ => _texWeatherClear,
            };
        }

        static Texture2D PickWeatherTier(Texture2D[] list, int idx)
        {
            if (list == null || list.Length == 0)
                return null;
            return list[Mathf.Clamp(idx, 0, list.Length - 1)];
        }

        void EnsureWeatherIconTextures()
        {
            if (_texWeatherClear != null)
                return;

            _texWeatherClear = Resources.Load<Texture2D>("UI/WeatherIcons/qingtian");
            // 档位按“强度从小到大”排列，idx = level - 1。
            // 云层资源里没有“大云”贴图，3 级复用中云。
            _texWeatherCloud = new[]
            {
                Resources.Load<Texture2D>("UI/WeatherIcons/xiaoyun"),
                Resources.Load<Texture2D>("UI/WeatherIcons/zhongyun"),
                Resources.Load<Texture2D>("UI/WeatherIcons/zhongyun"),
            };
            _texWeatherRain = new[]
            {
                Resources.Load<Texture2D>("UI/WeatherIcons/xiaoyu"),
                Resources.Load<Texture2D>("UI/WeatherIcons/zhongyu"),
                Resources.Load<Texture2D>("UI/WeatherIcons/dayu"),
            };
            _texWeatherFog = new[]
            {
                Resources.Load<Texture2D>("UI/WeatherIcons/xiaowu"),
                Resources.Load<Texture2D>("UI/WeatherIcons/zhongwu"),
                Resources.Load<Texture2D>("UI/WeatherIcons/dawu"),
            };
            _texWeatherWind = new[]
            {
                Resources.Load<Texture2D>("UI/WeatherIcons/xiaofeng"),
                Resources.Load<Texture2D>("UI/WeatherIcons/zhongfeng"),
                Resources.Load<Texture2D>("UI/WeatherIcons/dafeng"),
            };
        }

        void TryOpenPresetPanel()
        {
            // F1: block open while a preset MoveTo / veil is in flight.
            if (GameEntry.Presets != null && GameEntry.Presets.IsTransitioning)
                return;
            if (_presetTransitionBusy)
                return;
            UiAssets.OpenPanel(UiAssets.PresetForm);
        }

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
            _phaseElapsed = 0f;
            _idle.ExitOnReturnFromForeground();
            HidePresetLabelImmediate();
            HideTimeToastImmediate();
            _locomotion.ForceEnd();
            SetHudVisible(false);
            ShowInputBlocker(true);

            PresetWeatherHold.Begin();
            PresetIrisOverlay.Clear();
            // Do not ClearContent3D here — screen is still fully audible for ~0.5–2s.
            // PresetService.Apply (iris Peak / black) clears under veil; Release retriggers.

            IslandJoystickCamera.Current?.BeginExitDolly();
            _irisPhase = IrisPhase.Exiting;
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
            AbortPresetTransitionCleanup();
            HidePresetLabelImmediate();
            _presetTransitionBusy = false;
            _transitionPresetId = 0;
            _queuedPresetId = 0;
            _phaseElapsed = 0f;
            _irisPhase = IrisPhase.None;
        }

        void AbortPresetTransitionCleanup()
        {
            PresetIrisOverlay.Clear();
            PresetWeatherHold.Abort();
            IslandJoystickCamera.Current?.EndTransitionDrive();
            ShowInputBlocker(false);
            SetHudVisible(true);
            _locomotion.ForceEnd();
            GameEntry.Presets?.AbortTransition();
            _irisPhase = IrisPhase.None;
            _phaseElapsed = 0f;
            _presetTransitionBusy = false;
        }

        void TickPresetIris(float elapseSeconds)
        {
            if (_irisPhase == IrisPhase.None)
                return;

            IslandJoystickCamera cam = IslandJoystickCamera.Current;
            _phaseElapsed += elapseSeconds;

            if (_irisPhase == IrisPhase.Exiting)
            {
                float t = Mathf.Clamp01(_phaseElapsed / OutlensSeconds);
                cam?.SampleExitDolly(t);
                if (!IslandGraphicsTier.IsLowEnd)
                    cam?.NudgeExitWalk(elapseSeconds);
                ApplyExitIris(_phaseElapsed);

                if (_phaseElapsed < OutlensSeconds)
                    return;

                PresetIrisOverlay.Peak();
                GameEntry.Presets?.Apply(_transitionPresetId, teleportPlayer: true);
                _irisPhase = IrisPhase.Holding;
                _phaseElapsed = 0f;
                return;
            }

            if (_irisPhase == IrisPhase.Holding)
            {
                PresetIrisOverlay.Peak();
                float hold = IslandGraphicsTier.IsLowEnd ? BlackHoldLowEndSeconds : BlackHoldSeconds;
                if (_phaseElapsed < hold)
                    return;

                _irisPhase = IrisPhase.Entering;
                _phaseElapsed = 0f;
                SchedulePresetLabelAfterReveal();
                return;
            }

            if (_irisPhase == IrisPhase.Entering)
            {
                float t = Mathf.Clamp01(_phaseElapsed / InlensSeconds);
                float softness = t;
                float alpha = 1f - t;
                PresetIrisOverlay.Set(softness, alpha);
                cam?.SampleEnterDolly(t);

                if (_phaseElapsed < InlensSeconds)
                    return;

                FinishPresetReveal();
            }
        }

        void ApplyExitIris(float timer)
        {
            if (timer < VignetteStartSeconds)
            {
                PresetIrisOverlay.Clear();
                return;
            }

            if (timer <= VignetteStartSeconds + VignetteKeepSeconds)
            {
                float u = (timer - VignetteStartSeconds) / VignetteKeepSeconds;
                PresetIrisOverlay.Set(1f - u, u);
                return;
            }

            PresetIrisOverlay.Peak();
        }

        void FinishPresetReveal()
        {
            PresetIrisOverlay.Clear();
            IslandJoystickCamera.Current?.EndTransitionDrive();
            PresetWeatherHold.Release();
            ShowInputBlocker(false);
            SetHudVisible(true);
            _locomotion.ForceEnd();

            _irisPhase = IrisPhase.None;
            _phaseElapsed = 0f;
            _presetTransitionBusy = false;
            GameEntry.Presets?.MarkPlaying();

            if (_queuedPresetId > 0)
            {
                int next = _queuedPresetId;
                _queuedPresetId = 0;
                BeginPresetTransition(next);
            }
        }

        void ShowInputBlocker(bool on)
        {
            if (_presetVeil == null)
                return;
            if (on)
            {
                _presetVeil.style.display = DisplayStyle.Flex;
                _presetVeil.style.opacity = 1f;
                _presetVeil.pickingMode = PickingMode.Position;
                _presetVeil.BringToFront();
            }
            else
            {
                _presetVeil.style.display = DisplayStyle.None;
                _presetVeil.pickingMode = PickingMode.Ignore;
            }
        }

        void SetHudVisible(bool visible)
        {
            DisplayStyle d = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (_hudHeader != null)
                _hudHeader.style.display = d;
            if (_hudIcons != null)
                _hudIcons.style.display = d;
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
