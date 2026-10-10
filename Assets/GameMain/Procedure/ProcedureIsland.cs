using GameFramework.Procedure;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    public class ProcedureIsland : ProcedureBase
    {
        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            if (!GameEntry.UI.HasUIForm(UiAssets.IslandMainForm))
                GameEntry.UI.OpenUIForm(UiAssets.IslandMainForm, "Main");
        }
    }
}
