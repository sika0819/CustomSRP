using System;

namespace WanosAudio
{
    /// <summary>
    /// 音乐暂停 UI 回调，用于避免 WanosAudio 和 GamePlay 之间的循环依赖。
    /// 由 GamePlay 注册 Show/Hide 回调，BoluoMusicPlayer 在需要时调用。
    /// </summary>
    public static class MusicPauseUICallback
    {
        public enum TipKind
        {
            /// <summary>短提示：音频中断，滑动继续（来电/短暂中断，约 3s 消失）</summary>
            TransientInterrupt = 0,
            /// <summary>长期抢占：音频被其他应用占用</summary>
            CompetingLong = 1,
            /// <summary>
            /// 程序化暂停（定时结束 / 通知栏 PauseAll）：只切 yinyuePause 图标，不弹「音频中断」toast。
            /// </summary>
            ProgrammaticPause = 2,
        }

        private static Action _showCallback;
        private static Action _hideCallback;
        private static Action<TipKind> _showWithKindCallback;
        private static Action _blankAreaGestureCallback;

        /// <summary>最近一次 Show 的提示类型，供 InGameControlView 读文案。</summary>
        public static TipKind LastTipKind { get; private set; } = TipKind.TransientInterrupt;

        public static void SetCallbacks(Action showCallback, Action hideCallback)
        {
            _showCallback = showCallback;
            _hideCallback = hideCallback;
        }

        public static void SetShowWithKindCallback(Action<TipKind> showWithKindCallback)
        {
            _showWithKindCallback = showWithKindCallback;
        }

        /// <summary>
        /// 空白区点击/滑动恢复：由 BoluoMusicPlayer* 注册，GamePlay 摇杆空白起拖时 Notify。
        /// 勿再依赖 DragPoint/range 的 EventTrigger（range.raycastTarget 永关）。
        /// </summary>
        public static void SetBlankAreaGestureCallback(Action blankAreaGestureCallback)
        {
            _blankAreaGestureCallback = blankAreaGestureCallback;
        }

        /// <summary>岛内空白区手势（摇杆起拖/点击）：暂停提示展示中时应恢复播放。</summary>
        public static void NotifyBlankAreaGesture()
        {
            _blankAreaGestureCallback?.Invoke();
        }

        public static void Show()
        {
            Show(TipKind.TransientInterrupt);
        }

        public static void Show(TipKind kind)
        {
            LastTipKind = kind;

            // 焦点提示关闭时仍展示岛屿暂停态，文案由 InGameControlView 按开关隐藏
            if (_showWithKindCallback != null)
            {
                _showWithKindCallback.Invoke(kind);
                return;
            }

            _showCallback?.Invoke();
        }

        public static void Hide()
        {
            _hideCallback?.Invoke();
        }
    }
}
