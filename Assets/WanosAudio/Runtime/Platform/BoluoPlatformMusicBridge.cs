namespace WanosAudio
{
    /// <summary>
    /// 锁屏 / 后台曲终自动下一首：Unity 被 pause 时 PlayerLoop 不 Pump，
    /// <see cref="WanosEventCallbackQueue"/> 的 Ended 须在原生回调线程直接交给平台层。
    /// </summary>
    public static class BoluoPlatformMusicBridge
    {
        /// <summary>
        /// 后台且循环/随机时由原生切下一首。
        /// 返回 true = 已处理，C# 勿再入队 / Advance。
        /// </summary>
        public static bool TryHandleBackgroundTrackEnded(ulong instanceId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return BoluoAndroidEngineNative.TryHandleBackgroundMusicEnded(instanceId);
#elif UNITY_IOS && !UNITY_EDITOR
            return BoluoAudioSessionNative.TryHandleBackgroundMusicEnded(instanceId);
#else
            return false;
#endif
        }

        /// <summary>
        /// Unity 起播 BGM 后把 voice 记到原生，便于后台 Ended 识别与 keepPlaying 活实例判断。
        /// </summary>
        public static void NoteUnityMusicVoice(ulong instanceId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.NoteUnityMusicVoice(instanceId);
#elif UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSessionNative.NoteUnityMusicVoice(instanceId);
#endif
        }

        /// <summary>停曲 / 切曲前清掉原生侧 Unity voice 记账。</summary>
        public static void ClearUnityMusicVoice()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.NoteUnityMusicVoice(0);
#elif UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSessionNative.NoteUnityMusicVoice(0);
#endif
        }

        /// <summary>
        /// 同步当前曲与播放模式到 iOS 原生 playlist（后台 Ended 自动下一首靠这份索引）。
        /// Android 走 BackgroundTransportSync，此处 no-op。
        /// </summary>
        public static void NoteUnityMusicContext(int playMode, int albumIndex, int trackIndex)
        {
#if UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSessionNative.NoteUnityMusicContext(playMode, albumIndex, trackIndex);
#else
            _ = playMode;
            _ = albumIndex;
            _ = trackIndex;
#endif
        }
    }
}
