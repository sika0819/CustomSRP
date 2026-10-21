using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// Starts ambience / weather / rain-water interaction after island uncover.
    /// </summary>
    public static class AudioInteractionBootstrap
    {
        static bool _started;

        public static bool IsStarted => _started;

        public static void ResetSession()
        {
            if (_started)
                Shutdown();
        }

        /// <summary>Call after <see cref="WanosEngine.EnablePlayback"/>.</summary>
        public static void StartAfterUncover()
        {
            if (_started)
                return;

            WanosInteraction.EnsureLogicalObjects();
            WanosInteraction.StartListenerFollow();

            AmbienceZoneService.Start();
            WeatherAudioBridge.Start();
            RainWaterProximityService.Start();
            _started = true;
            Log.Info("[AudioInteraction] started after uncover");
        }

        public static void Shutdown()
        {
            WanosInteraction.StopListenerFollow();

            if (!_started)
            {
                AmbienceZoneService.Shutdown();
                WeatherAudioBridge.Shutdown();
                RainWaterProximityService.Shutdown();
                return;
            }

            RainWaterProximityService.Shutdown();
            WeatherAudioBridge.Shutdown();
            AmbienceZoneService.Shutdown();
            _started = false;
            Log.Info("[AudioInteraction] shutdown");
        }

        /// <summary>Pause/resume content ambience + weather with BGM media pause.</summary>
        public static void SetContentAudioPaused(bool paused)
        {
            AmbienceZoneService.SetContentAudioPaused(paused);
            WeatherAudioBridge.SetContentAudioPaused(paused);
            RainWaterProximityService.SetContentAudioPaused(paused);
        }

        /// <summary>
        /// Immediately stop all content 3D / weather loops (preset switch start).
        /// Does not touch the BGM content-pause latch.
        /// </summary>
        public static void ClearContent3D()
        {
            AmbienceZoneService.ClearAllPlaying();
            RainWaterProximityService.ClearPlaying();
            WeatherAudioBridge.ClearPlaying();
        }

        /// <summary>
        /// 回前台 / 打断后恢复内容声（环境声 / 天气 / 雨水）。
        /// <para>
        /// 引擎侧的 voice 可能已经被掐掉（原生 muted / 焦点丢失 / 引擎重建），但本地态
        /// 还认为「在播」→ 永远不会再 Post。这里统一停掉本地态再按当前状态重起。
        /// </para>
        /// </summary>
        public static void RestoreContentAudio(string reason)
        {
            if (!_started)
                return;

            // 解除「内容声随 BGM 暂停」的 latch（用户暂停音乐时也会置位）。
            SetContentAudioPaused(false);

            // 强制重起：本地态清干净 → 下一轮 Evaluate / Refresh 重新 Post。
            AmbienceZoneService.RestartPlaying();
            WeatherAudioBridge.RestorePlaying();
            RainWaterProximityService.RestorePlaying();

            Log.Info($"[AudioInteraction] content audio restored ({reason})");
        }

        /// <summary>
        /// After preset teleport: re-evaluate ambience / rain-water / weather from the current listener.
        /// No-op while content audio is paused for BGM.
        /// </summary>
        public static void RetriggerContentAtListener()
        {
            if (!_started)
                return;

            AmbienceZoneService.EvaluateNow();
            WeatherAudioBridge.Refresh();
        }
    }
}
