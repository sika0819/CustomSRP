using System;
using GameFramework.Event;
using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// Maps <see cref="WeatherService"/> Kind/Level to Wind/Rain bank loops + Boluo weather RTPCs.
    /// RTPC names and scale match old GameData: <c>Wind</c>/<c>Rain</c>/<c>Foggy</c> = state × 30.
    /// </summary>
    public static class WeatherAudioBridge
    {
        public const string WindBank = "Wind";
        public const string RainBank = "Rain";
        public const string WindPlay = "Play_Wind";
        public const string WindStop = "Stop_Wind";
        public const string RainPlay = "Play_Rain";
        public const string RainStop = "Stop_Rain";

        /// <summary>Boluo WeatherType.ToString() param names (not WindIntensity).</summary>
        public const string WindParam = "Wind";
        public const string RainParam = "Rain";
        public const string FoggyParam = "Foggy";

        static bool _started;
        static bool _subscribed;
        static bool _contentAudioPaused;
        static bool _windPlaying;
        static bool _rainPlaying;
        static bool _windBankHeld;
        static bool _rainBankHeld;
        static bool _windMissing;
        static bool _rainMissing;
        /// <summary>Logical weather intensity (pre-duck). Written value = logical × ContentAudioDuck.</summary>
        static float _windLogical;
        static float _rainLogical;
        static float _foggyLogical;
        static float _windWritten = float.NaN;
        static float _rainWritten = float.NaN;
        static float _foggyWritten = float.NaN;

        public static void Start()
        {
            if (_started)
                return;
            _started = true;
            Subscribe();
            ApplyFromWeather();
        }

        public static void Shutdown()
        {
            _started = false;
            Unsubscribe();
            StopLoop(ref _windPlaying, WanosInteraction.WindObjectId, WindStop, ref _windBankHeld, WindBank);
            StopLoop(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop, ref _rainBankHeld, RainBank);
            WriteLogicalRtpc(WindParam, 0f, ref _windLogical, ref _windWritten);
            WriteLogicalRtpc(RainParam, 0f, ref _rainLogical, ref _rainWritten);
            WriteLogicalRtpc(FoggyParam, 0f, ref _foggyLogical, ref _foggyWritten);
            _contentAudioPaused = false;
            _windMissing = false;
            _rainMissing = false;
        }

        public static void SetContentAudioPaused(bool paused)
        {
            if (_contentAudioPaused == paused)
                return;
            _contentAudioPaused = paused;
            if (paused)
            {
                StopLoop(ref _windPlaying, WanosInteraction.WindObjectId, WindStop, ref _windBankHeld, WindBank);
                StopLoop(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop, ref _rainBankHeld, RainBank);
                return;
            }

            ApplyFromWeather();
        }

        /// <summary>
        /// Hard-stop wind/rain loops without changing the BGM content-pause latch.
        /// Preset iris: zero RTPCs first (already ducked), then stop — keep banks retained.
        /// </summary>
        public static void ClearPlaying()
        {
            WriteLogicalRtpc(WindParam, 0f, ref _windLogical, ref _windWritten);
            WriteLogicalRtpc(RainParam, 0f, ref _rainLogical, ref _rainWritten);
            WriteLogicalRtpc(FoggyParam, 0f, ref _foggyLogical, ref _foggyWritten);
            StopLoopKeepBank(ref _windPlaying, WanosInteraction.WindObjectId, WindStop);
            StopLoopKeepBank(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop);
        }

        public static void Refresh() => ApplyFromWeather();

        /// <summary>Re-push Wind/Rain/Foggy × <see cref="ContentAudioDuck.Level01"/> (preset ramp).</summary>
        public static void ReapplyDuckedRtpcs()
        {
            if (!_started)
                return;
            ForceWrite(WindParam, _windLogical, ref _windWritten);
            ForceWrite(RainParam, _rainLogical, ref _rainWritten);
            ForceWrite(FoggyParam, _foggyLogical, ref _foggyWritten);
        }

        /// <summary>
        /// 回前台 / 打断恢复：风 / 雨循环的 voice 可能已被引擎掐掉，但本地态还认为在播
        /// （StartLoop 会因 <c>playing == true</c> 直接 return）→ 清本地态后按当前天气重起。
        /// </summary>
        public static void RestorePlaying()
        {
            if (!_started)
                return;
            if (_contentAudioPaused)
                return;

            ClearPlaying();
            ApplyFromWeather();
        }

        static void Subscribe()
        {
            if (_subscribed)
                return;
            if (GameEntry.Event != null)
                GameEntry.Event.Subscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
            // 首装 Critical 波未完成时切风雨会 miss bank；下载灌入后必须重试（对齐 AmbienceZone）。
            WanosBankStore.OnBankLoaded += OnBankLoaded;
            _subscribed = true;
        }

        static void Unsubscribe()
        {
            if (!_subscribed)
                return;
            if (GameEntry.Event != null)
                GameEntry.Event.Unsubscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
            WanosBankStore.OnBankLoaded -= OnBankLoaded;
            _subscribed = false;
        }

        static void OnWeatherChanged(object sender, GameEventArgs e) => ApplyFromWeather();

        static void OnBankLoaded(string bankName)
        {
            if (!_started || string.IsNullOrEmpty(bankName))
                return;
            bool isWind = string.Equals(bankName, WindBank, StringComparison.OrdinalIgnoreCase);
            bool isRain = string.Equals(bankName, RainBank, StringComparison.OrdinalIgnoreCase);
            if (!isWind && !isRain)
                return;
            if (isWind)
                _windMissing = false;
            if (isRain)
                _rainMissing = false;
            Log.Info($"[WeatherAudio] bank ready={bankName} — retry ApplyFromWeather");
            ApplyFromWeather();
        }

        static void ApplyFromWeather()
        {
            if (!_started || !WanosInteraction.IsReady || _contentAudioPaused)
                return;

            WeatherService weather = GameEntry.Weather;
            float wind = 0f;
            float rain = 0f;
            float foggy = 0f;
            bool wantWind = false;
            bool wantRain = false;

            if (weather != null && !PresetWeatherHold.IsHeld)
            {
                if (weather.IsWeatherTransitioning)
                {
                    // Same 2s blend as fog / rain / wind, so the loop does not pop in at full level.
                    wind = weather.VisualWind01 * 90f;
                    rain = weather.VisualRainAudio;
                    foggy = weather.VisualFoggyAudio;
                    wantWind = wind > 0.01f;
                    wantRain = rain > 0.01f;
                }
                else
                {
                    // Boluo: RTPC = curWeatherState * 30 (关闭=0, 小=30, 中=60, 大=90).
                    float intensity = weather.Level <= 0 ? 0f : Mathf.Clamp(weather.Level, 0, 3) * 30f;
                    switch (weather.Kind)
                    {
                        case IslandWeatherKind.Wind:
                            wantWind = intensity > 0f;
                            wind = intensity;
                            break;
                        case IslandWeatherKind.Rain:
                            wantRain = intensity > 0f;
                            rain = intensity;
                            break;
                        case IslandWeatherKind.Foggy:
                            foggy = intensity;
                            break;
                        case IslandWeatherKind.Cloud:
                        case IslandWeatherKind.Clear:
                        default:
                            break;
                    }
                }
            }

            // Always write all three so a prior kind does not leave a non-zero RTPC.
            WriteLogicalRtpc(WindParam, wind, ref _windLogical, ref _windWritten);
            WriteLogicalRtpc(RainParam, rain, ref _rainLogical, ref _rainWritten);
            WriteLogicalRtpc(FoggyParam, foggy, ref _foggyLogical, ref _foggyWritten);

            // Retry bank load after a prior miss (banks may land later).
            if (wantWind)
            {
                _windMissing = false;
                StartLoop(ref _windPlaying, WanosInteraction.WindObjectId, WindBank, WindPlay,
                    ref _windBankHeld, ref _windMissing);
            }
            else
                StopLoop(ref _windPlaying, WanosInteraction.WindObjectId, WindStop, ref _windBankHeld, WindBank);

            if (wantRain)
            {
                _rainMissing = false;
                StartLoop(ref _rainPlaying, WanosInteraction.RainObjectId, RainBank, RainPlay,
                    ref _rainBankHeld, ref _rainMissing);
            }
            else
                StopLoop(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop, ref _rainBankHeld, RainBank);

            RainWaterProximityService.EvaluateNow();
        }

        static void WriteLogicalRtpc(string name, float logical, ref float logicalField, ref float writtenField)
        {
            logicalField = logical;
            ForceWrite(name, logical, ref writtenField);
        }

        static void ForceWrite(string name, float logical, ref float writtenField)
        {
            float ducked = logical * ContentAudioDuck.Level01;
            if (!float.IsNaN(writtenField) && Mathf.Approximately(writtenField, ducked))
                return;
            writtenField = ducked;
            WanosEngine.SetGlobalParameter(name, ducked);
        }

        static void StartLoop(
            ref bool playing,
            ulong objectId,
            string bank,
            string playEvent,
            ref bool bankHeld,
            ref bool missing)
        {
            if (playing)
                return;
            if (missing)
                return;
            if (!bankHeld)
            {
                if (!WanosInteraction.RetainBank(bank))
                {
                    missing = true;
                    Log.Warning($"[WeatherAudio] bank missing: {bank}");
                    return;
                }

                bankHeld = true;
            }

            WanosInteraction.PostEvent(playEvent, objectId);
            playing = true;
            Log.Info($"[WeatherAudio] play {playEvent}");
        }

        static void StopLoop(
            ref bool playing,
            ulong objectId,
            string stopEvent,
            ref bool bankHeld,
            string bank)
        {
            if (!playing && !bankHeld)
                return;
            if (playing && !string.IsNullOrEmpty(stopEvent))
                WanosInteraction.PostEvent(stopEvent, objectId);
            playing = false;
            if (bankHeld)
            {
                WanosInteraction.ReleaseBank(bank);
                bankHeld = false;
            }
        }

        /// <summary>Stop voice/event only; leave <see cref="_windBankHeld"/> / rain retain intact.</summary>
        static void StopLoopKeepBank(ref bool playing, ulong objectId, string stopEvent)
        {
            if (!playing)
                return;
            if (!string.IsNullOrEmpty(stopEvent))
                WanosInteraction.PostEvent(stopEvent, objectId);
            playing = false;
        }
    }
}
