using System;
using System.Collections.Generic;
using UnityGameFramework.Runtime;

namespace Game.Native
{
    /// <summary>N→U Push 与业务 Handler 分发（AOT）。</summary>
    public static class MessageRouter
    {
        static readonly object Gate = new object();
        static readonly Dictionary<int, Action<int, int, int>> Handlers = new Dictionary<int, Action<int, int, int>>();

        public static void Register(int type, Action<int, int, int> handler)
        {
            if (handler == null)
                return;
            lock (Gate)
                Handlers[type] = handler;
        }

        public static void Unregister(int type)
        {
            lock (Gate)
                Handlers.Remove(type);
        }

        public static void Dispatch(int type, int a, int b, int c)
        {
            Action<int, int, int> handler;
            lock (Gate)
            {
                if (!Handlers.TryGetValue(type, out handler) || handler == null)
                {
                    Log.Warning($"[IMP] unknown push type={type} a={a} b={b} c={c}");
                    return;
                }
            }

            try
            {
                handler(a, b, c);
            }
            catch (Exception e)
            {
                Log.Error($"[IMP] handler type={type} failed: {e.Message}");
            }
        }
    }
}
