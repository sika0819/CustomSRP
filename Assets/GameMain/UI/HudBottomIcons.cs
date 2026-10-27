using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Boluo InGameControlView ShowHideIcon：打开天气/音乐/音量等面板时隐藏底部
    /// 预设 + 健康入口；引用计数，全部关闭后才恢复（避免连点/互切错乱）。
    /// 原生定时列表经 <see cref="SyncNativeHidden"/> 边沿同步进同一套计数。
    /// </summary>
    public static class HudBottomIcons
    {
        static int _hideCount;
        static bool _nativeHideHeld;
        static Action<bool> _apply;

        public static bool AreHidden => _hideCount > 0;

        public static void Bind(Action<bool> applyVisible)
        {
            _apply = applyVisible;
            _apply?.Invoke(_hideCount == 0);
        }

        public static void Unbind(Action<bool> applyVisible)
        {
            if (_apply == applyVisible)
                _apply = null;
        }

        /// <summary>Panel opened that should hide preset / health.</summary>
        public static void PushHide()
        {
            _hideCount++;
            if (_hideCount == 1)
                _apply?.Invoke(false);
        }

        /// <summary>Matching panel closed.</summary>
        public static void PopShow()
        {
            _hideCount = Mathf.Max(0, _hideCount - 1);
            if (_hideCount == 0)
                _apply?.Invoke(true);
        }

        /// <summary>
        /// 原生 <c>bottomIconsHidden</c> 边沿：定时列表开 → Push，关 → Pop；同值幂等。
        /// </summary>
        public static void SyncNativeHidden(bool hidden)
        {
            if (hidden == _nativeHideHeld)
                return;
            _nativeHideHeld = hidden;
            if (hidden)
                PushHide();
            else
                PopShow();
        }

        /// <summary>HUD torn down (IslandMainForm close / shutdown).</summary>
        public static void Reset()
        {
            _hideCount = 0;
            _nativeHideHeld = false;
            _apply?.Invoke(true);
        }
    }
}
