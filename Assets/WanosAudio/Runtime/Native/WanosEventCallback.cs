using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AOT;

namespace WanosAudio
{
    /// <summary>
    /// Native <c>PostEvent</c> per-instance state payload (align Boluo <c>EventCallbackInfo</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct WanosEventCallbackInfo
    {
        public ulong InstanceId;
        public int State;
    }

    /// <summary>WIAEngine instance states used by music Break / Ended auto-next.</summary>
    public static class WanosInstanceState
    {
        public const int Prepared = 0;
        public const int Playing = 0x03e3;
        public const int Ended = 0x03e5;
    }

    /// <summary>
    /// Marshals native voice callbacks onto the main thread (align Boluo
    /// <c>WanosInstanceCallBackQueue</c>). Pump from <see cref="WanosInteraction"/> LateUpdate.
    /// </summary>
    public static class WanosEventCallbackQueue
    {
        public delegate void NativeEntry(ref WanosEventCallbackInfo info);

        static readonly ConcurrentQueue<WanosEventCallbackInfo> Queue = new ConcurrentQueue<WanosEventCallbackInfo>();

        /// <summary>Pinned for the native PostEvent lifetime — do not recreate.</summary>
        public static readonly NativeEntry Entry = OnNative;

        /// <summary>Main-thread: (instanceId, state) where state is <see cref="WanosInstanceState"/>.</summary>
        public static event Action<ulong, int> StateChanged;

        [MonoPInvokeCallback(typeof(NativeEntry))]
        static void OnNative(ref WanosEventCallbackInfo info)
        {
            // 锁屏 / 后台：Unity PlayerLoop 不 Pump，Ended→Next 会丢。
            // 曲终在回调线程交给原生切下一首，不再入队（避免回前台 Pump 再 Advance 一次）。
            if (info.State == WanosInstanceState.Ended
                && BoluoPlatformMusicBridge.TryHandleBackgroundTrackEnded(info.InstanceId))
                return;

            Queue.Enqueue(info);
        }

        public static void Pump(int maxPerFrame = 16)
        {
            int processed = 0;
            while (processed < maxPerFrame && Queue.TryDequeue(out WanosEventCallbackInfo info))
            {
                StateChanged?.Invoke(info.InstanceId, info.State);
                processed++;
            }
        }

        public static void ResetSession()
        {
            while (Queue.TryDequeue(out _))
            {
            }

            StateChanged = null;
        }
    }
}
