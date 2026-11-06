using System;
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
        /// 通知权限刚放开后重刷 FGS MediaStyle（权限关闭期间 startForeground 的通知会被系统压掉）。
        /// </summary>
        public static void RefreshMediaNotification()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WIAEngineJNIClass))
                    jc.CallStatic("refreshMediaNotification");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] refreshMediaNotification failed: {ex.Message}");
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
        /// 清退登/离岛 teardown 钉住的 shouldStop+userPaused。
        /// Arm / InitializeAfterLogin 必须对称调用，否则上滑 Recents 会误走 unity_pause_preview。
        /// </summary>
        public static void ClearLogoutStopLatch()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("clearLogoutStopLatch");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] clearLogoutStopLatch failed: {ex.Message}");
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

        /// <summary>
        /// 只同步 Java 会话的到点闩，不 Pause、不 SetMute。
        /// Unity 醒着时停播仍由 <c>StopPaired</c> 完成；传 false 用于弹窗续播和离岛。
        /// </summary>
        public static void NotePausedForTimerEnd(bool paused)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("notePausedForTimerEnd", paused);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] notePausedForTimerEnd failed: {ex.Message}");
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

        /// <summary>Unity Playing / 超时 settle：清原生 skipInFlight，放行下一笔负一屏 skip。</summary>
        public static void NoteSkipSettled()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("noteSkipSettled");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] noteSkipSettled failed: {ex.Message}");
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

        /// <summary>系统更新等弹窗盖住游戏、且没有外来音源。进后台时不要因此停声。</summary>
        public static bool IsSystemDialogCover()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("isSystemDialogCover");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] isSystemDialogCover failed: {ex.Message}");
            }
#endif
            return false;
        }

        /// <summary>进后台读到系统弹窗标志后清掉，避免下一次按 Home 仍 KeepPlaying。</summary>
        public static void ClearSystemDialogCover()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("clearSystemDialogCover");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] clearSystemDialogCover failed: {ex.Message}");
            }
#endif
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
        /// 前台可以出声。启动期进程已不在前台，或对方媒体正在播：保持静音。
        /// </summary>
        public static bool AllowUnmuteNow()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("allowUnmuteNow");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] allowUnmuteNow failed: {ex.Message}");
            }
            return true;
#else
            return true;
#endif
        }

        public static void NoteUnityAudible()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("noteUnityAudible");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] noteUnityAudible failed: {ex.Message}");
            }
#endif
        }

        /// <summary>负一屏已写成播放中。原生开始盯对方播放器，切走时收起占位卡。</summary>
        public static void NoteForegroundCardPublished()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("noteForegroundCardPublished");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] noteForegroundCardPublished failed: {ex.Message}");
            }
#endif
        }

        public static void NoteForegroundCardCleared()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("noteForegroundCardCleared");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] noteForegroundCardCleared failed: {ex.Message}");
            }
#endif
        }

        public static void NoteUnitySilent()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("noteUnitySilent");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] noteUnitySilent failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// 揭罩出声前同步抢 AudioFocus。已在后台或被拒时保持 mute。
        /// </summary>
        public static bool TryArmFocusBeforeAudible()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("armFocusBeforeAudible");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] armFocusBeforeAudible failed: {ex.Message}");
            }
            return true;
#else
            return true;
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
        /// 回前台 Unity 与原生都会补 Resume_ALL / Resume_Music。
        /// 同一事件在短窗口内只允许发一次，第二发打在已 Started 的曲子上会把曲子打成 Ended。
        /// </summary>
        /// <returns>true：这次应该真正 Post。false：窗口内已经发过。</returns>
        public static bool ClaimVoiceResume(string eventName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("claimVoiceResume", eventName ?? "");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] claimVoiceResume failed: {ex.Message}");
            }
#endif
            return true;
        }

        /// <summary>
        /// 回前台抢回 AudioFocus（对齐 iOS becomeActive reclaim）。
        /// 原生内部会尊重 userPaused / 后台续播态，必要时做 reset + 重播。
        /// </summary>
        public static void ReclaimFocusOnForeground()
        {
            ReclaimFocusOnForeground(false);
        }

        /// <param name="userGesture">滑动 / 播放键。微信发语音取消的自动回前台必须传 false。</param>
        public static void ReclaimFocusOnForeground(bool userGesture)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("reclaimFocusOnForeground", userGesture);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] reclaimFocusOnForeground failed: {ex.Message}");
            }
#endif
        }

        /// <summary>微信发语音取消后回到波萝：保持暂停，显示音频打断，滑动再续。</summary>
        public static bool ShouldHoldWeChatInterruptOnReturn()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("shouldHoldWeChatInterruptOnReturn");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[BoluoAndroidEngineNative] shouldHoldWeChatInterruptOnReturn failed: {ex.Message}");
            }
