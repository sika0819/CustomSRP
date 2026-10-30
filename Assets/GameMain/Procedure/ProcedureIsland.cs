using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using Game.Native;
using GameFramework.Event;
using GameFramework.Procedure;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGameFramework.Runtime;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    /// <summary>
    /// 岛屿流程：先由 GameFramework 附加加载 Island 场景，再 Services → World → Camera → Hud → Uncover。
    /// 退登卸掉这张场景，卸载成功后才回到游客登录。
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
        ProcedureOwner _owner;
        bool _sceneEventsHooked;
        bool _sceneReady;
        bool _leaveRequested;
        bool _leavePoolsReleased;
        Coroutine _leaveUnloadRoutine;
        bool _unloadStarted;
        bool _leaveFinished;
        bool _fallbackStarted;
        int _unloadAttempts;
        float _sceneWaitSince;
        float _sceneWaitLogAt;
        float _worldRetryLogAt;

        /// <summary>宿主 HostLeaveIsland：下一帧退回 GuestLogin，便于再次 Arm 进岛。</summary>
        static int _returnToGuestLogin;

        /// <summary>仍在本轮 ProcedureIsland 里，且还没收到退登。迟到的世界就绪不得再 Notify。</summary>
        static int _alive;

        public static bool AcceptsIslandReadyNotify =>
            System.Threading.Volatile.Read(ref _alive) != 0
            && System.Threading.Volatile.Read(ref _returnToGuestLogin) == 0;

        /// <summary>Notify(2) 被丢掉时的原因：notAlive / leaving。</summary>
        public static string IslandReadyBlockReason =>
            System.Threading.Volatile.Read(ref _alive) == 0 ? "notAlive" : "leaving";

        /// <summary>递增以作废进行中的 <see cref="UncoverAsync"/>（退登时勿再 Hide 加载罩）。</summary>
        static int _uncoverGeneration;

        /// <summary>进程内进岛次数（含二次退登重进）；供 Load/telemetry。</summary>
        static int _enterCount;

        /// <summary>本进程进岛次数（含当前），供遥测 / GuestLogin 日志。</summary>
        public static int EnterCount =>
            System.Threading.Interlocked.CompareExchange(ref _enterCount, 0, 0);

        /// <summary>揭罩代际。Leave / 新一轮 Uncover 递增后，旧的淡出协程自行退出。</summary>
        public static int UncoverGeneration =>
            System.Threading.Interlocked.CompareExchange(ref _uncoverGeneration, 0, 0);

        public static void RequestReturnToGuestLogin()
        {
            System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 1);
        }

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            GameEntry.OpenLeaveHandles();
            // 旧包会把管线拆掉。没拆过则这里是空操作；拆过的必须装回，否则 Builtin 画岛。
            IslandLeaveAssetReclaim.RestoreParkedPipeline();
            IslandGpuReclaim.End();
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
            _owner = procedureOwner;
            _sceneReady = false;
            _leaveRequested = false;
            _leavePoolsReleased = false;
            _leaveUnloadRoutine = null;
            _unloadStarted = false;
            _leaveFinished = false;
            _fallbackStarted = false;
            _unloadAttempts = 0;
            _sceneWaitSince = 0f;
            _sceneWaitLogAt = 0f;
            _worldRetryLogAt = 0f;
            Log.Info(
                $"[ProcedureIsland] enter #{enter} reclaimBusy={IslandLeaveAssetReclaim.Ensure() is { IsBusy: true }}");
            // 负一屏不等音频加载。人在前台就先出「波萝」卡片，曲名等播放列表就绪再刷。
            NowPlayingService.ShowCardIfForeground();
            if (enter > 1)
                IslandLeaveAssetReclaim.LogFootprint("enter" + enter);
            IslandLeaveAssetReclaim.LogScudoStats("enter" + enter);
            IslandEntryLog.Ok(
                "island",
                "enter",
                "n=" + enter
                + " reclaimBusy=" + (IslandLeaveAssetReclaim.Ensure() is { IsBusy: true } ? 1 : 0)
                + " armed=" + (HostIslandEntryGate.IsArmed ? 1 : 0)
                + " gen=" + HostIslandEntryGate.Generation);
            HookSceneEvents();
            if (GameEntry.Scene == null)
            {
                Log.Error("[ProcedureIsland] Scene component missing — stay on loading cover");
                IslandEntryLog.Fail("island", "noSceneComponent");
                return;
            }

            if (GameEntry.Scene.SceneIsLoaded(SceneAssets.Island))
            {
                if (UnityIslandLoaded())
                {
                    IslandEntryLog.Ok("island", "sceneCached");
                    ActivateIslandScene();
                    return;
                }

                Log.Warning("[ProcedureIsland] framework still lists Island but the scene is gone — load again");
                IslandEntryLog.Wait("island", "sceneReloading");
                ForgetFrameworkIsland();
            }

            Log.Info("[ProcedureIsland] LoadScene " + SceneAssets.Island);
            IslandEntryLog.Ok("island", "loadScene");
            GameEntry.Scene.LoadScene(SceneAssets.Island);
        }

        static IslandJoystickCamera EnsureJoystickCamera()
        {
            Camera main = Camera.main;
            if (main == null)
            {
                Log.Error("[ProcedureIsland] Launch main camera is missing");
                return null;
            }

            IslandCameraSrpSetup.ApplyForIsland(main);

            IslandJoystickCamera cam = main.GetComponent<IslandJoystickCamera>();
            if (cam == null)
                cam = main.gameObject.AddComponent<IslandJoystickCamera>();
            else if (!cam.enabled)
                cam.enabled = true;
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
            IslandEntryLog.Fail("island", "world", "why=" + Compact(reason));
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
            {
                IslandEntryLog.Skip("island", "forceUncover", "why=" + IslandReadyBlockReason);
                return;
            }
            int gen = System.Threading.Interlocked.CompareExchange(ref _uncoverGeneration, 0, 0);
            EnvItemStreamerLite.ReleasePrefillHold();
            if (gen != _uncoverGeneration)
                return;
            BootLoadingService.Hide();
            IslandJoystickCamera.Current?.ReleaseSpawnHold();
            if (gen != _uncoverGeneration)
            {
                IslandEntryLog.Skip("island", "forceUncover", "why=stale gen=" + gen);
                return;
            }
            // Force path: wipe leftover Panel/Dialog from a stuck prior session once.
            UnlockLocomotionAfterUncover(closeLoadingCover: false, clearOverlayForms: true);
            IslandEntryLog.Ok("island", "forceReady");
            NativeBridgeFacade.NotifyIslandReady();
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (System.Threading.Interlocked.Exchange(ref _returnToGuestLogin, 0) != 0)
                BeginLeave(procedureOwner);

            if (_leaveRequested)
                return;

            if (!_sceneReady)
            {
                if (!_leaveRequested)
                    NoteSceneWait();
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
                IslandEntryLog.Fail("island", "phaseThrew", "phase=" + _phase + " type=" + e.GetType().Name);
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

            UnhookSceneEvents();

            if (!isShutdown)
            {
                IslandPlayerPosSave.ResetSession();
                IslandAudioSession.Instance?.Shutdown();
                WanosAudio.WanosEngine.Unload("ProcedureIsland.OnLeave");
            }
            else
            {
                if (_leaveUnloadRoutine != null && GameEntry.Base != null)
                    GameEntry.Base.StopCoroutine(_leaveUnloadRoutine);
                _leaveUnloadRoutine = null;
                IslandLeaveAssetReclaim.Ensure()?.Cancel();
                GameEntry.CloseLeaveHandles();
            }

            base.OnLeave(procedureOwner, isShutdown);
        }

        void HookSceneEvents()
        {
            if (_sceneEventsHooked || GameEntry.Event == null)
                return;
            GameEntry.Event.Subscribe(LoadSceneSuccessEventArgs.EventId, OnLoadSceneSuccess);
            GameEntry.Event.Subscribe(LoadSceneFailureEventArgs.EventId, OnLoadSceneFailure);
            GameEntry.Event.Subscribe(UnloadSceneSuccessEventArgs.EventId, OnUnloadSceneSuccess);
            GameEntry.Event.Subscribe(UnloadSceneFailureEventArgs.EventId, OnUnloadSceneFailure);
            _sceneEventsHooked = true;
        }

        void UnhookSceneEvents()
        {
            if (!_sceneEventsHooked || GameEntry.Event == null)
            {
                _sceneEventsHooked = false;
                return;
            }

            GameEntry.Event.Unsubscribe(LoadSceneSuccessEventArgs.EventId, OnLoadSceneSuccess);
            GameEntry.Event.Unsubscribe(LoadSceneFailureEventArgs.EventId, OnLoadSceneFailure);
            GameEntry.Event.Unsubscribe(UnloadSceneSuccessEventArgs.EventId, OnUnloadSceneSuccess);
            GameEntry.Event.Unsubscribe(UnloadSceneFailureEventArgs.EventId, OnUnloadSceneFailure);
            _sceneEventsHooked = false;
        }

        void OnLoadSceneSuccess(object sender, GameEventArgs e)
        {
            var args = (LoadSceneSuccessEventArgs)e;
            if (args.SceneAssetName != SceneAssets.Island)
                return;
            if (_leaveRequested)
            {
                Log.Info("[ProcedureIsland] scene loaded after leave — unload without entering");
                IslandEntryLog.Skip("island", "sceneAfterLeave");
                // 对象池还没放完时，等 ReleasePoolsThenUnload 看到场景已加载再卸。
                if (_leavePoolsReleased)
                    StartUnload();
                return;
            }

            ActivateIslandScene();
        }

        void OnLoadSceneFailure(object sender, GameEventArgs e)
        {
            var args = (LoadSceneFailureEventArgs)e;
            if (args.SceneAssetName != SceneAssets.Island)
                return;
            Log.Error("[ProcedureIsland] load island scene failed: " + args.ErrorMessage);
            IslandEntryLog.Fail("island", "sceneLoad", "why=" + Compact(args.ErrorMessage));
            if (_leaveRequested)
                FinishAfterSceneUnloaded();
        }

        void OnUnloadSceneSuccess(object sender, GameEventArgs e)
        {
            var args = (UnloadSceneSuccessEventArgs)e;
            if (args.SceneAssetName != SceneAssets.Island)
                return;
            Log.Info("[ProcedureIsland] island scene unload succeeded");
            if (UnityIslandLoaded())
            {
                MonoBehaviour host = GameEntry.Base;
                if (host != null)
                    host.StartCoroutine(FinishWhenIslandGone());
                return;
            }

            FinishAfterSceneUnloaded();
        }

        void OnUnloadSceneFailure(object sender, GameEventArgs e)
        {
            var args = (UnloadSceneFailureEventArgs)e;
            if (args.SceneAssetName != SceneAssets.Island)
                return;
            Log.Error("[ProcedureIsland] unload island scene failed");
            IslandEntryLog.Fail("island", "unload", "attempt=" + _unloadAttempts);
            _unloadStarted = false;
            if (!UnityIslandLoaded())
            {
                ForgetFrameworkIsland();
                FinishAfterSceneUnloaded();
                return;
            }

            if (_unloadAttempts < 2)
            {
                StartUnload();
                return;
            }

            StartFallbackUnload();
        }

        IEnumerator FinishWhenIslandGone()
        {
            yield return null;
            FinishAfterSceneUnloaded();
        }

        void FinishAfterSceneUnloaded()
        {
            if (_leaveFinished)
                return;
            if (UnityIslandLoaded())
            {
                Log.Error("[ProcedureIsland] Island still loaded — keep asset handles");
                IslandEntryLog.Fail("island", "sceneStuck");
                return;
            }

            _leaveFinished = true;
            ForgetFrameworkIsland();
            GameEntry.DetachIslandServices();
            GameEntry.CloseLeaveHandles();
            // 场景包的引用计数不归零，后面的 UnloadUnused 拆不掉它的依赖链。
            YooAssetResourceManager.Instance?.ReleaseScene(SceneAssets.Island);
            IslandGpuReclaim.NotifySceneUnloaded();
            BootLoadingService.ReleaseUnusedUiPool();
            if (IslandLeaveAssetReclaim.Ensure() is { } reclaim)
                reclaim.ScheduleAfterWorldRelease();
            else if (YooResourceModule.IsInitialized)
                YooResourceModule.UnloadUnusedAssets();
            if (_owner != null)
            {
                IslandEntryLog.Ok("island", "backToGuest", "attempts=" + _unloadAttempts);
                ChangeState<ProcedureGuestLogin>(_owner);
            }
        }

        static bool UnityIslandLoaded()
        {
            Scene byPath = SceneManager.GetSceneByPath(SceneAssets.Island);
            if (byPath.IsValid() && byPath.isLoaded)
                return true;
            Scene byName = SceneManager.GetSceneByName("Island");
            return byName.IsValid() && byName.isLoaded;
        }

        static Scene UnityIslandScene()
        {
            Scene byPath = SceneManager.GetSceneByPath(SceneAssets.Island);
            if (byPath.IsValid() && byPath.isLoaded)
                return byPath;
            return SceneManager.GetSceneByName("Island");
        }

        static void ForgetFrameworkIsland()
        {
            var scene = GameEntry.Scene;
            if (scene == null)
                return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object manager = scene.GetType().GetField("m_SceneManager", flags)?.GetValue(scene);
            for (Type type = manager?.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo[] fields = type.GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].GetValue(manager) is ICollection<string> names
                        && names.Contains(SceneAssets.Island))
                        names.Remove(SceneAssets.Island);
                }
            }

            FieldInfo orderField = scene.GetType().GetField("m_SceneOrder", flags);
            if (orderField?.GetValue(scene) is IDictionary<string, int> order)
                order.Remove(SceneAssets.Island);
        }

        void StartFallbackUnload()
        {
            if (_fallbackStarted || _leaveFinished || _owner == null)
                return;
            _fallbackStarted = true;
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
            {
                Log.Error("[ProcedureIsland] no host for fallback unload — handles kept");
                return;
            }

            Log.Warning("[ProcedureIsland] SceneManager fallback unload Island");
            IslandEntryLog.Wait("island", "fallbackUnload", "attempt=" + _unloadAttempts);
            host.StartCoroutine(FallbackUnloadIsland());
        }

        IEnumerator FallbackUnloadIsland()
        {
            Scene scene = UnityIslandScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                ForgetFrameworkIsland();
                FinishAfterSceneUnloaded();
                yield break;
            }

            AsyncOperation op = SceneManager.UnloadSceneAsync(scene);
            if (op == null)
            {
                Log.Error("[ProcedureIsland] SceneManager.UnloadSceneAsync returned null — handles kept");
                yield break;
            }

            while (!op.isDone)
                yield return null;

            if (UnityIslandLoaded())
            {
                Log.Error("[ProcedureIsland] fallback unload finished but Island is still loaded — handles kept");
                yield break;
            }

            ForgetFrameworkIsland();
            FinishAfterSceneUnloaded();
        }

        void ActivateIslandScene()
        {
            GameEntry.Scene.SetSceneOrder(SceneAssets.Island, 0);
            // 第一次进岛根本来就是激活的，只补打开场景里默认关掉的根。
            // 二次进岛禁止 SetActive(true)，也禁止 Relink 的 SetParent：
            // 实测主线程死在 FindActiveTransformWithPath，链表在打开根之后成环，
            // 重挂修的是 Transform 父子，不是这条活动根链表。
            if (EnterCount <= 1)
                ActivateIslandSceneRoots();
            else
                Log.Info("[ProcedureIsland] reentry keep roots active");
            GameEntry.Scene.RefreshMainCamera();
            if (IslandJoystickCamera.Current != null)
                IslandJoystickCamera.Current.enabled = true;
            GameEntry.BindIslandServices();
            _sceneReady = true;
            _phase = Phase.Services;
            Log.Info("[ProcedureIsland] island scene active");
            IslandEntryLog.Ok("island", "sceneActive");
        }

        void BeginLeave(ProcedureOwner procedureOwner)
        {
            _owner = procedureOwner;
            if (_leaveRequested)
                return;
            _leaveRequested = true;
            Log.Info("[ProcedureIsland] HostLeave → unload island scene");
            IslandEntryLog.Ok(
                "island",
                "leave",
                "sceneReady=" + (_sceneReady ? 1 : 0) + " phase=" + _phase);
            System.Threading.Interlocked.Increment(ref _uncoverGeneration);
            if (_worldLoadRoutine != null && GameEntry.Base != null)
            {
                GameEntry.Base.StopCoroutine(_worldLoadRoutine);
                _worldLoadRoutine = null;
                EnvItemStreamerLite.ReleasePrefillHold();
            }

            PendingDownloadIntent.Clear();
            (PresetService.Instance ?? GameEntry.Presets)?.ClearPlaybackForAccountSwitch();
            if (IslandJoystickCamera.Current != null)
            {
                IslandJoystickCamera.Current.ForceUnlockLocomotion();
                IslandJoystickCamera.Current.enabled = false;
            }
            if (GameEntry.UI != null)
            {
                UiAssets.CloseGroup("Main");
                UiAssets.CloseGroup("Panel");
                UiAssets.CloseGroup("Dialog");
                UiAssets.CloseGroup(UiAssets.TipGroup);
            }

            // 卸岛空窗仍有罩，但档位是下一次登录，不是岛内 0.70。
            BootLoadingService.Show("登录中", BootLoadingService.LoginStart);
            // 不拆世界、不卸场景。再 Instantiate 会把活动链表走成环，二次进岛主线程不再回来。
            // 实例保持第一次进岛时的激活状态。流送先闩住，再进岛按新出生点恢复距离检查，不整表 Relink。

            IslandPlayerPosSave.ResetSession();
            IslandAudioSession.Instance?.Shutdown();
            // StopEngine 只停 DSP，bank 的原生缓冲还占着 scudo 的 64KB 档。
            // 再进岛铺草时这档分配失败，进程被杀掉。Unload 拆掉引擎，下一趟 GuestLogin 会重新 Load。
            WanosAudio.WanosEngine.Unload("ProcedureIsland.Leave");
            // 不再 SetActive(false)。关掉再打开会把 Instantiate 留下的 next
            // 重新插进 Unity 的活动根链表。FindActiveTransformWithPath 顺着
            // node->next 走到哨兵，成环后 PlayerLoop 里的 UnitySendMessage 不再返回。
            // 场景和根都保持第一次进岛时的激活状态，登录页由宿主把 Unity 视图 GONE。
            EnvItemStreamerLite.HoldStream();
            Log.Info("[ProcedureIsland] leave keep scene roots active");
            if (_owner != null)
            {
                IslandEntryLog.Ok("island", "backToGuest", "kept=1");
                ChangeState<ProcedureGuestLogin>(_owner);
            }
        }

        static void DeactivateIslandSceneRoots()
        {
            Scene scene = UnityIslandScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Log.Info("[ProcedureIsland] island roots inactive skipped (scene not loaded)");
                return;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            int changed = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null || !root.activeSelf)
                    continue;
                root.SetActive(false);
                changed++;
            }

            Log.Info(
                "[ProcedureIsland] island roots inactive n=" + roots.Length + " off=" + changed);
        }

        static void ActivateIslandSceneRoots()
        {
            Scene scene = UnityIslandScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Log.Info("[ProcedureIsland] island roots active skipped (scene not loaded)");
                return;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            Log.Info("[ProcedureIsland] island roots reactivate begin n=" + roots.Length);
            int changed = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null || root.activeSelf)
                    continue;
                root.SetActive(true);
                changed++;
            }

            Log.Info(
                "[ProcedureIsland] island roots active n=" + roots.Length + " on=" + changed);
        }

        void ReleaseIslandObjectPools()
        {
            if (_leavePoolsReleased)
                return;
            _leavePoolsReleased = true;
            ObjectPoolComponent pools =
                UnityGameFramework.Runtime.GameEntry.GetComponent<ObjectPoolComponent>();
            pools?.ReleaseAllUnused();
            Log.Info("[ProcedureIsland] object pools released before unload");
        }

        void StartUnload()
        {
            if (_unloadStarted || _leaveFinished || _owner == null)
                return;
            if (!UnityIslandLoaded())
            {
                ForgetFrameworkIsland();
                FinishAfterSceneUnloaded();
                return;
            }

            if (GameEntry.Scene == null || !GameEntry.Scene.SceneIsLoaded(SceneAssets.Island))
            {
                StartFallbackUnload();
                return;
            }

            _unloadStarted = true;
            _unloadAttempts++;
            Log.Info("[ProcedureIsland] UnloadScene " + SceneAssets.Island);
            IslandEntryLog.Ok("island", "unload", "attempt=" + _unloadAttempts);
            GameEntry.Scene.UnloadScene(SceneAssets.Island);
        }

        void NoteSceneWait()
        {
            if (_sceneWaitSince <= 0f)
                _sceneWaitSince = Time.realtimeSinceStartup;
            float elapsed = Time.realtimeSinceStartup - _sceneWaitSince;
            if (elapsed < 2f)
                return;
            if (Time.realtimeSinceStartup - _sceneWaitLogAt < 5f)
                return;
            _sceneWaitLogAt = Time.realtimeSinceStartup;
            bool loading = GameEntry.Scene != null && GameEntry.Scene.SceneIsLoading(SceneAssets.Island);
            IslandEntryLog.Wait(
                "island",
                "scene",
                "sec=" + elapsed.ToString("0") + " loading=" + (loading ? 1 : 0));
        }

        static string Compact(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "none";
            string one = text.Replace('\n', '_').Replace('\r', '_').Replace(' ', '_');
            return one.Length <= 80 ? one : one.Substring(0, 80);
        }

        void TickPhase()
        {
            switch (_phase)
            {
                case Phase.Services:
                    Log.Info("[ProcedureIsland] phase services");
                    GameEntry.EnsureUiGroups();
                    GameEntry.EnsureEntityGroups();
                    WanosSoundSetup.EnsureSoundGroups();
                    if (GameEntry.World == null)
                        GameEntry.InitWorldService();
                    // 有 userId 就清密码节流（真机身份由原生给）；主存档加载后应用音量
                    if (!string.IsNullOrEmpty(GuestSession.UserId))
                        AccountPasswordLoginThrottle.ClearAll();
                    BoluoGameSave.Load();
                    DownloadedUnplayedManager.EnsureLoaded();
                    // 播放态跟着账号桶，不沿用上一趟留在岛上的 CurrentId。
                    (PresetService.Instance ?? GameEntry.Presets)?.RestorePlaybackFromAccount();
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
                        IslandEntryLog.Ok("island", "world");
                        _worldLoadRoutine = host.StartCoroutine(LoadWorld());
                        if (_worldLoadRoutine == null)
                        {
                            ContinuePastWorld("StartCoroutine(LoadWorld) returned null");
                            break;
                        }

                        if (_worldLoadFinished)
                            _worldLoadRoutine = null;
                    }

                    if (!_worldLoadFinished
                        && _worldSince > 0f
                        && Time.realtimeSinceStartup - _worldSince >= 5f
                        && Time.realtimeSinceStartup - _worldRetryLogAt >= 5f)
                    {
                        _worldRetryLogAt = Time.realtimeSinceStartup;
                        IslandEntryLog.Wait(
                            "island",
                            "world",
                            "sec=" + (Time.realtimeSinceStartup - _worldSince).ToString("0"));
                    }

                    // 近场 Prefill 就绪即可进岛；远场 Prefill / 草 / Nav 在 Load 协程里后台继续。
                    if (GameEntry.World != null && GameEntry.World.IsNearReady)
                    {
                        GameEntry.Weather?.PushVisuals();
                        IslandEntryLog.Ok("island", "camera");
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
                        if (_worldRetryLogAt <= 0f
                            || Time.realtimeSinceStartup - _worldRetryLogAt >= 2f)
                        {
                            _worldRetryLogAt = Time.realtimeSinceStartup;
                            IslandEntryLog.Wait(
                                "island",
                                "terrain",
                                "loadFailed=" + (GameEntry.World != null && GameEntry.World.LoadFailed ? 1 : 0)
                                + " yoo=" + Compact(YooResourceModule.LastError));
                        }
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
                    IslandEntryLog.Ok("island", "hud");
                    _phase = Phase.Hud;
                    break;

                case Phase.Hud:
                    if (!IslandLeakProbe.Allow(IslandLeakProbe.Module.Hud))
                    {
                        Log.Info("[LeakProbe] hud OFF");
                        BootLoadingService.ReportProgress(BootLoadingService.ItemsPrepareMax, "即将进入...");
                        _phase = Phase.Uncover;
                        break;
                    }

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
                        IslandEntryLog.Fail("island", "hudTimeout");
                    }

                    BootLoadingService.ReportProgress(BootLoadingService.ItemsPrepareMax, "即将进入...");
                    IslandEntryLog.Ok("island", "uncover");
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
                        IslandEntryLog.Fail(
                            "island",
                            "uncoverTimeout",
                            "sec=" + UncoverCapSeconds.ToString("0"));
                        ForceUncoverNow();
                        _phase = Phase.Done;
                    }

                    break;
            }
        }

        static async UniTaskVoid UncoverAsync()
        {
            int gen = System.Threading.Interlocked.CompareExchange(ref _uncoverGeneration, 0, 0);
            bool Stale(string where)
            {
                if (gen == _uncoverGeneration)
                    return false;
                IslandEntryLog.Skip(
                    "uncover",
                    "stale",
                    "where=" + where + " gen=" + gen + " now=" + UncoverGeneration);
                return true;
            }

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
                    IslandEntryLog.Fail("uncover", "audioTimeout", "sec=20");
                }
            }
            catch (System.Exception e)
            {
                Log.Error("[ProcedureIsland] audio before uncover failed: " + e.Message);
                IslandEntryLog.Fail("uncover", "audio", "type=" + e.GetType().Name);
            }

            if (Stale("afterAudio"))
                return;

            // 罩还是不透明的：用已经加载的地形把眼睛贴到落点，再开始淡出。
            if (IslandJoystickCamera.Current != null)
                IslandPlayerPosSave.ReplantAfterTerrainReady(IslandJoystickCamera.Current);

            // 先放行触摸，再淡出罩。二次进岛 Prefill 抢主线程时，揭罩前整包 PSO 会把「即将进入」拖成几十秒。
            // 这里只在罩下扫出生朝向左右几帧（墙钟 1.5s 封顶），把侧面变体和走路档 RT 提前画掉。
            // 淡出前不要关 Loading。Close 会拆掉罩，接着再打开就会整页闪一次。
            // 只放渲染。流送留到草地和 extras 结束，避免和淡出抢同一帧。
            EnvItemStreamerLite.ReleaseRenderHold();
            if (IslandJoystickCamera.Current != null)
            {
                await IslandJoystickCamera.Current.WarmupEntryLookAsync(
                    () => gen != _uncoverGeneration);
            }

            if (Stale("afterLookWarmup"))
                return;

            // 淡出前清一次残留 Panel/Dialog。此后用户已可点 HUD，再关会把刚打开的预设弹走。
            UnlockLocomotionAfterUncover(closeLoadingCover: false, clearOverlayForms: true);
            const float fadeSec = 0.35f;
            BootLoadingService.FadeHide(fadeSec);
            // 只等淡出协程。HasForm 在 Hide 之后仍为 true，界面要等静帧画完再关。
            // 墙钟超时：帧率被 Prefill 打穿时，协程要等下一帧才会自己收掉。
            float wallStart = Time.realtimeSinceStartup;
            const float wallCap = 1.5f;
            while (BootLoadingService.IsHiding)
            {
                if (Stale("fade"))
                    return;
                if (Time.realtimeSinceStartup - wallStart >= wallCap)
                {
                    Log.Error(
                        "[ProcedureIsland] FadeHide wall-clock cap — force Hide"
                        + " hiding=1 hasForm=" + (BootLoadingService.HasForm ? 1 : 0)
                        + " visible=" + (BootLoadingService.IsVisible ? 1 : 0));
                    IslandEntryLog.Fail("uncover", "fadeCap", "sec=1.5");
                    break;
                }

                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            if (Stale("afterFade"))
                return;

            BootLoadingService.Hide();
            IslandJoystickCamera.Current?.ReleaseSpawnHold();
            if (Stale("beforeNotify"))
                return;
            // 加载罩已撤 → 立刻 Notify(2)，宿主抬 islandHudReady 放行触摸；
            // 勿等 Prefill/Nav/Grass 的 IslandWorldReady（否则揭罩后长时间点不动）。
            // 不要在这一帧 Close Loading：Document 被关掉后，篝火静帧会冻在屏幕上。
            UnlockLocomotionAfterUncover(closeLoadingCover: false);
            NativeBridgeFacade.NotifyIslandReady();
            Log.Info(
                "[ProcedureIsland] uncover settle HasForm="
                + BootLoadingService.HasForm
                + " IsVisible="
                + BootLoadingService.IsVisible
                + " IslandReady notified");
            IslandEntryLog.Ok(
                "island",
                "ready",
                "hasForm=" + (BootLoadingService.HasForm ? 1 : 0)
                + " visible=" + (BootLoadingService.IsVisible ? 1 : 0));

            // 朝向预热在淡出前的罩下做完。这里只延迟松闩，防宿主晚推 overlay 钉死行走。
            PostUncoverWarmupAndRelockAsync(gen).Forget();
        }

        static async UniTaskVoid PostUncoverWarmupAndRelockAsync(int gen)
        {
            float wallStart = Time.realtimeSinceStartup;
            Log.Info("[Hitch] PostUncoverRelock begin");
            await UniTask.Delay(200, DelayType.UnscaledDeltaTime);
            if (gen != _uncoverGeneration)
                return;
            // 只松行走闩 / 关 Loading 残留；勿 CloseGroup Panel——揭罩后用户常已点开预设。
            UnlockLocomotionAfterUncover();
            await UniTask.Delay(600, DelayType.UnscaledDeltaTime);
            if (gen != _uncoverGeneration)
                return;
            UnlockLocomotionAfterUncover();
            Log.Info(
                "[Hitch] PostUncoverRelock end wallMs="
                + ((Time.realtimeSinceStartup - wallStart) * 1000f).ToString("F0"));
        }

        static async UniTask StartAudioBeforeUncover(int gen)
        {
            if (!IslandLeakProbe.Allow(IslandLeakProbe.Module.Audio))
            {
                Log.Info("[LeakProbe] audio OFF");
                return;
            }

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

            // 先 Arm 再出声：iOS 会话切到 Playback、Android 开闸，丢焦监听在 unmute 之前就位。
            // 否则启动期切到抖音 / QQ 音乐时引擎已经在响，但还没有焦点监听，声音不会停。
            sw.Restart();
            NowPlayingService.ArmSession();
            Log.Info("[Hitch] audio ArmSession ms=" + sw.ElapsedMilliseconds);
            if (gen != _uncoverGeneration)
                return;

            sw.Restart();
            // 加载期间人已经切走，或对方播放器在前台：不要 unmute。
            // 回前台由 AudioAppLifecycle 再拉起来。谁在前台谁播。
            bool allowAudible = !AudioAppLifecycle.IsInBackground
                && WanosAudio.BoluoAudioSessionNative.AllowUnmuteNow()
                && WanosAudio.BoluoAndroidEngineNative.TryArmFocusBeforeAudible();
            if (allowAudible)
                WanosAudio.WanosEngine.Unmute("ProcedureIsland.Uncover");
            else
            {
                // 记成打断：回波萝走完整恢复，解 mute 并补负一屏。
                // 不记的话回前台会当成「后台一直在播」直接跳过。
                WanosAudio.WanosEngine.KeepSilent("startup-no-audio-focus");
                WanosAudio.WanosEngine.MarkInterrupted("startup-no-audio-focus");
            }
            GameEntry.Music?.ApplySavedVolumes();
            Log.Info("[Hitch] audio Unmute+volumes ms=" + sw.ElapsedMilliseconds
                + " allowAudible=" + allowAudible);
            if (gen != _uncoverGeneration)
                return;

            sw.Restart();
            IslandAudioSession.Instance?.StartAfterUncover();
            Log.Info("[Hitch] audio interaction ms=" + sw.ElapsedMilliseconds);
            if (gen != _uncoverGeneration)
                return;
            sw.Restart();
            if (allowAudible)
                NowPlayingService.PublishPlayingForced();
            Log.Info("[Hitch] audio PublishPlayingForced ms=" + sw.ElapsedMilliseconds
                + " allowAudible=" + allowAudible);
            BankUpgradeBootstrap.WaitCriticalThenCueAsync(45f).Forget();
        }

        /// <param name="closeLoadingCover">关 Loading 组（淡出中勿传 true）。</param>
        /// <param name="clearOverlayForms">
        /// 清 Panel/Dialog 与 pending 意图。只在揭罩交互窗口打开前传一次；
        /// PostUncoverRelock 再清会把冷启动时用户刚点开的预设弹层关掉。
        /// </param>
        static void UnlockLocomotionAfterUncover(
            bool closeLoadingCover = true,
            bool clearOverlayForms = false)
        {
            try
            {
                if (clearOverlayForms)
                {
                    PendingDownloadIntent.Clear();
                    GameEntry.Presets?.AbortTransition();
                    PresetService.Instance?.AbortWeatherHold();
                }

                IslandJoystickCamera.Current?.ForceUnlockLocomotion();
                if (GameEntry.UI != null)
                {
                    if (clearOverlayForms)
                    {
                        UiAssets.CloseGroup("Panel");
                        UiAssets.CloseGroup("Dialog");
                    }

                    // Loading 残留会挡 UITK 命中 → 摇杆收不到指针。淡出进行中不能关，否则罩会被拆掉再打开。
                    if (closeLoadingCover && GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
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
                $"clearOverlay={clearOverlayForms} " +
                $"presetBusy={GameEntry.Presets?.IsTransitioning == true} " +
                $"overlayBlock={Native.NativeOverlayGate.BlocksLocomotion} " +
                $"popup={HudOverlayGate.HasPopup()}");
        }
    }
}
