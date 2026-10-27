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

        Phase _phase;
        bool _hudOpened;
        bool _worldLoadStarted;
        bool _fadeStarted;
        float _hudWait;
        Coroutine _worldLoadRoutine;

        /// <summary>宿主 HostLeaveIsland：下一帧退回 GuestLogin，便于再次 Arm 进岛。</summary>
        static int _returnToGuestLogin;

        /// <summary>递增以作废进行中的 <see cref="UncoverAsync"/>（退登时勿再 Hide 加载罩）。</summary>
        static int _uncoverGeneration;

        /// <summary>进程内进岛次数（含二次退登重进）；供 Load/telemetry。</summary>
        static int _enterCount;

        /// <summary>供 <see cref="IslandStartupWarmup"/> 检测揭罩是否已作废。</summary>
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
            int enter = System.Threading.Interlocked.Increment(ref _enterCount);
            System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 0);
            _phase = Phase.Services;
            _hudOpened = false;
            _worldLoadStarted = false;
            _fadeStarted = false;
            _hudWait = 0f;
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
            IslandWorldService world = GameEntry.World ?? IslandWorldService.Ensure();
            yield return world.Load((p, tip) => BootLoadingService.ReportProgress(p, tip));
            _worldLoadRoutine = null;
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 0) != 0)
            {
                Log.Info("[ProcedureIsland] HostLeave → ProcedureGuestLogin");
                // 作废揭罩：否则 UncoverAsync 延迟 Hide 会拆掉下面刚 Show 的加载罩。
                System.Threading.Interlocked.Increment(ref _uncoverGeneration);
                // 先于 Close Main：IdleImmersive.Dispose 会 OnExitImmersive；
                // 若不 suppress，会 Resume 镜面并与后续 World.Release/二次 Prefill 抢帧。
                IslandImmersiveSoftUnload.CancelForWorldTeardown();
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

            TickPhase();
        }

        protected override void OnLeave(ProcedureOwner procedureOwner, bool isShutdown)
        {
            // 任意离开岛流程都作废揭罩，避免延迟 Hide 打到 GuestLogin 的加载罩。
            System.Threading.Interlocked.Increment(ref _uncoverGeneration);

            if (_worldLoadRoutine != null && GameEntry.Base != null)
            {
                GameEntry.Base.StopCoroutine(_worldLoadRoutine);
                _worldLoadRoutine = null;
            }

            // 杀进程 / 正常离岛落盘。退登路径已在 ExecuteLeave 清会话前 flush；此处会话仍在时再兜底一刀。
            if (BoluoGamePersistence.AllowsAccountProgressDisk)
                IslandPlayerPosSave.TrySaveCurrentAndFlush();

            if (!isShutdown)
            {
                IslandPlayerPosSave.ResetSession();
                AudioInteractionBootstrap.Shutdown();
                WanosAudio.WanosEngine.StopEngine("ProcedureIsland.OnLeave");
                WanosAudio.WanosEngine.Unload("ProcedureIsland.OnLeave");
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
                            Log.Error("YooAsset not initialized — cannot load island world.");
                            break;
                        }

                        MonoBehaviour host = GameEntry.Base;
                        if (host == null)
                            break;

                        _worldLoadStarted = true;
                        _worldLoadRoutine = host.StartCoroutine(LoadWorld());
                    }

                    // 近场 Prefill 就绪即可进岛；远场 Prefill / 草 / Nav 在 Load 协程里后台继续。
                    if (GameEntry.World == null || !GameEntry.World.IsNearReady)
                        break;

                    GameEntry.Weather?.PushVisuals();
                    _phase = Phase.Camera;
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
                        Log.Info("[ProcedureIsland] FadeHide loading cover");
                        UncoverAsync().Forget();
                    }

                    if (!BootLoadingService.IsVisible && !GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
                        _phase = Phase.Done;
                    break;
            }
        }

        static async UniTaskVoid UncoverAsync()
        {
            int gen = System.Threading.Interlocked.CompareExchange(ref _uncoverGeneration, 0, 0);

            // bank 解压 / Critical 不挡揭罩（地形就绪即可进）；音效后台补上。
            StartAudioAfterBanksAsync(gen).Forget();

            if (gen != _uncoverGeneration)
                return;

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
                    Log.Warning("[ProcedureIsland] FadeHide wall-clock cap — force Hide");
                    break;
                }

                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            BootLoadingService.Hide();
            if (GameEntry.UI != null)
                UiAssets.CloseGroup("Loading");
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
            IslandStartupWarmup.RunAsync(gen).Forget();
            await UniTask.Delay(200, DelayType.UnscaledDeltaTime);
            if (gen != _uncoverGeneration)
                return;
            UnlockLocomotionAfterUncover();
            await UniTask.Delay(600, DelayType.UnscaledDeltaTime);
            if (gen != _uncoverGeneration)
                return;
            UnlockLocomotionAfterUncover();
        }

        static async UniTaskVoid StartAudioAfterBanksAsync(int gen)
        {
            await BankUpgradeBootstrap.EnsureLocalBanksReadyAsync();
            if (gen != _uncoverGeneration)
                return;

            WanosAudio.WanosEngine.EnablePlayback("ProcedureIsland.Uncover");
            WanosAudio.WanosEngine.StartEngine("ProcedureIsland.Uncover");
            WanosAudio.WanosEngine.Unmute("ProcedureIsland.Uncover");
            GameEntry.Music?.ApplySavedVolumes();
            AudioInteractionBootstrap.StartAfterUncover();
            // 一出声就推负一屏：须先显式 Arm（PublishPlayingForced 不再自动 Arm，避免退登拆除后
            // 残留回调重开闸拉 FGS → ForegroundServiceDidNotStartInTimeException）。
            if (gen != _uncoverGeneration)
                return;
            NowPlayingService.ArmSession();
            if (gen != _uncoverGeneration)
                return;
            NowPlayingService.PublishPlayingForced();
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
