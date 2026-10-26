using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Boluo InGameControlView ShowHideIcon：打开天气/音乐/音量等面板时隐藏底部
    /// 预设 + 健康入口；引用计数，全部关闭后才恢复（避免连点/互切错乱）。
    /// </summary>
    public static class HudBottomIcons
    {
        static int _hideCount;
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

        /// <summary>HUD torn down (IslandMainForm close / shutdown).</summary>
        public static void Reset()
        {
            _hideCount = 0;
            _apply?.Invoke(true);
        }
    }
}
