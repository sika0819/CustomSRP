using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// App 前后台 / 音频打断的统一恢复入口。
    /// <para>
    /// 需求：不管声音是停在哪个状态（引擎 mute / 原生焦点丢失 / 内容声被停 / 曲目被掐），
    /// 回前台（以及玩家点一下游戏场景）都应该把音乐和音效拉回来。
    /// </para>
    /// <para>
    /// 恢复分三层（仅当用户暂停、系统打断、或禁止后台播放时才走）：
    /// 1. 引擎层（<see cref="WanosEngine.RestoreForeground"/>）：解 mute + 重推音量 RTPC。
    /// 2. 平台层：Android 同步前后台态 + 抢回 AudioFocus（原生内部必要时 reset + 重播）。
    /// 3. 业务层：音乐续播 / 必要时重起当前曲目；环境声 / 天气 / 雨水强制重起。
    /// 后台一直在播时不走这三层，避免 Unmute / Resume / 重起把还在响的声音掐一下。
    /// </para>
    /// </summary>
    public sealed class AudioAppLifecycle : MonoBehaviour
    {
        static AudioAppLifecycle _instance;

        /// <summary>回到前台后需要跑一次恢复。</summary>
        static bool _needsRestore;

        /// <summary>最近一次系统前后台。启动出声前用来避免人已经切到别的播放器还 unmute。</summary>
        static bool _inBackground;

        public static bool IsInBackground => _inBackground;

        public static void EnsureExists()
        {
            if (_instance != null)
                return;
            var go = new GameObject(nameof(AudioAppLifecycle));
            _instance = go.AddComponent<AudioAppLifecycle>();
        }

        /// <summary>Disable Domain Reload 下静态字段不会自动清。</summary>
        public static void ResetSession()
        {
            _needsRestore = false;
            _inBackground = false;
        }

        /// <summary>
        /// 玩家点了游戏场景：兜底跑一次恢复。
        /// 宿主壳里 Unity 的 <c>OnApplicationFocus</c> 不一定触发（Unity 窗口不一定是 key），
        /// 一次真实点击是可靠的「我回前台了」信号。
        /// 负一屏暂停（<see cref="NowPlayingService.UserManuallyPaused"/>）也在此覆盖续播，
        /// 不挂到 focus 回调上，避免通知栏抢焦把岛内主动暂停误恢复。
        /// </summary>
        public static void OnSceneInteraction()
        {
            if (!NeedsRestore() && !NowPlayingService.UserManuallyPaused)
                return;
            RestoreForegroundAudio("scene-interaction");
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        void OnApplicationPause(bool paused)
        {
            _inBackground = paused;
            if (paused)
            {
                EnterBackground();
                return;
            }

            LeaveBackground("app-pause-resume");
        }

#if !UNITY_EDITOR
        void OnApplicationFocus(bool focused)
        {
            // 真机：焦点回来时若已武装恢复则跑一次。
            // Editor 不挂此回调——HostView 点 Console/Inspector 会疯狂 OnFocus，
            // 不能当「回前台」；Editor 靠 pause + 场景点击兜底。
            if (!focused)
                return;
            _inBackground = false;
            LeaveBackground("app-focus-gain");
        }
#endif

        /// <summary>进后台：同步原生前后台态（原生据此决定后台续播 / 压音策略）。</summary>
        static void EnterBackground()
        {
            _needsRestore = true;
            BoluoAndroidEngineNative.NotifyAppLifecycle(
                true, BackgroundPlayPolicyCallback.GetBackgroundPlayDisable());
            Log.Info("[AudioAppLifecycle] enter background — restore armed");
        }

        static void LeaveBackground(string reason)
        {
            if (!NeedsRestore())
                return;
            RestoreForegroundAudio(reason);
        }

        /// <summary>
        /// 是否需要跑一次恢复（回前台自动路径）。
        /// <para>
        /// 只认「进过后台」latch 与原生打断标记。
        /// 禁止用 <c>PlaybackState != Started</c> 当常驻条件：macOS / Editor 上该状态
        /// 经常与真实发声不同步，会导致每次焦点 / 点击都 Stop+Play 音乐与环境声。
        /// 手动暂停的点场景覆盖见 <see cref="OnSceneInteraction"/>。
        /// </para>
        /// </summary>
        static bool NeedsRestore()
        {
            if (_needsRestore)
                return true;
            if (WanosEngine.IsInterrupted)
                return true;
            return false;
        }

        /// <summary>回前台恢复：引擎 → 平台 → 业务（音乐 / 内容声）。</summary>
        public static void RestoreForegroundAudio(string reason)
        {
            if (!WanosEngine.IsLoaded)
            {
                // 引擎还没起来（不在岛内）→ 没有可恢复的东西。
                // 保留 latch，避免点场景时 NeedsRestore 已灭、再也补不回来。
                return;
            }

            // 先对账原生切歌 / 暂停，再决定要不要动正在响的声音。
            // 后台一直在播时 Unmute、Resume_Music、重起环境声都会把当前声掐一下。
            BackgroundTransportSync.ResyncFromNative();
            if (CanKeepAudibleAcrossLifecycle())
            {
                _needsRestore = false;
                BoluoAndroidEngineNative.NotifyAppLifecycle(
                    false, BackgroundPlayPolicyCallback.GetBackgroundPlayDisable());
                // 后台续播时系统负一屏已经让给对方。回来必须按播放中重推，暂停态推送不会把卡片拉回来。
                NowPlayingService.PublishPlayingForced();
                Log.Info($"[AudioAppLifecycle] foreground keep audible ({reason})");
                return;
            }

            // 先告诉原生「回到前台」，再解 mute。
            // 否则 Android 仍认为在后台，启动期未出过声会被拒绝，声音和负一屏都补不回来。
            BoluoAndroidEngineNative.NotifyAppLifecycle(
                false, BackgroundPlayPolicyCallback.GetBackgroundPlayDisable());

            // 0. 先清暂停意图：负一屏后台 pause 会把原生 userPaused=true，
            //    ReclaimFocusOnForeground / restoreAudible* 见此旗会直接 skip。
            //    需求：回前台 / 点场景覆盖暂停，必须先清再抢焦。
            NowPlayingService.NoteUserPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif

            // 1. 播放设备可能后台被系统释放 → 引擎没开就重新开一次。
            if (!WanosEngine.IsPlaybackEnabled)
                WanosEngine.EnablePlayback("app-foreground");

            // 2. 引擎层：解 mute + 重推音量 + 清打断标记。
            // 人还没真正回到前台时解 mute 会被拒绝，latch 留着，下次再试。
            if (!WanosEngine.RestoreForeground(reason))
            {
                _needsRestore = true;
                // 声音可以等下一次前台信号，负一屏不能等：人已经回到波萝，卡片要先出来。
                NowPlayingService.PublishPlayingForced();
                Log.Info($"[AudioAppLifecycle] foreground restore deferred ({reason})");
                return;
            }

            _needsRestore = false;

            // 3. 平台层：抢回 AudioFocus（原生必要时 reset + 重播）。前后台态已在解 mute 前同步。
            BoluoAndroidEngineNative.ReclaimFocusOnForeground();

            // 暂停态已在进入本方法时 Resync；清暂停意图后再盖一次，防止原生旗写回。
            NowPlayingService.NoteUserPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif

            // 4. 业务层：音乐续播 / 必要时重起当前曲目。
            (GameEntry.Music ?? MusicPlaylist.Instance)?.EnsureMusicRestored(reason);

            // 5. 业务层：环境声 / 天气 / 雨水强制重起。
            IslandAudioSession.Instance?.RestoreContentAudio(reason);

            // 6. 负一屏：必须按「播放中」推。启动期没出过卡时，暂停态推送不会拉起媒体会话，卡片就没了。
            NowPlayingService.NoteUserPaused(false);
            NowPlayingService.PublishPlayingForced();

            Log.Info($"[AudioAppLifecycle] foreground restore done ({reason})");
        }

        /// <summary>
        /// 后台续播且没有被系统打断、用户也没暂停：回前台不要碰引擎。
        /// 播放设备状态在 Unity pause/resume 间会短暂离开 Started，不能拿它当「已经停了」。
        /// </summary>
        static bool CanKeepAudibleAcrossLifecycle()
        {
            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
                return false;
            if (NowPlayingService.UserManuallyPaused)
                return false;
            if (WanosEngine.IsInterrupted)
                return false;
            if (!WanosEngine.IsPlaybackEnabled)
                return false;
            return true;
        }
    }
}
