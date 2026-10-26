using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Idle immersive: after 15s without popup, HUD fades out and breath hint shows;
    /// tap or return-from-background exits.
    /// </summary>
    public sealed class IslandIdleImmersive
    {
        const float IdleFadeAfterSeconds = 15f;
        const float FadeOutDuration = 1f;
        const float FadeInDuration = 0.35f;
        const float OilWarpFadeInSeconds = 1f;
        const float OilWarpFadeOutSeconds = 1f;

        /// <summary>HUD 已完全淡出；供相机压 renderScale / 目标帧率等查询。</summary>
        public static bool IsActive { get; private set; }

        readonly VisualElement[] _fadeRoots = new VisualElement[2];
        readonly IslandBreathHint _breath = new IslandBreathHint();

        float _lastActivityRealtime;
        float _fadeFrom;
        float _fadeTo;
        float _fadeDuration;
        float _fadeElapsed = -1f;
        bool _armed;
        bool _fullyFadedOut;
        bool _hadPopupLastFrame;
        float _currentAlpha = 1f;
        float _oilWarpWeight;
        bool _oilWarpDirty;

        public bool IsIdleImmersive => _fullyFadedOut;

        public void Bind(VisualElement header, VisualElement iconArea, VisualElement breathHint)
        {
            _fadeRoots[0] = header;
            _fadeRoots[1] = iconArea;
            for (int i = 0; i < _fadeRoots.Length; i++)
            {
                if (_fadeRoots[i] != null)
                    _fadeRoots[i].pickingMode = PickingMode.Ignore;
            }
            _breath.Bind(breathHint);
            ResetVisible();
        }

        public void ApplyLayout() => _breath.ApplyLayout();

        public void Tick(bool pointerPressedThisFrame, bool locomotionActive)
        {
            // 含原生定时窗 bottomIconsHidden / 半层 overlayPresent，否则 Compose 定时期间仍会淡掉 HUD 进度环。
            bool popup = HudOverlayGate.BlocksIdleImmersive();
            if (_hadPopupLastFrame && !popup)
                ArmCountdown();
            _hadPopupLastFrame = popup;

            bool fadingOut = _fadeElapsed >= 0f && _fadeTo <= 0.01f;

            if (popup)
            {
                if (_fullyFadedOut || fadingOut)
                    ExitImmersive();
                TickFade();
                TickOilWarp();
                return;
            }

            if (_fullyFadedOut)
            {
                // Stick move must exit too — otherwise OilWarp keeps a full-buffer
                // pass for ~1s while walk already forces every-frame planar mirror.
                if (pointerPressedThisFrame || locomotionActive)
                    ExitImmersive();
                else
                    _breath.Tick();
                TickFade();
                TickOilWarp();
                return;
            }

            if (pointerPressedThisFrame || locomotionActive)
                RegisterActivity();

            TickFade();
            TickOilWarp();

            if (!_armed || _fadeElapsed >= 0f)
                return;
            if (Time.realtimeSinceStartup - _lastActivityRealtime >= IdleFadeAfterSeconds)
                BeginFadeOut();
        }

        public void Dispose()
        {
            bool wasImmersive = IsActive || _fullyFadedOut;
            _breath.Hide();
            KillFade();
            _fullyFadedOut = false;
            IsActive = false;
            _armed = false;
            SetHudAlpha(1f);
            ClearOilWarp();
            if (wasImmersive)
                IslandImmersiveSoftUnload.OnExitImmersive();
            // 关界面也强制回到默认 60，避免静态 IsActive / 帧率缓存残留。
            IslandCameraSrpSetup.ReapplyTargetFrameRateAfterOverlay();
        }

        public void ExitOnReturnFromForeground()
        {
            if (!_fullyFadedOut && _fadeElapsed < 0f)
                return;
            ExitImmersive();
        }

        void RegisterActivity()
        {
            _lastActivityRealtime = Time.realtimeSinceStartup;
            bool fadingOut = _fadeElapsed >= 0f && _fadeTo <= 0.01f;
            if (_fullyFadedOut || fadingOut)
                ExitImmersive();
        }

        void ArmCountdown()
        {
            _lastActivityRealtime = Time.realtimeSinceStartup;
            _armed = true;
            _hadPopupLastFrame = HudOverlayGate.BlocksIdleImmersive();
        }

        void ResetVisible()
        {
            bool wasImmersive = IsActive || _fullyFadedOut;
            KillFade();
            _fullyFadedOut = false;
            IsActive = false;
            SetHudAlpha(1f);
            _breath.Hide();
            ClearOilWarp();
            if (wasImmersive)
                IslandImmersiveSoftUnload.OnExitImmersive();
            ArmCountdown();
            IslandCameraSrpSetup.ReapplyTargetFrameRateAfterOverlay();
        }

        void BeginFadeOut()
        {
            if (_fullyFadedOut || _fadeElapsed >= 0f)
                return;
            StartFade(_currentAlpha, 0f, FadeOutDuration);
        }

        void ExitImmersive()
        {
            bool wasImmersive = _fullyFadedOut || IsActive;
            KillFade();
            _fullyFadedOut = false;
            IsActive = false;
            _breath.Hide();
            // Drop warp immediately — 1s fade while walking reallocates PostFX RTs
            // on the same frames as every-frame planar mirror (真机花屏/卡死).
            ClearOilWarp();
            StartFade(_currentAlpha, 1f, FadeInDuration);
            ArmCountdown();
            // 退出沉浸必须拉回 60：清 _appliedFrameRate 缓存，避免与 Application 不同步时早退。
            if (wasImmersive)
            {
                IslandImmersiveSoftUnload.OnExitImmersive();
                IslandCameraSrpSetup.ReapplyTargetFrameRateAfterOverlay();
                Log.Info(
                    "[IslandIdleImmersive] exit fps="
                    + IslandCameraSrpSetup.DefaultFrameRate
                    + " app="
                    + Application.targetFrameRate);
            }
        }

        void StartFade(float from, float to, float duration)
        {
            _fadeFrom = from;
            _fadeTo = to;
            _fadeDuration = Mathf.Max(0.01f, duration);
            _fadeElapsed = 0f;
            SetHudAlpha(from);
        }

        void TickFade()
        {
            if (_fadeElapsed < 0f)
                return;

            _fadeElapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_fadeElapsed / _fadeDuration);
            SetHudAlpha(Mathf.Lerp(_fadeFrom, _fadeTo, t));
            if (t < 1f)
                return;

            KillFade();
            SetHudAlpha(_fadeTo);
            if (_fadeTo <= 0.01f)
                OnFadeOutComplete();
        }

        void OnFadeOutComplete()
        {
            _fullyFadedOut = true;
            IsActive = true;
            _breath.Show();
            IslandCameraSrpSetup.ReapplyTargetFrameRateAfterOverlay();
            IslandImmersiveSoftUnload.OnEnterImmersive();
            Log.Info(
                "[IslandIdleImmersive] enter fps="
                + IslandCameraSrpSetup.ImmersiveFrameRate
                + " app="
                + Application.targetFrameRate);
        }

        void KillFade() => _fadeElapsed = -1f;

        void SetHudAlpha(float alpha)
        {
            _currentAlpha = alpha;
            for (int i = 0; i < _fadeRoots.Length; i++)
            {
                if (_fadeRoots[i] != null)
                    _fadeRoots[i].style.opacity = alpha;
            }
        }

        void TickOilWarp()
        {
            float target = _fullyFadedOut ? 1f : 0f;
            float dur = target > _oilWarpWeight
                ? OilWarpFadeInSeconds
                : OilWarpFadeOutSeconds;
            float step = dur > 1e-4f ? Time.unscaledDeltaTime / dur : 1f;
            float next = Mathf.MoveTowards(_oilWarpWeight, target, step);
            if (Mathf.Abs(next - _oilWarpWeight) > 1e-5f || next > 1e-4f || _oilWarpDirty)
            {
                _oilWarpWeight = next;
                if (_oilWarpWeight > 1e-4f)
                {
                    OilWarpVisual.SetImmersiveWarp(
                        _breath.Breath01, _oilWarpWeight, _breath.BreathPhaseRadians);
                    _oilWarpDirty = true;
                }
                else
                    ClearOilWarp();
            }
        }

        void ClearOilWarp()
        {
            _oilWarpWeight = 0f;
            if (!_oilWarpDirty)
                return;
            OilWarpVisual.SetImmersiveWarp(0f, 0f, 0f);
            _oilWarpDirty = false;
        }
    }
}
