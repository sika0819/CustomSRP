using System.Collections;
using GameFramework.Procedure;
using UnityEngine;
using UnityGameFramework.Runtime;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    /// <summary>
    /// 初始化 YooAsset DefaultPackage，并把 GF UI 资源加载接到 YooAsset。
    /// </summary>
    public class ProcedureInitYooAsset : ProcedureBase
    {
        bool _started;
        bool _finished;
        Coroutine _routine;

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            _started = false;
            _finished = false;
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (!_started)
            {
                _started = true;
                MonoBehaviour host = GameEntry.Base;
                if (host != null)
                    _routine = host.StartCoroutine(RunInit());
                else
                    _finished = true;
            }

            if (!_finished)
                return;

            ChangeState<ProcedureGuestLogin>(procedureOwner);
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

        IEnumerator RunInit()
        {
            BootLoadingService.ReportProgress(0.12f, "YooAsset 初始化");
            yield return YooResourceModule.InitDefaultPackageCoroutine((ok, err) =>
            {
                if (!ok)
                    Log.Warning($"[InitYooAsset] init failed, continue: {err}");
            });

            if (YooResourceModule.IsInitialized)
            {
                BindYooResourceToGameFramework();

                BootLoadingService.ReportProgress(0.16f, "检查资源更新");
                yield return YooResourceModule.DownloadDefaultPackageCoroutine(
                    (progress, tip) =>
                    {
                        BootLoadingService.ReportProgress(Mathf.Lerp(0.16f, 0.22f, progress), tip);
                    },
                    (ok, err) =>
                    {
                        if (!ok)
                            Log.Warning($"[InitYooAsset] download failed, continue: {err}");
                    });
            }

            if (YooResourceModule.IsInitialized)
            {
                Log.Info("[InitYooAsset] Opening LoadingForm via YooGF");
                BootLoadingService.Show("资源系统就绪", 0.22f);

                float wait = 0f;
                while (!BootLoadingService.HasForm && wait < 8f)
                {
                    wait += Time.unscaledDeltaTime;
                    yield return null;
                }

                if (!BootLoadingService.HasForm)
                    Log.Error("[InitYooAsset] LoadingForm still missing after 8s");
                else
                    Log.Info("[InitYooAsset] LoadingForm open ok");
            }
            else
            {
                BootLoadingService.ReportProgress(0.22f, "资源系统就绪");
            }

            _finished = true;
        }

        static void BindYooResourceToGameFramework()
        {
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
                return;

            YooAssetResourceManager yoo = YooAssetResourceManager.Ensure(host);
            if (yoo == null)
                return;

            if (GameEntry.UI != null)
            {
                GameEntry.UI.SetResourceManager(yoo);
                GameEntry.UI.SetUIFormHelper(EnsureYooUIFormHelper(host));
            }

            Log.Info("[InitYooAsset] UI ResourceManager → YooAssetResourceManager");
        }

        static YooUIFormHelper EnsureYooUIFormHelper(MonoBehaviour host)
        {
            var helper = host.GetComponentInChildren<YooUIFormHelper>(true);
            if (helper != null)
                return helper;

            var go = new GameObject("Yoo UI Form Helper");
            go.transform.SetParent(host.transform, false);
            return go.AddComponent<YooUIFormHelper>();
        }
    }
}
