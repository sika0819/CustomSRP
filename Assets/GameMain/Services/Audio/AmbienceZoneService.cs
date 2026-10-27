using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;
using WanosBankStore = WanosAudio.WanosBankStore;

namespace Game
{
    /// <summary>
    /// Distance-based ambience zones: enter radius Posts to 3D emitters, exit stops + releases bank.
    /// Upgrades:
    /// - River/Waterfall 用 <see cref="WaterSurfaceProbe"/> 直接查场景里真实的水面最近点
    ///   （运行期采样，取代旧的编辑器烘焙 BakedWaterStation 方案）。
    /// - Ocean/Lake 用 catalog 里的岸点 / 湖点（xz 距离）。
    /// - Bank Ready 重试：bank 不再永久缓存失败，允许后续重试。
    /// - Voice 回收：订阅 <see cref="WanosInteraction.VoiceStateChanged"/>，voice stopped 时摘掉 id，
    ///   list 空且 source 仍在播放区间则 RepostSource。
    /// - Stop 时对每个 voice 显式 Stop with 250ms fade。
    /// - Emitter GameObject 预分配池，避免 enter/exit 时 new/Destroy。
    /// - Listener-still skip：玩家站着不动时跳过本 tick。
    /// - 共用 biome bank（Forest/Grassland/Plateau）：同名 Play 只 Post 一次；离开整组才 Stop+Unload。
    /// - Distance RTPC + AttenuationGain：显示 dB 经 GainInvTransform 再写入（对齐 Boluo CurveForGain）。
    /// </summary>
    public static class AmbienceZoneService
    {
        const float EvalIntervalSeconds = 0.25f;
        const int VoiceStopFadeMs = 250;
        /// <summary>
        /// Shared-bank bed switch: keep the playing bed until another is nearer by this much.
        /// Without hysteresis, midpoint walk flip-flops every Eval → emitter teleport crackle.
        /// </summary>
        const float ExclusiveShareHysteresisMeters = 12f;
        /// <summary>Cap per-eval emitter teleport (water nearest-point jitter).</summary>
        const float MaxEmitterJumpMetersPerEval = 10f;
        /// <summary>Engine floor; matches GainInvTransform far end (~−96 dB).</summary>
        const float GainFloorDb = -96f;
        /// <summary>
        /// Boluo <c>WanosCurveForGain.gainSmoothTime</c>：声源位/距离跳变时 SmoothDamp 增益，
        /// 避免边缘 Distance RTPC + 衰减硬跳成「电流音 / 刺啦」。
        /// </summary>
        const float GainSmoothTimeSeconds = 0.15f;
        /// <summary>Boluo <c>TeleportGainSnapMeters</c>：距离跳这么多立刻贴增益（预设落点）。</summary>
        const float TeleportGainSnapMeters = 8f;
        /// <summary>Distance RTPC 每秒最多追这么远；硬写会让 bank 内 Distance 曲线拉链。</summary>
        const float MaxDistanceSlewMetersPerSecond = 40f;
        /// <summary>走路出区软衰减时长（原 VoiceStopFadeMs/1000=0.25 太陡，边缘像电流）。</summary>
        const float WalkSoftStopSeconds = 0.55f;
        /// <summary>First Post starts below target then slews up — avoids enter click.</summary>
        const float SoftEnterStartDb = -36f;

        /// <summary>
        /// Boluo <c>WanosCurveForGain</c> 编辑器 Y 是「显示 dB」；远端点统一约 -17.25。
        /// 写入 <c>SetGameObjAttenuationGain</c> 前必须经 <see cref="GainInvTransform"/>
        /// （-17.25 → 引擎约 -96 dB）。此前直接写 -17，远听几乎无衰减。
        /// </summary>
        const float CurveFarDisplayDb = -17.25f;

        // Boluo Forest_Audio / GrassLand_* / Plateau_Audio CurveForGain 关键点（显示 dB）。
        static readonly Vector2[] ForestGainCurve =
        {
            new Vector2(79.23859f, 0f),
            new Vector2(108.466705f, -7.7523494f),
            new Vector2(160f, -17.25f),
        };
        static readonly Vector2[] GrasslandGainCurve =
        {
            new Vector2(39.793484f, 0f),
            new Vector2(150f, -17.25f),
        };
        static readonly Vector2[] GrasslandSecondaryGainCurve =
        {
            new Vector2(30.707796f, 0f),
            new Vector2(60f, -17.25f),
        };
        static readonly Vector2[] PlateauGainCurve =
        {
            new Vector2(70.79445f, 0f),
            new Vector2(316.06454f, -17.25f),
        };
        static readonly Vector2[] OceanGainCurve =
        {
            new Vector2(4f, 0f),
            new Vector2(60f, -17.25f),
        };
        static readonly Vector2[] Point20GainCurve =
        {
            new Vector2(4f, 0f),
            new Vector2(20f, -17.25f),
        };
        static readonly Vector2[] CampfireGainCurve =
        {
            new Vector2(4f, 0f),
            new Vector2(30f, -17.25f),
        };
        static readonly Vector2[] WaterfallBigGainCurve =
        {
            new Vector2(4f, 0f),
            new Vector2(50f, -17.25f),
        };
        static readonly Vector2[] WaterfallSmallGainCurve =
        {
            new Vector2(4f, 0f),
            new Vector2(72f, -17.25f),
        };

        public const string HostName = "AmbienceZones";
        public const string EmitterNamePrefix = "Amb_";

        // Scratch for exclusive-shared-bank Evaluate (Forest / Grassland / Plateau).
        static bool[] _wantScratch;
        static float[] _distScratch;
        static Vector3[] _posScratch;

        // Listener-still skip：听者移动 < 0.09m 时本 tick 结论不变，直接跳过。
        const float ListenerStillThresholdSq = 0.0078125f; // (0.09m)^2
        static Vector3 _lastListener;
        static bool _hasLastListener;

        static GameObject _host;
        static Transform _root;
        static bool _started;
        static bool _catalogBuilt;
        static bool _contentAudioPaused;
        /// <summary>Iris / black hold: Evaluate may run but must not StartSource / Retain.</summary>
        static bool _presetTransitionHeld;
        static bool _pendingEvaluate;
        /// <summary>回前台重起：已停本地态，等下一轮 Evaluate 重新 Post。</summary>
        static bool _restartPending;
        static float _restartNotBefore;
        static readonly List<RuntimeSource> Sources = new List<RuntimeSource>(32);

        // Emitter GameObject pool: one per source, pre-allocated under _root.
        // Avoids per-enter new GameObject + per-exit Destroy churn.
        static GameObject[] _emitterPool;
        static bool[] _emitterInUse;

        /// <summary>记录 voiceId → 所属 source index（用于回收）。</summary>
        static readonly Dictionary<ulong, int> VoiceToSource = new Dictionary<ulong, int>();

        /// <summary>因 bank 未就绪而被拒绝的 bank 名（收到 OnBankLoaded 时清掉并重 Evaluate）。</summary>
        static readonly HashSet<string> _pendingRetryBanks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>标记 inland water 已就绪（收到 OnInlandWaterReady 时设为 true，无需重复设）。</summary>
        static bool _inlandWaterReady;
        /// <summary>上一轮看见的篝火点燃态。变化时打破 listener-still，否则人站着火才点上永远不开播。</summary>
        static bool _campfireIgnitedLatched;
        static int _softStopDriveGen;
        static int _attenuationDriveGen;

        sealed class RuntimeSource
        {
            public AmbienceEmitterSpec Spec;
            public bool Playing;
            public GameObject Emitter;

            /// <summary>此 source 当前活跃的 voice ids。</summary>
            public readonly List<ulong> VoiceIds = new List<ulong>(4);

            /// <summary>此 source 是否应该 Post（区间内但 voice 已空）。</summary>
            public bool NeedsRepost;

            /// <summary>
            /// 上次推送到引擎的距离值（用于门限过滤，避免听者微小移动时频繁调用原生接口）。
            /// NaN 表示尚未推送过。
            /// </summary>
            public float LastPostedDistance = float.NaN;

            /// <summary>Last distance-curve target (pre-slew / pre-duck). NaN = none.</summary>
            public float LastTargetGainDb = float.NaN;

            /// <summary>Last gain actually written to the engine (post-slew, pre-duck lerp).</summary>
            public float LastAppliedGainDb = float.NaN;

            /// <summary>SmoothDamp velocity for <see cref="LastAppliedGainDb"/>.</summary>
            public float GainSlewVelocity;

            /// <summary>Last Distance RTPC written (slewed). NaN = none.</summary>
            public float LastSlewedDistance = float.NaN;

            /// <summary>Raw target distance this eval (pre Distance-RTPC slew).</summary>
            public float LastTargetDistance = float.NaN;

            /// <summary>Walk-exit: ramp gain to floor before StopEvent (native fade may be stub).</summary>
            public bool SoftStopping;
            public float SoftStopStartedAt;
            public float SoftStopSeconds;
            public float SoftStopFromGainDb;
            public bool SoftStopForceReleaseBank;
            public bool SoftStopKeepBank;

            /// <summary>Forest：第一次离开 150m 球的时刻。&lt;0 表示仍在球内。</summary>
            public float OutsideSince = -1f;

            /// <summary>True after a successful Retain; walk-stop may KeepBank without Release.</summary>
            public bool BankHeld;
        }

