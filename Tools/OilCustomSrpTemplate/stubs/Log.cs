using UnityEngine;

namespace UnityGameFramework.Runtime
{
    /// <summary>
    /// Minimal Log facade for the oil CustomSRP Hub template (no GameFramework package).
    /// </summary>
    public static class Log
    {
        public static void Debug(object message) => UnityEngine.Debug.Log(message);
        public static void Debug(string message) => UnityEngine.Debug.Log(message);

        public static void Info(object message) => UnityEngine.Debug.Log(message);
        public static void Info(string message) => UnityEngine.Debug.Log(message);

        public static void Warning(object message) => UnityEngine.Debug.LogWarning(message);
        public static void Warning(string message) => UnityEngine.Debug.LogWarning(message);

        public static void Error(object message) => UnityEngine.Debug.LogError(message);
        public static void Error(string message) => UnityEngine.Debug.LogError(message);
    }
}
