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

        /// <summary>
        /// 播放会话闸。false：拆除期拒绝 NowPlaying / 停 FGS；进岛 Arm 再置 true。
        /// </summary>
        public static void SetPlaybackActive(bool active)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WIAEngineJNIClass))
                    jc.CallStatic("setPlaybackActive", active);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] setPlaybackActive failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// 退登拆除：撤 native 排队 NowPlaying、关闸、停 FGS（须在 LoadScene Login 前）。
        /// </summary>
        public static void TeardownForLogout()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("teardownForLogout");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] teardownForLogout failed: {ex.Message}");
                SetPlaybackActive(false);
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

        /// <summary>
        /// Unity PostEvent 起播后补抢 AudioFocus + unmute（对齐旧 nativePlayTrackAt 门禁）。
        /// </summary>
        public static void EnsureAudibleForUnityPlayback()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("ensureAudibleForUnityPlayback");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] ensureAudibleForUnityPlayback failed: {ex.Message}");
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

        /// <summary>
        /// 回前台抢回 AudioFocus（对齐 iOS becomeActive reclaim）。
        /// 原生内部会尊重 userPaused / 后台续播态，必要时做 reset + 重播。
        /// </summary>
        public static void ReclaimFocusOnForeground()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("reclaimFocusOnForeground");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] reclaimFocusOnForeground failed: {ex.Message}");
            }
#endif
        }
    }
}
