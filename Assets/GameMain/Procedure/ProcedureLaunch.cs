using GameFramework.Procedure;
using UnityGameFramework.Runtime;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    public class ProcedureLaunch : ProcedureBase
    {
        bool _loadingRequested;
        float _formWait;

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            _loadingRequested = false;
            _formWait = 0f;
            // Fresh boot every entrance — do not keep a stuck fade from last Play.
            BootLoadingService.ResetSession();
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (GameEntry.UI == null)
                return;

            GameEntry.EnsureUiGroups();
            if (!GameEntry.UI.HasUIGroup("Loading"))
                return;

            if (!_loadingRequested)
            {
                _loadingRequested = true;
                // Device: lights BootEarlyCover (scene-baked). Editor: also opens LoadingForm via GF.
                BootLoadingService.Show("正在进入世界...", 0.05f);
                return;
            }

#if UNITY_EDITOR
            if (!GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
            {
                _formWait += elapseSeconds;
                // OpenUIForm can be one frame late in EditorResourceMode; retry briefly.
                if (_formWait < 1.5f)
                {
                    BootLoadingService.Show("正在进入世界...", 0.05f);
                    return;
                }

                Log.Warning("LoadingForm failed to open — continue boot with early cover only.");
            }
#endif

            ChangeState<ProcedureInitYooAsset>(procedureOwner);
        }
    }
}
