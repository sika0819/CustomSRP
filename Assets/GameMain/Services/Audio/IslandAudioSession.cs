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

        public bool IsStarted => _started;

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

            Ambience.Start();
            Weather.Start();
            Rain.Start();
            _started = true;
            Log.Info("[AudioInteraction] started after uncover");
        }

        /// <summary>可重入。离岛先调一次，场景 <see cref="OnDestroy"/> 再调一次。</summary>
        public void Shutdown()
        {
            Duck?.CancelDrive();
            WanosInteraction.StopListenerFollow();

            bool wasStarted = _started;
            _started = false;

            Rain?.Shutdown();
            Weather?.Shutdown();
            Ambience?.Shutdown();

            if (wasStarted)
                Log.Info("[AudioInteraction] shutdown");
        }

        /// <summary>Pause/resume content ambience + weather with BGM media pause.</summary>
        public void SetContentAudioPaused(bool paused)
        {
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
        /// </summary>
        public void RestoreContentAudio(string reason)
        {
            if (!_started)
                return;

            SetContentAudioPaused(false);

            Ambience.RestartPlaying();
            Weather.RestorePlaying();
            Rain.RestorePlaying();

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
