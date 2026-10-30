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
    public sealed class WeatherAudioBridge
    {
        readonly IslandAudioSession _session;

        public WeatherAudioBridge(IslandAudioSession session)
        {
            _session = session;
        }

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

        bool _started;
        bool _subscribed;
        bool _contentAudioPaused;
        bool _windPlaying;
        bool _rainPlaying;
        bool _windBankHeld;
        bool _rainBankHeld;
        bool _windMissing;
        bool _rainMissing;
        /// <summary>Logical weather intensity (pre-duck). Written value = logical × ContentAudioDuck.</summary>
        float _windLogical;
        float _rainLogical;
        float _foggyLogical;
        float _windWritten = float.NaN;
        float _rainWritten = float.NaN;
        float _foggyWritten = float.NaN;
        /// <summary>
        /// Rain RTPC at the moment <c>Play_Rain</c> was posted.
        /// Bank blend: small 0–55, medium starts at 32. A voice posted at 中/大
        /// does not arm Rain_Small when the parameter later falls to 30.
        /// </summary>
        float _rainPostedAt = float.NaN;

        /// <summary>Rain_Medium blend clip min. 小雨 is 30, so values below this are small-only.</summary>
        const float LightRainExclusiveMax = 32f;

        /// <summary>
        /// Bank blend / 起声门槛：0 档 <c>Play_Rain</c>/<c>Play_Wind</c> 不起声，之后只抬 RTPC 也补不回来。
        /// </summary>
        const float MinArmRtpc = 1f;

        public void Start()
        {
            if (_started)
                return;
            _started = true;
            Subscribe();
            ApplyFromWeather();
        }

        public void Shutdown()
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
            _rainPostedAt = float.NaN;
        }

        public void SetContentAudioPaused(bool paused)
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
        public void ClearPlaying()
        {
            WriteLogicalRtpc(WindParam, 0f, ref _windLogical, ref _windWritten);
            WriteLogicalRtpc(RainParam, 0f, ref _rainLogical, ref _rainWritten);
            WriteLogicalRtpc(FoggyParam, 0f, ref _foggyLogical, ref _foggyWritten);
            StopLoopKeepBank(ref _windPlaying, WanosInteraction.WindObjectId, WindStop);
            StopLoopKeepBank(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop);
            _rainPostedAt = float.NaN;
        }

        public void Refresh() => ApplyFromWeather();

        /// <summary>Re-push Wind/Rain/Foggy × <see cref="_session.Duck.Level01"/> (preset ramp).</summary>
        public void ReapplyDuckedRtpcs()
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
        public void RestorePlaying()
        {
            if (!_started)
                return;
            if (_contentAudioPaused)
                return;

            ClearPlaying();
            ApplyFromWeather();
        }

        void Subscribe()
        {
            if (_subscribed)
                return;
            if (GameEntry.Event != null)
                GameEntry.Event.Subscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
            // 首装 Critical 波未完成时切风雨会 miss bank；下载灌入后必须重试（对齐 AmbienceZone）。
            WanosBankStore.OnBankLoaded += OnBankLoaded;
            _subscribed = true;
        }

        void Unsubscribe()
        {
            if (!_subscribed)
                return;
            if (GameEntry.Event != null)
                GameEntry.Event.Unsubscribe(WeatherChangedEventArgs.EventId, OnWeatherChanged);
            WanosBankStore.OnBankLoaded -= OnBankLoaded;
            _subscribed = false;
        }

        void OnWeatherChanged(object sender, GameEventArgs e) => ApplyFromWeather();

        void OnBankLoaded(string bankName)
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

        void ApplyFromWeather()
        {
            if (!_started || !WanosInteraction.IsReady || _contentAudioPaused)
                return;

            WeatherService weather = GameEntry.Weather;
            float wind = 0f;
            float rain = 0f;
            float foggy = 0f;
            float windTarget = 0f;
            float rainTarget = 0f;
            bool wantWind = false;
            bool wantRain = false;

            if (weather != null && !(PresetService.Instance != null && PresetService.Instance.IsWeatherHeld))
            {
                // Boluo WeatherAudioSubscriber：按目标 Kind/Level 立刻 Enable；
                // RTPC 可 2s 淡入。切勿用 lerp 近 0 判定 want —— 0 档 Post 不起声。
                float intensity = weather.Level <= 0 ? 0f : Mathf.Clamp(weather.Level, 0, 3) * 30f;
                switch (weather.Kind)
                {
                    case IslandWeatherKind.Wind:
                        wantWind = intensity > 0f;
                        windTarget = intensity;
                        break;
                    case IslandWeatherKind.Rain:
                        wantRain = intensity > 0f;
                        rainTarget = intensity;
                        break;
                    case IslandWeatherKind.Foggy:
                        foggy = intensity;
                        break;
                    case IslandWeatherKind.Cloud:
                    case IslandWeatherKind.Clear:
                    default:
                        break;
                }

                if (weather.IsWeatherTransitioning)
                {
                    // Same 2s blend as fog / rain / wind visuals.
                    wind = weather.VisualWind01 * 90f;
                    rain = weather.VisualRainAudio;
                    foggy = weather.VisualFoggyAudio;
                }
                else
                {
                    wind = windTarget;
                    rain = rainTarget;
                }
            }

            // Always write all three so a prior kind does not leave a non-zero RTPC.
            WriteLogicalRtpc(WindParam, wind, ref _windLogical, ref _windWritten);
            WriteLogicalRtpc(RainParam, rain, ref _rainLogical, ref _rainWritten);
            WriteLogicalRtpc(FoggyParam, foggy, ref _foggyLogical, ref _foggyWritten);

            float duck = _session.Duck.Level01;

            // Retry bank load after a prior miss (banks may land later).
            if (wantWind)
            {
                _windMissing = false;
                if (!_windPlaying && windTarget * duck >= MinArmRtpc)
                {
                    // 面板插值开头 wind≈0：先按目标档 seed 再 Post，再写回 lerp，音量仍淡入。
                    bool seeded = SeedArmableRtpcBeforePlay(
                        WindParam, wind, windTarget, ref _windWritten);
                    StartLoop(ref _windPlaying, WanosInteraction.WindObjectId, WindBank, WindPlay,
                        ref _windBankHeld, ref _windMissing);
                    if (seeded)
                        RestoreLogicalRtpc(WindParam, wind, ref _windWritten);
                }
            }
            else
                StopLoop(ref _windPlaying, WanosInteraction.WindObjectId, WindStop, ref _windBankHeld, WindBank);

            if (wantRain)
            {
                // 大雨/中雨已在播时，只把 Rain RTPC 降到 30 不会拉起 Rain_Small
                // （0 档起声、或小雨那条已经按 loop_times=1 放完，抬/降参数都补不回来）。
                // 虹膜把 RTPC 乘到 0 时不要在这一帧重起：0 档 Play_Rain 起不来小雨。
                if (NeedsLightRainRearm(rain) && rainTarget * duck >= MinArmRtpc)
                {
                    Log.Info(
                        $"[WeatherAudio] rearm Play_Rain light rtpc={rain:F1} postedAt={_rainPostedAt:F1}");
                    StopLoopKeepBank(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop);
                    _rainPostedAt = float.NaN;
                }

                _rainMissing = false;
                if (!_rainPlaying && rainTarget * duck >= MinArmRtpc)
                {
                    float rainPostedAt = rain;
                    // 面板插值开头 rain≈0：先按目标档 seed 再 Post，再写回 lerp，音量仍淡入。
                    bool seeded = SeedArmableRtpcBeforePlay(
                        RainParam, rain, rainTarget, ref _rainWritten);
                    if (seeded)
                        rainPostedAt = rainTarget;
                    StartLoop(ref _rainPlaying, WanosInteraction.RainObjectId, RainBank, RainPlay,
                        ref _rainBankHeld, ref _rainMissing);
                    if (seeded)
                        RestoreLogicalRtpc(RainParam, rain, ref _rainWritten);
                    if (_rainPlaying)
                        _rainPostedAt = rainPostedAt;
                }
            }
            else
            {
                StopLoop(ref _rainPlaying, WanosInteraction.RainObjectId, RainStop, ref _rainBankHeld, RainBank);
                _rainPostedAt = float.NaN;
            }

            _session.Rain.EvaluateNow();
        }

        /// <summary>
        /// Post 前若当前 ducked RTPC 不足以起声，先写入目标档位（对齐 Boluo 揭幕「先 RTPC 再 Play」）。
        /// 返回 true 表示写了 seed（调用方应把 postedAt 记成 target）。
        /// </summary>
        bool SeedArmableRtpcBeforePlay(
            string name,
            float logical,
            float targetLogical,
            ref float writtenField)
        {
            float duck = _session.Duck.Level01;
            if (logical * duck >= MinArmRtpc)
                return false;
            if (targetLogical * duck < MinArmRtpc)
                return false;
            writtenField = float.NaN;
            ForceWrite(name, targetLogical, ref writtenField);
            return true;
        }

        void RestoreLogicalRtpc(string name, float logical, ref float writtenField)
        {
            writtenField = float.NaN;
            ForceWrite(name, logical, ref writtenField);
        }

        bool NeedsLightRainRearm(float rain)
        {
            return _rainPlaying
                   && !float.IsNaN(_rainPostedAt)
                   && _rainPostedAt >= LightRainExclusiveMax
                   && rain < LightRainExclusiveMax;
        }

        void WriteLogicalRtpc(string name, float logical, ref float logicalField, ref float writtenField)
        {
            logicalField = logical;
            ForceWrite(name, logical, ref writtenField);
        }

        void ForceWrite(string name, float logical, ref float writtenField)
        {
            float ducked = logical * _session.Duck.Level01;
            if (!float.IsNaN(writtenField) && Mathf.Approximately(writtenField, ducked))
                return;
            writtenField = ducked;
            WanosEngine.SetGlobalParameter(name, ducked);
        }

        void StartLoop(
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

        void StopLoop(
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
        void StopLoopKeepBank(ref bool playing, ulong objectId, string stopEvent)
        {
            if (!playing)
                return;
            if (!string.IsNullOrEmpty(stopEvent))
                WanosInteraction.PostEvent(stopEvent, objectId);
            playing = false;
        }
    }
}
