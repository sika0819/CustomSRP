using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Global tip bubble. Lives in the Tip UI group so it does not translate with
    /// music / weather / preset sheets.
    /// </summary>
    public class TipToastForm : GameFormLogic
    {
        public static TipToastForm Current { get; private set; }

        VisualElement _toast;
        IVisualElementScheduledItem _hide;

        public static void ClearCurrent()
        {
            Current = null;
        }

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            Current = this;

            VisualElement stage = Root.Q("stage") ?? Root;
            _toast = UiTipToast.Ensure(stage, "tipToast");
            IgnorePicks();
            UiTipToast.Hide(_toast);
            FlushPending();
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _hide?.Pause();
            _hide = null;
            if (Current == this)
                Current = null;
            UiTipToast.NotifyHostClosed();
            base.OnClose(isShutdown, userData);
        }

        public void FlushPending()
        {
            if (!UiTipToast.TryConsume(out UiTipToast.Request request))
                return;
            Present(request);
        }

        void Present(UiTipToast.Request request)
        {
            if (_toast == null)
                return;

            if (request.Error)
                UiTipToast.ApplyErrorStyle(_toast);
            else
                UiTipToast.ResetToNormalStyle(_toast);

            UiTipToast.Show(_toast, request.Message, request.AnchoredY);
            _hide?.Pause();
            long ms = (long)(UnityEngine.Mathf.Max(0.2f, request.Seconds) * 1000f);
            _hide = _toast.schedule.Execute(Hide).StartingIn(ms);
        }

        void Hide()
        {
            _hide?.Pause();
            _hide = null;
            UiTipToast.Hide(_toast);
            UiTipToast.ResetToNormalStyle(_toast);
        }

        void IgnorePicks()
        {
            if (Root == null)
                return;

            Root.pickingMode = PickingMode.Ignore;
            Root.Query<VisualElement>().ForEach(ve => ve.pickingMode = PickingMode.Ignore);
        }
    }
}
