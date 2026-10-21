using System;

namespace WanosAudio
{
    /// <summary>WanosAudio → GameMain 切歌桥（避免 asmdef 环依赖）。</summary>
    public static class NowPlayingSkipBridge
    {
        static Action _next;
        static Action _prev;

        public static void SetHandlers(Action next, Action prev)
        {
            _next = next;
            _prev = prev;
        }

        public static void Clear()
        {
            _next = null;
            _prev = null;
        }

        public static void RequestNext() => _next?.Invoke();
        public static void RequestPrev() => _prev?.Invoke();
    }
}
