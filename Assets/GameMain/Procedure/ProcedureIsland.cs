using System.Collections;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using Game.Native;
using GameFramework.Procedure;
using UnityEngine;
using UnityGameFramework.Runtime;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    /// <summary>
    /// 岛屿流程编排：Services → World → Camera → Hud → Uncover。世界加载见 IslandWorldService。
    /// </summary>
    public class ProcedureIsland : ProcedureBase
    {
        enum Phase
        {
            Services,
            World,
            Camera,
            Hud,
            Uncover,
            Done,
        }

        const float WorldLoadCapSeconds = 180f;
        const float UncoverCapSeconds = 30f;

        Phase _phase;
        bool _hudOpened;
        bool _worldLoadStarted;
        bool _worldLoadFinished;
        bool _fadeStarted;
        bool _uncoverForced;
        float _hudWait;
        float _worldSince;
        float _uncoverSince;
        Coroutine _worldLoadRoutine;

        /// <summary>宿主 HostLeaveIsland：下一帧退回 GuestLogin，便于再次 Arm 进岛。</summary>
        static int _returnToGuestLogin;

        /// <summary>仍在本轮 ProcedureIsland 里，且还没收到退登。迟到的世界就绪不得再 Notify。</summary>
        static int _alive;

        public static bool AcceptsIslandReadyNotify =>
            System.Threading.Volatile.Read(ref _alive) != 0
            && System.Threading.Volatile.Read(ref _returnToGuestLogin) == 0;

        /// <summary>递增以作废进行中的 <see cref="UncoverAsync"/>（退登时勿再 Hide 加载罩）。</summary>
        static int _uncoverGeneration;

        /// <summary>进程内进岛次数（含二次退登重进）；供 Load/telemetry。</summary>
        static int _enterCount;

        /// <summary>揭罩代际。Leave / 新一轮 Uncover 递增后，旧的淡出协程自行退出。</summary>
        public static int UncoverGeneration =>
            System.Threading.Interlocked.CompareExchange(ref _uncoverGeneration, 0, 0);

        /// <summary>当前进程第几次进入 ProcedureIsland（1=冷启）。</summary>
        public static int EnterCount =>
            System.Threading.Interlocked.CompareExchange(ref _enterCount, 0, 0);

        public static void RequestReturnToGuestLogin()
        {
            System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 1);
        }

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            System.Threading.Interlocked.Exchange(ref _alive, 1);
            int enter = System.Threading.Interlocked.Increment(ref _enterCount);
            System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 0);
            _phase = Phase.Services;
            _hudOpened = false;
            _worldLoadStarted = false;
            _worldLoadFinished = false;
            _fadeStarted = false;
            _uncoverForced = false;
            _hudWait = 0f;
            _worldSince = 0f;
            _uncoverSince = 0f;
            // HasForm 可能是假死罩：用 IsVisible，否则 ReportProgress 在 !_visible 时直接 return。
            if (!BootLoadingService.IsVisible)
                BootLoadingService.Show("正在进入世界...", BootLoadingService.VisualPrepareMin);
            else
                BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
            Log.Info(
                $"[ProcedureIsland] enter #{enter} reclaimBusy={IslandLeaveAssetReclaim.IsBusy}");
            EnsureSingleAudioListener();
        }

        static void EnsureSingleAudioListener()
        {
            AudioListener[] listeners = Object.FindObjectsByType<AudioListener>();
            Camera main = Camera.main;
            for (int i = 0; i < listeners.Length; i++)
            {
                bool keep = main != null && listeners[i].GetComponent<Camera>() == main;
                if (!keep && main != null)
                    listeners[i].enabled = false;
            }

            if (main != null)
            {
                AudioListener onCam = main.GetComponent<AudioListener>();
                if (onCam == null)
                    onCam = main.gameObject.AddComponent<AudioListener>();
                onCam.enabled = true;
            }
        }

        static IslandJoystickCamera EnsureJoystickCamera()
        {
            Camera main = Camera.main;
            if (main == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                main = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            IslandCameraSrpSetup.ApplyForIsland(main);

            IslandJoystickCamera cam = main.GetComponent<IslandJoystickCamera>();
            if (cam == null)
                cam = main.gameObject.AddComponent<IslandJoystickCamera>();
            IslandPlayerPosSave.InitAndArm(cam);
            Log.Info(
                "[ProcedureIsland] camera pose pos="
                + main.transform.position.ToString("F1")
                + " yaw=" + main.transform.eulerAngles.y.ToString("F1")
                + " far=" + main.farClipPlane.ToString("F0")
                + " terrainActive=" + (Terrain.activeTerrain != null));
            return cam;
        }

        IEnumerator LoadWorld()
        {
            try
            {
                IslandWorldService world = GameEntry.World ?? IslandWorldService.Ensure();
                yield return world.Load((p, tip) => BootLoadingService.ReportProgress(p, tip));
            }
            finally
            {
                _worldLoadFinished = true;
                _worldLoadRoutine = null;
            }
        }

        /// <summary>
        /// 世界阶段不能再空等：打一条 Error，松开铺装闩，继续相机 / 揭罩。
        /// </summary>
        void ContinuePastWorld(string reason)
        {
            Log.Error("[ProcedureIsland] " + reason + " — continue island entry");
            if (_worldLoadRoutine != null && GameEntry.Base != null)
                GameEntry.Base.StopCoroutine(_worldLoadRoutine);
            _worldLoadRoutine = null;
            _worldLoadStarted = true;
            _worldLoadFinished = true;
            EnvItemStreamerLite.ReleasePrefillHold();
            BootLoadingService.ReportProgress(
                BootLoadingService.TerrainPrepareMax, "世界加载未完成，继续进入...");
            if (_phase == Phase.Services || _phase == Phase.World)
                _phase = Phase.Camera;
            else if (_phase != Phase.Done)
                _phase = Phase.Uncover;
        }

        static void ForceUncoverNow()
        {
            if (!AcceptsIslandReadyNotify)
                return;
            int gen = System.Threading.Interlocked.CompareExchange(ref _uncoverGeneration, 0, 0);
            EnvItemStreamerLite.ReleasePrefillHold();
            if (gen != _uncoverGeneration)
                return;
            BootLoadingService.Hide();
            IslandJoystickCamera.Current?.ReleaseSpawnHold();
            if (GameEntry.UI != null && GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
                UiAssets.CloseGroup("Loading");
            if (gen != _uncoverGeneration)
                return;
            UnlockLocomotionAfterUncover();
            NativeBridgeFacade.NotifyIslandReady();
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 0) != 0)
            {
                Log.Info("[ProcedureIsland] HostLeave → ProcedureGuestLogin");
                // 作废揭罩：否则 UncoverAsync 延迟 Hide 会拆掉下面刚 Show 的加载罩。
                System.Threading.Interlocked.Increment(ref _uncoverGeneration);
                // 先松预设/相机闩，再关 UI（OnClose 也会 Abort，此处防 Mid-Leave 丢帧）。
                PendingDownloadIntent.Clear();
                GameEntry.Presets?.AbortTransition();
                PresetWeatherHold.Abort();
                IslandJoystickCamera.Current?.ForceUnlockLocomotion();
                if (GameEntry.UI != null)
                {
                    // Main=岛 HUD/摇杆；Panel/Dialog 残留会让 HudOverlayGate.BlocksLocomotion 钉死。
                    UiAssets.CloseGroup("Main");
                    UiAssets.CloseGroup("Panel");
                    UiAssets.CloseGroup("Dialog");
                    UiAssets.CloseGroup(UiAssets.TipGroup);
                }
                BootLoadingService.Show("正在进入世界...", BootLoadingService.VisualPrepareMin);
                ChangeState<ProcedureGuestLogin>(procedureOwner);
                return;
            }

            if (_phase == Phase.Done)
            {
                IslandPlayerPosSave.Tick();
                return;
            }

            try
            {
                TickPhase();
            }
            catch (System.Exception e)
            {
                Log.Error("[ProcedureIsland] phase " + _phase + " threw — " + e);
                if (_phase == Phase.Uncover || _phase == Phase.Done)
                {
                    if (!_uncoverForced)
                    {
                        _uncoverForced = true;
                        ForceUncoverNow();
                        _phase = Phase.Done;
                    }
                }
                else
                {
                    ContinuePastWorld("phase threw: " + e.Message);
                }
            }
        }

        protected override void OnLeave(ProcedureOwner procedureOwner, bool isShutdown)
        {
            System.Threading.Interlocked.Exchange(ref _alive, 0);
            // 任意离开岛流程都作废揭罩，避免延迟 Hide 打到 GuestLogin 的加载罩。
            System.Threading.Interlocked.Increment(ref _uncoverGeneration);

            if (_worldLoadRoutine != null && GameEntry.Base != null)
            {
                GameEntry.Base.StopCoroutine(_worldLoadRoutine);
                _worldLoadRoutine = null;
                EnvItemStreamerLite.ReleasePrefillHold();
            }

            // 杀进程 / 正常离岛落盘。退登路径已在 ExecuteLeave 清会话前 flush；此处会话仍在时再兜底一刀。
            // 揭罩前离岛也要松出生锁，避免主相机停在进岛姿态上不再更新。
            IslandJoystickCamera.Current?.ReleaseSpawnHold();
            if (BoluoGamePersistence.AllowsAccountProgressDisk)
                IslandPlayerPosSave.TrySaveCurrentAndFlush();

            if (!isShutdown)
            {
                IslandPlayerPosSave.ResetSession();
                AudioInteractionBootstrap.Shutdown();
                // 只停 DSP，不 Destroy 引擎。Destroy 会清掉已加载的 bank，重登时主线程
                // 同步 LoadBank 和近场 Prefill 叠在一起，LoadBank 不返回，罩停在「正在铺装环境」。
                WanosAudio.WanosEngine.StopEngine("ProcedureIsland.OnLeave");
                GameEntry.World?.Release();
                _worldLoadStarted = false;
                // Release 松 handle 后立刻排队卸未用包体；GuestLogin 等 Arm 时空窗消化。
                IslandLeaveAssetReclaim.ScheduleAfterWorldRelease();
            }
            else
            {
                IslandLeaveAssetReclaim.Cancel();
            }

            base.OnLeave(procedureOwner, isShutdown);
        }

        void TickPhase()
        {
            switch (_phase)
            {
                case Phase.Services:
                    GameEntry.EnsureUiGroups();
                    GameEntry.EnsureEntityGroups();
                    WanosSoundSetup.EnsureSoundGroups();
                    if (GameEntry.World == null)
                        GameEntry.InitWorldService();
                    // 正式账号进岛前清密码节流；主存档加载后应用音量
                    if (!GuestSession.IsGuest)
                        AccountPasswordLoginThrottle.ClearAll();
                    BoluoGameSave.Load();
                    DownloadedUnplayedManager.EnsureLoaded();
                    // 有存档则恢复天气/时段；否则跟墙钟并写入首份快照。
                    GameEntry.Weather?.RestoreOrApplyLocalClock();
                    GameEntry.Music?.ApplySavedVolumes();
                    // Prefill 焦点必须是本账号/游客桶恢复后的相机位；勿等 World 后再 InitAndArm。
                    EnsureJoystickCamera();
                    BootLoadingService.ReportProgress(0.71f, "正在进入世界...");
                    _phase = Phase.World;
                    break;

                case Phase.World:
                    if (!_worldLoadStarted)
                    {
                        if (!YooResourceModule.IsInitialized)
                        {
                            ContinuePastWorld(
                                "YooAsset not initialized — cannot load island world. last="
                                + (YooResourceModule.LastError ?? "none"));
                            break;
                        }

                        MonoBehaviour host = GameEntry.Base;
                        if (host == null)
                        {
                            ContinuePastWorld("GameEntry.Base null — cannot start world load");
                            break;
                        }

                        _worldLoadStarted = true;
                        _worldLoadFinished = false;
                        _worldSince = Time.realtimeSinceStartup;
                        _worldLoadRoutine = host.StartCoroutine(LoadWorld());
                        if (_worldLoadRoutine == null)
                        {
                            ContinuePastWorld("StartCoroutine(LoadWorld) returned null");
                            break;
                        }

                        if (_worldLoadFinished)
                            _worldLoadRoutine = null;
                    }

                    // 近场 Prefill 就绪即可进岛；远场 Prefill / 草 / Nav 在 Load 协程里后台继续。
                    if (GameEntry.World != null && GameEntry.World.IsNearReady)
                    {
                        GameEntry.Weather?.PushVisuals();
                        _phase = Phase.Camera;
                        break;
                    }

                    if (_worldLoadFinished)
                    {
                        // 地形或海洋没实例化出来时不要停在 World，也不要跳过铺装。
                        // 卸包和二次加载叠在一起时，下一帧重试才能再拿到实例。
                        Log.Error(
                            "[ProcedureIsland] terrain/ocean not ready — retry world load next frame"
                            + (GameEntry.World != null && GameEntry.World.LoadFailed ? " LoadFailed" : "")
                            + (string.IsNullOrEmpty(YooResourceModule.LastError)
                                ? ""
                                : " yoo=" + YooResourceModule.LastError));
                        _worldLoadStarted = false;
                        _worldLoadFinished = false;
                        _worldLoadRoutine = null;
                        break;
                    }

                    if (_worldSince > 0f
                        && Time.realtimeSinceStartup - _worldSince >= WorldLoadCapSeconds)
                    {
                        ContinuePastWorld(
                            "world load exceeded " + WorldLoadCapSeconds.ToString("0") + "s");
                    }

                    break;

                case Phase.Camera:
                    // Services 已 InitAndArm（供 Prefill 焦点）；地形就绪后必须重贴，
                    // 否则二次进岛会带着「无碰撞期间」的位姿揭罩（掉地底）。
                    if (IslandJoystickCamera.Current == null)
                        EnsureJoystickCamera();
                    else
                        IslandPlayerPosSave.ReplantAfterTerrainReady(IslandJoystickCamera.Current);
                    BootLoadingService.ReportProgress(BootLoadingService.CameraPrepareMax, "正在进入岛屿");
                    _phase = Phase.Hud;
                    break;

                case Phase.Hud:
                    if (GameEntry.UI == null)
                    {
                        Log.Error("[ProcedureIsland] UI missing — uncover without HUD");
                        _phase = Phase.Uncover;
                        break;
                    }

                    if (!_hudOpened)
                    {
                        _hudOpened = true;
                        // 二次进岛：先拆可能残留的旧 Main，避免双 IslandMainForm 双订 Preset 事件。
                        if (GameEntry.UI.HasUIForm(UiAssets.IslandMainForm))
                        {
                            Log.Warning("[ProcedureIsland] stale IslandMainForm — CloseGroup Main before reopen");
                            UiAssets.CloseGroup("Main");
                        }
                        // 不再 ApplyLocalClock：Services 已 Restore；此处只推视觉。
                        GameEntry.Weather?.PushVisuals();
                        Log.Info("[ProcedureIsland] OpenUIForm IslandMainForm");
                        GameEntry.UI.OpenUIForm(UiAssets.IslandMainForm, "Main");
                        UiTipToast.EnsureHost();
                    }

                    if (!GameEntry.UI.HasUIForm(UiAssets.IslandMainForm))
                    {
                        _hudWait += Time.unscaledDeltaTime;
                        if (_hudWait < 10f)
                            break;
                        Log.Error("[ProcedureIsland] IslandMainForm timeout — uncover without HUD");
                    }

                    BootLoadingService.ReportProgress(BootLoadingService.ItemsPrepareMax, "即将进入...");
                    _phase = Phase.Uncover;
                    break;

                case Phase.Uncover:
                    if (!_fadeStarted)
                    {
                        _fadeStarted = true;
                        _uncoverSince = Time.realtimeSinceStartup;
                        Log.Info("[ProcedureIsland] FadeHide loading cover");
                        UncoverAsync().Forget();
                    }

                    bool loadingFormOpen = GameEntry.UI != null
                        && GameEntry.UI.HasUIForm(UiAssets.LoadingForm);
                    if (!BootLoadingService.IsVisible && !loadingFormOpen)
                        _phase = Phase.Done;
                    else if (!_uncoverForced
                        && _uncoverSince > 0f
                        && Time.realtimeSinceStartup - _uncoverSince >= UncoverCapSeconds)
                    {
                        _uncoverForced = true;
                        Log.Error(
                            "[ProcedureIsland] uncover still covered after "
                            + UncoverCapSeconds.ToString("0")
                            + "s — force hide");
                        ForceUncoverNow();
                        _phase = Phase.Done;
                    }

                    break;
            }
        }

        static async UniTaskVoid UncoverAsync()
        {
            int gen = System.Threading.Interlocked.CompareExchange(ref _uncoverGeneration, 0, 0);

            // 起播（StartEngine 约 0.4–0.6s、封面解码）在罩下做完再 FadeHide。
            // 与远场 Prefill 叠在揭罩帧上时，主线程会打出可见卡顿。
            try
            {
                UniTask audio = StartAudioBeforeUncover(gen);
                int winner = await UniTask.WhenAny(
                    audio,
                    UniTask.Delay(20000, DelayType.UnscaledDeltaTime));
                if (winner != 0)
                {
                    Log.Error(
                        "[ProcedureIsland] audio before uncover exceeded 20s — uncover anyway");
                }
            }
            catch (System.Exception e)
            {
                Log.Error("[ProcedureIsland] audio before uncover failed: " + e.Message);
            }

            if (gen != _uncoverGeneration)
                return;

            // 罩还是不透明的：用已经加载的地形把眼睛贴到落点，再开始淡出。
            if (IslandJoystickCamera.Current != null)
                IslandPlayerPosSave.ReplantAfterTerrainReady(IslandJoystickCamera.Current);

            // 先揭罩 + 放行触摸，再做 Gmj 预热。
            // 二次进岛 Prefill 抢主线程时，揭罩前 warmup 会把「即将进入」拖成几十秒（有声无画面）。
            EnvStreamingConfig.BeginPostUncoverQuiet();
            UnlockLocomotionAfterUncover();
            const float fadeSec = 0.35f;
            BootLoadingService.FadeHide(fadeSec);
            // 墙钟超时：帧率被 Prefill 打穿时 UniTask.Delay(帧) 也会拖很久。
            float wallStart = Time.realtimeSinceStartup;
            const float wallCap = 1.5f;
            while (BootLoadingService.IsVisible || BootLoadingService.HasForm)
            {
                if (gen != _uncoverGeneration)
                    return;
                if (Time.realtimeSinceStartup - wallStart >= wallCap)
                {
                    Log.Error("[ProcedureIsland] FadeHide wall-clock cap — force Hide");
                    break;
                }

                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            if (gen != _uncoverGeneration)
                return;

            BootLoadingService.Hide();
            IslandJoystickCamera.Current?.ReleaseSpawnHold();
            if (GameEntry.UI != null)
                UiAssets.CloseGroup("Loading");
            if (gen != _uncoverGeneration)
                return;
            // 加载罩已撤 → 立刻 Notify(2)，宿主抬 islandHudReady 放行触摸；
            // 勿等 Prefill/Nav/Grass 的 IslandWorldReady（否则揭罩后长时间点不动）。
            UnlockLocomotionAfterUncover();
            NativeBridgeFacade.NotifyIslandReady();
            Log.Info(
                "[ProcedureIsland] uncover settle HasForm="
                + BootLoadingService.HasForm
                + " IsVisible="
                + BootLoadingService.IsVisible
                + " IslandReady notified");

            // Gmj PSO 预热挪到揭罩后；再延迟松闩，防宿主晚推 overlay 钉死行走。
            PostUncoverWarmupAndRelockAsync(gen).Forget();
        }

        static async UniTaskVoid PostUncoverWarmupAndRelockAsync(int gen)
        {
            float wallStart = Time.realtimeSinceStartup;
            Log.Info("[Hitch] PostUncoverWarmup begin");
            await UniTask.Delay(200, DelayType.UnscaledDeltaTime);
            if (gen != _uncoverGeneration)
                return;
            UnlockLocomotionAfterUncover();
            await UniTask.Delay(600, DelayType.UnscaledDeltaTime);
            if (gen != _uncoverGeneration)
                return;
            UnlockLocomotionAfterUncover();
            Log.Info(
                "[Hitch] PostUncoverWarmup end wallMs="
                + ((Time.realtimeSinceStartup - wallStart) * 1000f).ToString("F0"));
        }

        static async UniTask StartAudioBeforeUncover(int gen)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await BankUpgradeBootstrap.EnsureLocalBanksReadyAsync();
            Log.Info("[Hitch] audio banks ms=" + sw.ElapsedMilliseconds);
            if (gen != _uncoverGeneration)
                return;

            sw.Restart();
            WanosAudio.WanosEngine.EnablePlayback("ProcedureIsland.Uncover");
            Log.Info("[Hitch] audio EnablePlayback ms=" + sw.ElapsedMilliseconds);
            if (gen != _uncoverGeneration)
                return;

            sw.Restart();
            WanosAudio.WanosEngine.StartEngine("ProcedureIsland.Uncover");
            Log.Info("[Hitch] audio native StartEngine ms=" + sw.ElapsedMilliseconds);
            if (gen != _uncoverGeneration)
                return;

            sw.Restart();
            WanosAudio.WanosEngine.Unmute("ProcedureIsland.Uncover");
            GameEntry.Music?.ApplySavedVolumes();
            Log.Info("[Hitch] audio Unmute+volumes ms=" + sw.ElapsedMilliseconds);
            if (gen != _uncoverGeneration)
                return;

            sw.Restart();
            AudioInteractionBootstrap.StartAfterUncover();
            Log.Info("[Hitch] audio interaction ms=" + sw.ElapsedMilliseconds);
            // 一出声就推负一屏：须先显式 Arm（PublishPlayingForced 不再自动 Arm，避免退登拆除后
            // 残留回调重开闸拉 FGS → ForegroundServiceDidNotStartInTimeException）。
            if (gen != _uncoverGeneration)
                return;
            sw.Restart();
            NowPlayingService.ArmSession();
            Log.Info("[Hitch] audio ArmSession ms=" + sw.ElapsedMilliseconds);
            if (gen != _uncoverGeneration)
                return;
            sw.Restart();
            NowPlayingService.PublishPlayingForced();
            Log.Info("[Hitch] audio PublishPlayingForced ms=" + sw.ElapsedMilliseconds);
            BankUpgradeBootstrap.WaitCriticalThenCueAsync(45f).Forget();
        }

        static void UnlockLocomotionAfterUncover()
        {
            try
            {
                PendingDownloadIntent.Clear();
                GameEntry.Presets?.AbortTransition();
                PresetWeatherHold.Abort();
                IslandJoystickCamera.Current?.ForceUnlockLocomotion();
                if (GameEntry.UI != null)
                {
                    UiAssets.CloseGroup("Panel");
                    UiAssets.CloseGroup("Dialog");
                    // Loading 残留会挡 UITK 命中 → 摇杆收不到指针。
                    if (GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
                        UiAssets.CloseGroup("Loading");
                }
                Native.NativeOverlayGate.Apply(present: 0, touchEnabled: 1, bottomHidden: 0);
                NativeBridgeFacade.SyncOverlayFromGate();
            }
            catch (System.Exception e)
            {
                Log.Warning("[ProcedureIsland] UnlockLocomotionAfterUncover: " + e.Message);
            }
            Log.Info(
                "[ProcedureIsland] UnlockLocomotionAfterUncover " +
                $"presetBusy={GameEntry.Presets?.IsTransitioning == true} " +
                $"overlayBlock={Native.NativeOverlayGate.BlocksLocomotion} " +
                $"popup={HudOverlayGate.HasPopup()}");
        }
    }
}