        struct DeferredEmitterRelease
        {
            public GameObject Go;
            public float ReleaseAt;
        }

        /// <summary>
        /// Stop 之后稍等再 Unregister。同一帧卸掉 object，引擎来不及吃 Stop_*，鸟叫会变成无主循环。
        /// </summary>
        static readonly List<DeferredEmitterRelease> DeferredEmitterReleases = new List<DeferredEmitterRelease>(8);
        const float EmitterReleaseDelaySeconds = 0.5f;

        public static bool IsReady => _started && _catalogBuilt;

        public static void SetContentAudioPaused(bool paused)
        {
            if (_contentAudioPaused == paused)
                return;
            _contentAudioPaused = paused;
            if (paused)
            {
                StopAllPlaying();
                return;
            }

            EvaluateNow();
        }

        /// <summary>
        /// Iris window: block new zone loads / plays. Cleared on reveal via
        /// <see cref="PresetWeatherHold.Release"/>.
        /// </summary>
        public static void SetPresetTransitionHeld(bool held)
        {
            if (_presetTransitionHeld == held)
                return;
            _presetTransitionHeld = held;
            // Hold only blocks Evaluate / StartSource. Hard stop is deferred to
            // ClearAllPlaying at iris Peak so cuts happen under black, not mid-fade.
        }

        /// <summary>
        /// Hard-stop every zone voice without changing the BGM content-pause latch.
        /// Used when switching island presets so old emitters do not linger across teleport.
        /// Forces bank Release (doc: 虹膜中卸掉走路区域加载器).
        /// Preset path should already be ducked to silence — snap floor then stop with no fade.
        /// </summary>
        public static void ClearAllPlaying()
        {
            CancelAllSoftStops();
            SnapAllPlayingGainsToFloor();
            StopAllPlaying(forceReleaseBank: true, keepBank: false, fadeMs: 0);
        }

        /// <summary>
        /// Re-push attenuation for live emitters using <see cref="ContentAudioDuck.Level01"/>.
        /// Called every frame while a preset duck ramp runs (Evaluate alone is too coarse).
        /// </summary>
        public static void ApplyDuckToPlayingEmitters()
        {
            if (!_started || !_catalogBuilt)
                return;
            float duck = ContentAudioDuck.Level01;
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if ((!src.Playing && !src.SoftStopping) || src.Emitter == null)
                    continue;
                // Duck 只乘在写出层；不要把 ducked 写回 LastAppliedGainDb，
                // 否则 duck 结束后 SmoothDamp 会从地板猛追，边缘再走又是电流音。
                float preDuck = float.IsNaN(src.LastAppliedGainDb) ? 0f : src.LastAppliedGainDb;
                if (src.SoftStopping)
                    preDuck = SoftStopGainNow(src);
                float gainDb = Mathf.Lerp(GainFloorDb, preDuck, duck);
                float dist = float.IsNaN(src.LastSlewedDistance)
                    ? (float.IsNaN(src.LastPostedDistance) ? 0f : src.LastPostedDistance)
                    : src.LastSlewedDistance;
                WanosInteraction.ApplyEmitterDistanceAttenuation(src.Emitter, dist, gainDb);
            }
        }

        /// <summary>
        /// 强制重起所有在播 source（回前台 / 打断恢复用）。
        /// <para>
        /// 引擎侧 voice 死亡在本工程没有回调（<c>WanosInteraction.VoiceStateChanged</c> 未接），
        /// 本地态若还认为「在播」就永远不会重新 Post → 回前台一片死寂。
        /// 这里停掉本地态（保留 bank，避免重载空窗），下一轮 Evaluate 重新 Post。
        /// </para>
        /// </summary>
        public static void RestartPlaying()
        {
            if (!_started || !_catalogBuilt)
                return;
            if (_contentAudioPaused)
                return;

            StopAllPlaying(forceReleaseBank: false, keepBank: true);
            InvalidateListenerStill();
            _restartPending = true;
            _restartNotBefore = Time.realtimeSinceStartup + (VoiceStopFadeMs / 1000f);
            Log.Info("[AmbienceZone] restart playing (foreground restore)");
        }

        public static void EvaluateNow()
        {
            if (_started && _catalogBuilt)
            {
                _pendingEvaluate = false;
                InvalidateListenerStill();
                Evaluate();
                RainWaterProximityService.EvaluateNow();
                return;
            }

            _pendingEvaluate = true;
        }

        public static void Start()
        {
            if (_started)
                return;
            _started = true;

            // 订阅 voice state 变化（用于回收）
            WanosInteraction.VoiceStateChanged += OnVoiceStateChanged;

            // 订阅 bank loaded（用于重试）
            WanosBankStore.OnBankLoaded += OnBankLoaded;

            // 订阅 inland water loaded（用于触发首次河/瀑布 Evaluate）
            IslandWorldService.OnInlandWaterReady += OnInlandWaterReady;

            StartAsync().Forget();
        }

