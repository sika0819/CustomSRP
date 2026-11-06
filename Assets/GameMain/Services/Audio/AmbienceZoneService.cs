using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using GameFramework.Event;
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
    /// - Ocean 量到地形外海水线（60m 岸带）；Lake 用 catalog 湖点（xz 距离）。
    /// - Bank Ready 重试：Retain 失败记入 pending；落盘（<see cref="BankDownloader.OnBankLocalReady"/>）
    ///   或灌引擎（<see cref="WanosBankStore.OnBankLoaded"/>）后立刻 Evaluate。目录环境音即使
    ///   从未 Retain 失败也 seed pending + 短强制重评窗——防止 Prune 清 pending 后 listener-still 锁死。
    /// - Voice 回收：订阅 <see cref="WanosInteraction.VoiceStateChanged"/>，voice stopped 时摘掉 id，
    ///   list 空且 source 仍在播放区间则 RepostSource。
    /// - Stop 时对每个 voice 显式 Stop with 250ms fade。
    /// - Emitter GameObject 预分配池，避免 enter/exit 时 new/Destroy。
    /// - Listener-still skip：玩家站着不动时跳过本 tick（pending bank 已可解析时例外）。
    /// - 共用 biome bank（Forest/Grassland/Plateau）：同名 Play 只 Post 一次；离开整组才 Stop+Unload。
    /// - Distance RTPC + AttenuationGain：显示 dB 经 GainInvTransform 再写入（对齐 Boluo CurveForGain）。
    /// </summary>
    public sealed class AmbienceZoneService
    {
        readonly IslandAudioSession _session;

        public AmbienceZoneService(IslandAudioSession session)
        {
            _session = session;
        }

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
        /// <summary>Boluo 水域物体级钉正午，避免全局夜间 Time of A Day 把河/瀑/海/湖打到 0。</summary>
        const string TimeOfDayRtpcName = "Time of A Day";
        const float WaterAmbienceDaytimeRtpcHours = 12f;
        /// <summary>贴着瀑特征时禁止被湖互斥抢走（Mist/湖边曾把瀑掐成「没声」）。</summary>
        const float LakeWaterfallPreferFallWithinMeters =
            AmbienceWaterSilencePolicy.PreferFallWithinMeters;

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
        /// <summary>boluo-main Ocean_RD CurveForGain。</summary>
        static readonly Vector2[] OceanGainCurve =
        {
            new Vector2(20.759832f, 0f),
            new Vector2(79.93804f, -7.508814f),
            new Vector2(124.90317f, -17.25f),
        };
        /// <summary>boluo-main lake_0 CurveForGain（lake_1 略短，取湖心满音更长的一条）。</summary>
        static readonly Vector2[] LakeGainCurve =
        {
            new Vector2(17.46708f, 0f),
            new Vector2(48.055775f, -17.25f),
        };
        /// <summary>
        /// boluo-main River_Sound_1/2/3 CurveForGain（radius=40）。
        /// 近场 0dB 平台拉到 NearSurface（12m）：桥面水平壳距常 6–12m，原 4.2m 平台会在桥上偏闷。
        /// </summary>
        static readonly Vector2[] RiverGainCurve =
        {
            new Vector2(12f, 0f),
            new Vector2(22f, -5f),
            new Vector2(32f, -11f),
            new Vector2(40f, -17.25f),
        };
        /// <summary>
        /// boluo-main CampFire_0_Audio CurveForGain（radius=30）。
        /// 近场 0dB 拉到 12m：火边常站 6–10m，原 7.8m 拐点外太早掉，再被 0dB 瀑盖住。
        /// </summary>
        static readonly Vector2[] CampfireGainCurve =
        {
            new Vector2(12f, 0f),
            new Vector2(18f, -6f),
            new Vector2(24f, -12f),
            new Vector2(30f, -17.25f),
        };
        /// <summary>boluo-main WaterFall_Sound_Big_1 CurveForGain（radius=80，盖观瀑点）。</summary>
        static readonly Vector2[] WaterfallBigGainCurve =
        {
            new Vector2(27.473417f, 0f),
            new Vector2(52.000847f, -5.9664736f),
            new Vector2(80f, -17.25f),
        };
        /// <summary>boluo-main WaterFall_Sound_Small_* CurveForGain（radius=50）。</summary>
        static readonly Vector2[] WaterfallSmallGainCurve =
        {
            new Vector2(7.6839657f, 0f),
            new Vector2(23.299768f, -5.0735292f),
            new Vector2(40f, -12.460581f),
            new Vector2(50f, -17.25f),
        };

        public const string HostName = "AmbienceZones";
        public const string EmitterNamePrefix = "Amb_";

        // Scratch for exclusive-shared-bank Evaluate (Forest / Grassland / Plateau).
        bool[] _wantScratch;
        float[] _distScratch;
        Vector3[] _posScratch;

        // Listener-still skip：听者移动 < 0.09m 时本 tick 结论不变，直接跳过。
        const float ListenerStillThresholdSq = 0.0078125f; // (0.09m)^2
        Vector3 _lastListener;
        bool _hasLastListener;
        bool _oceanShoreReady;

        GameObject _host;
        Transform _root;
        bool _started;
        bool _catalogBuilt;
        bool _contentAudioPaused;
        /// <summary>Iris / black hold: Evaluate may run but must not StartSource / Retain.</summary>
        bool _presetTransitionHeld;
        bool _pendingEvaluate;
        /// <summary>回前台重起：已停本地态，等下一轮 Evaluate 重新 Post。</summary>
        bool _restartPending;
        float _restartNotBefore;
        readonly List<RuntimeSource> Sources = new List<RuntimeSource>(32);

        // Emitter GameObject pool: one per source, pre-allocated under _root.
        // Avoids per-enter new GameObject + per-exit Destroy churn.
        GameObject[] _emitterPool;
        bool[] _emitterInUse;

        /// <summary>记录 voiceId → 所属 source index（用于回收）。</summary>
        readonly Dictionary<ulong, int> VoiceToSource = new Dictionary<ulong, int>();

        /// <summary>因 bank 未就绪而被拒绝的 bank 名（收到 OnBankLoaded 时清掉并重 Evaluate）。</summary>
        readonly HashSet<string> _pendingRetryBanks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 落盘 / 灌引擎后短窗内禁止 listener-still 跳过（盖住 want 一帧抖 + Prune 清 pending）。
        /// </summary>
        float _forceEvalUntil;

        /// <summary>
        /// 贴河/瀑/火已触发过的优先下载（防每 tick 重入）。失败且仍未就绪时清掉以允许重试。
        /// </summary>
        readonly HashSet<string> _proximityBankKick =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>标记 inland water 已就绪（收到 OnInlandWaterReady 时设为 true，无需重复设）。</summary>
        bool _inlandWaterReady;
        /// <summary>上一轮看见的篝火点燃态。变化时打破 listener-still，否则人站着火才点上永远不开播。</summary>
        bool _campfireIgnitedLatched;
        /// <summary>
        /// 本轮 Evaluate 贴点燃篝火时，河/瀑/湖额外衰减（dB，≤0）。
        /// Mute 圈产品保持 0，改用增益压盖，避免火边瀑 0dB 盖住篝火。
        /// </summary>
        float _waterDuckDbThisEval;
        int _softStopDriveGen;
        int _attenuationDriveGen;

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

            /// <summary>
            /// 上一轮 Keep 的距离。走进近场时若仍 Playing 但引擎 voice 已死
            /// （VoiceStateChanged 未接），用来触发一次强制 Repost。
            /// </summary>
            public float LastKeepDistance = float.NaN;

            /// <summary>
            /// 河/海 Quad：L/Ls/R/Rs 四个世界落点（Boluo WanosBoxHelper 四角）。
            /// null = 单 emitter 叠轨（旧路径）。
            /// </summary>
            public GameObject[] ChannelEmitters;

            /// <summary>四角中心（SmoothDamp 后）。</summary>
            public Vector3 QuadCenterSlewed;

            /// <summary>Evaluate 给出的河面/岸带目标中心（每帧 SmoothDamp 追它）。</summary>
            public Vector3 QuadFollowTarget;

            /// <summary>四角中心是否已初始化。</summary>
            public bool QuadCenterReady;

            public Vector3 QuadCenterVelocity;

            /// <summary>上次 LayoutQuad 时刻，用于 Boluo 式 dt（禁止只塞 Time.deltaTime）。</summary>
            public float LastQuadLayoutAt = -1f;

            /// <summary>四角各自已写引擎增益（Boluo 每角独立 CurveForGain）。</summary>
            public float[] ChannelAppliedGainDb;

            /// <summary>四角增益 SmoothDamp 速度。</summary>
            public float[] ChannelGainSlewVelocity;

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

            /// <summary>Ocean 岸点 / River·Waterfall 扁平样点 sticky 下标；-1 表示尚未锁定。</summary>
            public int StickyShoreIndex = -1;
        }

        struct DeferredEmitterRelease
        {
            public GameObject Go;
            public float ReleaseAt;
        }

        /// <summary>
        /// Stop 之后稍等再 Unregister。同一帧卸掉 object，引擎来不及吃 Stop_*，鸟叫会变成无主循环。
        /// </summary>
        readonly List<DeferredEmitterRelease> DeferredEmitterReleases = new List<DeferredEmitterRelease>(8);
        const float EmitterReleaseDelaySeconds = 0.5f;

        public bool IsReady => _started && _catalogBuilt;

        public void SetContentAudioPaused(bool paused)
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
        /// <see cref="PresetService.ReleaseWeatherHold"/>.
        /// </summary>
        public void SetPresetTransitionHeld(bool held)
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
        public void ClearAllPlaying()
        {
            CancelAllSoftStops();
            SnapAllPlayingGainsToFloor();
            StopAllPlaying(forceReleaseBank: true, keepBank: false, fadeMs: 0);
        }

        /// <summary>
        /// Re-push attenuation for live emitters using <see cref="_session.Duck.Level01"/>.
        /// Called every frame while a preset duck ramp runs (Evaluate alone is too coarse).
        /// </summary>
        public void ApplyDuckToPlayingEmitters()
        {
            if (!_started || !_catalogBuilt)
                return;
            float duck = _session.Duck.Level01;
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
                ApplyAttenuationToSourceEmitters(src, dist, gainDb);
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
        public void RestartPlaying()
        {
            if (!_started || !_catalogBuilt)
                return;
            if (_contentAudioPaused || IslandAudioSession.IsYieldHeld)
                return;

            StopAllPlaying(forceReleaseBank: false, keepBank: true);
            InvalidateListenerStill();
            _restartPending = true;
            _restartNotBefore = Time.realtimeSinceStartup + (VoiceStopFadeMs / 1000f);
            Log.Info("[AmbienceZone] restart playing (foreground restore)");
        }

        public void EvaluateNow()
        {
            if (_started && _catalogBuilt)
            {
                _pendingEvaluate = false;
                InvalidateListenerStill();
                Evaluate();
                _session.Rain.EvaluateNow();
                return;
            }

            _pendingEvaluate = true;
        }

        public void Start()
        {
            if (_started)
                return;
            _started = true;

            // 订阅 voice state 变化（用于回收）
            WanosInteraction.VoiceStateChanged += OnVoiceStateChanged;

            // 订阅 bank loaded / 落盘就绪（用于重试；Critical 常 loadIntoEngine=false）
            WanosBankStore.OnBankLoaded += OnBankLoaded;
            BankDownloader.OnBankLocalReady += OnBankLocalReady;

            // 订阅 inland water loaded（用于触发首次河/瀑布 Evaluate）
            IslandWorldService.OnInlandWaterReady += OnInlandWaterReady;

            if (GameEntry.Event != null)
                GameEntry.Event.Subscribe(TimeOfDayChangedEventArgs.EventId, OnTimeOfDayChanged);
            PushGlobalTimeOfDayFromWeather();

            StartAsync().Forget();
        }

        public void Shutdown()
        {
            _started = false;
            WanosInteraction.VoiceStateChanged -= OnVoiceStateChanged;
            WanosBankStore.OnBankLoaded -= OnBankLoaded;
            BankDownloader.OnBankLocalReady -= OnBankLocalReady;
            IslandWorldService.OnInlandWaterReady -= OnInlandWaterReady;
            // BeginLeave 与 OnLeave 都会调 IslandAudioSession.Shutdown（文档要求可重入）。
            // 未订阅时 Unsubscribe 会抛 GameFrameworkException，打断 ChangeState→GuestLogin，
            // 表现为「退登后再登录进不去岛」。Check 后再退，对齐 IslandMainForm。
            if (GameEntry.Event != null &&
                GameEntry.Event.Check(TimeOfDayChangedEventArgs.EventId, OnTimeOfDayChanged))
                GameEntry.Event.Unsubscribe(TimeOfDayChangedEventArgs.EventId, OnTimeOfDayChanged);
            StopAllPlaying(forceReleaseBank: true);
            FlushDeferredEmitterReleases(force: true);
            Sources.Clear();
            VoiceToSource.Clear();
            _pendingRetryBanks.Clear();
            _proximityBankKick.Clear();
            _forceEvalUntil = 0f;
            _catalogBuilt = false;
            _contentAudioPaused = false;
            _presetTransitionHeld = false;
            _pendingEvaluate = false;
            _restartPending = false;
            _inlandWaterReady = false;
            _campfireIgnitedLatched = false;
            _hasLastListener = false;
            _oceanShoreReady = false;
            AmbienceZoneCatalog.ClearLiveShoreline();
            _softStopDriveGen++;
            _attenuationDriveGen++;
            _emitterPool = null;
            _emitterInUse = null;
            GameEntry.World?.WaterProbe.Clear();
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
        bool ShouldEvaluateListener(Vector3 listener)
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
        void InvalidateListenerStill()
        {
            _hasLastListener = false;
        }

        /// <summary>
        /// Bank 灌入引擎后 seed pending 并重新 Evaluate。
        /// </summary>
        void OnBankLoaded(string bankName)
            => HandleAmbienceBankReadySignal(bankName, "ready");

        /// <summary>
        /// Bank 落盘但未必灌引擎（Bootstrap Critical <c>loadIntoEngine=false</c>）。
        /// 目录环境音 / 已有 source / pending 任一命中即重评——禁止「必须先 Retain 失败」。
        /// </summary>
        void OnBankLocalReady(string bankName)
            => HandleAmbienceBankReadySignal(bankName, "local-ready");

        void HandleAmbienceBankReadySignal(string bankName, string tag)
        {
            if (!_started)
                return;
            if (string.IsNullOrEmpty(bankName))
                return;

            bool catalog = AmbienceEventCatalog.TryGet(bankName, out _);
            bool uses = AnySourceUsesBank(bankName);
            bool pending = _pendingRetryBanks.Contains(bankName);
            if (!AmbienceBankRetryPolicy.ShouldHandleAmbienceBankReady(catalog, uses, pending))
                return;

            // 即使从未 Retain 失败也 seed：圈外下完再走进 / Prune 清过 pending 时仍可被 HasResolvable 拉起。
            _pendingRetryBanks.Add(bankName);
            // 短强制窗：盖住同帧 want 抖 → Prune → still 锁死。
            _forceEvalUntil = Time.realtimeSinceStartup + AmbienceZoneCatalog.ExitGraceSeconds;
            InvalidateListenerStill();
            Log.Info($"[AmbienceZone] bank {tag}={bankName} — retry Evaluate");
            EvaluateNow();
            // Critical bank 常先 Post 后 loaded：Playing+Voice 已挂但引擎无声且无 Stopped 回调，
            // KeepSourcePlaying 不会再 Post。bank ready 后对点源强制 HealRepost。
            HealPlayingPointSourcesForBank(bankName);
        }

        void HealPlayingPointSourcesForBank(string bankName)
        {
            if (string.IsNullOrEmpty(bankName) || !WanosInteraction.IsReady)
                return;
            if (!WanosInteraction.TryGetListenerPosition(out Vector3 listener))
                return;

            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                var spec = src?.Spec;
                if (spec == null || !src.Playing)
                    continue;
                if (!string.Equals(spec.Bank, bankName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsPointSourceKind(spec.Kind))
                    continue;

                Vector3 pos = ResolveRepostPosition(spec, listener);
                float dist = Distance(spec, listener, pos);
                Log.Info(
                    $"[AmbienceZone] bank-ready heal id={spec.Id} bank={bankName} dist={dist:F1}m");
                HealRepostSource(src, pos, dist);
            }
        }

        bool AnySourceUsesBank(string bankName)
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var spec = Sources[i]?.Spec;
                if (spec != null
                    && string.Equals(spec.Bank, bankName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 走进河/瀑/火可听圈：Critical 常 <c>loadIntoEngine=false</c>，只靠走路 Retain 会 miss；
        /// 未落盘时 preempt+prioritize 插队下载并灌引擎，已落盘则立刻 <see cref="WanosBankStore.Load"/>。
        /// </summary>
        void EnsureProximityBankReady(AmbienceEmitterSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Bank))
                return;

            string bank = spec.Bank;
            bool loaded = WanosBankStore.IsLoaded(bank);
            bool local = WanosBankStore.CanResolve(bank) || BankDownloader.IsLocalReady(bank);
            var action = AmbienceProximityBankPolicy.Resolve(
                AmbienceProximityBankPolicy.IsPriorityKind(spec.Kind),
                loaded,
                local);

            if (action == AmbienceProximityBankPolicy.Action.None)
            {
                if (loaded)
                    _proximityBankKick.Remove(bank);
                return;
            }

            if (action == AmbienceProximityBankPolicy.Action.LoadLocal)
            {
                if (WanosBankStore.Load(bank))
                {
                    _pendingRetryBanks.Add(bank);
                    _forceEvalUntil =
                        Time.realtimeSinceStartup + AmbienceZoneCatalog.ExitGraceSeconds;
                    InvalidateListenerStill();
                    Log.Info($"[AmbienceZone] proximity Load bank={bank}");
                }
                return;
            }

            if (!_proximityBankKick.Add(bank))
                return;

            // 用户批占用中：静默放弃本次邻近 Kick（不 Toast，避免走路刷屏）。
            if (UserBankDownloadFlow.IsBusy)
            {
                _proximityBankKick.Remove(bank);
                return;
            }

            Log.Info($"[AmbienceZone] proximity prioritize download+load bank={bank}");
            string kickBank = bank;
            UserBankDownloadFlow.Start(
                new[] { kickBank },
                new DownloadUiOptions { SuppressProgressDialog = true },
                onComplete: () =>
                {
                    if (!WanosBankStore.IsLoaded(kickBank)
                        && !BankDownloader.IsLocalReady(kickBank))
                        _proximityBankKick.Remove(kickBank);
                },
                prioritize: true,
                preempt: true,
                loadIntoEngine: true,
                // 邻近自动 Kick 不占用户批：勿挡音乐/预设「未下载」点击优先插队。
                occupyUserBusy: false);
        }

        bool IsForceEvalWindowActive()
            => Time.realtimeSinceStartup < _forceEvalUntil;

        /// <summary>pending 里已有可解析/已加载的 bank → 站着也要重评开播。</summary>
        bool HasResolvablePendingRetry()
        {
            if (_pendingRetryBanks.Count == 0)
                return false;
            foreach (string bank in _pendingRetryBanks)
            {
                if (string.IsNullOrEmpty(bank))
                    continue;
                if (WanosBankStore.IsLoaded(bank) || WanosBankStore.CanResolve(bank))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 出圈后没人 want 的 pending 清掉，避免 bank 已落盘却永远挡住 listener-still。
        /// 落盘强制窗内不剪——同帧 want 抖会把刚 seed 的 pending 拆掉再仍锁死。
        /// </summary>
        void PruneUnwantedPendingRetries(int sourceCount)
        {
            if (_pendingRetryBanks.Count == 0 || sourceCount <= 0)
                return;
            if (IsForceEvalWindowActive())
                return;
            _pendingRetryBanks.RemoveWhere(bank =>
            {
                if (string.IsNullOrEmpty(bank))
                    return true;
                for (int i = 0; i < sourceCount && i < Sources.Count; i++)
                {
                    if (!_wantScratch[i])
                        continue;
                    var spec = Sources[i]?.Spec;
                    if (spec != null
                        && string.Equals(spec.Bank, bank, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            });
        }

        /// <summary>
        /// Inland water 加载完毕后触发 Evaluate（让河/瀑布 source 在 mesh collider 就绪后正确开始播放）。
        /// </summary>
        void OnInlandWaterReady(bool hasWater)
        {
            if (!_started)
                return;
            Log.Info($"[AmbienceZone] inland water ready={hasWater} — retry Evaluate");
            _inlandWaterReady = true;
            // 内河就绪 → 重新采样真实水面点（河 / 瀑的最近点来源）。
            GameEntry.World?.WaterProbe.Rebuild();
            EvaluateNow();
        }

        void OnTimeOfDayChanged(object sender, GameEventArgs e)
        {
            if (!_started)
                return;
            // 表盘/预设改时刻：先写全局，再把已播水域钉回正午（Boluo GameData.ApplyWanosTimeRtpc）。
            if (e is TimeOfDayChangedEventArgs tod)
                WanosEngine.SetGlobalParameter(TimeOfDayRtpcName, tod.Hours);
            else
                PushGlobalTimeOfDayFromWeather();
            PinPostedWaterAmbienceTimeOfDay();
        }

        void PushGlobalTimeOfDayFromWeather()
        {
            WeatherService weather = GameEntry.Weather;
            float hours = weather != null ? weather.Hours : WaterAmbienceDaytimeRtpcHours;
            WanosEngine.SetGlobalParameter(TimeOfDayRtpcName, hours);
        }

        /// <summary>
        /// 河/瀑/海/湖物体级钉正午。夜间全局 RTPC 会把已 Post 的 voice 打到 0 或明显变小；
        /// 夜鸟/虫/风仍读全局，不钉。
        /// </summary>
        void PinPostedWaterAmbienceTimeOfDay()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (src == null || !src.Playing || src.Emitter == null)
                    continue;
                if (!ShouldPinDaytimeTimeOfDayRtpc(src.Spec))
                    continue;
                WanosInteraction.SetEmitterParameter(src.Emitter, TimeOfDayRtpcName, WaterAmbienceDaytimeRtpcHours);
            }
        }

        static bool ShouldPinDaytimeTimeOfDayRtpc(AmbienceEmitterSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Bank))
                return false;
            return AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(spec.Kind);
        }

        /// <summary>
        /// Voice 停止时回调（由 WanosInteraction 转发 native 事件）。
        /// 摘掉 id；id 全部摘完且 source 仍在区间内则触发 RepostSource。
        /// </summary>
        void OnVoiceStateChanged(ulong voiceId, AudioVoiceState state)
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

        async UniTaskVoid StartAsync()
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

        async UniTaskVoid WaitMapItemsAndExtend()
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

            // 河 / 瀑来自 AllMapItem，Prefill 后即可建；篝火在 deferred extras，后面再补。
            BuildMapItemSources();
            Log.Info($"[AmbienceZone] map sources={Sources.Count}");
            EnsureEmitterPool();
            InvalidateListenerStill();

            if (IslandWorldService.InlandWaterReady)
                Evaluate();
            else
                Log.Debug("[AmbienceZone] inland water pending — will Evaluate on OnInlandWaterReady");

            // Huodui 只在 MapItemExtras；IsReady 时 LoadedItems 往往还空。
            // 等 ExtrasSettled 再 Append，否则委屈泛滥等篝火预设永远没有 Campfire 声源。
            for (int i = 0; i < 1800; i++)
            {
                if (!_started)
                    return;
                var spawner = GameEntry.World?.MapItemSpawner;
                if (spawner == null || spawner.ExtrasSettled)
                    break;
                await UniTask.DelayFrame(1);
            }

            if (!_started)
                return;

            int before = Sources.Count;
            AppendCampfireExtras();
            if (Sources.Count > before)
            {
                Log.Info($"[AmbienceZone] map sources after campfire={Sources.Count}");
                EnsureEmitterPool();
                InvalidateListenerStill();
                Evaluate();
            }
            else
            {
                Log.Info("[AmbienceZone] no campfire sources after ExtrasSettled");
            }
        }

        void EnsureHost()
        {
            if (_host != null)
                return;
            _host = new GameObject(HostName);
            _host.transform.SetParent(_session.transform, false);
            _root = _host.transform;
        }

        /// <summary>
        /// Pre-allocate one inactive GameObject per source under the host root.
        /// Called once after <see cref="BuildStaticSources"/> + <see cref="BuildMapItemSources"/>.
        /// Subsequent enter/exit cycles reuse these instead of allocating/destroying.
        /// </summary>
        void EnsureEmitterPool()
        {
            if (Sources.Count == 0) return;
            EnsureHost();
            if (_emitterPool == null || _emitterPool.Length != Sources.Count)
            {
                // 篝火 extras 后补会拉长 Sources；扩容时保留已占用的 emitter，别整表换掉。
                var oldPool = _emitterPool;
                var oldInUse = _emitterInUse;
                _emitterPool = new GameObject[Sources.Count];
                _emitterInUse = new bool[Sources.Count];
                if (oldPool != null)
                {
                    int copy = Mathf.Min(oldPool.Length, Sources.Count);
                    for (int i = 0; i < copy; i++)
                    {
                        _emitterPool[i] = oldPool[i];
                        _emitterInUse[i] = oldInUse != null && i < oldInUse.Length && oldInUse[i];
                    }
                }
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
        void ReturnEmitter(GameObject go)
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
        GameObject AcquireEmitterForSource(RuntimeSource src)
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

        void BuildStaticSources()
        {
            var statics = AmbienceZoneCatalog.StaticEmitters;
            for (int i = 0; i < statics.Length; i++)
            {
                AddSource(statics[i]);
            }
        }

        void BuildMapItemSources()
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
        /// 可在 Prefill 后再调一次；已有 <c>campfire_extra_*</c> 则跳过，避免重复锚点。
        /// </summary>
        void AppendCampfireExtras()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                AmbienceEmitterSpec s = Sources[i].Spec;
                if (s != null && s.Kind == AmbienceEmitterKind.Campfire
                    && s.Id != null && s.Id.StartsWith("campfire_extra_", System.StringComparison.Ordinal))
                    return;
            }

            IReadOnlyList<MapItemData> extras = GameEntry.MapItemExtras != null
                ? GameEntry.MapItemExtras.LoadedItems
                : null;
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

            Log.Info("[AmbienceZone] campfire extras=" + added);
        }

        void AddSource(AmbienceEmitterSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.PlayEvent))
                return;
            Sources.Add(new RuntimeSource { Spec = spec });
        }

        void Evaluate()
        {
            FlushDeferredEmitterReleases(force: false);
            TickSoftStops();
            if (!_started || !_catalogBuilt || !WanosInteraction.IsReady)
                return;
            if (_contentAudioPaused || IslandAudioSession.IsYieldHeld)
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

            EnsureLiveOceanShoreline();

            bool campfireIgnited = AtmospherePropService.Instance != null
                && AtmospherePropService.Instance.IsVisuallyIgnited;
            if (campfireIgnited != _campfireIgnitedLatched)
            {
                _campfireIgnitedLatched = campfireIgnited;
                InvalidateListenerStill();
            }

            // Mute 圈=0 允许同听；贴火时压河/瀑/湖增益，否则火边瀑 0dB 盖死篝火。
            _waterDuckDbThisEval = 0f;
            if (campfireIgnited
                && TryNearestCampfireHorizontalMeters(listener, out float nearFireM))
            {
                _waterDuckDbThisEval =
                    AmbienceWaterSilencePolicy.WaterDuckDbNearCampfire(nearFireM);
            }

            // Listener-still skip: if the listener moved < 0.09m since the last
            // tick, the previous Enter/Exit decisions are still valid. Cheaper
            // than scanning all sources for nothing.
            // Soft-stop / gain-slew / pending bank 已可解析 need ticks even when still
            // （边走边下：人站圈内等落盘，下完必须立刻 StartSource）。
            if (AmbienceBankRetryPolicy.ShouldSkipListenerStillEval(
                    listenerMovedEnough: ShouldEvaluateListener(listener),
                    softStopping: AnySoftStopping(),
                    gainSlewPending: AnyGainSlewPending(),
                    hasResolvablePendingRetry: HasResolvablePendingRetry(),
                    forceEvalWindow: IsForceEvalWindowActive()))
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
                    // Emitter 跟真实水面 / 外海水线最近点。增益用这个距离：
                    // 海是到水线的水平距离（贴岸响，60m 收到底），不是两个落点圆。
                    // 河 / 瀑：EvaluateWaterSource 已返回 shell 投影点（sticky+线段），
                    // 再量一次 XZ 与 shell 一致；禁止退回 catalog 原点（会 -96dB）。
                    pos = EvaluateWaterSource(src, listener, out want);
                    float gainDist = Distance(spec, listener, pos);
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
            PruneUnwantedPendingRetries(n);

            for (int i = 0; i < n; i++)
            {
                var src = Sources[i];
                if (_wantScratch[i])
                {
                    // 贴河/瀑/火：未落盘优先插队下载并灌引擎；已落盘未 Load 则立刻 Load。
                    EnsureProximityBankReady(src.Spec);
                    if (src.SoftStopping)
                        CancelSoftStop(src);
                    if (!src.Playing)
                        Log.Info($"[AmbienceZone] want->keep id={src.Spec.Id} bank={src.Spec.Bank} event={src.Spec.PlayEvent}");
                    KeepSourcePlaying(src, listener, _posScratch[i], _distScratch[i]);
                }
                else if (src.Playing && !src.SoftStopping)
                {
                    // 同组已无任何床位 want：软衰减到静音再 Stop（不靠可能 stub 的 native fade）。
                    // 边缘出区用 WalkSoftStopSeconds，避免 0.25s 拉到 −96dB 的电流音。
                    Log.Info($"[AmbienceZone] want->stop id={src.Spec.Id} bank={src.Spec.Bank}");
                    BeginSoftStop(src, WalkSoftStopSeconds);
                }
            }

            // 走路「贴着却没声」诊断：林/草 3D 日志会抢槽，点源单独心跳。
            LogPointSourceHeartbeat(listener, n);
        }

        float _nextPointSourceLogTime;

        void LogPointSourceHeartbeat(Vector3 listener, int n)
        {
            if (Time.realtimeSinceStartup < _nextPointSourceLogTime)
                return;
            _nextPointSourceLogTime = Time.realtimeSinceStartup + 2f;

            var sb = new System.Text.StringBuilder(256);
            sb.Append("[AmbienceZone] point-hb listener=(")
                .Append(listener.x.ToString("F1")).Append(',')
                .Append(listener.y.ToString("F1")).Append(',')
                .Append(listener.z.ToString("F1")).Append(')')
                .Append(" waterDuck=")
                .Append(_waterDuckDbThisEval.ToString("F1"));
            int logged = 0;
            for (int i = 0; i < n; i++)
            {
                var src = Sources[i];
                var kind = src.Spec.Kind;
                if (kind != AmbienceEmitterKind.Waterfall
                    && kind != AmbienceEmitterKind.River
                    && kind != AmbienceEmitterKind.Campfire
                    && kind != AmbienceEmitterKind.Lake)
                    continue;
                if (!_wantScratch[i] && !src.Playing && !src.SoftStopping)
                    continue;
                float gain = float.IsNaN(src.LastAppliedGainDb) ? -999f : src.LastAppliedGainDb;
                float target = float.IsNaN(src.LastTargetGainDb) ? -999f : src.LastTargetGainDb;
                sb.Append(" | ").Append(src.Spec.Id)
                    .Append(" want=").Append(_wantScratch[i] ? 1 : 0)
                    .Append(" play=").Append(src.Playing ? 1 : 0)
                    .Append(" soft=").Append(src.SoftStopping ? 1 : 0)
                    .Append(" dist=").Append(_distScratch[i].ToString("F1"))
                    .Append(" gain=").Append(gain.ToString("F1"))
                    .Append('/').Append(target.ToString("F1"))
                    .Append(" v=").Append(src.VoiceIds.Count)
                    .Append(" pos=(")
                    .Append(_posScratch[i].x.ToString("F0")).Append(',')
                    .Append(_posScratch[i].z.ToString("F0")).Append(')');
                logged++;
            }
            if (logged == 0)
                sb.Append(" | (none)");
            Log.Info(sb.ToString());
        }

        void EnsureEvalScratch(int n)
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
        void CollapseExclusiveSharedPlayWants(int n)
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
        bool IsExclusiveSharedPlayBank(string bank)
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
                || string.Equals(bank, "Waterfall_Small_Quad", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, "Campfire", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Lake ↔ Waterfall mutual exclusion: only the nearer side keeps want=true.
        /// Sticky hysteresis so the lake/fall boundary does not flip-flop Stop↔Play.
        /// Stop path KeepBank so exclusivity never unloads the suppressed side's bank.
        /// 固定锚瀑（若有）不参与湖↔瀑互斥；大瀑现靠 Big_0/1，仍与湖互斥。
        /// </summary>
        void ApplyLakeWaterfallExclusion(int n)
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

            // 人已经贴在瀑上（飞溅/瀑口）时禁止湖抢：湖原点离 Mist 约 20m，
            // sticky 曾把「路过瀑布」收成只有湖声或两边都掐。
            bool preferLake = AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                bestLakeDist,
                bestFallDist,
                lakePlaying,
                fallPlaying,
                LakeWaterfallPreferFallWithinMeters,
                ExclusiveShareHysteresisMeters);

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
        void TransferExclusiveSharedPlays(int n)
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

        void TransferPlayOwnership(RuntimeSource from, RuntimeSource to, Vector3 toPos, float toDist)
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
            to.ChannelEmitters = from.ChannelEmitters;
            to.QuadCenterSlewed = from.QuadCenterSlewed;
            to.QuadFollowTarget = from.QuadFollowTarget;
            to.QuadCenterReady = from.QuadCenterReady;
            to.QuadCenterVelocity = from.QuadCenterVelocity;
            to.LastQuadLayoutAt = from.LastQuadLayoutAt;
            to.ChannelAppliedGainDb = from.ChannelAppliedGainDb;
            to.ChannelGainSlewVelocity = from.ChannelGainSlewVelocity;
            from.ChannelEmitters = null;
            from.QuadCenterReady = false;
            from.QuadCenterVelocity = Vector3.zero;
            from.LastQuadLayoutAt = -1f;
            from.ChannelAppliedGainDb = null;
            from.ChannelGainSlewVelocity = null;
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
            // 河/海岸 sticky：床位切换后继续沿同一壳走，禁止 sticky 重置造成 shell 跳变。
            to.StickyShoreIndex = from.StickyShoreIndex;
            from.LastAppliedGainDb = float.NaN;
            from.LastTargetGainDb = float.NaN;
            from.GainSlewVelocity = 0f;
            from.LastSlewedDistance = float.NaN;
            from.LastTargetDistance = float.NaN;
            from.LastPostedDistance = float.NaN;
            from.StickyShoreIndex = -1;

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
                bool forceSnap = jump > TeleportGainSnapMeters;
                if (forceSnap)
                {
                    Log.Info(
                        $"[AmbienceZone] transfer-snap bank={to.Spec.Bank} id={to.Spec.Id} Δdist={jump:F1}m");
                }
                PushSlewedAttenuation(
                    to, toDist, GainDbForDistance(to.Spec, toDist), forceSnap);
                if (ShouldPinDaytimeTimeOfDayRtpc(to.Spec))
                    WanosInteraction.SetEmitterParameter(
                        to.Emitter, TimeOfDayRtpcName, WaterAmbienceDaytimeRtpcHours);
                EnsureAttenuationDrive();
            }
        }

        Vector3 ClampEmitterStep(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float mag = delta.magnitude;
            if (mag <= MaxEmitterJumpMetersPerEval || mag < 1e-4f)
                return to;
            return from + delta * (MaxEmitterJumpMetersPerEval / mag);
        }

        /// <summary>
        /// 地形就绪后采样外海水线，贴岸 60m 内都能开海浪。
        /// </summary>
        void EnsureLiveOceanShoreline()
        {
            if (_oceanShoreReady)
                return;

            Terrain terrain = IslandSceneComponent.Active != null
                ? IslandSceneComponent.Active.Terrain
                : null;
            if (terrain == null)
                terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null)
                return;

            int count = AmbienceZoneCatalog.RebuildLiveShoreline(terrain);
            if (count <= 0)
                return;

            _oceanShoreReady = true;
            InvalidateListenerStill();
            Log.Info($"[AmbienceZone] ocean shoreline pts={count}");
        }

        /// <summary>
        /// River / Waterfall / Ocean / Lake：算出该床位的代表点 + 是否该播。
        /// - River/Waterfall：用 <see cref="WaterSurfaceProbe"/> 查场景里真实水面最近点。
        /// - Ocean：到地形外海水线的水平距离，整段岸线可听圈内都播，发射器落在最近岸点。
        /// - Lake：catalog 里的湖点（xz 距离）。
        /// </summary>
        Vector3 EvaluateWaterSource(RuntimeSource src, Vector3 listener, out bool want)
        {
            var spec = src.Spec;

            if (spec.Kind == AmbienceEmitterKind.Lake
                && (listener.y < AmbienceZoneCatalog.LakeMinPlayerY
                    || listener.y > AmbienceZoneCatalog.LakeMaxPlayerY))
            {
                want = false;
                return ResolveEmitterPosition(spec);
            }

            if (spec.Kind == AmbienceEmitterKind.Ocean)
            {
                if (!AmbienceZoneCatalog.TryNearestOceanShore(
                        listener, ref src.StickyShoreIndex, out Vector3 shore, out float shell))
                {
                    want = false;
                    return ResolveEmitterPosition(spec);
                }

                // 高度门控：只挡「离岸较远的内陆爬高」；贴岸悬崖/堤路不掐（否则忽大忽小）。
                if (AmbienceZoneCatalog.IsOceanMutedByPlayerHeight(listener, shell))
                {
                    want = false;
                    return shore;
                }

                float oceanEnter = spec.EnterRadius;
                float oceanExit = src.Playing ? EffectiveExitRadius(spec) : oceanEnter;
                want = KeepPlayingWithExitGrace(src, shell, oceanEnter, oceanExit, 0f);
                return shore;
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

            // 大瀑：固定特征（Big_0/1/Splash）∪ 瀑面探针 ∪ 瀑盆内河面。
            // 禁止只认 Big_1——观瀑点才响、瀑底一圈河不响。
            if (spec.Kind == AmbienceEmitterKind.Waterfall
                && AmbienceQuadChannelLayout.IsWaterfallBig(spec))
            {
                TryWaterfallProbeShells(
                    listener,
                    ref src.StickyShoreIndex,
                    out float fallShell,
                    out Vector3 fallPt,
                    out bool hasFall,
                    out float riverShell,
                    out Vector3 riverPt,
                    out bool hasRiver);
                float dFall = AmbienceQuadChannelLayout.DistanceToWaterfallAudible(
                    listener,
                    fallShell,
                    fallPt,
                    hasFall,
                    riverShell,
                    riverPt,
                    hasRiver,
                    out Vector3 nearestFall);
                bool mutedFall = IsWaterfallMutedByCampfire(spec, listener);
                want = !mutedFall && KeepPlayingWithExitGrace(src, dFall, enter, exit, nearExtra);
                return nearestFall;
            }

            // 小瀑：MapItem 根 + Small_0/1，再并瀑面探针。
            if (spec.Kind == AmbienceEmitterKind.Waterfall)
            {
                Vector3 root = ResolveEmitterPosition(spec);
                AmbienceQuadChannelLayout.WaterfallDualWorldPoints(
                    root, big: false, out Vector3 lsRs, out Vector3 lr);
                float dFall = Distance(spec, listener, root);
                Vector3 nearestFall = root;
                float d0 = Distance(spec, listener, lsRs);
                float d1 = Distance(spec, listener, lr);
                if (d0 < dFall) { dFall = d0; nearestFall = lsRs; }
                if (d1 < dFall) { dFall = d1; nearestFall = lr; }
                WaterSurfaceProbe probe = GameEntry.World?.WaterProbe;
                int sticky = src.StickyShoreIndex;
                if (probe != null
                    && probe.TryNearestShell(
                        WaterSurfaceProbe.SurfaceKind.Waterfall,
                        listener,
                        ref sticky,
                        out Vector3 fallPt,
                        out float fallShell)
                    && fallShell < dFall)
                {
                    dFall = fallShell;
                    nearestFall = fallPt;
                    src.StickyShoreIndex = sticky;
                }
                bool mutedFall = IsWaterfallMutedByCampfire(spec, listener);
                want = !mutedFall && KeepPlayingWithExitGrace(src, dFall, enter, exit, nearExtra);
                return nearestFall;
            }

            if (spec.Kind == AmbienceEmitterKind.River)
            {
                // 河：优先整网水面 shell（贴河就播），禁止只量本床 catalog 原点
                // （否则必须走到 River_01/02/03 钉点才响）。
                WaterSurfaceProbe probe = GameEntry.World?.WaterProbe;
                if (probe != null
                    && probe.TryNearestShell(
                        WaterSurfaceProbe.SurfaceKind.River,
                        listener,
                        ref src.StickyShoreIndex,
                        out Vector3 nearest,
                        out float shell))
                {
                    want = KeepPlayingWithExitGrace(src, shell, enter, exit, nearExtra);
                    return nearest;
                }

                // 探针未建好：所有河床位原点取最近，仍按「附近有河」而不是本床钉点。
                if (TryNearestRiverCatalogPoint(listener, out Vector3 catPt, out float catDist))
                {
                    want = KeepPlayingWithExitGrace(src, catDist, enter, exit, nearExtra);
                    return catPt;
                }
            }

            // 降级：catalog / MapItem 单点
            Vector3 fallback = ResolveEmitterPosition(spec);
            float d = Distance(spec, listener, fallback);
            want = KeepPlayingWithExitGrace(src, d, enter, exit, nearExtra);
            return fallback;
        }

        void TryWaterfallProbeShells(
            Vector3 listener,
            ref int stickyIndex,
            out float fallShell,
            out Vector3 fallPt,
            out bool hasFall,
            out float riverShell,
            out Vector3 riverPt,
            out bool hasRiver)
        {
            fallShell = float.PositiveInfinity;
            fallPt = listener;
            hasFall = false;
            riverShell = float.PositiveInfinity;
            riverPt = listener;
            hasRiver = false;
            WaterSurfaceProbe probe = GameEntry.World?.WaterProbe;
            if (probe == null)
                return;

            int fallSticky = stickyIndex;
            if (probe.TryNearestShell(
                    WaterSurfaceProbe.SurfaceKind.Waterfall,
                    listener,
                    ref fallSticky,
                    out fallPt,
                    out fallShell))
            {
                hasFall = true;
                stickyIndex = fallSticky;
            }

            int riverSticky = -1;
            if (probe.TryNearestShell(
                    WaterSurfaceProbe.SurfaceKind.River,
                    listener,
                    ref riverSticky,
                    out riverPt,
                    out riverShell))
                hasRiver = true;
        }

        /// <summary>探针未就绪时：所有河床位世界点取最近（含 native / MapItem）。</summary>
        bool TryNearestRiverCatalogPoint(Vector3 listener, out Vector3 nearest, out float dist)
        {
            nearest = default;
            dist = float.MaxValue;
            bool any = false;
            for (int i = 0; i < Sources.Count; i++)
            {
                var s = Sources[i].Spec;
                if (s == null || s.Kind != AmbienceEmitterKind.River)
                    continue;
                Vector3 p = ResolveEmitterPosition(s);
                float d = Distance(s, listener, p);
                if (d >= dist)
                    continue;
                dist = d;
                nearest = p;
                any = true;
            }
            return any;
        }

        float EffectiveExitRadius(AmbienceEmitterSpec spec)
        {
            // 河/海/湖/火/瀑 Exit 已是 boluo-main SphereCollider，不再叠 NearSurface。
            return spec.ExitRadius;
        }

        bool IsWaterfallMutedByCampfire(AmbienceEmitterSpec spec, Vector3 listener)
        {
            if (spec.Kind != AmbienceEmitterKind.Waterfall)
                return false;
            // 仅大瀑受篝火静音圈；Enter 明显大于 Big 常量的（旧观景代理）跳过。
            if (spec.EnterRadius > AmbienceZoneCatalog.WaterfallBigEnterMeters + 0.5f)
                return false;
            return IsInsideCampfireWaterfallMute(listener);
        }

        Vector3 ResolveEmitterPosition(AmbienceEmitterSpec spec)
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
        Vector3 ResolveRepostPosition(AmbienceEmitterSpec spec, Vector3 listener)
        {
            if (spec.FixedWorldAnchor)
                return ResolveEmitterPosition(spec);
            if (spec.Kind == AmbienceEmitterKind.Waterfall
                && AmbienceQuadChannelLayout.IsWaterfallBig(spec))
            {
                int sticky = -1;
                TryWaterfallProbeShells(
                    listener,
                    ref sticky,
                    out float fallShell,
                    out Vector3 fallPt,
                    out bool hasFall,
                    out float riverShell,
                    out Vector3 riverPt,
                    out bool hasRiver);
                AmbienceQuadChannelLayout.DistanceToWaterfallAudible(
                    listener,
                    fallShell,
                    fallPt,
                    hasFall,
                    riverShell,
                    riverPt,
                    hasRiver,
                    out Vector3 nearest);
                return nearest;
            }
            if (spec.Kind == AmbienceEmitterKind.Waterfall)
            {
                Vector3 root = ResolveEmitterPosition(spec);
                AmbienceQuadChannelLayout.WaterfallDualWorldPoints(
                    root, big: false, out Vector3 lsRs, out Vector3 lr);
                float dRoot = Distance(spec, listener, root);
                float d0 = Distance(spec, listener, lsRs);
                float d1 = Distance(spec, listener, lr);
                if (d0 <= dRoot && d0 <= d1)
                    return lsRs;
                if (d1 <= dRoot)
                    return lr;
                return root;
            }
            if (spec.Kind == AmbienceEmitterKind.River)
            {
                WaterSurfaceProbe probe = GameEntry.World?.WaterProbe;
                int sticky = -1;
                if (probe != null && probe.TryNearestShell(
                        WaterSurfaceProbe.SurfaceKind.River,
                        listener,
                        ref sticky,
                        out Vector3 anchor,
                        out _))
                    return anchor;
                if (TryNearestRiverCatalogPoint(listener, out Vector3 catPt, out _))
                    return catPt;
            }
            return ResolveEmitterPosition(spec);
        }

        bool ShouldPlay(RuntimeSource src, Vector3 listener, Vector3 emitterPos)
        {
            var spec = src.Spec;
            if (spec.Kind == AmbienceEmitterKind.Campfire
                && (AtmospherePropService.Instance == null || !AtmospherePropService.Instance.IsVisuallyIgnited))
                return false;
            if (spec.Kind == AmbienceEmitterKind.Ocean
                && AmbienceZoneCatalog.IsOceanMutedByPlayerHeight(
                    listener, Distance(spec, listener, emitterPos)))
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

        /// <summary>
        /// 大瀑静音圈（水平距离）。半径 ≤ 0 时不静音：火边河/瀑与篝火同播。
        /// </summary>
        bool IsInsideCampfireWaterfallMute(Vector3 listener)
        {
            float mute = AmbienceZoneCatalog.WaterfallCampfireMuteMeters;
            if (mute <= 0f)
                return false;
            if (AtmospherePropService.Instance == null
                || !AtmospherePropService.Instance.IsVisuallyIgnited)
                return false;
            float muteSq = mute * mute;
            for (int i = 0; i < Sources.Count; i++)
            {
                var s = Sources[i].Spec;
                if (s.Kind != AmbienceEmitterKind.Campfire)
                    continue;
                Vector3 c = ResolveEmitterPosition(s);
                float dx = listener.x - c.x;
                float dz = listener.z - c.z;
                if (dx * dx + dz * dz <= muteSq)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 河/海/湖/火/瀑：Exit 已是 boluo-main SphereCollider，不再叠 NearSurface。
        /// Forest / Grassland / Plateau：出球后贴表面
        /// <see cref="AmbienceZoneCatalog.NearSurfaceMeters"/> 继续播，
        /// 再远仍有 <see cref="AmbienceZoneCatalog.ExitGraceSeconds"/> 时间宽限。
        /// </summary>
        float NearMetersForExitGrace(AmbienceEmitterSpec spec)
        {
            if (spec == null)
                return 0f;
            switch (spec.Kind)
            {
                case AmbienceEmitterKind.River:
                case AmbienceEmitterKind.Ocean:
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.Waterfall:
                case AmbienceEmitterKind.Campfire:
                    return 0f;
                case AmbienceEmitterKind.Forest:
                case AmbienceEmitterKind.Grassland:
                case AmbienceEmitterKind.Plateau:
                    return AmbienceZoneCatalog.NearSurfaceMeters;
                default:
                    return AmbienceZoneCatalog.NearSurfaceMeters;
            }
        }

        /// <summary>
        /// Boluo trigger-overlap keep：进区立刻播；已播时 exit+near 内保持；
        /// 刚出 near 后 <see cref="AmbienceZoneCatalog.ExitGraceSeconds"/> 内仍保持（取消 Stop/UnLoad）。
        /// </summary>
        bool KeepPlayingWithExitGrace(
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

        bool IsBiomeKind(AmbienceEmitterKind kind) =>
            kind == AmbienceEmitterKind.Forest
            || kind == AmbienceEmitterKind.Grassland
            || kind == AmbienceEmitterKind.Plateau;

        float Distance(AmbienceEmitterSpec spec, Vector3 listener, Vector3 emitterPos)
        {
            if (spec.UseXzDistance)
            {
                float dx = listener.x - emitterPos.x;
                float dz = listener.z - emitterPos.z;
                return Mathf.Sqrt(dx * dx + dz * dz);
            }

            return Vector3.Distance(listener, emitterPos);
        }

        float _nextAttenuationLogTime;

        void KeepSourcePlaying(RuntimeSource src, Vector3 listener, Vector3 pos, float dist)
        {
            var spec = src.Spec;
            // 走进近场：本地 Playing+VoiceIds 仍在，但引擎 voice 可能已死（无 Stopped 回调），
            // 走路贴着河/瀑/火却没声；切预设 Clear+EvaluateNow 能好。近场上升沿强制重 Post。
            float prevKeepDist = src.LastKeepDistance;
            src.LastKeepDistance = dist;
            if (src.Playing
                && src.VoiceIds.Count > 0
                && IsPointSourceKind(spec.Kind)
                && dist <= 12f
                && !float.IsNaN(prevKeepDist)
                && prevKeepDist > 18f)
            {
                Log.Info(
                    $"[AmbienceZone] near-field heal id={spec.Id} bank={spec.Bank} {prevKeepDist:F1}→{dist:F1}m");
                HealRepostSource(src, pos, dist);
                return;
            }

            // Already audible: never re-Load / re-Post (Boluo「已播禁止再 Loading」).
            // Only pose + attenuation. Voice-empty while Playing → Repost once.
            if (src.Playing && src.VoiceIds.Count > 0)
            {
                if (src.Emitter != null || HasSpatialChannels(src))
                {
                    // 河/海：Boluo 四角分轨 +「河→人」朝向（转头不甩 L/R）。
                    // 瀑：Boluo Big_0/Big_1 固定双落点。
                    // 旧单点叠轨：转头时整团 3D 甩耳，左右切换过快。
                    if (AmbienceQuadChannelLayout.UsesQuadChannels(spec.Kind))
                    {
                        src.QuadFollowTarget = pos;
                        LayoutQuadChannels(src, listener, pos, forceSnap: false);
                    }
                    else if (AmbienceQuadChannelLayout.UsesWaterfallDualChannels(spec.Kind))
                        LayoutWaterfallDualChannels(src, forceSnap: false);
                    else
                    {
                        pos = ClampEmitterStep(src.Emitter.transform.position, pos);
                        src.Emitter.transform.position = pos;
                        WanosInteraction.EnsureEmitter(src.Emitter, pos, spec.EnterRadius);
                    }

                    // 每 tick 都推增益（不靠 MinPostDistanceDelta）：Post 后引擎可能重置 gain enable。
                    float targetDb = GainDbForDistance(spec, dist);
                    float prevDist = src.LastSlewedDistance;
                    float gainDb = PushSlewedAttenuation(src, dist, targetDb);
                    EnsureAttenuationDrive();

                    if (!float.IsNaN(prevDist) && Mathf.Abs(dist - prevDist) > TeleportGainSnapMeters)
                    {
                        Log.Info(
                            $"[AmbienceZone] dist-jump id={spec.Id} {prevDist:F1}→{dist:F1}m (slew)");
                    }

                    if (Time.realtimeSinceStartup >= _nextAttenuationLogTime)
                    {
                        _nextAttenuationLogTime = Time.realtimeSinceStartup + 1f;
                        ulong oid = 0;
                        if (src.Emitter != null)
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
        float GainInvTransform(float displayY)
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

        Vector2[] GainCurveForSpec(AmbienceEmitterSpec spec)
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
                    return LakeGainCurve;
                case AmbienceEmitterKind.River:
                    return RiverGainCurve;
                case AmbienceEmitterKind.Campfire:
                    return CampfireGainCurve;
                case AmbienceEmitterKind.Waterfall:
                    return AmbienceQuadChannelLayout.IsWaterfallBig(spec)
                        ? WaterfallBigGainCurve
                        : WaterfallSmallGainCurve;
                default:
                    return OceanGainCurve;
            }
        }

        /// <summary>
        /// boluo-main CurveForGain 折线：距离 → 显示 Y → <see cref="GainInvTransform"/>。
        /// 海/河/瀑/湖/篝火与 prefab 同节点；远端显示 −17.25 → 引擎约 −96dB（对齐停播）。
        /// 林/草/高原整体压低，避免盖住水声。
        /// </summary>
        float GainDbForDistance(AmbienceEmitterSpec spec, float distMeters)
        {
            Vector2[] curve = GainCurveForSpec(spec);
            float displayY = EvaluateGainCurveDisplayY(curve, distMeters);
            float engineDb = GainInvTransform(displayY);
            // 鸟叫/虫鸣/风床：相对河瀑略退后，避免贴水时生物声压过水声。
            if (spec != null && IsBiomeKind(spec.Kind))
                engineDb -= 5f;
            // 火边压水：硬静音圈产品关着，用增益把河/瀑让给篝火。
            if (spec != null
                && _waterDuckDbThisEval < 0f
                && IsCampfireWaterDuckKind(spec.Kind))
                engineDb += _waterDuckDbThisEval;
            return engineDb;
        }

        static bool IsCampfireWaterDuckKind(AmbienceEmitterKind kind)
        {
            return kind == AmbienceEmitterKind.River
                || kind == AmbienceEmitterKind.Waterfall
                || kind == AmbienceEmitterKind.Lake;
        }

        bool TryNearestCampfireHorizontalMeters(Vector3 listener, out float meters)
        {
            meters = float.MaxValue;
            bool any = false;
            for (int i = 0; i < Sources.Count; i++)
            {
                var s = Sources[i]?.Spec;
                if (s == null || s.Kind != AmbienceEmitterKind.Campfire)
                    continue;
                Vector3 c = ResolveEmitterPosition(s);
                float dx = listener.x - c.x;
                float dz = listener.z - c.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d >= meters)
                    continue;
                meters = d;
                any = true;
            }
            return any;
        }

        float EvaluateGainCurveDisplayY(Vector2[] curve, float distMeters)
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

        void StartSource(RuntimeSource src, Vector3 pos, float dist)
        {
            if (_contentAudioPaused || IslandAudioSession.IsYieldHeld)
                return;
            var spec = src.Spec;
            Log.Info($"[AmbienceZone] StartSource enter id={spec.Id} bank={spec.Bank}");

            if (!src.BankHeld)
            {
                if (!WanosBankStore.Retain(spec.Bank))
                {
                    Log.Warning($"[AmbienceZone] StartSource Retain FAIL id={spec.Id} bank={spec.Bank} (added to retry)");
                    _pendingRetryBanks.Add(spec.Bank);
                    EnsureProximityBankReady(spec);
                    return;
                }

                src.BankHeld = true;
                // 落盘重试成功：清掉 pending，避免站着时 HasResolvablePendingRetry 每 tick 空转。
                _pendingRetryBanks.Remove(spec.Bank);
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
                LayoutSpatialChannelsOrPoint(src, pos, forceSnap: true);
                src.NeedsRepost = true;
                // 先 Post，再 enable+gain（Boluo CurveForGain 也是 voice 起来后持续推）。
                PostSourceEvents(src, plays);
                float targetDb = GainDbForDistance(spec, dist);
                // 贴身（桥/岸/火边）：跳过 SoftEnter −36dB，避免近场先闷半秒。
                bool nearSnap = IsPointSourceKind(spec.Kind)
                    && dist <= AmbienceZoneCatalog.NearSurfaceMeters;
                src.LastTargetGainDb = targetDb;
                src.LastAppliedGainDb = nearSnap
                    ? targetDb
                    : Mathf.Min(SoftEnterStartDb, targetDb);
                src.GainSlewVelocity = 0f;
                src.LastSlewedDistance = dist;
                src.LastTargetDistance = dist;
                float gainDb = WriteDuckedGain(src, dist, src.LastAppliedGainDb, forceSnap: true);
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

        void RepostSource(RuntimeSource src, Vector3 pos, float dist)
        {
            if (_contentAudioPaused || IslandAudioSession.IsYieldHeld)
                return;
            var spec = src.Spec;
            if (src.Emitter == null)
            {
                EnsureHost();
                src.Emitter = AcquireEmitterForSource(src) ?? CreateEmitter(pos);
            }
            LayoutSpatialChannelsOrPoint(src, pos, forceSnap: true);

            string[] plays = ResolvePlayEventsForSingleEmitter(spec);
            if (plays.Length == 0)
                return;

            PostSourceEvents(src, plays);
            float targetDb = GainDbForDistance(spec, dist);
            bool nearSnap = IsPointSourceKind(spec.Kind)
                && dist <= AmbienceZoneCatalog.NearSurfaceMeters;
            src.LastTargetGainDb = targetDb;
            src.LastAppliedGainDb = nearSnap
                ? targetDb
                : Mathf.Min(SoftEnterStartDb, targetDb);
            src.GainSlewVelocity = 0f;
            src.LastSlewedDistance = dist;
            src.LastTargetDistance = dist;
            WriteDuckedGain(src, dist, src.LastAppliedGainDb, forceSnap: true);
            src.NeedsRepost = true;
            EnsureAttenuationDrive();
        }

        /// <summary>
        /// 近场治愈：重 Post 但跳过 SoftEnter −36dB，直接贴目标增益，避免贴着河/火时先闷一下。
        /// </summary>
        void HealRepostSource(RuntimeSource src, Vector3 pos, float dist)
        {
            if (_contentAudioPaused || IslandAudioSession.IsYieldHeld)
                return;
            var spec = src.Spec;
            if (src.Emitter == null)
            {
                EnsureHost();
                src.Emitter = AcquireEmitterForSource(src) ?? CreateEmitter(pos);
            }
            LayoutSpatialChannelsOrPoint(src, pos, forceSnap: true);

            string[] plays = ResolvePlayEventsForSingleEmitter(spec);
            if (plays.Length == 0)
                return;

            PostSourceEvents(src, plays);
            float targetDb = GainDbForDistance(spec, dist);
            src.LastTargetGainDb = targetDb;
            src.LastAppliedGainDb = targetDb;
            src.GainSlewVelocity = 0f;
            src.LastSlewedDistance = dist;
            src.LastTargetDistance = dist;
            WriteDuckedGain(src, dist, targetDb, forceSnap: true);
            src.NeedsRepost = true;
            EnsureAttenuationDrive();
        }

        static bool IsPointSourceKind(AmbienceEmitterKind kind)
        {
            return kind == AmbienceEmitterKind.River
                || kind == AmbienceEmitterKind.Waterfall
                || kind == AmbienceEmitterKind.Campfire
                || kind == AmbienceEmitterKind.Lake
                || kind == AmbienceEmitterKind.Ocean;
        }

        /// <summary>
        /// Water Quad bank 走 catalog 多声道（同点叠轨，对齐 Boluo 响度）；
        /// biome / 点源只用 Spec.PlayEvent，避免 Forest 重复叠音。
        /// </summary>
        string[] ResolvePlayEventsForSingleEmitter(AmbienceEmitterSpec spec)
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
        GameObject CreateEmitter(Vector3 pos)
        {
            var go = new GameObject(EmitterName(pos));
            go.transform.SetParent(_root, false);
            go.transform.position = pos;
            return go;
        }

        string EmitterName(Vector3 pos) => $"Amb_{pos.x:F0}_{pos.y:F0}_{pos.z:F0}";

        void PostSourceEvents(RuntimeSource src, string[] plays)
        {
            // 清旧 voice 记录
            for (int i = 0; i < src.VoiceIds.Count; i++)
                VoiceToSource.Remove(src.VoiceIds[i]);
            src.VoiceIds.Clear();

            bool spatial = HasSpatialChannels(src);
            if (!spatial && src.Emitter == null)
                return;

            bool quad = HasQuadChannels(src);
            bool dual = HasWaterfallDualChannels(src);
            int srcIdx = Sources.IndexOf(src);
            for (int i = 0; i < plays.Length; i++)
            {
                string play = plays[i];
                if (string.IsNullOrEmpty(play))
                    continue;
                GameObject target = src.Emitter;
                if (quad)
                {
                    int corner = AmbienceQuadChannelLayout.CornerIndexForPlayEvent(play);
                    if (corner >= 0 && corner < src.ChannelEmitters.Length)
                        target = src.ChannelEmitters[corner];
                }
                else if (dual)
                {
                    int ch = AmbienceQuadChannelLayout.ChannelIndexForWaterfallPlayEvent(play);
                    if (ch >= 0 && ch < src.ChannelEmitters.Length)
                        target = src.ChannelEmitters[ch];
                }
                if (target == null)
                    continue;
                ulong id = WanosInteraction.PostEvent(play, target);
                if (id != 0)
                {
                    src.VoiceIds.Add(id);
                    VoiceToSource[id] = srcIdx;
                }
            }

            // Post 后再钉：部分 bank 起声时会吃全局夜间 Time of A Day。
            if (ShouldPinDaytimeTimeOfDayRtpc(src.Spec))
                PinDaytimeOnSourceEmitters(src);
        }

        void LayoutSpatialChannelsOrPoint(RuntimeSource src, Vector3 pos, bool forceSnap)
        {
            var spec = src.Spec;
            if (AmbienceQuadChannelLayout.UsesQuadChannels(spec.Kind)
                && WanosInteraction.TryGetListenerPosition(out Vector3 listenerNow))
            {
                src.QuadFollowTarget = pos;
                LayoutQuadChannels(src, listenerNow, pos, forceSnap);
                return;
            }

            if (AmbienceQuadChannelLayout.UsesWaterfallDualChannels(spec.Kind))
            {
                LayoutWaterfallDualChannels(src, forceSnap);
                return;
            }

            src.Emitter.transform.position = pos;
            WanosInteraction.EnsureEmitter(src.Emitter, pos, spec.EnterRadius);
        }

        static bool HasSpatialChannels(RuntimeSource src) =>
            HasQuadChannels(src) || HasWaterfallDualChannels(src);

        static bool HasQuadChannels(RuntimeSource src) =>
            src != null
            && src.ChannelEmitters != null
            && src.ChannelEmitters.Length == 4
            && src.ChannelEmitters[0] != null;

        static bool HasWaterfallDualChannels(RuntimeSource src) =>
            src != null
            && src.ChannelEmitters != null
            && src.ChannelEmitters.Length == 2
            && src.ChannelEmitters[0] != null;

        void EnsureQuadChannelObjects(RuntimeSource src)
        {
            if (src.ChannelEmitters != null && src.ChannelEmitters.Length == 4)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (src.ChannelEmitters[i] != null)
                    {
                        src.ChannelEmitters[i].SetActive(true);
                        continue;
                    }
                    src.ChannelEmitters[i] = CreateQuadChannelObject(src, i);
                }
                return;
            }

            src.ChannelEmitters = new GameObject[4];
            for (int i = 0; i < 4; i++)
                src.ChannelEmitters[i] = CreateQuadChannelObject(src, i);
        }

        GameObject CreateQuadChannelObject(RuntimeSource src, int corner)
        {
            string id = src.Spec != null ? src.Spec.Id : "q";
            var go = new GameObject($"AmbQ_{id}_{corner}");
            go.transform.SetParent(_root, false);
            return go;
        }

        /// <summary>
        /// 河/海四角：对齐 Boluo WanosBoxHelper —
        /// 中心持续 SmoothDamp（moveThreshold=100 才闪现）；朝向=特征→听者（转头不改四角）。
        /// </summary>
        void LayoutQuadChannels(
            RuntimeSource src, Vector3 listener, Vector3 targetCenter, bool forceSnap)
        {
            EnsureHost();
            EnsureQuadChannelObjects(src);
            var spec = src.Spec;
            float half = AmbienceQuadChannelLayout.HalfMetersFor(spec.Kind);

            float now = Time.realtimeSinceStartup;
            float dt;
            if (forceSnap || !src.QuadCenterReady || src.LastQuadLayoutAt < 0f)
                dt = AmbienceQuadChannelLayout.LayoutMinDeltaSeconds;
            else
                dt = Mathf.Max(now - src.LastQuadLayoutAt, 1f / 120f);
            src.LastQuadLayoutAt = now;

            Vector3 center;
            float jump = src.QuadCenterReady
                ? Vector3.Distance(src.QuadCenterSlewed, targetCenter)
                : 0f;
            bool snap = forceSnap
                || !src.QuadCenterReady
                || jump > AmbienceQuadChannelLayout.MoveThresholdMeters;
            if (snap)
            {
                center = targetCenter;
                src.QuadCenterVelocity = Vector3.zero;
                src.QuadCenterReady = true;
            }
            else
            {
                // Boluo：目标每 refresh 更新，中心用 centerSmoothTime 追，禁止 8m 粘门控。
                center = Vector3.SmoothDamp(
                    src.QuadCenterSlewed,
                    targetCenter,
                    ref src.QuadCenterVelocity,
                    AmbienceQuadChannelLayout.CenterSmoothSeconds,
                    Mathf.Infinity,
                    dt);
            }
            src.QuadCenterSlewed = center;
            src.QuadFollowTarget = targetCenter;

            Vector3 fwd = AmbienceQuadChannelLayout.StableFacingFromFeatureToListener(
                center, listener, Vector3.forward);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < 1e-6f)
                right = Vector3.right;
            else
                right.Normalize();

            AmbienceQuadChannelLayout.ComputeEdges(
                center, fwd, right, half, half,
                out Vector3 e0, out Vector3 e1, out Vector3 e2, out Vector3 e3);
            Vector3[] edges = { e0, e1, e2, e3 };
            for (int i = 0; i < 4; i++)
            {
                GameObject ch = src.ChannelEmitters[i];
                if (ch == null)
                    continue;
                ch.transform.position = edges[i];
                WanosInteraction.EnsureEmitter(ch, edges[i], spec.EnterRadius);
            }

            if (src.Emitter != null)
            {
                src.Emitter.transform.position = center;
                WanosInteraction.EnsureEmitter(src.Emitter, center, spec.EnterRadius);
            }
        }

        /// <summary>
        /// 瀑双轨：钉在 Boluo Big_0/Big_1（或 Small_0/1），不跟听者转。
        /// </summary>
        void LayoutWaterfallDualChannels(RuntimeSource src, bool forceSnap)
        {
            EnsureHost();
            EnsureWaterfallDualChannelObjects(src);
            var spec = src.Spec;
            bool big = AmbienceQuadChannelLayout.IsWaterfallBig(spec);
            Vector3 root = big
                ? AmbienceQuadChannelLayout.WaterfallBigRoot
                : ResolveEmitterPosition(spec);
            AmbienceQuadChannelLayout.WaterfallDualWorldPoints(
                root, big, out Vector3 lsRs, out Vector3 lr);
            Vector3[] points = { lsRs, lr };
            for (int i = 0; i < 2; i++)
            {
                GameObject ch = src.ChannelEmitters[i];
                if (ch == null)
                    continue;
                Vector3 p = points[i];
                if (!forceSnap && src.QuadCenterReady)
                    p = ClampEmitterStep(ch.transform.position, p);
                ch.transform.position = p;
                WanosInteraction.EnsureEmitter(ch, p, spec.EnterRadius);
            }

            if (src.Emitter != null)
            {
                src.Emitter.transform.position = root;
                WanosInteraction.EnsureEmitter(src.Emitter, root, spec.EnterRadius);
            }
            src.QuadCenterSlewed = root;
            src.QuadCenterReady = true;
            src.QuadCenterVelocity = Vector3.zero;
        }

        void EnsureWaterfallDualChannelObjects(RuntimeSource src)
        {
            if (src.ChannelEmitters != null && src.ChannelEmitters.Length == 2)
            {
                for (int i = 0; i < 2; i++)
                {
                    if (src.ChannelEmitters[i] != null)
                    {
                        src.ChannelEmitters[i].SetActive(true);
                        continue;
                    }
                    src.ChannelEmitters[i] = CreateQuadChannelObject(src, i);
                }
                return;
            }

            // 曾当四角用过：先丢掉再建成双轨。
            if (src.ChannelEmitters != null && src.ChannelEmitters.Length != 2)
                ReleaseQuadChannels(src);

            src.ChannelEmitters = new GameObject[2];
            for (int i = 0; i < 2; i++)
                src.ChannelEmitters[i] = CreateQuadChannelObject(src, i);
        }

        void PinDaytimeOnSourceEmitters(RuntimeSource src)
        {
            if (src.Emitter != null)
                WanosInteraction.SetEmitterParameter(
                    src.Emitter, TimeOfDayRtpcName, WaterAmbienceDaytimeRtpcHours);
            if (src.ChannelEmitters == null)
                return;
            for (int i = 0; i < src.ChannelEmitters.Length; i++)
            {
                if (src.ChannelEmitters[i] != null)
                    WanosInteraction.SetEmitterParameter(
                        src.ChannelEmitters[i], TimeOfDayRtpcName, WaterAmbienceDaytimeRtpcHours);
            }
        }

        void ReleaseQuadChannels(RuntimeSource src)
        {
            if (src.ChannelEmitters == null)
                return;
            for (int i = 0; i < src.ChannelEmitters.Length; i++)
            {
                GameObject ch = src.ChannelEmitters[i];
                if (ch == null)
                    continue;
                DeferredEmitterReleases.Add(new DeferredEmitterRelease
                {
                    Go = ch,
                    ReleaseAt = Time.realtimeSinceStartup + EmitterReleaseDelaySeconds,
                });
                src.ChannelEmitters[i] = null;
            }
            src.ChannelEmitters = null;
            src.QuadCenterReady = false;
            src.QuadCenterVelocity = Vector3.zero;
            src.LastQuadLayoutAt = -1f;
            src.ChannelAppliedGainDb = null;
            src.ChannelGainSlewVelocity = null;
        }

        /// <summary>
        /// 走路出区：河/海/湖/瀑只停播、保留引擎 bank（Boluo ShouldKeepRiver/OceanEngineBank）。
        /// 立刻 UnLoad 再走进可听圈会重新解码 + Post，桥边听感刺啦。
        /// 预设瞬移仍走 forceReleaseBank，不跟走路混用。
        /// </summary>
        bool ShouldKeepBankOnWalkStop(AmbienceEmitterSpec spec)
        {
            if (spec == null)
                return false;
            switch (spec.Kind)
            {
                case AmbienceEmitterKind.Ocean:
                case AmbienceEmitterKind.River:
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.Waterfall:
                    return true;
                default:
                    return false;
            }
        }

        float UnloadDelaySecondsFor(AmbienceEmitterSpec spec)
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
                case AmbienceEmitterKind.River:
                    return AmbienceZoneCatalog.RiverUnloadDelaySeconds;
                case AmbienceEmitterKind.Ocean:
                    return AmbienceZoneCatalog.OceanUnloadDelaySeconds;
                case AmbienceEmitterKind.Lake:
                    return AmbienceZoneCatalog.LakeUnloadDelaySeconds;
                case AmbienceEmitterKind.Waterfall:
                    return AmbienceQuadChannelLayout.IsWaterfallBig(spec)
                        ? AmbienceZoneCatalog.WaterfallBigUnloadDelaySeconds
                        : AmbienceZoneCatalog.WaterfallSmallUnloadDelaySeconds;
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
        float PushSlewedAttenuation(
            RuntimeSource src, float dist, float targetDb, bool forceSnap = false)
        {
            src.LastTargetGainDb = targetDb;
            src.LastTargetDistance = dist;

            float prevDist = src.LastSlewedDistance;
            // 走路岸线抖 >8m 时禁止硬贴增益（听感忽大忽小）；瞬移/交接显式 forceSnap。
            bool snap = forceSnap
                || float.IsNaN(src.LastAppliedGainDb)
                || float.IsNaN(prevDist);

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
            return WriteDuckedGain(src, slewedDist, slewedGain, forceSnap: snap);
        }

        float WriteDuckedGain(RuntimeSource src, float dist, float preDuckGainDb, bool forceSnap = false)
        {
            // dist here is the Distance RTPC actually written (already slewed by caller).
            src.LastPostedDistance = dist;
            if (float.IsNaN(src.LastSlewedDistance))
                src.LastSlewedDistance = dist;
            float ducked = Mathf.Lerp(GainFloorDb, preDuckGainDb, _session.Duck.Level01);
            ApplyAttenuationToSourceEmitters(src, dist, ducked, forceSnap);
            return ducked;
        }

        void EnsureChannelGainState(RuntimeSource src, int count)
        {
            if (src.ChannelAppliedGainDb != null && src.ChannelAppliedGainDb.Length == count
                && src.ChannelGainSlewVelocity != null && src.ChannelGainSlewVelocity.Length == count)
                return;
            src.ChannelAppliedGainDb = new float[count];
            src.ChannelGainSlewVelocity = new float[count];
            for (int i = 0; i < count; i++)
            {
                src.ChannelAppliedGainDb[i] = float.NaN;
                src.ChannelGainSlewVelocity[i] = 0f;
            }
        }

        void ApplyAttenuationToSourceEmitters(
            RuntimeSource src, float dist, float gainDb, bool forceSnapChannels = false)
        {
            if (src.Emitter != null)
                WanosInteraction.ApplyEmitterDistanceAttenuation(src.Emitter, dist, gainDb);

            if (src.ChannelEmitters == null)
                return;

            // 河/海：Boluo 每角独立 CurveForGain（听者→该角距离）+ 0.15s SmoothDamp。
            // 禁止四角共用 shell 距离——走几步 / 转圈时整团同响同静，3D 过渡发硬。
            if (HasQuadChannels(src)
                && src.Spec != null
                && AmbienceQuadChannelLayout.UsesQuadChannels(src.Spec.Kind)
                && WanosInteraction.TryGetListenerPosition(out Vector3 listener))
            {
                EnsureChannelGainState(src, 4);
                float dt = Mathf.Max(Time.deltaTime, 1f / 120f);
                float duck01 = _session.Duck.Level01;
                // SoftEnter：中心仍远低于目标时，四角跟中心静音起步，再各自追 CurveForGain。
                bool softEnterHold = !float.IsNaN(src.LastTargetGainDb)
                    && gainDb < src.LastTargetGainDb - 1f;
                for (int i = 0; i < 4; i++)
                {
                    GameObject ch = src.ChannelEmitters[i];
                    if (ch == null)
                        continue;
                    float dx = listener.x - ch.transform.position.x;
                    float dz = listener.z - ch.transform.position.z;
                    float chDist = Mathf.Sqrt(dx * dx + dz * dz);
                    float targetDb = GainDbForDistance(src.Spec, chDist);
                    targetDb = Mathf.Lerp(GainFloorDb, targetDb, duck01);
                    if (softEnterHold)
                        targetDb = Mathf.Min(targetDb, gainDb);
                    float applied;
                    if (forceSnapChannels || float.IsNaN(src.ChannelAppliedGainDb[i]))
                    {
                        applied = softEnterHold ? gainDb : targetDb;
                        src.ChannelGainSlewVelocity[i] = 0f;
                    }
                    else
                    {
                        applied = Mathf.SmoothDamp(
                            src.ChannelAppliedGainDb[i],
                            targetDb,
                            ref src.ChannelGainSlewVelocity[i],
                            GainSmoothTimeSeconds,
                            Mathf.Infinity,
                            dt);
                    }
                    src.ChannelAppliedGainDb[i] = applied;
                    WanosInteraction.ApplyEmitterDistanceAttenuation(ch, chDist, applied);
                }
                return;
            }

            for (int i = 0; i < src.ChannelEmitters.Length; i++)
            {
                if (src.ChannelEmitters[i] != null)
                    WanosInteraction.ApplyEmitterDistanceAttenuation(
                        src.ChannelEmitters[i], dist, gainDb);
            }
        }

        /// <summary>
        /// Boluo CurveForGain 每 FixedUpdate 推增益；本工程 Evaluate 只有 4Hz，
        /// 边缘走时必须每帧把 SmoothDamp / Distance RTPC 追完，否则听感就是电流音。
        /// </summary>
        void EnsureAttenuationDrive()
        {
            if (!_started)
                return;
            int gen = ++_attenuationDriveGen;
            DriveAttenuationSlews(gen).Forget();
        }

        async UniTaskVoid DriveAttenuationSlews(int gen)
        {
            while (gen == _attenuationDriveGen && _started)
            {
                TickAttenuationSlews();
                // 河/海四角在播时每帧追中心 + 角点 CurveForGain（Boluo BoxHelper 持续 refresh）。
                if (!AnyGainSlewPending()
                    && !AnyDistanceSlewPending()
                    && !AnyPlayingQuadChannels())
                    return;
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }

        bool AnyPlayingQuadChannels()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.Playing || src.SoftStopping || src.Spec == null)
                    continue;
                if (AmbienceQuadChannelLayout.UsesQuadChannels(src.Spec.Kind) && src.QuadCenterReady)
                    return true;
            }
            return false;
        }

        void TickAttenuationSlews()
        {
            if (!_started || !_catalogBuilt || _contentAudioPaused || _presetTransitionHeld
                || IslandAudioSession.IsYieldHeld)
                return;
            float dt = Mathf.Max(Time.deltaTime, 1f / 120f);
            bool hasListener = WanosInteraction.TryGetListenerPosition(out Vector3 listener);
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.Playing || src.SoftStopping || src.Emitter == null)
                    continue;
                if (float.IsNaN(src.LastTargetGainDb) || float.IsNaN(src.LastTargetDistance))
                    continue;

                // 河/海：每帧追四角（Boluo refresh≈0.1–0.2s SmoothDamp），禁止只靠 4Hz Evaluate。
                if (hasListener
                    && src.Spec != null
                    && AmbienceQuadChannelLayout.UsesQuadChannels(src.Spec.Kind)
                    && src.QuadCenterReady)
                {
                    LayoutQuadChannels(src, listener, src.QuadFollowTarget, forceSnap: false);
                }

                float targetDist = src.LastTargetDistance;
                float targetGain = src.LastTargetGainDb;
                float prevDist = float.IsNaN(src.LastSlewedDistance) ? targetDist : src.LastSlewedDistance;
                // 帧驱动只做平滑；硬贴只发生在 PushSlewedAttenuation(forceSnap) / 首帧 NaN。
                bool snap = float.IsNaN(src.LastAppliedGainDb);

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
                WriteDuckedGain(src, slewedDist, slewedGain, forceSnap: snap);
            }
        }

        bool AnyDistanceSlewPending()
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

        void BeginSoftStop(
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

        async UniTaskVoid DriveSoftStops(int gen)
        {
            while (gen == _softStopDriveGen && _started)
            {
                TickSoftStops();
                if (!AnySoftStopping())
                    return;
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }

        void CancelSoftStop(RuntimeSource src)
        {
            if (src == null || !src.SoftStopping)
                return;
            src.SoftStopping = false;
            src.SoftStopForceReleaseBank = false;
            src.SoftStopKeepBank = false;
        }

        void CancelAllSoftStops()
        {
            _softStopDriveGen++;
            for (int i = 0; i < Sources.Count; i++)
                CancelSoftStop(Sources[i]);
        }

        bool AnySoftStopping()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                if (Sources[i].SoftStopping)
                    return true;
            }
            return false;
        }

        bool AnyGainSlewPending()
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

        float SoftStopGainNow(RuntimeSource src)
        {
            float t = Mathf.Clamp01(
                (Time.realtimeSinceStartup - src.SoftStopStartedAt) / src.SoftStopSeconds);
            return Mathf.Lerp(src.SoftStopFromGainDb, GainFloorDb, t);
        }

        void TickSoftStops()
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

        void SnapAllPlayingGainsToFloor()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (!src.Playing || (src.Emitter == null && !HasQuadChannels(src)))
                    continue;
                src.LastTargetGainDb = GainFloorDb;
                src.LastAppliedGainDb = GainFloorDb;
                float dist = float.IsNaN(src.LastPostedDistance) ? 0f : src.LastPostedDistance;
                ApplyAttenuationToSourceEmitters(src, dist, GainFloorDb);
            }
        }

        void StopSource(
            RuntimeSource src,
            bool forceReleaseBank = false,
            bool keepBank = false,
            int fadeMs = VoiceStopFadeMs)
        {
            var spec = src.Spec;
            src.Playing = false;
            src.NeedsRepost = false;
            src.OutsideSince = -1f;
            src.StickyShoreIndex = -1;
            src.LastPostedDistance = float.NaN;  // 重置距离状态，下次 Start 时重新推送
            src.LastTargetGainDb = float.NaN;
            src.LastAppliedGainDb = float.NaN;
            src.GainSlewVelocity = 0f;
            src.LastSlewedDistance = float.NaN;
            src.LastTargetDistance = float.NaN;
            src.LastKeepDistance = float.NaN;
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

            string stop = AmbienceEventCatalog.ResolveStopEvent(spec.Bank, spec.StopEvent);
            if (HasQuadChannels(src))
            {
                // 2. 四角各自 StopEvent（voice 挂在分轨上）。
                if (!string.IsNullOrEmpty(stop))
                {
                    for (int i = 0; i < src.ChannelEmitters.Length; i++)
                    {
                        if (src.ChannelEmitters[i] != null)
                            WanosInteraction.PostEvent(stop, src.ChannelEmitters[i]);
                    }
                }
                ReleaseQuadChannels(src);
            }

            if (src.Emitter != null)
            {
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

        bool AnyOtherSourceHoldsBank(RuntimeSource self, string bank)
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

        void StopAllPlaying(
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

        void FlushDeferredEmitterReleases(bool force)
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
                    var src = Sources[s];
                    if (!src.Playing)
                        continue;
                    if (src.Emitter == go)
                    {
                        live = true;
                        break;
                    }
                    if (src.ChannelEmitters != null)
                    {
                        for (int c = 0; c < src.ChannelEmitters.Length; c++)
                        {
                            if (src.ChannelEmitters[c] == go)
                            {
                                live = true;
                                break;
                            }
                        }
                        if (live)
                            break;
                    }
                }

                if (live)
                    continue;

                WanosInteraction.ReleaseEmitter(go);
                // 池内 host → 还池；四角临时 GO → Destroy。
                bool pooled = false;
                if (_emitterPool != null)
                {
                    for (int p = 0; p < _emitterPool.Length; p++)
                    {
                        if (_emitterPool[p] == go)
                        {
                            pooled = true;
                            break;
                        }
                    }
                }
                if (pooled)
                    ReturnEmitter(go);
                else
                    UnityEngine.Object.Destroy(go);
            }
        }
    }
}
