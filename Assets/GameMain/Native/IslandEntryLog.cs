using UnityGameFramework.Runtime;

namespace Game.Native
{
    /// <summary>
    /// 进岛全流程日志。logcat 过滤 <c>IslandEntry</c>（与 Android 宿主同一字段）。
    /// 每行固定 <c>step</c>、<c>result</c>（ok / wait / block / fail / skip）。卡住时最后一行就是停点。
    /// <c>reason</c> 用单个词，不要空格。
    /// </summary>
    public static class IslandEntryLog
    {
        public static void Ok(string step, string reason = null, string extra = null) =>
            Log.Info(Line(step, "ok", reason, extra));

        public static void Wait(string step, string reason, string extra = null) =>
            Log.Info(Line(step, "wait", reason, extra));

        public static void Block(string step, string reason, string extra = null) =>
            Log.Warning(Line(step, "block", reason, extra));

        public static void Fail(string step, string reason, string extra = null) =>
            Log.Error(Line(step, "fail", reason, extra));

        public static void Skip(string step, string reason, string extra = null) =>
            Log.Warning(Line(step, "skip", reason, extra));

        static string Line(string step, string result, string reason, string extra)
        {
            string line = "[IslandEntry] step=" + step + " result=" + result;
            if (!string.IsNullOrEmpty(reason))
                line += " reason=" + reason;
            if (!string.IsNullOrEmpty(extra))
                line += " " + extra;
            return line;
        }
    }
}