#endif
            return false;
        }

        /// <summary>
        /// 原生是否仍处丢焦 yield。陈旧 OnCompetingAudioMuted 若已 Restored 必须丢弃。
        /// </summary>
        public static bool IsFocusYieldLatched()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("isFocusYieldLatched");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] isFocusYieldLatched failed: {ex.Message}");
            }
#endif
            return true;
        }

        /// <summary>
        /// 原生仍因丢焦 mute / Pause 着：回前台不能走「后台一直在播」短路，
        /// 必须 Resume 音乐 + 声景。
        /// </summary>
        public static bool NeedsForegroundVoiceRestore()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    if (obj != null)
                        return obj.Call<bool>("needsForegroundVoiceRestore");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] needsForegroundVoiceRestore failed: {ex.Message}");
            }
#endif
            return false;
        }

        /// <summary>Unity 起播 BGM 后登记 voice，供后台 Ended / keepPlaying 识别。</summary>
        public static void NoteUnityMusicVoice(ulong instanceId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // 主线程预热 GlobalRef：锁屏曲终回调在引擎线程，FindClass 不可靠。
            WarmBackgroundEndedJni();
            try
            {
                using (var jc = new AndroidJavaClass(WanosMusicPlayerNativeClass))
                {
                    var obj = jc.CallStatic<AndroidJavaObject>("getInstance");
                    obj?.Call("noteUnityMusicVoice", (long)instanceId);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] noteUnityMusicVoice failed: {ex.Message}");
            }
#endif
        }

        /// <summary>主线程缓存 jclass/jmethod，供引擎回调线程 InvokeAttached 使用。</summary>
        public static void WarmBackgroundEndedJni()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureBackgroundEndedJni();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        const string WanosMusicPlayerNativeJniPath = "com/wanos/app/health/WanosMusicPlayerNative";
        static IntPtr sBgEndedClass = IntPtr.Zero;
        static IntPtr sTryHandleBgEndedMethod = IntPtr.Zero;
        static readonly object sBgEndedJniLock = new object();

        static bool EnsureBackgroundEndedJni()
        {
            if (sTryHandleBgEndedMethod != IntPtr.Zero && sBgEndedClass != IntPtr.Zero)
                return true;
            lock (sBgEndedJniLock)
            {
                if (sTryHandleBgEndedMethod != IntPtr.Zero && sBgEndedClass != IntPtr.Zero)
                    return true;
                try
                {
                    IntPtr local = AndroidJNI.FindClass(WanosMusicPlayerNativeJniPath);
                    if (local == IntPtr.Zero)
                        return false;
                    if (sBgEndedClass != IntPtr.Zero)
                        AndroidJNI.DeleteGlobalRef(sBgEndedClass);
                    sBgEndedClass = AndroidJNI.NewGlobalRef(local);
                    AndroidJNI.DeleteLocalRef(local);
                    sTryHandleBgEndedMethod = AndroidJNI.GetStaticMethodID(
                        sBgEndedClass, "tryHandleBackgroundMusicEndedStatic", "(J)Z");
                    return sTryHandleBgEndedMethod != IntPtr.Zero;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BoluoAndroidEngineNative] WarmBackgroundEndedJni failed: {ex.Message}");
                    return false;
                }
            }
        }
#endif

        /// <summary>
        /// 锁屏曲终：原生按传输计划切下一首（循环/随机）。
        /// true = 已处理，C# 勿再 Advance。
        /// <para>
        /// 必须可在引擎回调线程调用：Unity pause 后 PlayerLoop 不转，
        /// AndroidJavaObject 不可用，走 <see cref="AndroidJNI.InvokeAttached"/> + 预热 GlobalRef。
        /// </para>
        /// </summary>
        public static bool TryHandleBackgroundMusicEnded(ulong instanceId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bool handled = false;
            try
            {
                AndroidJNI.InvokeAttached(() =>
                {
                    if (!EnsureBackgroundEndedJni())
                        return;

                    var args = new jvalue[1];
                    args[0].j = unchecked((long)instanceId);
                    handled = AndroidJNI.CallStaticBooleanMethod(
                        sBgEndedClass, sTryHandleBgEndedMethod, args);
                    // 计划未就绪时 Java 已写 pendingUnityMusicEndedId，observe 会再试；
                    // 前台 !appInBackground 不写 pending，避免误切。
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BoluoAndroidEngineNative] tryHandleBackgroundMusicEnded failed: {ex.Message}");
            }

            return handled;
#else
            return false;
#endif
        }
    }
}
