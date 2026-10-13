using System.Collections;
using GameFramework.Procedure;
using UnityEngine;
using UnityGameFramework.Runtime;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    /// <summary>
    /// 编辑器游客登录 → 进岛。对齐 BoluoUnity：Play 后资源/会话就绪再进世界。
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
            BootLoadingService.ReportProgress(0.28f, "游客登录中");
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

            // 仍进岛，避免编辑器卡死；失败则本地游客兜底。
            if (!_success)
                GuestSession.ApplyLocalEditorGuest();
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
            BootLoadingService.ReportProgress(0.32f, "读取 StreamingAssets");
            bool bootOk = false;
            yield return StreamingAssetsUtil.ReadTextCoroutine(
                StreamingAssetsUtil.BootRelativePath,
                (ok, _) => bootOk = ok);
            if (!bootOk)
                Log.Warning("[GuestLogin] StreamingAssets Game/boot.json missing — using defaults");

            BootLoadingService.ReportProgress(0.40f, "游客登录中");
            yield return GuestLoginClient.EnsureGuestThenEnter((ok, mode) =>
            {
                _success = ok;
                Log.Info($"[GuestLogin] done ok={ok} mode={mode} guest={GuestSession.IsGuest}");
            });

            BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
            _finished = true;
        }
    }
}
