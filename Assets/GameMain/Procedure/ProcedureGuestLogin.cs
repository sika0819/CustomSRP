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
    /// Native 初始化 → device/user 换票 → 后台 bank 下载 → 进岛。
    /// 冷启动与退登后再登录：无网弹窗，确认后退出。
    /// Editor：复用已落盘正式/游客会话，否则等登录窗把票交给宿主；进岛仍等 106 再 Query。
    /// 真机 Hybrid 换票失败停在加载罩，不回落本地游客。
    /// </summary>
    public class ProcedureGuestLogin : ProcedureBase
    {
        bool _started;
        bool _finished;
        bool _success;
        bool _postLoginDone;
        bool _offlineBlocked;
#if !UNITY_EDITOR
        bool _noNetworkShown;
#endif
        bool _islandHandoffStarted;
        bool _hostWaitLogged;
        bool _baseNullLogged;
        float _hostWaitSince;
        Coroutine _routine;
        Coroutine _returnNotify;

        /// <summary>当前流程是 GuestLogin。退登时若已在这里，不必等卸岛再通知宿主 GONE。</summary>
        public static bool IsCurrent { get; private set; }

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            IsCurrent = true;
            base.OnEnter(procedureOwner);
            _started = false;
            _finished = false;
            _success = false;
            _postLoginDone = false;
            _offlineBlocked = false;
#if !UNITY_EDITOR
            _noNetworkShown = false;
#endif
            _islandHandoffStarted = false;
            _hostWaitLogged = false;
            _baseNullLogged = false;
            _hostWaitSince = 0f;
#if UNITY_EDITOR
            EditorLoginGate.ResetSessionFlags();
#endif
            // 冷启动从 Yoo 的进度收到登录档；退登重进从岛内 0.70/1 回到登录档。
            BootLoadingService.Show("登录中", BootLoadingService.LoginStart);
            IslandEntryLog.Ok(
                "guestLogin",
                "enter",
                "armed=" + (HostIslandEntryGate.IsArmed ? 1 : 0)
                + " gen=" + HostIslandEntryGate.Generation
                + " islandEnter=" + ProcedureIsland.EnterCount);
            // 冷启动没有上一轮岛的 GPU 资源，立刻通知宿主即可。
            // 退登重进：宿主收到这条会把 Surface GONE。若赶在 FlushGpuPool 提交之前，
            // Adreno 会丢掉还没执行的 vkDestroy，下一趟进岛再分配整屏目标就 OOM。
            if (ProcedureIsland.EnterCount > 0)
                ScheduleReturnNotifyAfterGpuFlush();
            else
                NotifyReturnedToGuestLogin();
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            // 单场景重载会拆掉 BeforeSceneLoad 建的桥。没人 Tick 就不会有 106。
            BoluoNativeBridge.EnsureExists();

            if (_offlineBlocked)
                return;

            // 冷启动、退登后再登录：无网立刻收起 Loading（depth 高于 Dialog）→ 弹无网 → 确定退出。
            // 进岛之后的局内断网不走这里。
            // Editor 不拦：本地游客 / 登录窗不依赖 reachability；Unity 在 Editor 也常误报 NotReachable。
#if !UNITY_EDITOR
            if (!_started
                && Application.internetReachability == NetworkReachability.NotReachable)
            {
                _offlineBlocked = true;
                BootLoadingService.Hide();
                if (!_noNetworkShown)
                {
                    _noNetworkShown = true;
                    Log.Warning(
                        "[GuestLogin] NotReachable — show NoNetwork and quit on confirm " +
                        $"(islandEnter={ProcedureIsland.EnterCount})");
                    IslandEntryLog.Block(
                        "guestLogin",
                        "offline",
                        "islandEnter=" + ProcedureIsland.EnterCount);
                    UiDialogService.ShowNoNetwork(
                        () => UnityGameFramework.Runtime.GameEntry.Shutdown(ShutdownType.Quit),
                        title: "无网络连接",
                        body: "请检查网络设置，确保设备已连接到互联网。",
                        primaryLabel: "确定");
                }

                return;
            }
#endif

            // Hybrid：必须先等宿主 Arm 再换票。否则退登 Leave→GuestLogin 时宿主已无 token，
            // 会锁成 Unity 游客票；再登录只 Arm 进岛，表现为二次登录无法正常进岛/无声。
            if (!_started)
            {
                if (HostIslandEntryGate.ShouldWaitForHost() && !HostIslandEntryGate.IsArmed)
                {
                    // 上一轮 Uncover Hide 可能已 _visible=false；ReportProgress 会空转。
                    NoteHostArmWait("beforeLogin");
                    EnsureLoadingCoverWhileWaitingHost();
                    return;
                }

                ClearHostArmWait();
                MonoBehaviour host = GameEntry.Base;
                if (host == null)
                {
                    if (!_baseNullLogged)
                    {
                        _baseNullLogged = true;
                        Log.Error("[GuestLogin] GameEntry.Base null — cannot start login");
                        IslandEntryLog.Fail("guestLogin", "baseNull", "where=beforeLogin");
                    }

                    return;
                }

                _started = true;
                _routine = host.StartCoroutine(RunLoginGuarded());
            }

            if (!_finished)
                return;

            // 换票后若被 Leave 拆掉 Arm：必须重跑 RunLogin，不能只死等再进岛。
            // 否则 Leave 已清 Unity 票，再 Arm 会带着空/游客缓存进岛，加载罩出不来。
            if (HostIslandEntryGate.ShouldWaitForHost() && !HostIslandEntryGate.IsArmed)
            {
                NoteHostArmWait("afterLogin");
                AbandonLogin("armDropped");
                return;
            }

            ClearHostArmWait();

            if (!_postLoginDone)
            {
                // 空票不能锁成编辑器游客，否则下一次 106 不再向宿主要票。
                if (!_success || !GuestSession.IsLoggedIn)
                {
                    Log.Warning("[GuestLogin] ticket not ready — stay on cover and retry");
                    IslandEntryLog.Wait(
                        "guestLogin",
                        "ticket",
                        "success=" + (_success ? 1 : 0)
                        + " loggedIn=" + (GuestSession.IsLoggedIn ? 1 : 0)
                        + " armed=" + (HostIslandEntryGate.IsArmed ? 1 : 0));
                    _started = false;
                    _finished = false;
                    _success = false;
                    // 不会重进 OnEnter。Show 把进度写回登录档，否则 ReportProgress 被钳在 0.70。
                    BootLoadingService.Show("登录中", BootLoadingService.LoginStart);
                    return;
                }
                _postLoginDone = true;

                WanosAudio.WanosBankStore.SetPaths(WanosBankPaths.Instance);
                bool engineOk = WanosAudio.WanosEngine.Load("ProcedureGuestLogin");
                // Android：StreamingAssets bank 在 jar 内，EnsureLocalBanksReadyAsync 会先解压再灌引擎。
                // Start() 内部也会 await 解压；此处异步启动，不挡进岛流程。
                if (engineOk)
                    BankUpgradeBootstrap.EnsureLocalBanksReadyAsync().Forget();
                Log.Info(
                    $"[ProcedureGuestLogin] Wanos engine load result={engineOk} " +
                    $"isCreated={WanosAudio.WanosNative.Instance.IsCreated} " +
                    $"loadIntoEngine={engineOk} platform={Application.platform}");
                if (GuestSession.IsLoggedIn
                    && (!HostIslandEntryGate.ShouldWaitForHost() || HostIslandEntryGate.IsArmed))
                {
                    BankUpgradeBootstrap.Start(loadIntoEngine: engineOk);
                    BankDownloader.ResumeAfterLeave();
                }
            }

            if (_islandHandoffStarted)
                return;

            // 退登回收未完成时先等空窗，避免进岛 Prefill 与 Yoo Unload 抢主线程。
            IslandLeaveAssetReclaim reclaim = IslandLeaveAssetReclaim.Ensure();
            if (reclaim != null && reclaim.IsBusy)
            {
                EnsureLoadingCoverWhileWaitingHost();
                if (_routine == null && GameEntry.Base != null)
                {
                    IslandEntryLog.Wait(
                        "guestLogin",
                        "reclaim",
                        "gen=" + HostIslandEntryGate.Generation);
                    _islandHandoffStarted = true;
                    _routine = GameEntry.Base.StartCoroutine(WaitReclaimThenIsland(procedureOwner));
                }
                else if (GameEntry.Base == null)
                {
                    Log.Error(
                        "[GuestLogin] GameEntry.Base null while reclaim is busy — enter island anyway");
                    IslandEntryLog.Fail("guestLogin", "baseNull", "where=reclaim");
                    _islandHandoffStarted = true;
                    ChangeState<ProcedureIsland>(procedureOwner);
                }

                return;
            }

            _islandHandoffStarted = true;
            IslandEntryLog.Ok(
                "guestLogin",
                "handoff",
                "via=direct armed=" + (HostIslandEntryGate.IsArmed ? 1 : 0)
                + " gen=" + HostIslandEntryGate.Generation);
            ChangeState<ProcedureIsland>(procedureOwner);
        }

        IEnumerator WaitReclaimThenIsland(ProcedureOwner procedureOwner)
        {
            BootLoadingService.ReportProgress(
                BootLoadingService.VisualPrepareMin, "正在整理资源...");
            IslandLeaveAssetReclaim reclaim = IslandLeaveAssetReclaim.Ensure();
            if (reclaim != null)
                yield return reclaim.WaitIfBusy();
            _routine = null;
            if (procedureOwner == null)
            {
                Log.Error(
                    "[GuestLogin] reclaim finished but procedure owner is null — island handoff aborted");
                IslandEntryLog.Fail("guestLogin", "ownerNull", "where=reclaimDone");
                _islandHandoffStarted = false;
                yield break;
            }
            // Leave 可能在等待期间又拆掉 Arm。
            if (HostIslandEntryGate.ShouldWaitForHost() && !HostIslandEntryGate.IsArmed)
            {
                AbandonLogin("armDroppedReclaim");
                yield break;
            }

            IslandEntryLog.Ok("guestLogin", "handoff", "via=reclaim gen=" + HostIslandEntryGate.Generation);
            ChangeState<ProcedureIsland>(procedureOwner);
        }

        /// <summary>
        /// 这一代换票作废：清掉可能已经写回的用户票，回到等 Arm。
        /// </summary>
        void AbandonLogin(string reason)
        {
            GuestSession.Clear();
            _started = false;
            _finished = false;
            _success = false;
            _postLoginDone = false;
            _islandHandoffStarted = false;
            BankDownloader.PauseForLeave();
            Coroutine routine = _routine;
            _routine = null;
            if (routine != null && GameEntry.Base != null)
                GameEntry.Base.StopCoroutine(routine);
            // 这一代作废，不会重进 OnEnter。进度回到登录档，不要沿用岛内 0.70。
            BootLoadingService.Show("登录中", BootLoadingService.LoginStart);
            Log.Warning($"[GuestLogin] abandon login — {reason}");
            IslandEntryLog.Block(
                "guestLogin",
                "abandon",
                "why=" + reason.Replace(' ', '_')
                + " gen=" + HostIslandEntryGate.Generation
                + " armed=" + (HostIslandEntryGate.IsArmed ? 1 : 0));
        }

        bool LoginStillCurrent(int generation)
        {
            if (generation != HostIslandEntryGate.Generation)
                return false;
            if (HostIslandEntryGate.ShouldWaitForHost() && !HostIslandEntryGate.IsArmed)
                return false;
            return true;
        }

        static void EnsureLoadingCoverWhileWaitingHost()
        {
#if UNITY_EDITOR
            if (EditorLoginGate.CancelPendingLogin)
            {
                ShowHostWait(0.45f, "注销确认期 — 请在游戏内弹窗选择…");
                return;
            }

            if (!EditorLoginGate.HasAnySession && !EditorNativeHost.HasShellUser)
            {
                ShowHostWait(0.45f, "请在「编辑器登录」窗口选择登录方式…");
                return;
            }
#endif
            // 等 Arm / 等回收：罩还在就不要把进度抬到 0.70。丢了才从登录档重新打开。
            if (!BootLoadingService.IsVisible)
                BootLoadingService.Show("登录中", BootLoadingService.LoginStart);
        }

