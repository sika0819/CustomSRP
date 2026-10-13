using UnityEngine;

namespace YooAsset
{
    /// <summary>启动分步打点，格式对齐 TEngine.BoluoPerfBoot，logcat 滤 BoluoPerfBoot。</summary>
    internal static class BootPerf
    {
        public static float Now => Time.realtimeSinceStartup;

        public static void Begin(string phase)
        {
            YooLogger.Log(Format("begin", phase, Now, 0f, null));
        }

        public static void End(string phase, float start, string extra = null)
        {
            float now = Now;
            YooLogger.Log(Format("end", phase, now, (now - start) * 1000f, extra));
        }

        public static void Mark(string phase, string extra = null)
        {
            YooLogger.Log(Format("mark", phase, Now, 0f, extra));
        }

        static string Format(string kind, string phase, float now, float dtMs, string extra)
        {
            int frame = Time.frameCount;
            if (string.IsNullOrEmpty(extra))
                return $"[BoluoPerfBoot] {kind}={phase} t={now:F3}s cost={dtMs:F0}ms frame={frame}";
            return $"[BoluoPerfBoot] {kind}={phase} t={now:F3}s cost={dtMs:F0}ms frame={frame} {extra}";
        }
    }
}
