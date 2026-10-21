using System.Runtime.InteropServices;
using UnityEngine;

namespace WanosAudio
{
    /// <summary>
    /// Wanos 薄平台 AVAudioSession / 路径原生门面（AOT 随包）。
    /// GamePlay handler 只调本门面，禁止在热更集内散落 DllImport。
    /// 岛内会话播放总闸：SilenceForLogout=Disarm；ArmSessionForIslandPlayback=Arm。
    /// 与 userManuallyPaused（真实用户暂停）正交，勿混用。
    /// </summary>
    public static class BoluoAudioSessionNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Lib = "__Internal";

        [DllImport(Lib)]
        private static extern int BoluoAudioSession_IsHostForeground();

        [DllImport(Lib)]
        private static extern void BoluoAudioSession_LockPlayback();

        [DllImport(Lib)]
        private static extern void BoluoAudioSession_RestorePlayback();

        [DllImport(Lib)]
        private static extern void BoluoAudioSession_RestorePlaybackAfterHudReady();

        [DllImport(Lib)]
        private static extern void BoluoAudioSession_ArmSessionForIslandPlayback();

        [DllImport(Lib)]
        private static extern void BoluoAudioSession_PrepareAfterLogin();

        [DllImport(Lib)]
        private static extern void BoluoAudioSession_SilenceForLogout();

        [DllImport(Lib)]
        private static extern int BoluoAudioSession_IsLogoutSilenced();

        [DllImport(Lib)]
        private static extern void BoluoAudioSession_AppendTrail(string msg);

        [DllImport(Lib)]
        private static extern void WanosMusicPlayer_NativeSetPaths(string persistent, string streaming);
#endif
        /// <summary>
        /// 追加音频诊断行。统一过滤字段 <c>[BoluoAudioDbg]</c>（iOS trail 文件 + console；其它平台 Debug.Log）。
        /// </summary>
        public static void AppendTrail(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;
            // 调用方勿再自带 [BoluoAudioDbg]；原生落盘会统一加前缀。
            if (msg.StartsWith("[BoluoAudioDbg] "))
                msg = msg.Substring("[BoluoAudioDbg] ".Length);
#if UNITY_IOS && !UNITY_EDITOR
            try { BoluoAudioSession_AppendTrail(msg); }
            catch { /* 未强链时忽略 */ }
#else
            UnityEngine.Debug.Log($"[BoluoAudioDbg] {msg}");
#endif
        }
        /// <summary>
        /// 宿主场景前台态。iOS 真机读 .mm；Editor / 非 iOS 回退 Application.isFocused。
        /// </summary>
        public static bool IsHostForeground
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return BoluoAudioSession_IsHostForeground() != 0;
#else
                return Application.isFocused;
#endif
            }
        }

        public static void LockPlayback()
        {
#if UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSession_LockPlayback();
#endif
        }

        public static void RestorePlayback()
        {
#if UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSession_RestorePlayback();
#endif
        }

        /// <summary>HUD 就绪后延迟恢复（0.3s）；Disarmed 时 no-op。</summary>
        public static void RestoreAfterHudReady()
        {
#if UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSession_RestorePlaybackAfterHudReady();
#endif
        }

        /// <summary>
        /// 会话唯一 Arm：开闸 + 重置播放意图（+ 退登后 Rebuild）。
        /// 仅 DelayPlayMusic 首播前调用；Enter* 不得调用。
        /// </summary>
        public static void ArmSessionForIslandPlayback()
        {
#if UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSession_ArmSessionForIslandPlayback();
#endif
        }

        /// <summary>兼容旧名：原生侧已 no-op，请用 <see cref="ArmSessionForIslandPlayback"/>。</summary>
        public static void PrepareAfterLogin()
        {
#if UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSession_PrepareAfterLogin();
#endif
        }

        /// <summary>会话 Disarm：关闸 + 停播清空（由 Unity 驱动，不经 Swift）。</summary>
        public static void SilenceForLogout()
        {
#if UNITY_IOS && !UNITY_EDITOR
            BoluoAudioSession_SilenceForLogout();
#endif
        }

        /// <summary>会话是否 Disarmed（登录页禁止 unmute / Resume）。</summary>
        public static bool IsLogoutSilenced
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return BoluoAudioSession_IsLogoutSilenced() != 0;
#else
                return false;
#endif
            }
        }

        public static void NativeSetPaths(string persistent, string streaming)
        {
#if UNITY_IOS && !UNITY_EDITOR
            WanosMusicPlayer_NativeSetPaths(persistent, streaming);
#endif
        }
    }
}
