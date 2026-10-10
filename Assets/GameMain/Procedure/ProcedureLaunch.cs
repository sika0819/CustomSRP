using GameFramework.Event;
using GameFramework.Procedure;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    public class ProcedureLaunch : ProcedureBase
    {
        bool _enterIsland;

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            _enterIsland = false;
            GameEntry.Event.Subscribe(EnterIslandEventArgs.EventId, OnEnterIsland);
            GameEntry.UI.OpenUIForm(UiAssets.LoadingForm, "Loading");
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (_enterIsland)
                ChangeState<ProcedureIsland>(procedureOwner);
        }

        protected override void OnLeave(ProcedureOwner procedureOwner, bool isShutdown)
        {
            GameEntry.Event.Unsubscribe(EnterIslandEventArgs.EventId, OnEnterIsland);
            if (GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
                GameEntry.UI.CloseUIForm(GameEntry.UI.GetUIForm(UiAssets.LoadingForm).SerialId);
            base.OnLeave(procedureOwner, isShutdown);
        }

        void OnEnterIsland(object sender, GameEventArgs e)
        {
            _enterIsland = true;
        }
    }
}
