using UnityEngine;

namespace WanosAudio
{
    /// <summary>
    /// Android 后台传输计划 JNI 薄封装。
    /// Unity 被 pause 时原生靠这份计划直接切歌 / 暂停；回前台 C# 用 GetStateJson 对账。
    /// </summary>
    public static class AndroidBackgroundTransport
    {
        const string WanosMusicPlayerNativeClass = "com.wanos.app.health.WanosMusicPlayerNative";

        public static void SetPlan(string json)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("setBackgroundTransportPlan", json ?? "");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AndroidBackgroundTransport] SetPlan failed: {ex.Message}");
            }
#endif
        }

        public static void ClearPlan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("clearBackgroundTransportPlan");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AndroidBackgroundTransport] ClearPlan failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// 回前台对账用。JSON：
        /// <c>{"event":"...","name":"...","paused":false,"index":1,"valid":true,"inBackground":false}</c>
        /// Editor / 非 Android 返回空串。
        /// </summary>
        public static string GetStateJson()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<string>("getBackgroundTransportState") ?? "";
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AndroidBackgroundTransport] GetStateJson failed: {ex.Message}");
            }
#endif
            return "";
        }
    }
}
