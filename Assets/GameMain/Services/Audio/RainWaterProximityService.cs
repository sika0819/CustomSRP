using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// While raining, post <c>Play_Rain_Water</c> when the listener is near river/lake/ocean anchors.
    /// </summary>
    public static class RainWaterProximityService
    {
        const float EvalIntervalSeconds = 0.25f;
        const float EnableMeters = 28f;
        const float DisableMeters = 32f;

        public const string BankName = "Rain_Water";
        public const string PlayEvent = "Play_Rain_Water";
        public const string StopEvent = "Stop_Rain_Water";

        static bool _started;
        static bool _playing;
        static bool _bankHeld;
        static bool _bankMissing;
        static bool _contentAudioPaused;
        static bool _pendingEvaluate;
        static readonly List<Vector3> WaterPoints = new List<Vector3>(64);

        public static void Start()
        {
            if (_started)
                return;
            _started = true;
            RunLoop().Forget();
        }

        public static void Shutdown()
        {
            _started = false;
            StopPlaying();
            ReleaseBank();
            _bankMissing = false;
            _contentAudioPaused = false;
            _pendingEvaluate = false;
            WaterPoints.Clear();
        }

        public static void SetContentAudioPaused(bool paused)
        {
            if (_contentAudioPaused == paused)
                return;
            _contentAudioPaused = paused;
            if (paused)
            {
                StopPlaying();
                return;
            }

            EvaluateNow();
        }

        /// <summary>Hard-stop Rain_Water without changing the BGM content-pause latch.</summary>
        public static void ClearPlaying()
        {
            StopPlaying();
        }

        public static void EvaluateNow()
        {
            if (_started)
            {
                _pendingEvaluate = false;
                Evaluate();
                return;
            }

            _pendingEvaluate = true;
        }

        static async UniTaskVoid RunLoop()
        {
            try
            {
                for (int i = 0; i < 600; i++)
                {
                    if (!_started)
                        return;
                    if (WanosInteraction.IsReady)
                        break;
                    await UniTask.DelayFrame(1);
                }

                RebuildWaterPoints();
                if (_pendingEvaluate)
                {
                    _pendingEvaluate = false;
                    Evaluate();
                }

                while (_started)
                {
                    Evaluate();
                    await UniTask.Delay(TimeSpan.FromSeconds(EvalIntervalSeconds));
                }
            }
            catch (Exception e)
            {
                Log.Error($"[RainWater] failed: {e}");
            }
        }

        static void Evaluate()
        {
            if (!_started || !WanosInteraction.IsReady)
                return;
            if (_contentAudioPaused)
                return;

            if (!WantRainWeather())
            {
                StopPlaying();
                ReleaseBank();
                return;
            }

            if (!WanosInteraction.TryGetListenerPosition(out Vector3 listener))
                return;

            if (WaterPoints.Count == 0)
                RebuildWaterPoints();

            float radius = _playing ? DisableMeters : EnableMeters;
            bool nearWater = TryNearestWater(listener, radius, out _);
            if (nearWater && !_playing)
                StartPlaying();
            else if (!nearWater && _playing)
                StopPlaying();
        }

        static bool WantRainWeather()
        {
            WeatherService weather = GameEntry.Weather;
            return weather != null && weather.Kind == IslandWeatherKind.Rain && weather.Level > 0;
        }

        static void RebuildWaterPoints()
        {
            WaterPoints.Clear();
            // 优先使用烘焙静态数据（O(1) 查表）—— 覆盖 Ocean / Lake / River / Waterfall。
            if (StaticWaterStationIndex.IsReady)
            {
                StaticWaterStationIndex.GetAllWaterPoints(WaterPoints);
                if (WaterPoints.Count > 0)
                    return;
            }

            // 降级路径：SO 未烘焙时回退到 catalog 扫描。
            var statics = AmbienceZoneCatalog.StaticEmitters;
            for (int i = 0; i < statics.Length; i++)
            {
                var s = statics[i];
                if (s.Kind == AmbienceEmitterKind.Ocean || s.Kind == AmbienceEmitterKind.Lake)
                    WaterPoints.Add(s.Position);
            }

            var items = GameEntry.World?.MapItemSpawner?.AllMapItems;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null || !item.IsActive)
                        continue;
                    string prefab = item.PrefabName ?? "";
                    if (prefab.StartsWith("River_", StringComparison.Ordinal)
                        || AmbienceZoneCatalog.IsWaterfallPrefab(prefab))
                        WaterPoints.Add(item.Pos);
                }
            }

            if (WaterPoints.Count == 0)
            {
                AmbienceZoneCatalog.AppendNativeRivers(spec =>
                {
                    if (spec != null)
                        WaterPoints.Add(spec.Position);
                });
            }
        }

        static bool TryNearestWater(Vector3 listener, float maxDistance, out Vector3 closest)
        {
            closest = default;
            float best = maxDistance + 1f;
            for (int i = 0; i < WaterPoints.Count; i++)
            {
                float d = Vector3.Distance(listener, WaterPoints[i]);
                if (d >= best)
                    continue;
                best = d;
                closest = WaterPoints[i];
            }

            return best <= maxDistance;
        }

        static void StartPlaying()
        {
            if (_contentAudioPaused)
            {
                _pendingEvaluate = true;
                return;
            }

            if (!_bankHeld)
            {
                if (_bankMissing)
                    return;
                if (!WanosInteraction.RetainBank(BankName))
                {
                    _bankMissing = true;
                    Log.Warning("[RainWater] bank missing: Rain_Water");
                    return;
                }

                _bankHeld = true;
            }

            WanosInteraction.PostEvent(PlayEvent, WanosInteraction.RainWaterObjectId);
            _playing = true;
            Log.Info("[RainWater] play near water");
        }

        static void StopPlaying()
        {
            if (!_playing)
                return;
            WanosInteraction.PostEvent(StopEvent, WanosInteraction.RainWaterObjectId);
            _playing = false;
            Log.Info("[RainWater] stop");
        }

        static void ReleaseBank()
        {
            if (!_bankHeld)
                return;
            WanosInteraction.ReleaseBank(BankName);
            _bankHeld = false;
            _bankMissing = false;
        }
    }
}
