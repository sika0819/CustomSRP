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
    /// 换票失败回退本地游客，不挡进岛。
    /// </summary>
    public class ProcedureGuestLogin : ProcedureBase
    {
        bool _started;
        bool _finished;
        bool _success;
        Coroutine _routine;

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            _started = false;
            _finished = false;
            _success = false;
            BootLoadingService.ReportProgress(0.28f, "登录中");
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (!_started)
            {
                _started = true;
                MonoBehaviour host = GameEntry.Base;
                if (host != null)
                    _routine = host.StartCoroutine(RunLogin());
                else
                {
                    GuestSession.ApplyLocalEditorGuest();
                    _success = true;
                    _finished = true;
                }
            }

            if (!_finished)
                return;

            if (!_success)
                GuestSession.ApplyLocalEditorGuest();

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
            BankUpgradeBootstrap.Start(loadIntoEngine: engineOk);
            ChangeState<ProcedureIsland>(procedureOwner);
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

        IEnumerator RunLogin()
        {
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

#if UNITY_EDITOR
            // Local guest session for UI, but still exchange tokens so upgrade/bank is non-empty.
            if (!GuestLoginClient.ShouldTryRemoteInEditor())
            {
                GuestSession.ApplyLocalEditorGuest();
                Log.Info($"[GuestLogin] editor local guest (Try Remote off) userId={GuestSession.UserId}");
                BootLoadingService.ReportProgress(0.40f, "换取 bank 所需 token");
                yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                    .ToCoroutine(r => deviceOk = r);
                if (deviceOk)
                {
                    yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                        .ToCoroutine(r => userOk = r);
                }

                Log.Info(
                    $"[GuestLogin] editor bank tokens deviceOk={deviceOk} userOk={userOk}");
                _success = true;
                BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
                _finished = true;
                yield break;
            }
#endif

            yield return BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true)
                .ToCoroutine(r => deviceOk = r);
            if (deviceOk)
            {
                BootLoadingService.ReportProgress(0.48f, "换取 userToken");
                yield return BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true)
                    .ToCoroutine(r => userOk = r);
            }

            if (userOk || deviceOk)
            {
                GuestSession.SyncFromBoluo();
                if (!GuestSession.IsLoggedIn)
                    GuestSession.ApplyLocalEditorGuest();
                _success = true;
                Log.Info(
                    $"[GuestLogin] tokens deviceOk={deviceOk} userOk={userOk} " +
                    $"guest={GuestSession.IsGuest} userId={GuestSession.UserId}");
            }
            else
            {
                Log.Warning("[GuestLogin] remote login failed — local guest fallback");
                GuestSession.ApplyLocalEditorGuest();
                _success = true;
            }

            BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
            _finished = true;
        }
    }
}
