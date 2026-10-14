using System;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.Native
{
    /// <summary>原生定时进度。</summary>
    public static class NativeTimerService
    {
        const int DefaultRingSeconds = 3600;

        public static int LastRemaining { get; private set; }
        public static int LastTotal { get; private set; }
        public static float FillRatio { get; private set; }

        static float _remaining;
        static DateTime? _lastUtc;

        public static void Reset()
        {
            LastRemaining = 0;
            LastTotal = 0;
            FillRatio = 0f;
            _remaining = 0f;
            _lastUtc = null;
        }

        public static void ApplyProgress(int remainingSeconds, int totalSeconds)
        {
            LastRemaining = remainingSeconds;
            LastTotal = totalSeconds;
            if (remainingSeconds <= 0)
            {
                _remaining = 0f;
                _lastUtc = null;
            }
            else
            {
                _remaining = remainingSeconds;
                _lastUtc = DateTime.UtcNow;
            }

            if (remainingSeconds <= 0 && totalSeconds < 0)
            {
                Game.MusicService music = Game.MusicService.Ensure();
                music?.Pause();
            }

            RefreshFill();
            if (Game.GameEntry.Event != null)
            {
                Game.GameEntry.Event.Fire(null, NativeTimerEventArgs.Create(remainingSeconds, totalSeconds));
            }
        }

        public static void AttachTo(GameObject host)
        {
            if (host == null)
                return;
            if (host.GetComponent<NativeTimerTicker>() == null)
                host.AddComponent<NativeTimerTicker>();
        }

        internal static void Tick()
        {
            if (_remaining <= 0f || !_lastUtc.HasValue)
                return;

            var now = DateTime.UtcNow;
            _remaining -= (float)(now - _lastUtc.Value).TotalSeconds;
            _lastUtc = now;
            if (_remaining <= 0f)
            {
                _remaining = 0f;
                _lastUtc = null;
            }

            float prevFill = FillRatio;
            RefreshFill();
            int remaining = Mathf.Max(0, Mathf.CeilToInt(_remaining));
            if (remaining == LastRemaining && Mathf.Abs(FillRatio - prevFill) < 0.0005f)
                return;
            LastRemaining = remaining;
            if (Game.GameEntry.Event != null)
                Game.GameEntry.Event.Fire(null, NativeTimerEventArgs.Create(LastRemaining, LastTotal));
        }

        static void RefreshFill()
        {
            float ring = LastTotal > 0 ? LastTotal : DefaultRingSeconds;
            FillRatio = Mathf.Clamp01(_remaining / ring);
        }
    }

    sealed class NativeTimerTicker : MonoBehaviour
    {
        void LateUpdate() => NativeTimerService.Tick();
    }
}
