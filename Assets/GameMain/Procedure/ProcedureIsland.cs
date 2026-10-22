using System.Collections;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
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

        public static void RequestReturnToGuestLogin()
        {
            System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 1);
        }

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
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

            if (!isShutdown)
            {
                IslandPlayerPosSave.TrySaveCurrent();
                IslandPlayerPosSave.ResetSession();
                AudioInteractionBootstrap.Shutdown();
                WanosAudio.WanosEngine.StopEngine("ProcedureIsland.OnLeave");
                WanosAudio.WanosEngine.Unload("ProcedureIsland.OnLeave");
                GameEntry.World?.Release();
                _worldLoadStarted = false;
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
                    GameEntry.Weather?.ApplyLocalClockIfNeeded();
                    GameEntry.Music?.ApplySavedVolumes();
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

                    if (GameEntry.World == null || !GameEntry.World.IsLoaded)
                        break;

                    GameEntry.Weather?.PushVisuals();
                    _phase = Phase.Camera;
                    break;

                case Phase.Camera:
                    EnsureJoystickCamera();
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
                        GameEntry.Weather?.ApplyLocalClockIfNeeded();
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
            // Android：jar 内 bank 须先落到 persistent，Ambience Retain 才能找到 Ocean_Calm_Quad 等。
            await BankUpgradeBootstrap.EnsureLocalBanksReadyAsync();
            if (gen != _uncoverGeneration)
                return;

            // Device first, then DSP (macOS WanosDSP_StartEngine needs playback device open).
            WanosAudio.WanosEngine.EnablePlayback("ProcedureIsland.Uncover");
            WanosAudio.WanosEngine.StartEngine("ProcedureIsland.Uncover");
            WanosAudio.WanosEngine.Unmute("ProcedureIsland.Uncover");
            // Volumes may have been applied before device open — re-push 0–100 RTPCs now.
            GameEntry.Music?.ApplySavedVolumes();
            AudioInteractionBootstrap.StartAfterUncover();
            // Cue Music1_1 as soon as bank is ready (does not wait for full Critical wave).
            BankUpgradeBootstrap.WaitCriticalThenCueAsync(45f).Forget();
            if (gen != _uncoverGeneration)
                return;

            // 二次进岛：清 Panel 残留 + 预设/相机闩，避免有声但不能走。
            UnlockLocomotionAfterUncover();
            BootLoadingService.FadeHide(1f);
            // FadeHide 若被上一轮 Leave 打断卡在 _hiding，补一刀立刻关罩。
            await UniTask.Delay(1100, DelayType.UnscaledDeltaTime);
            if (gen != _uncoverGeneration)
                return;

            if (BootLoadingService.IsVisible || BootLoadingService.HasForm)
            {
                Log.Warning("[ProcedureIsland] loading still up after FadeHide — force Hide + CloseGroup Loading");
                BootLoadingService.Hide();
                if (GameEntry.UI != null)
                    UiAssets.CloseGroup("Loading");
            }

            // 再确认：假死罩 / UITK 残留必须清掉，否则会出现「IslandMain 在跑、画面仍『正在进入岛屿』」。
            await UniTask.Yield();
            if (gen != _uncoverGeneration)
                return;

            BootLoadingService.Hide();
            if (GameEntry.UI != null)
                UiAssets.CloseGroup("Loading");
            Log.Info(
                "[ProcedureIsland] uncover settle HasForm="
                + BootLoadingService.HasForm
                + " IsVisible="
                + BootLoadingService.IsVisible);
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
                }
                Native.NativeOverlayGate.Apply(present: 0, touchEnabled: 1, bottomHidden: 0);
            }
            catch (System.Exception e)
            {
                Log.Warning("[ProcedureIsland] UnlockLocomotionAfterUncover: " + e.Message);
            }
            Log.Info("[ProcedureIsland] UnlockLocomotionAfterUncover");
        }
    }
}
