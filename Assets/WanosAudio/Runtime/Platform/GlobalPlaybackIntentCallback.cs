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

        public static void SetHandlers(Action requestPauseAll, Action requestResumeAll)
        {
            _requestPauseAll = requestPauseAll;
            _requestResumeAll = requestResumeAll;
        }

        public static void SetMediaTrackChangedHandler(Action onMediaTrackChanged)
        {
            _mediaTrackChanged = onMediaTrackChanged;
        }

        public static void ClearHandlers()
        {
            _requestPauseAll = null;
            _requestResumeAll = null;
            _mediaTrackChanged = null;
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
    }
}