        public static void Shutdown()
        {
            _started = false;
            WanosInteraction.VoiceStateChanged -= OnVoiceStateChanged;
            WanosBankStore.OnBankLoaded -= OnBankLoaded;
            IslandWorldService.OnInlandWaterReady -= OnInlandWaterReady;
            StopAllPlaying(forceReleaseBank: true);
            FlushDeferredEmitterReleases(force: true);
            Sources.Clear();
            VoiceToSource.Clear();
            _pendingRetryBanks.Clear();
            _catalogBuilt = false;
            _contentAudioPaused = false;
            _presetTransitionHeld = false;
            _pendingEvaluate = false;
            _restartPending = false;
            _inlandWaterReady = false;
            _campfireIgnitedLatched = false;
            _hasLastListener = false;
            WaterSurfaceProbe.Clear();
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
                _root = null;
            }
        }

        /// <summary>
        /// 听者移动小于阈值时本 tick 结论不变，跳过整轮评估。
        /// </summary>
        static bool ShouldEvaluateListener(Vector3 listener)
        {
            if (!_hasLastListener)
            {
                _lastListener = listener;
                _hasLastListener = true;
                return true;
            }

            float dx = listener.x - _lastListener.x;
            float dy = listener.y - _lastListener.y;
            float dz = listener.z - _lastListener.z;
            float sq = dx * dx + dy * dy + dz * dz;
            _lastListener = listener;
            return sq >= ListenerStillThresholdSq;
        }

        /// <summary>清掉「听者没动」缓存，强制下一轮重新评估（传送 / 硬清后用）。</summary>
        static void InvalidateListenerStill()
        {
            _hasLastListener = false;
        }

        /// <summary>
        /// Bank Ready 后清掉 pending 记录并重新 Evaluate（让之前因 bank 未就绪而 Stop 的 source 重试）。
        /// </summary>
        static void OnBankLoaded(string bankName)        {
            if (!_started)
                return;
            if (string.IsNullOrEmpty(bankName))
                return;
            if (!_pendingRetryBanks.Remove(bankName))
                return;
            Log.Info($"[AmbienceZone] bank ready={bankName} — retry Evaluate");
            EvaluateNow();
        }

        /// <summary>
        /// Inland water 加载完毕后触发 Evaluate（让河/瀑布 source 在 mesh collider 就绪后正确开始播放）。
        /// </summary>
        static void OnInlandWaterReady(bool hasWater)
        {
            if (!_started)
                return;
            Log.Info($"[AmbienceZone] inland water ready={hasWater} — retry Evaluate");
            _inlandWaterReady = true;
            // 内河就绪 → 重新采样真实水面点（河 / 瀑的最近点来源）。
            WaterSurfaceProbe.Rebuild();
            EvaluateNow();
        }

        /// <summary>
        /// Voice 停止时回调（由 WanosInteraction 转发 native 事件）。
        /// 摘掉 id；id 全部摘完且 source 仍在区间内则触发 RepostSource。
        /// </summary>
        static void OnVoiceStateChanged(ulong voiceId, AudioVoiceState state)
        {
            if (!_started)
                return;
            if (state != AudioVoiceState.Stopped)
                return;

            if (!VoiceToSource.TryGetValue(voiceId, out int srcIdx))
                return;
            VoiceToSource.Remove(voiceId);

            if (srcIdx < 0 || srcIdx >= Sources.Count)
                return;
            var src = Sources[srcIdx];
            if (!src.Playing)
                return;

            // 从 list 中摘掉此 id
            int listIdx = src.VoiceIds.IndexOf(voiceId);
            if (listIdx >= 0)
                src.VoiceIds.RemoveAt(listIdx);

            // 全部摘完且 source 仍在播放区间 → 触发 Repost。
            // 出区后禁止补 Post，否则容器 voice 一报 Stopped 就会在林外把鸟叫重新打起来。
            if (src.VoiceIds.Count == 0 && src.NeedsRepost)
            {
                if (WanosInteraction.TryGetListenerPosition(out Vector3 listener))
                {
                    Vector3 pos = ResolveRepostPosition(src.Spec, listener);
                    float dist = Distance(src.Spec, listener, pos);
                    if (!ShouldPlay(src, listener, pos))
                    {
                        src.NeedsRepost = false;
                        return;
                    }

                    RepostSource(src, pos, dist);
                }
            }
        }

        static async UniTaskVoid StartAsync()
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

                if (!WanosInteraction.IsReady)
                {
                    Log.Warning("[AmbienceZone] engine not ready — skip");
                    return;
                }

                EnsureHost();
                BuildStaticSources();
                _catalogBuilt = true;
                Log.Info($"[AmbienceZone] static sources={Sources.Count}");
                if (_pendingEvaluate)
                {
                    _pendingEvaluate = false;
                    Evaluate();
                }

                WaitMapItemsAndExtend().Forget();

                while (_started)
                {
                    // Listener pose is driven every frame by WanosInteraction.StartListenerFollow.
                    WanosBankStore.Update(); // 驱动延迟卸载队列
                    Evaluate();
                    await UniTask.Delay(TimeSpan.FromSeconds(EvalIntervalSeconds));
                }
            }
            catch (Exception e)
            {
                Log.Error($"[AmbienceZone] failed: {e}");
            }
        }

        static async UniTaskVoid WaitMapItemsAndExtend()
        {
            for (int i = 0; i < 1200; i++)
            {
                if (!_started)
                    return;
                var spawner = GameEntry.World?.MapItemSpawner;
                if (spawner != null && spawner.IsReady)
                    break;
                await UniTask.DelayFrame(1);
            }

            if (!_started)
                return;

            BuildMapItemSources();
            Log.Info($"[AmbienceZone] map sources={Sources.Count}");
            EnsureEmitterPool();
            // 新声源（含 extras 篝火）进来时听者可能没动，不能被 still-skip 吃掉。
            InvalidateListenerStill();

            // InlandWaterInstance 可能比 MapItemSpawner 更晚就绪（异步加载顺序）。
            // 若 water 已就绪（包括事件早于订阅时的兜底），立即 Evaluate；否则等 OnInlandWaterReady。
            if (IslandWorldService.InlandWaterReady)
                Evaluate();
            else
                Log.Debug("[AmbienceZone] inland water pending — will Evaluate on OnInlandWaterReady");
        }

        static void EnsureHost()
        {
            if (_host != null)
                return;
            _host = new GameObject(HostName);
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _root = _host.transform;
        }

        /// <summary>
        /// Pre-allocate one inactive GameObject per source under the host root.
        /// Called once after <see cref="BuildStaticSources"/> + <see cref="BuildMapItemSources"/>.
        /// Subsequent enter/exit cycles reuse these instead of allocating/destroying.
        /// </summary>
        static void EnsureEmitterPool()
        {
            if (Sources.Count == 0) return;
            EnsureHost();
            if (_emitterPool == null || _emitterPool.Length != Sources.Count)
            {
                _emitterPool = new GameObject[Sources.Count];
                _emitterInUse = new bool[Sources.Count];
            }
            for (int i = 0; i < Sources.Count; i++)
            {
                if (_emitterPool[i] != null) continue;
                var go = new GameObject($"Amb_{i}");
                go.transform.SetParent(_root, false);
                go.SetActive(false);
                _emitterPool[i] = go;
            }
        }

        /// <summary>Mark the slot holding `go` as free and deactivate the GameObject.</summary>
        static void ReturnEmitter(GameObject go)
        {
            if (go == null || _emitterPool == null) return;
            for (int i = 0; i < _emitterPool.Length; i++)
            {
                if (_emitterPool[i] == go)
                {
                    _emitterInUse[i] = false;
                    go.SetActive(false);
                    return;
                }
            }
        }

        /// <summary>Find an unused pool slot for `src`. Returns the GameObject or null if full.</summary>
        static GameObject AcquireEmitterForSource(RuntimeSource src)
        {
            if (_emitterPool == null) return null;
            int srcIdx = Sources.IndexOf(src);
            if (srcIdx < 0) return null;
            if (_emitterInUse[srcIdx])
            {
                // Already acquired — reuse.
                return _emitterPool[srcIdx];
            }
            _emitterInUse[srcIdx] = true;
            var go = _emitterPool[srcIdx];
            go.SetActive(true);
            return go;
        }

        static void BuildStaticSources()
        {
            var statics = AmbienceZoneCatalog.StaticEmitters;
            for (int i = 0; i < statics.Length; i++)
            {
                AddSource(statics[i]);
            }
        }

        static void BuildMapItemSources()
        {
            var items = GameEntry.World?.MapItemSpawner?.AllMapItems;
            // 仅统计真正的 River_* 床位。MapItem 里的 WaterfallSplash/Mist 不能挡住
            // AppendNativeRivers，否则河链（River_01..03）整组丢失。
            bool hasRiverBed = false;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (AmbienceZoneCatalog.TryCreateFromMapItem(item, i, out var spec))
                    {
                        if (spec.Kind == AmbienceEmitterKind.River)
                            hasRiverBed = true;
                        AddSource(spec);
                    }
                }
            }

            if (!hasRiverBed)
                AmbienceZoneCatalog.AppendNativeRivers(AddSource);

            AppendCampfireExtras();
        }

        /// <summary>
        /// Huodui 只在 MapItemExtras，不在 AllMapItem。不扫这份表就没有篝火声源。
        /// 只收 Campfire：河/瀑的 catalogIndex 会指回 AllMapItem 行。
        /// </summary>
        static void AppendCampfireExtras()
        {
            IReadOnlyList<MapItemData> extras = EnvMapItemExtras.LoadedItems;
            if (extras == null || extras.Count == 0)
                return;

            int added = 0;
            for (int i = 0; i < extras.Count; i++)
            {
                if (!AmbienceZoneCatalog.TryCreateFromMapItem(extras[i], i, out AmbienceEmitterSpec spec))
                    continue;
                if (spec.Kind != AmbienceEmitterKind.Campfire)
                    continue;
                spec.Id = "campfire_extra_" + i;
                AddSource(spec);
                added++;
            }

            if (added > 0)
                Log.Info("[AmbienceZone] campfire extras=" + added);
        }

        static void AddSource(AmbienceEmitterSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.PlayEvent))
                return;
            Sources.Add(new RuntimeSource { Spec = spec });
        }

        static void Evaluate()
        {
            FlushDeferredEmitterReleases(force: false);
            TickSoftStops();
            if (!_started || !_catalogBuilt || !WanosInteraction.IsReady)
                return;
            if (_contentAudioPaused)
                return;
            if (_presetTransitionHeld)
                return;
            if (_restartPending)
            {
                // 等 Stop 的淡出跑完再重新 Post，避免叠音。
                if (Time.realtimeSinceStartup < _restartNotBefore)
                    return;
                _restartPending = false;
            }
            if (!WanosInteraction.TryGetListenerPosition(out Vector3 listener))
                return;

            bool campfireIgnited = CampfireWeatherGate.IsVisuallyIgnited;
            if (campfireIgnited != _campfireIgnitedLatched)
            {
                _campfireIgnitedLatched = campfireIgnited;
                InvalidateListenerStill();
            }

            // Listener-still skip: if the listener moved < 0.09m since the last
            // tick, the previous Enter/Exit decisions are still valid. Cheaper
            // than scanning all sources for nothing.
            // Soft-stop / gain-slew need ticks even when the listener stands still.
            if (!ShouldEvaluateListener(listener) && !AnySoftStopping() && !AnyGainSlewPending())
                return;

            int n = Sources.Count;
            EnsureEvalScratch(n);

            for (int i = 0; i < n; i++)
            {
                var src = Sources[i];
                var spec = src.Spec;
                bool want;
                Vector3 pos;

                if (spec.Kind == AmbienceEmitterKind.River
                    || spec.Kind == AmbienceEmitterKind.Waterfall
                    || spec.Kind == AmbienceEmitterKind.Ocean
                    || spec.Kind == AmbienceEmitterKind.Lake)
                {
                    // Emitter 跟真实水面最近点；增益：
                    // - Ocean：仍用 catalog 岸点（跟最近点会让 dist≈0、全岸满音）
                    // - River/Waterfall：必须用表面最近点，否则沿河远离 catalog 原点时
                    //   gainDist 上百米 → -96dB，贴着河也听不见（见 play dist=235 gain=-96 日志）
                    pos = EvaluateWaterSource(src, listener, out want);
                    float gainDist = spec.Kind == AmbienceEmitterKind.Ocean
                        ? Distance(spec, listener, ResolveEmitterPosition(spec))
                        : Distance(spec, listener, pos);
                    _wantScratch[i] = want;
                    _posScratch[i] = pos;
                    _distScratch[i] = gainDist;
                }
                else
                {
                    pos = ResolveEmitterPosition(spec);
                    float dist = Distance(spec, listener, pos);
                    want = ShouldPlay(src, listener, pos);
                    // 草地/高原：触发球可大于增益曲线远端；圈内已播只靠衰减贴地板，
                    // 未播且增益已近静音则不开播（避免远距空 Post）。
                    if (want && !src.Playing && IsBiomeKind(spec.Kind) && spec.Kind != AmbienceEmitterKind.Forest)
                    {
                        if (GainDbForDistance(spec, dist) < -40f)
                            want = false;
                    }
                    _wantScratch[i] = want;
                    _posScratch[i] = pos;
                    _distScratch[i] = dist;
                }
            }

            // Boluo：同名 Play（Forest/Grassland/Plateau）只 Post 一次；一侧 Exit 时若
            // 另一侧仍持有共用 bank，不得 Stop/Unload。这里压成「每 bank 只保留最近床位」，
            // 床位切换时 Transfer 播放权（不 Stop↔Play 叠音）。
            CollapseExclusiveSharedPlayWants(n);
            ApplyLakeWaterfallExclusion(n);
            TransferExclusiveSharedPlays(n);

            for (int i = 0; i < n; i++)
            {
                var src = Sources[i];
                if (_wantScratch[i])
                {
                    if (src.SoftStopping)
                        CancelSoftStop(src);
                    if (!src.Playing)
                        Log.Info($"[AmbienceZone] want->keep id={src.Spec.Id} bank={src.Spec.Bank} event={src.Spec.PlayEvent}");
                    KeepSourcePlaying(src, _posScratch[i], _distScratch[i]);
                }
                else if (src.Playing && !src.SoftStopping)
                {
                    // 同组已无任何床位 want：软衰减到静音再 Stop（不靠可能 stub 的 native fade）。
                    // 边缘出区用 WalkSoftStopSeconds，避免 0.25s 拉到 −96dB 的电流音。
                    Log.Info($"[AmbienceZone] want->stop id={src.Spec.Id} bank={src.Spec.Bank}");
                    BeginSoftStop(src, WalkSoftStopSeconds);
                }
            }
        }

        static void EnsureEvalScratch(int n)
        {
            if (_wantScratch != null && _wantScratch.Length >= n)
                return;
            _wantScratch = new bool[n];
            _distScratch = new float[n];
            _posScratch = new Vector3[n];
        }

        /// <summary>
        /// Forest / Grassland / Plateau 多床位共用一个 bank + 一个 Play_*：只留最近床位 want=true。
        /// Sticky hysteresis: keep the currently playing bed until another is clearly nearer.
        /// </summary>
        static void CollapseExclusiveSharedPlayWants(int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (!_wantScratch[i])
                    continue;
                string bank = Sources[i].Spec.Bank;
                if (!IsExclusiveSharedPlayBank(bank))
                    continue;

                int best = i;
                float bestDist = _distScratch[i];
                int playing = -1;
                float playingDist = float.MaxValue;
                for (int j = i; j < n; j++)
                {
                    if (!string.Equals(Sources[j].Spec.Bank, bank, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Sources[j].Playing)
                    {
                        playing = j;
                        playingDist = _distScratch[j];
                    }
                    if (!_wantScratch[j])
                        continue;
                    if (_distScratch[j] < bestDist)
                    {
                        bestDist = _distScratch[j];
                        best = j;
                    }
                }

                // Midpoint walk without sticky → Transfer every Eval → crackle.
                if (playing >= 0 && _wantScratch[playing] && playing != best
                    && playingDist <= bestDist + ExclusiveShareHysteresisMeters)
                {
                    best = playing;
                }

                for (int j = i; j < n; j++)
                {
                    if (!_wantScratch[j])
                        continue;
                    if (!string.Equals(Sources[j].Spec.Bank, bank, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (j != best)
                        _wantScratch[j] = false;
                }
            }
        }

        /// <summary>
        /// 多床位 / 多岸点共用同一 Play/Stop：只留最近一处 Post，避免叠成「全岛一起响」。
        /// Ocean 两岸共用 Ocean_Calm_Quad；biome / 河 / 瀑同 Boluo 同名事件只打一次。
        /// </summary>
        static bool IsExclusiveSharedPlayBank(string bank)
        {
            if (string.IsNullOrEmpty(bank))
                return false;
            return string.Equals(bank, "Boluodao_Forest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "Boluodao_Grassland", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "Boluodao_Plateau", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "Ocean_Calm_Quad", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "Lake_Quad", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "River_Quad", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "River_ADD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "Waterfall_Big_Quad", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "Waterfall_Small_Quad", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Lake ↔ Waterfall mutual exclusion: only the nearer side keeps want=true.
        /// Sticky hysteresis so the lake/fall boundary does not flip-flop Stop↔Play.
        /// Stop path KeepBank so exclusivity never unloads the suppressed side's bank.
        /// <see cref="AmbienceEmitterSpec.FixedWorldAnchor"/> 观景点瀑不参与互斥（可与湖同播）。
        /// </summary>
        static void ApplyLakeWaterfallExclusion(int n)
        {
            int bestLake = -1;
            float bestLakeDist = float.MaxValue;
            int bestFall = -1;
            float bestFallDist = float.MaxValue;
            bool lakePlaying = false;
            bool fallPlaying = false;
            for (int i = 0; i < n; i++)
            {
                if (!_wantScratch[i])
                    continue;
                var spec = Sources[i].Spec;
                if (spec.FixedWorldAnchor)
                    continue;
                AmbienceEmitterKind kind = spec.Kind;
                float d = _distScratch[i];
                if (kind == AmbienceEmitterKind.Lake)
                {
                    if (Sources[i].Playing)
                        lakePlaying = true;
                    if (d < bestLakeDist)
                    {
                        bestLakeDist = d;
                        bestLake = i;
                    }
                }
                else if (kind == AmbienceEmitterKind.Waterfall)
                {
                    if (Sources[i].Playing)
                        fallPlaying = true;
                    if (d < bestFallDist)
                    {
                        bestFallDist = d;
                        bestFall = i;
                    }
                }
            }

            if (bestLake < 0 || bestFall < 0)
                return;

            bool preferLake = bestLakeDist <= bestFallDist;
            if (lakePlaying && !fallPlaying
                && bestLakeDist <= bestFallDist + ExclusiveShareHysteresisMeters)
                preferLake = true;
            else if (fallPlaying && !lakePlaying
                && bestFallDist <= bestLakeDist + ExclusiveShareHysteresisMeters)
                preferLake = false;

            for (int i = 0; i < n; i++)
            {
                if (!_wantScratch[i])
                    continue;
                var spec = Sources[i].Spec;
                if (spec.FixedWorldAnchor)
                    continue;
                AmbienceEmitterKind kind = spec.Kind;
                if (preferLake && kind == AmbienceEmitterKind.Waterfall)
                    _wantScratch[i] = false;
                else if (!preferLake && kind == AmbienceEmitterKind.Lake)
                    _wantScratch[i] = false;
            }
        }

        /// <summary>
        /// 共用 bank 床位切换：把 Playing/Emitter/Voice 转到胜出床位，禁止 Stop↔Play 叠两层鸟叫。
        /// </summary>
        static void TransferExclusiveSharedPlays(int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (!_wantScratch[i])
                    continue;
                string bank = Sources[i].Spec.Bank;
                if (!IsExclusiveSharedPlayBank(bank))
                    continue;

                int winner = i;
                int playing = -1;
                for (int j = 0; j < n; j++)
                {
                    if (!string.Equals(Sources[j].Spec.Bank, bank, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Sources[j].Playing)
                        playing = j;
                }

                if (playing < 0 || playing == winner)
                    continue;

                Log.Info(
                    $"[AmbienceZone] transfer bank={bank} from={Sources[playing].Spec.Id} to={Sources[winner].Spec.Id}");
                TransferPlayOwnership(
                    Sources[playing], Sources[winner], _posScratch[winner], _distScratch[winner]);
            }
        }

        static void TransferPlayOwnership(RuntimeSource from, RuntimeSource to, Vector3 toPos, float toDist)
        {
            // 胜出床位接管 emitter / voice；from 只清本地态，不 Stop、不 Release bank。
            GameObject emitter = from.Emitter;
            int fromIdx = Sources.IndexOf(from);
            int toIdx = Sources.IndexOf(to);

            // 池槽跟 source index 绑定：把 in-use 从 from 转到 to，避免下次 Acquire 误判。
            if (_emitterInUse != null && fromIdx >= 0 && toIdx >= 0
                && fromIdx < _emitterInUse.Length && toIdx < _emitterInUse.Length)
            {
                _emitterInUse[fromIdx] = false;
                _emitterInUse[toIdx] = true;
                // 若池按 index 预分配，把 GO 指针也换到 to 槽（同一实例即可）。
                if (_emitterPool != null && emitter != null
                    && toIdx < _emitterPool.Length && fromIdx < _emitterPool.Length)
                {
                    _emitterPool[toIdx] = emitter;
                }
            }

            to.Emitter = emitter;
            to.VoiceIds.Clear();
            for (int i = 0; i < from.VoiceIds.Count; i++)
                to.VoiceIds.Add(from.VoiceIds[i]);
            to.Playing = true;
            to.NeedsRepost = true;
            to.BankHeld = from.BankHeld;

            for (int i = 0; i < to.VoiceIds.Count; i++)
                VoiceToSource[to.VoiceIds[i]] = toIdx;

            from.Playing = false;
            from.NeedsRepost = false;
            from.LastPostedDistance = float.NaN;
            from.BankHeld = false;
            from.VoiceIds.Clear();
            from.Emitter = null;

            // Inherit applied gain / Distance RTPC so transfer does not zipper from NaN→target.
            to.LastAppliedGainDb = from.LastAppliedGainDb;
            to.LastTargetGainDb = from.LastTargetGainDb;
            to.GainSlewVelocity = from.GainSlewVelocity;
            to.LastSlewedDistance = from.LastSlewedDistance;
            to.LastTargetDistance = from.LastTargetDistance;
            to.LastPostedDistance = from.LastPostedDistance;
            from.LastAppliedGainDb = float.NaN;
            from.LastTargetGainDb = float.NaN;
            from.GainSlewVelocity = 0f;
            from.LastSlewedDistance = float.NaN;
            from.LastTargetDistance = float.NaN;
            from.LastPostedDistance = float.NaN;

            if (to.Emitter != null)
            {
                // Clamp first transfer jump so Distance RTPC / spatializer do not zipper.
                Vector3 cur = to.Emitter.transform.position;
                Vector3 clamped = ClampEmitterStep(cur, toPos);
                to.Emitter.transform.position = clamped;
                WanosInteraction.EnsureEmitter(to.Emitter, clamped, to.Spec.EnterRadius);
                float jump = float.IsNaN(to.LastSlewedDistance)
                    ? float.PositiveInfinity
                    : Mathf.Abs(toDist - to.LastSlewedDistance);
                if (jump > TeleportGainSnapMeters)
                {
                    Log.Info(
                        $"[AmbienceZone] transfer-snap bank={to.Spec.Bank} id={to.Spec.Id} Δdist={jump:F1}m");
                }
                PushSlewedAttenuation(to, toDist, GainDbForDistance(to.Spec, toDist));
                EnsureAttenuationDrive();
            }
        }

        static Vector3 ClampEmitterStep(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float mag = delta.magnitude;
            if (mag <= MaxEmitterJumpMetersPerEval || mag < 1e-4f)
                return to;
            return from + delta * (MaxEmitterJumpMetersPerEval / mag);
        }

        /// <summary>
        /// River / Waterfall / Ocean / Lake：算出该床位的代表点 + 是否该播。
        /// - River/Waterfall：用 <see cref="WaterSurfaceProbe"/> 查场景里真实水面最近点
        ///   （运行期采样，跟场景一致；走近水边距离就变近，贴着水一定有声）。
        /// - Ocean/Lake：catalog 里的岸点 / 湖点（xz 距离）。
        /// </summary>
        static Vector3 EvaluateWaterSource(RuntimeSource src, Vector3 listener, out bool want)
        {
            var spec = src.Spec;

            // Ocean / Lake 高度门控：原先只在 ShouldPlay（biome 路径）里，岸线网格路径漏了，
            // 导致进岛爬高后海声仍按 xz 贴岸衰减「一直响」。
            if (spec.Kind == AmbienceEmitterKind.Ocean
                && listener.y > AmbienceZoneCatalog.OceanMaxPlayerY)
            {
                want = false;
                return ResolveEmitterPosition(spec);
            }
            if (spec.Kind == AmbienceEmitterKind.Lake
                && (listener.y < AmbienceZoneCatalog.LakeMinPlayerY
                    || listener.y > AmbienceZoneCatalog.LakeMaxPlayerY))
            {
                want = false;
                return ResolveEmitterPosition(spec);
            }

            float enter = spec.EnterRadius;
            float exit = src.Playing ? EffectiveExitRadius(spec) : enter;
            // Ocean: exit already includes +12m near. River: no near on stop radius (Boluo audible-split).
            float nearExtra = NearMetersForExitGrace(spec);

            // 观景点固定锚：禁止用水面最近点（否则湖边观瀑会量到 Splash ~86m 永远不进区）。
            if (spec.FixedWorldAnchor)
            {
                Vector3 anchor = ResolveEmitterPosition(spec);
                float dAnchor = Distance(spec, listener, anchor);
                bool mutedAnchor = IsWaterfallMutedByCampfire(spec, listener);
                want = !mutedAnchor && KeepPlayingWithExitGrace(src, dAnchor, enter, exit, nearExtra);
                return anchor;
            }

            if ((spec.Kind == AmbienceEmitterKind.River || spec.Kind == AmbienceEmitterKind.Waterfall)
                && (_inlandWaterReady || IslandWorldService.InlandWaterReady))
            {
                var kind = spec.Kind == AmbienceEmitterKind.River
                    ? WaterSurfaceProbe.SurfaceKind.River
                    : WaterSurfaceProbe.SurfaceKind.Waterfall;

                // 场景里还没内河（还没加载完）时探针没数据，先按 catalog / MapItem 单点走。
                if (WaterSurfaceProbe.TryNearestAny(kind, listener, out Vector3 nearest, out float dWater))
                {
                    bool muted = IsWaterfallMutedByCampfire(spec, listener);
                    want = !muted && KeepPlayingWithExitGrace(src, dWater, enter, exit, nearExtra);
                    return nearest;
                }
            }

            // 降级：catalog / MapItem 单点
            Vector3 fallback = ResolveEmitterPosition(spec);
            float d = Distance(spec, listener, fallback);
            bool mutedFb = IsWaterfallMutedByCampfire(spec, listener);
            want = !mutedFb && KeepPlayingWithExitGrace(src, d, enter, exit, nearExtra);
            return fallback;
        }

        static float EffectiveExitRadius(AmbienceEmitterSpec spec)
        {
            if (spec.Kind == AmbienceEmitterKind.Ocean)
                return Mathf.Max(spec.ExitRadius, spec.EnterRadius + AmbienceZoneCatalog.OceanNearSurfaceMeters);
            return spec.ExitRadius;
        }

        static bool IsWaterfallMutedByCampfire(AmbienceEmitterSpec spec, Vector3 listener)
        {
            if (spec.Kind != AmbienceEmitterKind.Waterfall)
                return false;
            // Big waterfall only (enter ~42); small keeps full radius.
            if (spec.EnterRadius > AmbienceZoneCatalog.WaterfallBigEnterMeters + 0.5f)
                return false;
            return IsInsideCampfireWaterfallMute(listener);
        }

        static Vector3 ResolveEmitterPosition(AmbienceEmitterSpec spec)
        {
            if (spec.Kind == AmbienceEmitterKind.River || spec.Kind == AmbienceEmitterKind.Waterfall)
            {
                if (spec.RiverCatalogIndex >= 0)
                {
                    var items = GameEntry.World?.MapItemSpawner?.AllMapItems;
                    if (items != null && (uint)spec.RiverCatalogIndex < (uint)items.Count)
                        return items[spec.RiverCatalogIndex].Pos;
                }
            }
            return spec.Position;
        }

        /// <summary>Repost 时取代表点（与 Evaluate 走同一条水面查询路径，探针没数据时退回 catalog）。</summary>
        static Vector3 ResolveRepostPosition(AmbienceEmitterSpec spec, Vector3 listener)
        {
            if (spec.FixedWorldAnchor)
                return ResolveEmitterPosition(spec);
            if (spec.Kind == AmbienceEmitterKind.River || spec.Kind == AmbienceEmitterKind.Waterfall)
            {
                var kind = spec.Kind == AmbienceEmitterKind.River
                    ? WaterSurfaceProbe.SurfaceKind.River
                    : WaterSurfaceProbe.SurfaceKind.Waterfall;
                if (WaterSurfaceProbe.TryNearestAny(kind, listener, out Vector3 anchor, out _))
                    return anchor;
            }
            return ResolveEmitterPosition(spec);
        }

        static bool ShouldPlay(RuntimeSource src, Vector3 listener, Vector3 emitterPos)
        {
            var spec = src.Spec;
            if (spec.Kind == AmbienceEmitterKind.Campfire && !CampfireWeatherGate.IsVisuallyIgnited)
                return false;
            if (spec.Kind == AmbienceEmitterKind.Ocean && listener.y > AmbienceZoneCatalog.OceanMaxPlayerY)
                return false;
            if (spec.Kind == AmbienceEmitterKind.Lake &&
                (listener.y < AmbienceZoneCatalog.LakeMinPlayerY || listener.y > AmbienceZoneCatalog.LakeMaxPlayerY))
                return false;

            float dist = Distance(spec, listener, emitterPos);
            if (IsWaterfallMutedByCampfire(spec, listener))
                return false;

            float enter = spec.EnterRadius;
            float exit = src.Playing ? EffectiveExitRadius(spec) : enter;
            float nearExtra = NearMetersForExitGrace(spec);
            return KeepPlayingWithExitGrace(src, dist, enter, exit, nearExtra);
        }

        /// <summary>Big waterfall muted inside campfire 22m silence ring.</summary>
        static bool IsInsideCampfireWaterfallMute(Vector3 listener)
        {
            float mute = AmbienceZoneCatalog.WaterfallCampfireMuteMeters;
            float muteSq = mute * mute;
            for (int i = 0; i < Sources.Count; i++)
            {
                var s = Sources[i].Spec;
                if (s.Kind != AmbienceEmitterKind.Campfire)
                    continue;
                Vector3 c = ResolveEmitterPosition(s);
                float dx = listener.x - c.x;
                float dy = listener.y - c.y;
                float dz = listener.z - c.z;
                if (dx * dx + dy * dy + dz * dz <= muteSq)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// River：不加 near（可听带与停播带已拆开）。Ocean / Forest / Grassland / Plateau：
        /// 出球后贴表面 <see cref="AmbienceZoneCatalog.NearSurfaceMeters"/> 继续播，
        /// 再远仍有 <see cref="AmbienceZoneCatalog.ExitGraceSeconds"/> 时间宽限
        /// （对齐 Boluo Trigger Exit→Enter 抖动，避免 UnLoad↔Play 刺啦）。
        /// </summary>
        static float NearMetersForExitGrace(AmbienceEmitterSpec spec)
        {
            if (spec == null)
                return 0f;
            switch (spec.Kind)
            {
                case AmbienceEmitterKind.River:
                    return 0f;
                case AmbienceEmitterKind.Ocean:
                    // EffectiveExitRadius 已含 OceanNearSurface；时间宽限仍要，near 不再叠一层。
                    return 0f;
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.Waterfall:
                case AmbienceEmitterKind.Forest:
                case AmbienceEmitterKind.Grassland:
                case AmbienceEmitterKind.Plateau:
                case AmbienceEmitterKind.Campfire:
                    return AmbienceZoneCatalog.NearSurfaceMeters;
                default:
                    return AmbienceZoneCatalog.NearSurfaceMeters;
            }
        }

        /// <summary>
        /// Boluo trigger-overlap keep：进区立刻播；已播时 exit+near 内保持；
        /// 刚出 near 后 <see cref="AmbienceZoneCatalog.ExitGraceSeconds"/> 内仍保持（取消 Stop/UnLoad）。
        /// </summary>
        static bool KeepPlayingWithExitGrace(
            RuntimeSource src, float dist, float enterRadius, float exitRadius, float nearExtraMeters)
        {
            float enter = enterRadius > 0f ? enterRadius : 0f;
            float exit = exitRadius > enter ? exitRadius : enter;

            if (dist <= enter || (src.Playing && dist <= exit))
            {
                src.OutsideSince = -1f;
                return true;
            }

            if (!src.Playing)
            {
                src.OutsideSince = -1f;
                return false;
            }

            if (src.OutsideSince < 0f)
                src.OutsideSince = Time.realtimeSinceStartup;

            if (nearExtraMeters > 0f && dist <= exit + nearExtraMeters)
                return true;

            return Time.realtimeSinceStartup - src.OutsideSince < AmbienceZoneCatalog.ExitGraceSeconds;
        }

        static bool IsBiomeKind(AmbienceEmitterKind kind) =>
            kind == AmbienceEmitterKind.Forest
            || kind == AmbienceEmitterKind.Grassland
            || kind == AmbienceEmitterKind.Plateau;

        static float Distance(AmbienceEmitterSpec spec, Vector3 listener, Vector3 emitterPos)
        {
            if (spec.UseXzDistance)
            {
                float dx = listener.x - emitterPos.x;
                float dz = listener.z - emitterPos.z;
                return Mathf.Sqrt(dx * dx + dz * dz);
            }

            return Vector3.Distance(listener, emitterPos);
        }

        static float _nextAttenuationLogTime;

        static void KeepSourcePlaying(RuntimeSource src, Vector3 pos, float dist)
        {
            var spec = src.Spec;
            // Already audible: never re-Load / re-Post (Boluo「已播禁止再 Loading」).
            // Only pose + attenuation. Voice-empty while Playing → Repost once.
            if (src.Playing && src.VoiceIds.Count > 0)
            {
                if (src.Emitter != null)
                {
                    // Water nearest-point / catalog handoff can jump tens of meters per Eval.
                    pos = ClampEmitterStep(src.Emitter.transform.position, pos);
                    src.Emitter.transform.position = pos;
                    WanosInteraction.EnsureEmitter(src.Emitter, pos, spec.EnterRadius);

                    // 每 tick 都推增益（不靠 MinPostDistanceDelta）：Post 后引擎可能重置 gain enable。
                    float targetDb = GainDbForDistance(spec, dist);
                    float prevDist = src.LastSlewedDistance;
                    float gainDb = PushSlewedAttenuation(src, dist, targetDb);
                    EnsureAttenuationDrive();

                    if (!float.IsNaN(prevDist) && Mathf.Abs(dist - prevDist) > TeleportGainSnapMeters)
                    {
                        Log.Info(
                            $"[AmbienceZone] dist-jump id={spec.Id} {prevDist:F1}→{dist:F1}m (snap/slew)");
                    }

                    if (Time.realtimeSinceStartup >= _nextAttenuationLogTime)
                    {
                        _nextAttenuationLogTime = Time.realtimeSinceStartup + 1f;
                        ulong oid = 0;
                        WanosInteraction.TryGetEmitterObjectId(src.Emitter, out oid);
                        float posted = float.IsNaN(src.LastSlewedDistance) ? dist : src.LastSlewedDistance;
                        Log.Info(
                            $"[AmbienceZone] 3D id={spec.Id} bank={spec.Bank} dist={dist:F1}m posted={posted:F1}m gain={gainDb:F1}dB target={targetDb:F1}dB obj={oid} voices={src.VoiceIds.Count}");
                    }
                }
                src.NeedsRepost = true;
                return;
            }

            if (!src.Playing)
                StartSource(src, pos, dist);
            else
                RepostSource(src, pos, dist);
        }

        /// <summary>
        /// Boluo <c>WanosCurveForGain.GainInvTransform</c>：显示 Y → 引擎衰减增益（dB）。
        /// </summary>
        static float GainInvTransform(float displayY)
        {
            const float d1 = -9f;
            const float d2 = -12f;
            const float d3 = -15f;
            if (displayY >= d1)
                return displayY;
            if (displayY >= d2)
                return -9f + 3f * (displayY + 9f);
            if (displayY >= d3)
                return -18f + 6f * (displayY + 12f);
            return -36f + (80f / 3f) * (displayY + 15f);
        }

        static Vector2[] GainCurveForSpec(AmbienceEmitterSpec spec)
        {
            switch (spec.Kind)
            {
                case AmbienceEmitterKind.Forest:
                    return ForestGainCurve;
                case AmbienceEmitterKind.Grassland:
                    // 卫星 Enter≈75 / 曲线远端 60；主区 Enter≈180 / 曲线远端 150。
                    return spec.EnterRadius <= 100f ? GrasslandSecondaryGainCurve : GrasslandGainCurve;
                case AmbienceEmitterKind.Plateau:
                    return PlateauGainCurve;
                case AmbienceEmitterKind.Ocean:
                    return OceanGainCurve;
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.River:
                    return Point20GainCurve;
                case AmbienceEmitterKind.Campfire:
                    return CampfireGainCurve;
                case AmbienceEmitterKind.Waterfall:
                    // Big enter 42 / exit 50；Small enter 50 / exit 72.
                    return spec.ExitRadius >= 70f ? WaterfallSmallGainCurve : WaterfallBigGainCurve;
                default:
                    return OceanGainCurve;
            }
        }

        /// <summary>
        /// Boluo CurveForGain 折线：距离 → 显示 Y → <see cref="GainInvTransform"/>。
        /// Forest 在 79m 内显示 Y=0（满音量）是 Boluo 原曲线，不是 bug；走出 100m+ 才明显掉。
        /// 河 / 瀑 / 湖：不用 display→engine 变换（远端会压到 -96dB 听感「没声」），
        /// 改成直接引擎 dB：4m 内满音，到 Exit 半径 -24dB，靠近必能听见。
        /// </summary>
        static float GainDbForDistance(AmbienceEmitterSpec spec, float distMeters)
        {
            if (spec != null
                && (spec.Kind == AmbienceEmitterKind.River
                    || spec.Kind == AmbienceEmitterKind.Waterfall
                    || spec.Kind == AmbienceEmitterKind.Lake))
            {
                float far = spec.ExitRadius > 1f ? spec.ExitRadius : Mathf.Max(1f, spec.EnterRadius);
                if (distMeters <= 4f)
                    return 0f;
                float t = Mathf.Clamp01(Mathf.InverseLerp(4f, far, distMeters));
                return Mathf.Lerp(0f, -24f, t);
            }

            Vector2[] curve = GainCurveForSpec(spec);
            float displayY = EvaluateGainCurveDisplayY(curve, distMeters);
            return GainInvTransform(displayY);
        }

        static float EvaluateGainCurveDisplayY(Vector2[] curve, float distMeters)
        {
            if (curve == null || curve.Length == 0)
                return CurveFarDisplayDb;
            if (distMeters <= curve[0].x)
                return curve[0].y;
            for (int i = 1; i < curve.Length; i++)
            {
                if (distMeters <= curve[i].x)
                {
                    float t = Mathf.InverseLerp(curve[i - 1].x, curve[i].x, distMeters);
                    return Mathf.Lerp(curve[i - 1].y, curve[i].y, t);
                }
            }
            return curve[curve.Length - 1].y;
        }

        static void StartSource(RuntimeSource src, Vector3 pos, float dist)
        {
            var spec = src.Spec;
            Log.Info($"[AmbienceZone] StartSource enter id={spec.Id} bank={spec.Bank}");

            if (!src.BankHeld)
            {
                if (!WanosBankStore.Retain(spec.Bank))
                {
                    Log.Warning($"[AmbienceZone] StartSource Retain FAIL id={spec.Id} bank={spec.Bank} (added to retry)");
                    _pendingRetryBanks.Add(spec.Bank);
                    return;
                }

                src.BankHeld = true;
            }

            string[] plays = ResolvePlayEventsForSingleEmitter(spec);
            if (plays.Length == 0)
            {
                Log.Warning($"[AmbienceZone] StartSource no-events id={spec.Id} bank={spec.Bank} (bank released)");
                if (src.BankHeld)
                {
                    WanosBankStore.Release(spec.Bank);
                    src.BankHeld = false;
                }
                return;
            }

            try
            {
                EnsureHost();
                src.Emitter = AcquireEmitterForSource(src) ?? CreateEmitter(pos);
                src.Emitter.transform.position = pos;
                WanosInteraction.EnsureEmitter(src.Emitter, pos, spec.EnterRadius);
                src.NeedsRepost = true;
                // 先 Post，再 enable+gain（Boluo CurveForGain 也是 voice 起来后持续推）。
                PostSourceEvents(src, plays);
                float targetDb = GainDbForDistance(spec, dist);
                // Soft enter: write quiet first frame; later frames SmoothDamp toward target.
                src.LastTargetGainDb = targetDb;
                src.LastAppliedGainDb = Mathf.Min(SoftEnterStartDb, targetDb);
                src.GainSlewVelocity = 0f;
                src.LastSlewedDistance = dist;
                src.LastTargetDistance = dist;
                float gainDb = WriteDuckedGain(src, dist, src.LastAppliedGainDb);
                src.Playing = true;
                EnsureAttenuationDrive();
                Log.Info(
                    $"[AmbienceZone] play id={spec.Id} events={string.Join(",", plays)} pos={pos} radius={spec.EnterRadius} dist={dist:F1} gain={gainDb:F1}dB");
            }
            catch (Exception e)
            {
                Log.Error($"[AmbienceZone] StartSource EXC id={spec.Id}: {e}");
                src.Playing = false;
            }
        }

        static void RepostSource(RuntimeSource src, Vector3 pos, float dist)
        {
            var spec = src.Spec;
            if (src.Emitter == null)
            {
                EnsureHost();
                src.Emitter = AcquireEmitterForSource(src) ?? CreateEmitter(pos);
            }
            src.Emitter.transform.position = pos;
            WanosInteraction.EnsureEmitter(src.Emitter, pos, spec.EnterRadius);

            string[] plays = ResolvePlayEventsForSingleEmitter(spec);
            if (plays.Length == 0)
                return;

            PostSourceEvents(src, plays);
            float targetDb = GainDbForDistance(spec, dist);
            src.LastTargetGainDb = targetDb;
            src.LastAppliedGainDb = Mathf.Min(SoftEnterStartDb, targetDb);
            src.GainSlewVelocity = 0f;
            src.LastSlewedDistance = dist;
            src.LastTargetDistance = dist;
            WriteDuckedGain(src, dist, src.LastAppliedGainDb);
            src.NeedsRepost = true;
            EnsureAttenuationDrive();
        }

        /// <summary>
        /// Water Quad bank 走 catalog 多声道（同点叠轨，对齐 Boluo 响度）；
        /// biome / 点源只用 Spec.PlayEvent，避免 Forest 重复叠音。
        /// </summary>
        static string[] ResolvePlayEventsForSingleEmitter(AmbienceEmitterSpec spec)
        {
            if (spec == null)
                return Array.Empty<string>();
            switch (spec.Kind)
            {
                case AmbienceEmitterKind.Ocean:
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.River:
                case AmbienceEmitterKind.Waterfall:
                    return AmbienceEventCatalog.ResolvePlayEvents(spec.Bank, spec.PlayEvent);
                default:
                    if (!string.IsNullOrEmpty(spec.PlayEvent))
                        return new[] { spec.PlayEvent };
                    return AmbienceEventCatalog.ResolvePlayEvents(spec.Bank, spec.PlayEvent);
            }
        }

        // Last-resort fallback when the pool is missing. Keep behavior identical
        // to the original implementation (new GameObject under _root at pos).
        static GameObject CreateEmitter(Vector3 pos)
        {
            var go = new GameObject(EmitterName(pos));
            go.transform.SetParent(_root, false);
            go.transform.position = pos;
            return go;
        }

        static string EmitterName(Vector3 pos) => $"Amb_{pos.x:F0}_{pos.y:F0}_{pos.z:F0}";

        static void PostSourceEvents(RuntimeSource src, string[] plays)
        {
            // 清旧 voice 记录
            for (int i = 0; i < src.VoiceIds.Count; i++)
                VoiceToSource.Remove(src.VoiceIds[i]);
            src.VoiceIds.Clear();

            if (src.Emitter == null)
                return;

            int srcIdx = Sources.IndexOf(src);
            for (int i = 0; i < plays.Length; i++)
            {
                string play = plays[i];
                if (string.IsNullOrEmpty(play))
                    continue;
                ulong id = WanosInteraction.PostEvent(play, src.Emitter);
                if (id != 0)
                {
                    src.VoiceIds.Add(id);
                    VoiceToSource[id] = srcIdx;
                }
            }
        }

        /// <summary>
        /// Walk-stop KeepBank for river/ocean/lake/waterfall (doc: 只停播常保留资源；
        /// 湖瀑互斥同理). Biome/campfire Release with per-kind delay.
        /// Preset clear uses <paramref name="forceReleaseBank"/>.
        /// </summary>
        static bool ShouldKeepBankOnWalkStop(AmbienceEmitterSpec spec)
        {
            if (spec == null)
                return false;
            switch (spec.Kind)
            {
                case AmbienceEmitterKind.River:
                case AmbienceEmitterKind.Ocean:
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.Waterfall:
                    return true;
                default:
                    return false;
            }
        }

        static float UnloadDelaySecondsFor(AmbienceEmitterSpec spec)
        {
            if (spec == null)
                return WanosBankStore.DefaultUnloadDelaySeconds;
            switch (spec.Kind)
            {
                case AmbienceEmitterKind.Forest:
                    return AmbienceZoneCatalog.ForestUnloadDelaySeconds;
                case AmbienceEmitterKind.Grassland:
                    return spec.EnterRadius <= 100f
                        ? AmbienceZoneCatalog.GrasslandSecondaryUnloadDelaySeconds
                        : AmbienceZoneCatalog.GrasslandUnloadDelaySeconds;
                case AmbienceEmitterKind.Plateau:
                    return AmbienceZoneCatalog.PlateauUnloadDelaySeconds;
                case AmbienceEmitterKind.Campfire:
                    return AmbienceZoneCatalog.CampfireUnloadDelaySeconds;
                default:
                    return WanosBankStore.DefaultUnloadDelaySeconds;
            }
        }

        /// <summary>
        /// 停止 source：
        /// 1. 对每个 voice 显式 Stop with fade
        /// 2. 发 StopEvent
        /// 3. 释放 emitter
        /// 4. 按策略 Release bank（或 KeepBank）
        /// </summary>
        static float PushSlewedAttenuation(RuntimeSource src, float dist, float targetDb)
        {
            src.LastTargetGainDb = targetDb;
            src.LastTargetDistance = dist;

            float prevDist = src.LastSlewedDistance;
            bool snap = float.IsNaN(src.LastAppliedGainDb)
                || float.IsNaN(prevDist)
                || Mathf.Abs(dist - prevDist) > TeleportGainSnapMeters;

            float slewedDist;
            float slewedGain;
            if (snap)
            {
                slewedDist = dist;
                slewedGain = targetDb;
                src.GainSlewVelocity = 0f;
            }
            else
            {
                // Eval is 4 Hz; SmoothDamp uses real dt so edge walks stay continuous.
                float dt = Mathf.Max(Time.deltaTime, 1f / 120f);
                slewedDist = Mathf.MoveTowards(
                    prevDist, dist, MaxDistanceSlewMetersPerSecond * dt);
                float current = src.LastAppliedGainDb;
                slewedGain = Mathf.SmoothDamp(
                    current, targetDb, ref src.GainSlewVelocity, GainSmoothTimeSeconds, Mathf.Infinity, dt);
            }

            src.LastSlewedDistance = slewedDist;
            src.LastAppliedGainDb = slewedGain;
            return WriteDuckedGain(src, slewedDist, slewedGain);
        }

        static float WriteDuckedGain(RuntimeSource src, float dist, float preDuckGainDb)
        {
            // dist here is the Distance RTPC actually written (already slewed by caller).
            src.LastPostedDistance = dist;
            if (float.IsNaN(src.LastSlewedDistance))
                src.LastSlewedDistance = dist;
            float ducked = Mathf.Lerp(GainFloorDb, preDuckGainDb, ContentAudioDuck.Level01);
            if (src.Emitter != null)
                WanosInteraction.ApplyEmitterDistanceAttenuation(src.Emitter, dist, ducked);
            return ducked;
        }

        /// <summary>
        /// Boluo CurveForGain 每 FixedUpdate 推增益；本工程 Evaluate 只有 4Hz，
        /// 边缘走时必须每帧把 SmoothDamp / Distance RTPC 追完，否则听感就是电流音。
        /// </summary>
        static void EnsureAttenuationDrive()
        {
            if (!_started)
                return;
            int gen = ++_attenuationDriveGen;
            DriveAttenuationSlews(gen).Forget();
        }

        static async UniTaskVoid DriveAttenuationSlews(int gen)
        {
            while (gen == _attenuationDriveGen && _started)
            {
                TickAttenuationSlews();
                if (!AnyGainSlewPending() && !AnyDistanceSlewPending())
                    return;
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }

        static void TickAttenuationSlews()
        {
            if (!_started || !_catalogBuilt || _contentAudioPaused || _presetTransitionHeld)
                return;
            float dt = Mathf.Max(Time.deltaTime, 1f / 120f);
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.Playing || src.SoftStopping || src.Emitter == null)
                    continue;
                if (float.IsNaN(src.LastTargetGainDb) || float.IsNaN(src.LastTargetDistance))
                    continue;

                float targetDist = src.LastTargetDistance;
                float targetGain = src.LastTargetGainDb;
                float prevDist = float.IsNaN(src.LastSlewedDistance) ? targetDist : src.LastSlewedDistance;
                bool snap = float.IsNaN(src.LastAppliedGainDb)
                    || Mathf.Abs(targetDist - prevDist) > TeleportGainSnapMeters;

                float slewedDist;
                float slewedGain;
                if (snap)
                {
                    slewedDist = targetDist;
                    slewedGain = targetGain;
                    src.GainSlewVelocity = 0f;
                }
                else
                {
                    slewedDist = Mathf.MoveTowards(
                        prevDist, targetDist, MaxDistanceSlewMetersPerSecond * dt);
                    float current = float.IsNaN(src.LastAppliedGainDb) ? targetGain : src.LastAppliedGainDb;
                    slewedGain = Mathf.SmoothDamp(
                        current, targetGain, ref src.GainSlewVelocity, GainSmoothTimeSeconds, Mathf.Infinity, dt);
                }

                src.LastSlewedDistance = slewedDist;
                src.LastAppliedGainDb = slewedGain;
                WriteDuckedGain(src, slewedDist, slewedGain);
            }
        }

        static bool AnyDistanceSlewPending()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.Playing || src.SoftStopping)
                    continue;
                if (float.IsNaN(src.LastSlewedDistance) || float.IsNaN(src.LastTargetDistance))
                    continue;
                if (Mathf.Abs(src.LastSlewedDistance - src.LastTargetDistance) > 0.25f)
                    return true;
            }
            return false;
        }

        static void BeginSoftStop(
            RuntimeSource src,
            float seconds,
            bool forceReleaseBank = false,
            bool keepBank = false)
        {
            if (src == null || (!src.Playing && !src.SoftStopping))
                return;
            src.SoftStopping = true;
            src.SoftStopStartedAt = Time.realtimeSinceStartup;
            src.SoftStopSeconds = Mathf.Max(0.05f, seconds);
            src.SoftStopFromGainDb = float.IsNaN(src.LastAppliedGainDb) ? 0f : src.LastAppliedGainDb;
            src.SoftStopForceReleaseBank = forceReleaseBank;
            src.SoftStopKeepBank = keepBank;
            src.LastTargetGainDb = src.SoftStopFromGainDb;
            // Evaluate is 4 Hz — drive soft-stop gain every frame so the ramp is continuous.
            int gen = ++_softStopDriveGen;
            DriveSoftStops(gen).Forget();
        }

        static async UniTaskVoid DriveSoftStops(int gen)
        {
            while (gen == _softStopDriveGen && _started)
            {
                TickSoftStops();
                if (!AnySoftStopping())
                    return;
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }

        static void CancelSoftStop(RuntimeSource src)
        {
            if (src == null || !src.SoftStopping)
                return;
            src.SoftStopping = false;
            src.SoftStopForceReleaseBank = false;
            src.SoftStopKeepBank = false;
        }

        static void CancelAllSoftStops()
        {
            _softStopDriveGen++;
            for (int i = 0; i < Sources.Count; i++)
                CancelSoftStop(Sources[i]);
        }

        static bool AnySoftStopping()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                if (Sources[i].SoftStopping)
                    return true;
            }
            return false;
        }

        static bool AnyGainSlewPending()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.Playing || src.SoftStopping)
                    continue;
                if (float.IsNaN(src.LastAppliedGainDb) || float.IsNaN(src.LastTargetGainDb))
                    continue;
                // Match Boluo SmoothDamp epsilon (~0.05 dB); keep a little slack for float noise.
                if (Mathf.Abs(src.LastAppliedGainDb - src.LastTargetGainDb) > 0.1f)
                    return true;
            }
            return false;
        }

        static float SoftStopGainNow(RuntimeSource src)
        {
            float t = Mathf.Clamp01(
                (Time.realtimeSinceStartup - src.SoftStopStartedAt) / src.SoftStopSeconds);
            return Mathf.Lerp(src.SoftStopFromGainDb, GainFloorDb, t);
        }

        static void TickSoftStops()
        {
            if (!_started || !_catalogBuilt)
                return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.SoftStopping)
                    continue;

                float gain = SoftStopGainNow(src);
                src.LastTargetGainDb = gain;
                src.LastAppliedGainDb = gain;
                src.GainSlewVelocity = 0f;
                float dist = float.IsNaN(src.LastSlewedDistance)
                    ? (float.IsNaN(src.LastPostedDistance) ? 0f : src.LastPostedDistance)
                    : src.LastSlewedDistance;
                WriteDuckedGain(src, dist, gain);

                if (now < src.SoftStopStartedAt + src.SoftStopSeconds)
                    continue;

                bool force = src.SoftStopForceReleaseBank;
                bool keep = src.SoftStopKeepBank;
                CancelSoftStop(src);
                StopSource(src, force, keep, fadeMs: 0);
            }
        }

        static void SnapAllPlayingGainsToFloor()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.Playing || src.Emitter == null)
                    continue;
                src.LastTargetGainDb = GainFloorDb;
                src.LastAppliedGainDb = GainFloorDb;
                float dist = float.IsNaN(src.LastPostedDistance) ? 0f : src.LastPostedDistance;
                WanosInteraction.ApplyEmitterDistanceAttenuation(src.Emitter, dist, GainFloorDb);
            }
        }

        static void StopSource(
            RuntimeSource src,
            bool forceReleaseBank = false,
            bool keepBank = false,
            int fadeMs = VoiceStopFadeMs)
        {
            var spec = src.Spec;
            src.Playing = false;
            src.NeedsRepost = false;
            src.OutsideSince = -1f;
            src.LastPostedDistance = float.NaN;  // 重置距离状态，下次 Start 时重新推送
            src.LastTargetGainDb = float.NaN;
            src.LastAppliedGainDb = float.NaN;
            src.GainSlewVelocity = 0f;
            src.LastSlewedDistance = float.NaN;
            src.LastTargetDistance = float.NaN;
            CancelSoftStop(src);
            
            Log.Info($"[AmbienceZone] StopSource id={spec.Id} voiceCount={src.VoiceIds.Count}");

            // 1. 对每个 voice 显式 Stop with fade
            for (int i = 0; i < src.VoiceIds.Count; i++)
            {
                ulong voiceId = src.VoiceIds[i];
                VoiceToSource.Remove(voiceId);
                WanosInteraction.StopVoice(voiceId, fadeMs);
            }
            src.VoiceIds.Clear();

            if (src.Emitter != null)
            {
                // 2. 发 StopEvent（object 仍注册，引擎才能把循环停掉）
                string stop = AmbienceEventCatalog.ResolveStopEvent(spec.Bank, spec.StopEvent);
                if (!string.IsNullOrEmpty(stop))
                    WanosInteraction.PostEvent(stop, src.Emitter);

                // 3. 延迟注销。同一帧 Unregister 会把刚 Post 的 Stop 一起丢掉。
                DeferredEmitterReleases.Add(new DeferredEmitterRelease
                {
                    Go = src.Emitter,
                    ReleaseAt = Time.realtimeSinceStartup + EmitterReleaseDelaySeconds,
                });
                src.Emitter = null;
            }

            // Shared bank (e.g. GrassLand_* → Boluodao_Grassland): another bed still holds
            // → never Release/UnLoad (Boluo AnyListedBankHeldByOtherLoader).
            bool keep = keepBank || (!forceReleaseBank && ShouldKeepBankOnWalkStop(spec));
            if (!keep && !forceReleaseBank && AnyOtherSourceHoldsBank(src, spec.Bank))
                keep = true;
            if (src.BankHeld && !keep)
            {
                WanosBankStore.Release(spec.Bank, UnloadDelaySecondsFor(spec));
                src.BankHeld = false;
            }
            Log.Info($"[AmbienceZone] stop id={spec.Id} keepBank={keep}");
        }

        static bool AnyOtherSourceHoldsBank(RuntimeSource self, string bank)
        {
            if (self == null || string.IsNullOrEmpty(bank))
                return false;
            for (int i = 0; i < Sources.Count; i++)
            {
                var other = Sources[i];
                if (ReferenceEquals(other, self))
                    continue;
                if (!other.BankHeld)
                    continue;
                if (string.Equals(other.Spec.Bank, bank, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static void StopAllPlaying(
            bool forceReleaseBank = false,
            bool keepBank = false,
            int fadeMs = VoiceStopFadeMs)
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (src.Playing || (forceReleaseBank && src.BankHeld))
                    StopSource(src, forceReleaseBank, keepBank, fadeMs);
            }
        }

        static void FlushDeferredEmitterReleases(bool force)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = DeferredEmitterReleases.Count - 1; i >= 0; i--)
            {
                DeferredEmitterRelease item = DeferredEmitterReleases[i];
                if (!force && now < item.ReleaseAt)
                    continue;

                DeferredEmitterReleases.RemoveAt(i);
                GameObject go = item.Go;
                if (go == null)
                    continue;

                bool live = false;
                for (int s = 0; s < Sources.Count; s++)
                {
                    if (Sources[s].Emitter == go && Sources[s].Playing)
                    {
                        live = true;
                        break;
                    }
                }

                if (live)
                    continue;

                WanosInteraction.ReleaseEmitter(go);
                ReturnEmitter(go);
            }
        }
    }
}
