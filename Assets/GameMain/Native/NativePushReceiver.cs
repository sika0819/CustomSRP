using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;
using UnityGameFramework.Runtime;
using UnityEngine;

namespace Game.Native
{
    /// <summary>
    /// N→U：MonoPInvokeCallback 入队，主线程 Drain。必须在 AOT（Launcher）。
    /// </summary>
    public static class NativePushReceiver
    {
        const int MaxQueue = 256;

        public delegate void PushFn(int type, int a, int b, int c);

        static readonly ConcurrentQueue<PushMsg> Queue = new ConcurrentQueue<PushMsg>();
        static readonly object DrainGate = new object();
        static int _queued;
        static bool _registered;

        struct PushMsg
        {
            public int Type;
            public int A;
            public int B;
            public int C;
        }

#if UNITY_IOS && !UNITY_EDITOR
        const string Lib = "__Internal";
#elif UNITY_ANDROID && !UNITY_EDITOR
        // Separate from BBP libboluobridge.so
        const string Lib = "boluoimp";
#endif

#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern void BoluoImp_RegisterPush(PushFn fn);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern void BoluoImp_UnregisterPush();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            EnsureRegistered();
            BoluoNativeBridge.EnsureExists();
        }

        public static void EnsureRegistered()
        {
            if (_registered)
                return;
            _registered = true;
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
            try
            {
                BoluoImp_RegisterPush(OnNativePush);
                Log.Info("[IMP] BoluoImp_RegisterPush ok");
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] RegisterPush failed (standalone?): {e.Message}");
            }
#else
            Log.Info("[IMP] RegisterPush skipped (Editor/standalone stub)");
#endif
        }

        [MonoPInvokeCallback(typeof(PushFn))]
        static void OnNativePush(int type, int a, int b, int c)
        {
            while (Volatile.Read(ref _queued) >= MaxQueue && Queue.TryDequeue(out _))
                Interlocked.Decrement(ref _queued);

            Queue.Enqueue(new PushMsg { Type = type, A = a, B = b, C = c });
            Interlocked.Increment(ref _queued);
            // iOS 主线程就是 Unity 主线程，可以当场 Drain。
            // Android 热更（HybridCLR）仍等 BoluoNativeBridge.Update。
#if !UNITY_ANDROID || UNITY_EDITOR
            Drain();
#endif
        }

        /// <summary>Editor / 单测：模拟原生 Push。</summary>
        public static void SimulatePush(int type, int a = 0, int b = 0, int c = 0)
        {
            OnNativePush(type, a, b, c);
        }

        public static void Drain()
        {
            lock (DrainGate)
            {
                while (Queue.TryDequeue(out var m))
                {
                    Interlocked.Decrement(ref _queued);
                    MessageRouter.Dispatch(m.Type, m.A, m.B, m.C);
                }
            }
        }
    }
}
