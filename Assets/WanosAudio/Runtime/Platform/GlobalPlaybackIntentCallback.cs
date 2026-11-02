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

        public static void ClearHandlers()
        {
            _requestPauseAll = null;
            _requestResumeAll = null;
            _mediaTrackChanged = null;
            _systemAudioInterrupted = null;
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
    }
}
