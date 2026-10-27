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
                // 摇杆走动也退出：否则全屏扭曲还会占一整张颜色 RT，和走路时的每帧水面反射叠在一起。
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
            _breath.Hide();
            KillFade();
            _fullyFadedOut = false;
            IsActive = false;
            _armed = false;
            SetHudAlpha(1f);
            ClearOilWarp();
            IslandCameraSrpSetup.ApplyTargetFrameRate();
            IslandAssetReclaimScheduler.Cancel();
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
            KillFade();
            _fullyFadedOut = false;
            IsActive = false;
            SetHudAlpha(1f);
            _breath.Hide();
            ClearOilWarp();
            ArmCountdown();
        }

        void BeginFadeOut()
        {
            if (_fullyFadedOut || _fadeElapsed >= 0f)
                return;
            StartFade(_currentAlpha, 0f, FadeOutDuration);
        }

        void ExitImmersive()
        {
            bool wasImmersive = _fullyFadedOut;
            KillFade();
            _fullyFadedOut = false;
            IsActive = false;
            _breath.Hide();
            // 退出立刻关掉扭曲，避免淡出那一秒还在分配全屏 RT。
            ClearOilWarp();
            StartFade(_currentAlpha, 1f, FadeInDuration);
            ArmCountdown();
            if (wasImmersive)
            {
                IslandCameraSrpSetup.ApplyTargetFrameRate();
                IslandAssetReclaimScheduler.Cancel();
                Log.Info("[IslandIdleImmersive] exit fps=" + IslandCameraSrpSetup.DefaultFrameRate);
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
            IslandCameraSrpSetup.ApplyTargetFrameRate();
            IslandAssetReclaimScheduler.Begin();
            Log.Info("[IslandIdleImmersive] enter fps=" + IslandCameraSrpSetup.ImmersiveFrameRate);
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
