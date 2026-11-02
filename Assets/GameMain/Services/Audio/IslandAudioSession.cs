using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// 岛屿内容声（环境 / 天气 / 雨水 / 闪避）。挂在岛屿场景里，卸载时 <see cref="OnDestroy"/> 停播并放开 bank。
    /// </summary>
    public sealed class IslandAudioSession : MonoBehaviour
    {
        public static IslandAudioSession Instance { get; private set; }

        /// <summary>没有会话时当作满音量，避免 Launch 上的播放列表把闪避钉死。</summary>
        public static float DuckLevel01 =>
            Instance != null && Instance.Duck != null ? Instance.Duck.Level01 : 1f;

        public AmbienceZoneService Ambience { get; private set; }
        public WeatherAudioBridge Weather { get; private set; }
        public RainWaterProximityService Rain { get; private set; }
        public ContentAudioDuck Duck { get; private set; }

        /// <summary>
        /// 不序列化。热重载会留下这个 bool，但 <see cref="Rain"/> 等普通对象不会回来，
        /// <see cref="OnDestroy"/> 若仍走已启动分支就会空引用。
        /// </summary>
        [System.NonSerialized]
        bool _started;

        [System.NonSerialized]
        bool _contentAudioPaused;

        /// <summary>
        /// 启动期离开，或其它播放器拿走焦点。实例还没 Start 也先记下，
        /// 避免 Pause_ALL 之后环境声才 Post，音乐停了声景还在响。
        /// </summary>
        static bool _yieldHold;

        /// <summary>已经在前台成功出过声。此前切走要连声景一起停。</summary>
        static bool _audibleCommitted;

        public bool IsStarted => _started;

        /// <summary>系统丢焦 / 媒体暂停闩住内容声时为 true；回前台必须走 <see cref="RestoreContentAudio"/>。</summary>
        public bool IsContentAudioPaused => _contentAudioPaused;

        /// <summary>声景必须保持停止，直到回前台或用户明确续播。</summary>
        public static bool IsYieldHeld => _yieldHold;

        public static bool AudibleCommitted => _audibleCommitted;

        /// <summary>前台解 mute 成功：之后的普通后台续播不再把声景当启动期让出。</summary>
        public static void NoteAudibleCommitted() => _audibleCommitted = true;

        public static void ClearYieldHold() => _yieldHold = false;

        public static void ResetStartupGate()
        {
            _yieldHold = false;
            _audibleCommitted = false;
        }

        /// <summary>
        /// 音乐已因丢焦 / 启动期让出而暂停时，声景走同一道闸。
        /// Pause_ALL 盖不住随后才 Post 的 3D，所以要停掉已播并禁止再 Evaluate。
        /// </summary>
        public static void HoldYield(string reason)
        {
            bool already = _yieldHold;
            _yieldHold = true;
            WanosEngine.MarkInterrupted(reason);
            if (!already)
            {
                try
                {
                    WanosEngine.PauseAll();
                }
                catch (System.Exception e)
                {
                    Log.Warning($"[AudioInteraction] yield Pause_ALL: {e.Message}");
                }

                WanosEngine.KeepSilent(reason);
            }

            Instance?.SetContentAudioPaused(true);
            Log.Info($"[AudioInteraction] yield hold ({reason})");
        }

        public static IslandAudioSession Ensure()
        {
            IslandAudioSession session = IslandServiceHost.Ensure(Instance, "IslandAudioSession");
            Instance = session;
            return session;
        }

        void Awake()
        {
            Instance = this;
            // 服务是这次 Awake 新建的，旧的已启动标记不能沿用。
            _started = false;
            EnsureServices();
        }

        void EnsureServices()
        {
            Duck ??= new ContentAudioDuck(this);
            Ambience ??= new AmbienceZoneService(this);
            Weather ??= new WeatherAudioBridge(this);
            Rain ??= new RainWaterProximityService(this);
        }

        /// <summary>热重载或关机：有会话才停。</summary>
        public static void ResetSession()
        {
            ResetStartupGate();
            Instance?.Shutdown();
        }

        /// <summary>揭罩并且引擎允许播放之后调用。</summary>
        public void StartAfterUncover()
        {
            if (_started)
                return;

            EnsureServices();
            WanosInteraction.EnsureLogicalObjects();
            WanosInteraction.StartListenerFollow();

            // 让出已经发生（人或焦点比 Start 更早）：先闩再 Start，天气 Start 里的 Apply 不会出声。
            if (_yieldHold)
                SetContentAudioPaused(true);

            Ambience.Start();
            Weather.Start();
            Rain.Start();
            _started = true;
            if (_yieldHold)
                SetContentAudioPaused(true);
            Log.Info("[AudioInteraction] started after uncover hold=" + (_yieldHold ? 1 : 0));
        }

        /// <summary>
        /// 可重入。离岛先调一次（BeginLeave），ChangeState→OnLeave 再调一次；
        /// 场景 <see cref="OnDestroy"/> 也可能再调。子服务 Shutdown 不得抛，否则会打断
        /// ChangeState&lt;ProcedureGuestLogin&gt;，退登后再登录进不了岛。
        /// </summary>
        public void Shutdown()
        {
            Duck?.CancelDrive();
            WanosInteraction.StopListenerFollow();

            bool wasStarted = _started;
            _started = false;
            _contentAudioPaused = false;

            try { Rain?.Shutdown(); }
            catch (System.Exception e) { Log.Warning($"[AudioInteraction] Rain.Shutdown: {e.Message}"); }
            try { Weather?.Shutdown(); }
            catch (System.Exception e) { Log.Warning($"[AudioInteraction] Weather.Shutdown: {e.Message}"); }
            try { Ambience?.Shutdown(); }
            catch (System.Exception e) { Log.Warning($"[AudioInteraction] Ambience.Shutdown: {e.Message}"); }

            if (wasStarted)
                Log.Info("[AudioInteraction] shutdown");
        }

        /// <summary>Pause/resume content ambience + weather with BGM media pause.</summary>
        public void SetContentAudioPaused(bool paused)
        {
            _contentAudioPaused = paused;
            Ambience.SetContentAudioPaused(paused);
            Weather.SetContentAudioPaused(paused);
            Rain.SetContentAudioPaused(paused);
        }

        /// <summary>
        /// Immediately stop all content 3D / weather loops (preset switch start).
        /// Does not touch the BGM content-pause latch.
        /// </summary>
        public void ClearContent3D()
        {
            Ambience.ClearAllPlaying();
            Rain.ClearPlaying();
            Weather.ClearPlaying();
        }

        /// <summary>
        /// 回前台 / 打断后恢复内容声（环境声 / 天气 / 雨水）。
        /// 只续一次：暂停时环境声和雨水已经 Stop，解开暂停会重新 Post。
        /// 天气循环只被 Pause_ALL 暂停，Resume_ALL 能续上。
        /// 不要再 Stop 一遍重播，否则刚出声会被掐掉，听感就是回前台停一拍。
        /// </summary>
        public void RestoreContentAudio(string reason)
        {
            ClearYieldHold();
            if (!_started)
                return;

            try
            {
                WanosEngine.ResumeAll();
            }
            catch (System.Exception e)
            {
                Log.Warning($"[AudioInteraction] Resume_ALL: {e.Message}");
            }

            SetContentAudioPaused(false);
            Weather.ReapplyDuckedRtpcs();
            Weather.Refresh();

            Log.Info($"[AudioInteraction] content audio restored ({reason})");
        }

        /// <summary>
        /// After preset teleport: re-evaluate ambience / rain-water / weather from the current listener.
        /// No-op while content audio is paused for BGM.
        /// </summary>
        public void RetriggerContentAtListener()
        {
            if (!_started)
                return;

            Ambience.EvaluateNow();
            Weather.Refresh();
        }

        void OnDestroy()
        {
            Shutdown();
            if (Instance == this)
                Instance = null;
        }
    }
}
