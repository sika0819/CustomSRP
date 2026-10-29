using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 一次铺装一份进度。监视线程只看自己的会话，停掉后不会接着看下一次。
    /// 主线程卡在同步加载里时玩家循环不会再走，所以用墙钟记进度，不靠下一帧。
    /// </summary>
    public sealed class PrefillFrameGate
    {
        const double StallSeconds = 2.0;
        const double LogIntervalSeconds = 1.0;
        const int SampleMs = 250;

        static readonly object FileLock = new object();

        sealed class Session
        {
            public volatile bool Run;
            public long Packed;
            public long ChangedTicks;
            public string Path;
            public int Id;
        }

        Session _session;
        int _nextId;

        public void Begin()
        {
            End();
            var session = new Session
            {
                Run = true,
                Path = Path.Combine(Application.persistentDataPath, "prefill_watch.txt"),
                Id = ++_nextId,
                ChangedTicks = Stopwatch.GetTimestamp(),
            };
            Volatile.Write(ref session.Packed, Pack(1, 0));
            _session = session;
            var thread = new Thread(() => Watch(session))
            {
                IsBackground = true,
                Name = "PrefillWatch",
            };
            thread.Start();
        }

        public void End()
        {
            Session session = _session;
            _session = null;
            if (session != null)
                session.Run = false;
        }

        /// <summary>phase 与 done 一次写完。done 变化才刷新墙钟，只改 phase 不算有进度。</summary>
        public void Mark(int phase, int done)
        {
            Session session = _session;
            if (session == null || !session.Run)
                return;

            long prev = Volatile.Read(ref session.Packed);
            if ((int)prev != done)
                Volatile.Write(ref session.ChangedTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref session.Packed, Pack(phase, done));
        }

        static long Pack(int phase, int done)
        {
            return ((long)phase << 32) | (uint)done;
        }

        static void Watch(Session session)
        {
            long lastChanged = Volatile.Read(ref session.ChangedTicks);
            long lastLog = 0;
            while (session.Run)
            {
                Thread.Sleep(SampleMs);
                if (!session.Run)
                    break;

                long packed = Volatile.Read(ref session.Packed);
                long changed = Volatile.Read(ref session.ChangedTicks);
                if (changed != lastChanged)
                {
                    lastChanged = changed;
                    lastLog = 0;
                }

                long now = Stopwatch.GetTimestamp();
                double still = Seconds(changed, now);
                if (still < StallSeconds)
                    continue;
                if (lastLog != 0 && Seconds(lastLog, now) < LogIntervalSeconds)
                    continue;
                if (!session.Run)
                    break;

                lastLog = now;
                int phase = (int)(packed >> 32);
                int done = (int)packed;
                string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " s=" + session.Id
                    + " done=" + done
                    + " phase=" + phase
                    + " still=" + still.ToString("0.0", CultureInfo.InvariantCulture);
                try
                {
                    lock (FileLock)
                        File.AppendAllText(session.Path, line + "\n");
                }
                catch (Exception)
                {
                    // 监视线程不能再拖垮铺装。
                }

                UnityEngine.Debug.Log("[PrefillFrameGate] stall " + line);
            }
        }

        static double Seconds(long from, long to)
        {
            return (to - from) / (double)Stopwatch.Frequency;
        }
    }
}
