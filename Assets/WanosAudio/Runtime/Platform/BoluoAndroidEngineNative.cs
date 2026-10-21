using UnityEngine;

namespace WanosAudio
{
    /// <summary>
    /// Android 引擎 / MediaSession 原生门面。
    /// </summary>
    public static class BoluoAndroidEngineNative
    {
        const string WIAEngineJNIClass = "com.wanos.app.health.WIAEngineJNI";
        const string WanosMusicPlayerNativeClass = "com.wanos.app.health.WanosMusicPlayerNative";

        public static void UpdateNowPlayingInfo(string title, string artworkPath, bool isPlaying)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WIAEngineJNIClass))
                    jc.CallStatic("updateNowPlayingInfo", title ?? "", artworkPath ?? "", isPlaying);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] updateNowPlayingInfo failed: {ex.Message}");
            }
#endif
        }

        public static void MarkUserPausedFromUnity(bool paused)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("markUserPausedFromUnity", paused);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] markUserPausedFromUnity failed: {ex.Message}");
            }
#endif
        }

        public static bool IsUserPaused()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("isUserPaused");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] isUserPaused failed: {ex.Message}");
            }
#endif
            return false;
        }

        public static void ResumeFromUserAction()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("resumeFromUserAction");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] resumeFromUserAction failed: {ex.Message}");
            }
#endif
        }

        public static void NotifyAppLifecycle(bool inBackground, bool backgroundPlayDisabled)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("notifyAppLifecycle", inBackground, backgroundPlayDisabled);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] notifyAppLifecycle failed: {ex.Message}");
            }
#endif
        }

        public static void RestoreAudibleAfterUnityPause()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("restoreAudibleAfterUnityPause");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] restoreAudibleAfterUnityPause failed: {ex.Message}");
            }
#endif
        }
    }
}
