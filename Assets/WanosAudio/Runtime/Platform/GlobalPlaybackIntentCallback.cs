using System;

namespace WanosAudio
{
    /// <summary>
    /// 全局 Pause / Resume 意图回调，避免 WanosAudio ↔ GameMain 程序集环依赖。
    /// GameMain 注册 <see cref="SetHandlers"/>；未注册时回退 <see cref="WanosEngine"/>。
    /// </summary>
    public static class GlobalPlaybackIntentCallback
    {
        static Action _requestPauseAll;
        static Action _requestResumeAll;
        static Action _mediaTrackChanged;
        static Action _systemAudioInterrupted;
        static Action _systemAudioRestored;
        static Action _systemDialogCover;

        public static void SetHandlers(Action requestPauseAll, Action requestResumeAll)
        {
            _requestPauseAll = requestPauseAll;
            _requestResumeAll = requestResumeAll;
        }

        public static void SetMediaTrackChangedHandler(Action onMediaTrackChanged)
        {
            _mediaTrackChanged = onMediaTrackChanged;
        }

        /// <summary>
        /// 系统 / 其它 App 抢焦点：只闩内容声，不抬用户暂停旗。
        /// 回前台由 <c>AudioAppLifecycle</c> 恢复，勿走 <see cref="RequestPauseAll"/>（会记成用户暂停）。
        /// </summary>
        public static void SetSystemAudioInterruptedHandler(Action onSystemAudioInterrupted)
        {
            _systemAudioInterrupted = onSystemAudioInterrupted;
        }

        /// <summary>原生短暂打断结束后（微信/来电/蓝牙）后台可续：清 yield 并恢复内容声。</summary>
        public static void SetSystemAudioRestoredHandler(Action onSystemAudioRestored)
        {
            _systemAudioRestored = onSystemAudioRestored;
        }

        /// <summary>系统更新弹窗：没有外来媒体，不停声，也不抢回焦点。</summary>
        public static void SetSystemDialogCoverHandler(Action onSystemDialogCover)
        {
            _systemDialogCover = onSystemDialogCover;
        }

        public static void ClearHandlers()
        {
            _requestPauseAll = null;
            _requestResumeAll = null;
            _mediaTrackChanged = null;
            _systemAudioInterrupted = null;
            _systemAudioRestored = null;
            _systemDialogCover = null;
        }

        /// <summary>锁屏/通知栏等应走此入口，勿直接 WanosEngine.PauseAll。</summary>
        public static void RequestPauseAll()
        {
            if (_requestPauseAll != null)
                _requestPauseAll.Invoke();
            else
                WanosEngine.PauseAll();
        }

        public static void RequestResumeAll()
        {
            if (_requestResumeAll != null)
                _requestResumeAll.Invoke();
            else
                WanosEngine.ResumeAll();
        }

        /// <summary>负一屏切歌后调用：抬 intend + UI，禁止走 RequestResumeAll。</summary>
        public static void NotifyMediaTrackChanged()
        {
            _mediaTrackChanged?.Invoke();
        }

        /// <summary>
        /// 原生丢焦护栏已 mute / Pause_Music 后调用：补 Pause_ALL + 闩环境/天气，禁止 Evaluate 重 Post。
        /// </summary>
        public static void NotifySystemAudioInterrupted()
        {
            if (_systemAudioInterrupted != null)
            {
                _systemAudioInterrupted.Invoke();
                return;
            }

            WanosEngine.PauseAll();
        }

        /// <summary>原生自动续播后调用：清 yield + Resume 内容声（前后台均可）。</summary>
        public static void NotifySystemAudioRestored()
        {
            if (_systemAudioRestored != null)
            {
                _systemAudioRestored.Invoke();
                return;
            }

            WanosEngine.ResumeAll();
            WanosEngine.ClearInterrupted("system-audio-restored");
        }

        /// <summary>系统弹窗盖住游戏。没有注册时什么都不做，禁止顺便 Resume。</summary>
        public static void NotifySystemDialogCover()
        {
            _systemDialogCover?.Invoke();
        }
    }
}
