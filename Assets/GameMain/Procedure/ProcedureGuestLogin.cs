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
    /// Editor：复用已落盘正式/游客会话，否则等登录窗。
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

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
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
            BootLoadingService.ReportProgress(0.28f, "登录中");
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);

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
                    UiDialogService.ShowNoNetwork(() =>
                        UnityGameFramework.Runtime.GameEntry.Shutdown(ShutdownType.Quit));
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
                    NoteHostArmWait("before login");
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
                NoteHostArmWait("after login");
                AbandonLogin("arm dropped before island");
                return;
            }

            ClearHostArmWait();

            if (!_postLoginDone)
            {
#if !UNITY_EDITOR
                // 空票不能锁成编辑器游客，否则下一次 106 不再向宿主要票。
                if (!_success || !GuestSession.IsLoggedIn)
                {
                    Log.Warning("[GuestLogin] ticket not ready — stay on cover and retry");
                    _started = false;
                    _finished = false;
                    _success = false;
                    return;
                }
#endif
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
            if (IslandLeaveAssetReclaim.IsBusy)
            {
                EnsureLoadingCoverWhileWaitingHost();
                if (_routine == null && GameEntry.Base != null)
                {
                    _islandHandoffStarted = true;
                    _routine = GameEntry.Base.StartCoroutine(WaitReclaimThenIsland(procedureOwner));
                }
                else if (GameEntry.Base == null)
                {
                    Log.Error(
                        "[GuestLogin] GameEntry.Base null while reclaim is busy — enter island anyway");
                    _islandHandoffStarted = true;
                    ChangeState<ProcedureIsland>(procedureOwner);
                }

                return;
            }

            _islandHandoffStarted = true;
            ChangeState<ProcedureIsland>(procedureOwner);
        }

        IEnumerator WaitReclaimThenIsland(ProcedureOwner procedureOwner)
        {
            BootLoadingService.ReportProgress(
                BootLoadingService.VisualPrepareMin, "正在整理资源...");
            yield return IslandLeaveAssetReclaim.WaitIfBusy();
            _routine = null;
            if (procedureOwner == null)
            {
                Log.Error(
                    "[GuestLogin] reclaim finished but procedure owner is null — island handoff aborted");
                _islandHandoffStarted = false;
                yield break;
            }
            // Leave 可能在等待期间又拆掉 Arm。
            if (HostIslandEntryGate.ShouldWaitForHost() && !HostIslandEntryGate.IsArmed)
            {
                AbandonLogin("arm dropped while reclaiming");
                yield break;
            }

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
            EnsureLoadingCoverWhileWaitingHost();
            Log.Warning($"[GuestLogin] abandon login — {reason}");
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
            if (!BootLoadingService.IsVisible)
                BootLoadingService.Show("正在进入世界...", BootLoadingService.VisualPrepareMin);
            else
                BootLoadingService.ReportProgress(
                    BootLoadingService.VisualPrepareMin, "正在进入世界...");
        }

        protected override void OnLeave(ProcedureOwner procedureOwner, bool isShutdown)
        {
            if (_routine != null && GameEntry.Base != null)
            {
                GameEntry.Base.StopCoroutine(_routine);
                _routine = null;
            }

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
                _hostWaitSince = Time.realtimeSinceStartup;
            if (_hostWaitLogged || Time.realtimeSinceStartup - _hostWaitSince < 20f)
                return;

            _hostWaitLogged = true;
            Log.Error(
                "[GuestLogin] HostArmIslandEntry still missing after 20s (" + where
                + "). Island entry stays on the loading cover until the host arms.");
        }

        void ClearHostArmWait()
        {
            _hostWaitSince = 0f;
            _hostWaitLogged = false;
        }

        IEnumerator RunLogin()
        {
            int generation = HostIslandEntryGate.Generation;
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

#if UNITY_EDITOR
            yield return RunEditorLogin(deviceOkResult => deviceOk = deviceOkResult);
            if (!LoginStillCurrent(generation))
            {
                AbandonLogin("editor login generation changed");
                yield break;
            }

            // 正式复用 / 游客复用 / 本轮游客或手机登录成功后均应有 userToken。
            _success = GuestSession.IsLoggedIn;
            BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
            _finished = true;
#else
            bool userOk = false;
            yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                .ToCoroutine(r => deviceOk = r);
            if (!LoginStillCurrent(generation))
            {
                AbandonLogin("device token generation changed");
                yield break;
            }

            if (deviceOk)
            {
                BootLoadingService.ReportProgress(0.48f, "换取 userToken");
                yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                    .ToCoroutine(r => userOk = r);
                if (!LoginStillCurrent(generation))
                {
                    AbandonLogin("user token generation changed");
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
                            AbandonLogin("user token retry generation changed");
                            yield break;
                        }

                        BootLoadingService.ReportProgress(0.48f, "换取正式 userToken…");
                        yield return new WaitForSecondsRealtime(0.2f);
                        if (!LoginStillCurrent(generation))
                        {
                            AbandonLogin("user token retry generation changed");
                            yield break;
                        }

                        yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                            .ToCoroutine(r => userOk = r);
                    }

                    if (!LoginStillCurrent(generation))
                    {
                        AbandonLogin("user token retry generation changed");
                        yield break;
                    }
                }
            }

            if (!LoginStillCurrent(generation))
            {
                AbandonLogin("login generation changed");
                yield break;
            }

            if (userOk)
            {
                GuestSession.SyncFromBoluo();
                _success = GuestSession.IsLoggedIn;
                Log.Info(
                    $"[GuestLogin] tokens deviceOk={deviceOk} userOk={userOk} " +
                    $"guest={GuestSession.IsGuest} userId={GuestSession.UserId}");
            }
            else if (NativeSession.IsHybridAvailable())
            {
                Log.Error(
                    "[GuestLogin] hybrid userToken still missing after retry — stay on loading cover");
                _success = false;
                _started = false;
                _finished = false;
                _postLoginDone = false;
                _routine = null;
                // 保持 Arm；下一帧 OnUpdate 因 !_started && IsArmed 会再跑 RunLogin。
                EnsureLoadingCoverWhileWaitingHost();
                yield break;
            }
            else
            {
                Log.Warning("[GuestLogin] remote login failed — local guest fallback");
                GuestSession.ApplyLocalEditorGuest();
                _success = true;
            }

            BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
            _finished = true;
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor：复用已落盘正式/游客会话；否则等「编辑器登录」窗口放行后再换票。
        /// 本地游客不挡在 boluoRsa/deviceLogin 上（否则点按钮后仍要干等网络超时）。
        /// </summary>
        IEnumerator RunEditorLogin(System.Action<bool> setDeviceOk)
        {
            // 已有正式会话：补 deviceToken（bank）后进岛。
            if (EditorLoginGate.HasFormalSession)
            {
                GuestSession.SyncFromBoluo();
                bool deviceOkReuse = false;
                BootLoadingService.ReportProgress(0.40f, "换取 deviceToken");
                yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                    .ToCoroutine(r => deviceOkReuse = r);
                setDeviceOk?.Invoke(deviceOkReuse);
                Log.Info(
                    $"[GuestLogin] editor reuse formal userId={GuestSession.UserId} " +
                    $"deviceOk={deviceOkReuse}");
                yield break;
            }

            // 已有游客会话：复用 token，除非主动「清空用户会话」/退登。
            if (EditorLoginGate.HasGuestSession)
            {
                yield return FinishEditorGuestReuse(setDeviceOk);
                yield break;
            }

            BootLoadingService.ReportProgress(
                0.45f, "请在「编辑器登录」窗口选择登录方式…");
            Log.Info(
                "[GuestLogin] editor waiting login — menu GameMain/Boluo/编辑器登录");

            // 正式会话已落盘但仍可能处于注销确认期（CancelPendingLogin）：等弹窗 resolve。
            while (!EditorLoginGate.HasFormalSession || EditorLoginGate.CancelPendingLogin)
            {
                if (EditorLoginGate.GuestProceedRequested
                    && !EditorLoginGate.CancelPendingLogin)
                {
                    yield return FinishEditorGuestProceed(setDeviceOk);
                    yield break;
                }

                if (EditorLoginGate.CancelPendingLogin)
                    BootLoadingService.ReportProgress(
                        0.45f, "注销确认期 — 请在游戏内弹窗选择…");

                yield return null;
            }

            // 手机号登录已写入正式会话；deviceToken 多半已在 PhoneLoginClient 里换过。
            GuestSession.SyncFromBoluo();
            bool deviceOkPhone = BoluoSession.IsDeviceTokenValid();
            if (!deviceOkPhone)
            {
                BootLoadingService.ReportProgress(0.48f, "换取 deviceToken");
                yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                    .ToCoroutine(r => deviceOkPhone = r);
            }

            setDeviceOk?.Invoke(deviceOkPhone);
            Log.Info(
                $"[GuestLogin] editor phone login ok userId={GuestSession.UserId} " +
                $"deviceOk={deviceOkPhone}");
        }

        /// <summary>复用 Prefs 游客票进岛（与正式账号 reuse 对称）。</summary>
        static IEnumerator FinishEditorGuestReuse(System.Action<bool> setDeviceOk)
        {
            GuestSession.SyncFromBoluo();
            if (!GuestLoginClient.ShouldTryRemoteInEditor())
            {
                setDeviceOk?.Invoke(BoluoSession.IsDeviceTokenValid());
                KickEditorDeviceTokenInBackground();
                Log.Info(
                    $"[GuestLogin] editor reuse guest userId={GuestSession.UserId}");
                yield break;
            }

            BootLoadingService.ReportProgress(0.46f, "游客远端换票…");
            bool deviceOk = false;
            bool userOk = false;
            yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                .ToCoroutine(r => deviceOk = r);
            setDeviceOk?.Invoke(deviceOk);
            if (deviceOk)
            {
                // Prefs 已有 userToken 时 EnsureUserToken 会 short-circuit；失效才重新 touristLogin。
                yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                    .ToCoroutine(r => userOk = r);
            }

            GuestSession.SyncFromBoluo();
            if (!GuestSession.IsLoggedIn)
                GuestSession.ApplyLocalEditorGuest();
            Log.Info(
                $"[GuestLogin] editor reuse guest remote deviceOk={deviceOk} userOk={userOk} " +
                $"userId={GuestSession.UserId}");
        }

        /// <summary>游客放行：默认本地瞬间进岛；仅 Try Remote 时才等网络换票。</summary>
        static IEnumerator FinishEditorGuestProceed(System.Action<bool> setDeviceOk)
        {
            if (!GuestLoginClient.ShouldTryRemoteInEditor())
            {
                GuestSession.ApplyLocalEditorGuest();
                setDeviceOk?.Invoke(BoluoSession.IsDeviceTokenValid());
                // bank 目录换票放到后台，不挡进岛。
                KickEditorDeviceTokenInBackground();
                Log.Info(
                    $"[GuestLogin] editor guest local fast-path userId={GuestSession.UserId}");
                yield break;
            }

            BootLoadingService.ReportProgress(0.46f, "游客远端换票…");
            bool deviceOk = false;
            bool userOk = false;
            yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                .ToCoroutine(r => deviceOk = r);
            setDeviceOk?.Invoke(deviceOk);
            if (deviceOk)
            {
                yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                    .ToCoroutine(r => userOk = r);
            }

            GuestSession.SyncFromBoluo();
            if (!GuestSession.IsLoggedIn)
                GuestSession.ApplyLocalEditorGuest();
            Log.Info(
                $"[GuestLogin] editor guest remote deviceOk={deviceOk} userOk={userOk} " +
                $"userId={GuestSession.UserId}");
        }

        static void KickEditorDeviceTokenInBackground()
        {
            if (BoluoSession.IsDeviceTokenValid())
                return;
            KickEditorDeviceTokenAsync().Forget();
        }

        static async UniTaskVoid KickEditorDeviceTokenAsync()
        {
            bool ok = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
            if (ok)
                Log.Info("[GuestLogin] editor background deviceToken ok");
            else
                Log.Warning("[GuestLogin] editor background deviceToken failed — bank 可能为空");
        }
#endif
    }
}
