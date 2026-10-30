using UnityEngine;
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
        int _groupDepth = 4;
        int _depthInGroup;
        bool _aboveLoading;

        /// <summary>高于 Loading 组（depth 10 → 1000+）和 BootEarlyCover（1000）。</summary>
        const int AboveLoadingSortingOrder = 2000;

        // 对齐原 Tip 动画：先弹出，停住，再淡出。弹出不跟打开那一帧的卡顿绑在一起。
        const float PopSeconds = 0.2f;
        const float FadeSeconds = 0.35f;
        const float PopScale = 0.84f;
        const float PopOffsetY = 28f;

        float _tipShownAt;
        float _tipDuration;
        bool _tipPlaying;
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
            if (_toast != null)
            {
                _toast.usageHints |= UsageHints.DynamicTransform;
                _toast.style.transformOrigin = new TransformOrigin(
                    Length.Percent(50), Length.Percent(50), 0f);
            }
            IgnorePicks();
            UiTipToast.Hide(_toast);
            FlushPending();
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _tipPlaying = false;
            _hide?.Pause();
            _hide = null;
            if (Current == this)
                Current = null;
            UiTipToast.NotifyHostClosed();
            base.OnClose(isShutdown, userData);
        }

        public override void OnUpdate(float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(elapseSeconds, realElapseSeconds);
            if (!_tipPlaying || _toast == null)
                return;

            // 用真实时间。以前每帧最多累加 0.05s，掉帧时 3 秒的气泡会停很久。
            float life = Time.unscaledTime - _tipShownAt;
            ApplyTipPose(life, _tipDuration);
            if (life < _tipDuration)
                return;

            Hide();
        }

        public override void OnDepthChanged(int uiGroupDepth, int depthInUIGroup)
        {
            _groupDepth = uiGroupDepth;
            _depthInGroup = depthInUIGroup;
            base.OnDepthChanged(uiGroupDepth, depthInUIGroup);
            if (_aboveLoading)
                LiftAboveLoading();
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

            _aboveLoading = request.AboveLoading;
            if (_aboveLoading)
                LiftAboveLoading();
            else
                RestoreSorting();

            if (request.Error)
                UiTipToast.ApplyErrorStyle(_toast);
            else
                UiTipToast.ResetToNormalStyle(_toast);

            UiTipToast.Show(_toast, request.Message, request.AnchoredY);
            _tipDuration = Mathf.Max(0.2f, request.Seconds);
            _tipShownAt = Time.unscaledTime;
            _tipPlaying = true;
            ApplyTipPose(0f, _tipDuration);
            ArmHide(_tipDuration);
        }

        void ArmHide(float seconds)
        {
            _hide?.Pause();
            _hide = null;
            if (Root == null)
                return;

            long ms = (long)(Mathf.Max(0.2f, seconds) * 1000f);
            _hide = Root.schedule.Execute(Hide).StartingIn(ms);
        }

        void Hide()
        {
            if (!_tipPlaying)
            {
                _hide?.Pause();
                _hide = null;
                return;
            }

            _tipPlaying = false;
            _hide?.Pause();
            _hide = null;
            _aboveLoading = false;
            RestoreSorting();
            if (_toast != null)
            {
                _toast.style.opacity = 1f;
                _toast.style.scale = new Scale(Vector3.one);
                _toast.style.translate = StyleKeyword.Null;
            }
            UiTipToast.Hide(_toast);
            UiTipToast.ResetToNormalStyle(_toast);
        }

        static void SplitTipMotion(float total, out float pop, out float fadeStart)
        {
            float popSpan = Mathf.Min(PopSeconds, total * 0.45f);
            float fadeSpan = Mathf.Min(FadeSeconds, total * 0.4f);
            if (popSpan + fadeSpan > total)
            {
                float fit = total / Mathf.Max(0.01f, popSpan + fadeSpan);
                popSpan *= fit;
                fadeSpan *= fit;
            }

            pop = popSpan;
            fadeStart = Mathf.Max(popSpan, total - fadeSpan);
        }

        void ApplyTipPose(float life, float total)
        {
            if (_toast == null)
                return;

            SplitTipMotion(total, out float pop, out float fadeStart);
            float opacity;
            float scale;
            float ty;
            if (life < pop)
            {
                float u = EaseOutCubic(pop > 0f ? life / pop : 1f);
                opacity = u;
                scale = Mathf.Lerp(PopScale, 1f, u);
                ty = Mathf.Lerp(PopOffsetY, 0f, u);
            }
            else if (life < fadeStart)
            {
                opacity = 1f;
                scale = 1f;
                ty = 0f;
            }
            else
            {
                float span = Mathf.Max(0.01f, total - fadeStart);
                float u = Mathf.Clamp01((life - fadeStart) / span);
                opacity = 1f - u;
                scale = 1f;
                ty = 0f;
            }

            _toast.style.opacity = opacity;
            _toast.style.scale = new Scale(new Vector3(scale, scale, 1f));
            if (Mathf.Approximately(ty, 0f))
                _toast.style.translate = StyleKeyword.Null;
            else
                _toast.style.translate = new Translate(0f, ty);
        }

        static float EaseOutCubic(float t)
        {
            float u = 1f - Mathf.Clamp01(t);
            return 1f - u * u * u;
        }

        void LiftAboveLoading()
        {
            UIDocument document = GetComponent<UIDocument>();
            if (document != null)
                document.sortingOrder = AboveLoadingSortingOrder;
        }

        void RestoreSorting()
        {
            UIDocument document = GetComponent<UIDocument>();
            if (document != null)
                document.sortingOrder = _groupDepth * 100 + _depthInGroup;
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
