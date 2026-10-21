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
        static float _windRtpc = float.NaN;
        static float _rainRtpc = float.NaN;
        static float _foggyRtpc = float.NaN;

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
            WriteWeatherRtpc(WindParam, 0f, ref _windRtpc);
            WriteWeatherRtpc(RainParam, 0f, ref _rainRtpc);
            WriteWeatherRtpc(FoggyParam, 0f, ref _foggyRtpc);
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
        /// Preset iris: stop events / zero RTPCs but <b>keep</b> banks retained so reveal
        /// can re-Post without a late Release racing a fresh Retain.
        /// </summary>
        public static void ClearPlaying()
        {
            StopLoopKeepBank(ref _windPlaying, WanosInteraction.WindObjectId, WindStop);
            StopLoopKeepBank(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop);
            WriteWeatherRtpc(WindParam, 0f, ref _windRtpc);
            WriteWeatherRtpc(RainParam, 0f, ref _rainRtpc);
            WriteWeatherRtpc(FoggyParam, 0f, ref _foggyRtpc);
        }

        public static void Refresh() => ApplyFromWeather();

        static void Subscribe()
        {
            if (_subscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Subscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
            _subscribed = true;
        }

        static void Unsubscribe()
        {
            if (!_subscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Unsubscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
            _subscribed = false;
        }

        static void OnWeatherChanged(object sender, GameEventArgs e) => ApplyFromWeather();

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

            // Always write all three so a prior kind does not leave a non-zero RTPC.
            WriteWeatherRtpc(WindParam, wind, ref _windRtpc);
            WriteWeatherRtpc(RainParam, rain, ref _rainRtpc);
            WriteWeatherRtpc(FoggyParam, foggy, ref _foggyRtpc);

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

        static void WriteWeatherRtpc(string name, float value, ref float cached)
        {
            if (!float.IsNaN(cached) && Mathf.Approximately(cached, value))
                return;
            cached = value;
            WanosEngine.SetGlobalParameter(name, value);
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
