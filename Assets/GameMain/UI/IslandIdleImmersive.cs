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

        /// <summary>HUD 已完全淡出；供相机压 renderScale 等查询。</summary>
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
                return;
            }

            if (_fullyFadedOut)
            {
                if (pointerPressedThisFrame)
                    ExitImmersive();
                else
                    _breath.Tick();
                TickFade();
                return;
            }

            if (pointerPressedThisFrame || locomotionActive)
                RegisterActivity();

            TickFade();

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
            StartFade(_currentAlpha, 1f, FadeInDuration);
            ArmCountdown();
            if (wasImmersive)
                Log.Info("[IslandIdleImmersive] exit");
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
            Log.Info("[IslandIdleImmersive] enter");
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
    }
}
