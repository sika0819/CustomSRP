using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 铺装进度监视。不改 PlayerLoop：拿掉 Present 之后下一帧照样不回来。
    /// </summary>
    public static class PrefillFrameGate
    {
        static Thread _watch;
        static volatile bool _watchRun;
        static string _watchPath;

        public static int Done;
        public static int Phase;

        public static void Begin()
        {
            Done = 0;
            Phase = 1;
            StartWatch();
        }

        public static void End()
        {
            Phase = 0;
            StopWatch();
        }

        static void StartWatch()
        {
            StopWatch();
            _watchPath = Path.Combine(Application.persistentDataPath, "prefill_watch.txt");
            _watchRun = true;
            _watch = new Thread(Watch)
            {
                IsBackground = true,
                Name = "PrefillWatch",
            };
            _watch.Start();
        }

        static void StopWatch()
        {
            _watchRun = false;
            _watch = null;
        }

        static void Watch()
        {
            int lastDone = -1;
            int still = 0;
            while (_watchRun)
            {
                Thread.Sleep(1000);
                if (!_watchRun)
                    break;
                int done = Done;
                int phase = Phase;
                if (done == lastDone)
                    still++;
                else
                {
                    still = 0;
                    lastDone = done;
                }

                if (still < 2)
                    continue;

                string line = DateTime.Now.ToString("HH:mm:ss.fff")
                    + " done=" + done + " phase=" + phase + " still=" + still;
                try
                {
                    File.AppendAllText(_watchPath, line + "\n");
                }
                catch (Exception)
                {
                    // 监视线程不能再拖垮铺装。
                }

                Debug.Log("[PrefillFrameGate] stall " + line);
            }
        }
    }
}