#if UNITY_EDITOR
        static void ShowHostWait(float progress, string tip)
        {
            if (!BootLoadingService.IsVisible)
                BootLoadingService.Show(tip, progress);
            else
                BootLoadingService.ReportProgress(progress, tip);
        }
#endif

        void ScheduleReturnNotifyAfterGpuFlush()
        {
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
            {
                NotifyReturnedToGuestLogin();
                return;
            }

            if (_returnNotify != null)
                host.StopCoroutine(_returnNotify);
            _returnNotify = host.StartCoroutine(ReturnNotifyAfterGpuFlush());
        }

        IEnumerator ReturnNotifyAfterGpuFlush()
        {
            // FlushGpuPool 在回收末尾，排在 Yoo 卸包和 Resources.UnloadUnused 之后。
            // 8 秒到点就发 108 时，销毁命令还没提交，再进岛会在上一轮的 kgsl 上继续堆。
            IslandLeaveAssetReclaim reclaim = IslandLeaveAssetReclaim.Ensure();
            float waited = 0f;
            while (reclaim != null && reclaim.IsBusy && IsCurrent)
            {
                if (waited == 0f || waited >= 15f)
                {
                    Log.Info("[GuestLogin] hold 108 until GPU reclaim finishes waited="
                        + waited.ToString("F0") + "s");
                    waited = 0.01f;
                }

                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!IsCurrent)
                yield break;

            // 回收协程里已经 Flush 并空等过帧。再留几帧给渲染线程提交销毁，然后才让宿主 GONE。
            for (int i = 0; i < 3; i++)
                yield return new WaitForEndOfFrame();

            _returnNotify = null;
            if (!IsCurrent)
                yield break;

            Log.Info("[GuestLogin] notify host returned after GPU flush");
            // 连发两次：主线程 post 偶发丢一条时，第二次仍能把 Surface GONE。
            // 每次按当下门闩填 a，避免第一拍还在等、第二拍已经 Arm 却报成同一个状态。
            NotifyReturnedToGuestLogin();
            yield return null;
            NotifyReturnedToGuestLogin();
        }

        /// <summary>a=1 表示本轮 106 已经在门口，宿主不必再推。</summary>
        void NotifyReturnedToGuestLogin()
        {
            NativeMessages.ReturnedToGuestLogin(HostIslandEntryGate.IsArmed ? 1 : 0);
        }

        protected override void OnLeave(ProcedureOwner procedureOwner, bool isShutdown)
        {
            IsCurrent = false;
            if (GameEntry.Base != null)
            {
                if (_routine != null)
                    GameEntry.Base.StopCoroutine(_routine);
                if (_returnNotify != null)
                    GameEntry.Base.StopCoroutine(_returnNotify);
            }

            _routine = null;
            _returnNotify = null;

            base.OnLeave(procedureOwner, isShutdown);
        }

        IEnumerator RunLoginGuarded()
        {
            IEnumerator inner = RunLogin();
            while (true)
            {
                bool moved;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (System.Exception e)
                {
                    Log.Error("[GuestLogin] login threw — " + e);
                    IslandEntryLog.Fail("guestLogin", "threw", "type=" + e.GetType().Name);
                    _success = false;
                    _finished = true;
                    _routine = null;
                    yield break;
                }

                if (!moved)
                {
                    _routine = null;
                    yield break;
                }

                yield return inner.Current;
            }
        }

        void NoteHostArmWait(string where)
        {
            if (_hostWaitSince <= 0f)
            {
                _hostWaitSince = Time.realtimeSinceStartup;
                IslandEntryLog.Wait(
                    "guestLogin",
                    "hostArm",
                    "where=" + where + " gen=" + HostIslandEntryGate.Generation);
            }
            if (_hostWaitLogged || Time.realtimeSinceStartup - _hostWaitSince < 20f)
                return;

            _hostWaitLogged = true;
            Log.Error(
                "[GuestLogin] HostArmIslandEntry still missing after 20s (" + where
                + "). Island entry stays on the loading cover until the host arms.");
            IslandEntryLog.Fail(
                "guestLogin",
                "hostArm",
                "where=" + where + " sec=20 gen=" + HostIslandEntryGate.Generation);
        }

        void ClearHostArmWait()
        {
            _hostWaitSince = 0f;
            _hostWaitLogged = false;
        }

        IEnumerator RunLogin()
        {
            int generation = HostIslandEntryGate.Generation;
            IslandEntryLog.Ok(
                "guestLogin",
                "login",
                "gen=" + generation + " armed=" + (HostIslandEntryGate.IsArmed ? 1 : 0));
            BootLoadingService.ReportProgress(0.30f, "初始化原生桥");
            NativeBridgeFacade.Init();

            BootLoadingService.ReportProgress(0.32f, "读取 StreamingAssets");
            bool bootOk = false;
            yield return StreamingAssetsUtil.ReadTextCoroutine(
                StreamingAssetsUtil.BootRelativePath,
                (ok, _) => bootOk = ok);
            if (!bootOk)
                Log.Warning("[GuestLogin] StreamingAssets Game/boot.json missing — using defaults");

            BootLoadingService.ReportProgress(0.40f, "换取 deviceToken");
            bool deviceOk = false;
            bool userOk = false;
            yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                .ToCoroutine(r => deviceOk = r);
            if (!LoginStillCurrent(generation))
            {
                AbandonLogin("deviceGen");
                yield break;
            }

            if (deviceOk)
            {
                BootLoadingService.ReportProgress(0.48f, "换取 userToken");
                yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                    .ToCoroutine(r => userOk = r);
                if (!LoginStillCurrent(generation))
                {
                    AbandonLogin("userGen");
                    yield break;
                }

                // 游客→正式：宿主刚落盘正式票时可能晚一拍；清游客缓存后向原生重试，勿回落本地游客。
                if (!userOk && NativeSession.IsHybridAvailable())
                {
                    GuestSession.Clear();
                    for (int i = 0; i < 15 && !userOk; i++)
                    {
                        if (!LoginStillCurrent(generation))
                        {
                            AbandonLogin("userRetryGen");
                            yield break;
                        }

                        BootLoadingService.ReportProgress(0.48f, "换取正式 userToken…");
                        yield return new WaitForSecondsRealtime(0.2f);
                        if (!LoginStillCurrent(generation))
                        {
                            AbandonLogin("userRetryGen");
                            yield break;
                        }

                        yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                            .ToCoroutine(r => userOk = r);
                    }

                    if (!LoginStillCurrent(generation))
                    {
                        AbandonLogin("userRetryGen");
                        yield break;
                    }
                }
            }

            if (!LoginStillCurrent(generation))
            {
                AbandonLogin("loginGen");
                yield break;
            }

            if (userOk)
            {
                GuestSession.SyncFromBoluo();
                _success = GuestSession.IsLoggedIn;
                Log.Info(
                    $"[GuestLogin] tokens deviceOk={deviceOk} userOk={userOk} " +
                    $"guest={GuestSession.IsGuest} userId={GuestSession.UserId}");
                IslandEntryLog.Ok(
                    "guestLogin",
                    "ticket",
                    "deviceOk=" + (deviceOk ? 1 : 0)
                    + " userOk=" + (userOk ? 1 : 0)
                    + " guest=" + (GuestSession.IsGuest ? 1 : 0)
                    + " loggedIn=" + (GuestSession.IsLoggedIn ? 1 : 0)
                    + " userId=" + GuestSession.UserId);
            }
            else if (NativeSession.IsHybridAvailable())
            {
                Log.Error(
                    "[GuestLogin] hybrid userToken still missing after retry — stay on loading cover");
                IslandEntryLog.Fail(
                    "guestLogin",
                    "userToken",
                    "deviceOk=" + (deviceOk ? 1 : 0) + " retries=15");
                _success = false;
                _started = false;
                _finished = false;
                _postLoginDone = false;
                _routine = null;
                // 保持 Arm；下一帧 OnUpdate 因 !_started && IsArmed 会再跑 RunLogin。
                // 这次换票作废，进度回到登录档，避免停在 0.70。
                BootLoadingService.Show("登录中", BootLoadingService.LoginStart);
                yield break;
            }
            else
            {
                Log.Warning("[GuestLogin] remote login failed — local guest fallback");
                IslandEntryLog.Block("guestLogin", "localGuest");
                GuestSession.ApplyLocalEditorGuest();
                _success = true;
            }

            BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
            _finished = true;
        }
    }
}
