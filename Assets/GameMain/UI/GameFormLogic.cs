using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    public class GameFormLogic : UIFormLogic
    {
        protected VisualElement Root { get; private set; }
        protected virtual bool CloseWhenAnyButtonClicked => false;

        /// <summary>
        /// Popups in the original project slide up from below the 1125×2436 canvas.
        /// </summary>
        protected virtual bool SlidesUpFromBottom => false;

        /// <summary>
        /// Tap empty area (game visible, no panel chrome) to dismiss. Default: all bottom sheets.
        /// </summary>
        protected virtual bool CloseOnBackdropClick => SlidesUpFromBottom;

        const float SlideDuration = 0.32f;
        const float SlideDistance = 2436f;

        readonly List<(VisualElement element, EventCallback<ClickEvent> callback)> _clicks = new();
        IVisualElementScheduledItem _slide;
        float _slideY;
        bool _closing;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            // Forms are pooled: clear close/slide state from the previous life.
            _closing = false;
            _slide?.Pause();
            _slide = null;

            UIDocument document = GetComponent<UIDocument>();
            ScreenLayout.Apply(document);
            Root = document.rootVisualElement;
            ScreenLayout.Changed += OnScreenChanged;

            if (SlidesUpFromBottom)
            {
                _slideY = SlideDistance;
                Root.style.translate = new Translate(0, SlideDistance);
                PlaySlide(0f);
            }
            else
            {
                _slideY = 0f;
                Root.style.translate = new Translate(0, 0);
            }

            if (CloseOnBackdropClick)
                BindBackdropDismiss();

            if (CloseWhenAnyButtonClicked)
            {
                Root.Query<Button>().ForEach(button =>
                    RegisterClick(button, CloseSelf));
            }
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            ScreenLayout.Changed -= OnScreenChanged;
            ClearClicks();
            _slide?.Pause();
            _slide = null;
            Root = null;
            base.OnClose(isShutdown, userData);
        }

        public override void OnDepthChanged(int uiGroupDepth, int depthInUIGroup)
        {
            base.OnDepthChanged(uiGroupDepth, depthInUIGroup);
            UIDocument document = GetComponent<UIDocument>();
            if (document != null)
                document.sortingOrder = uiGroupDepth * 100 + depthInUIGroup;
        }

        protected void OnClick(string elementName, Action action)
        {
            VisualElement element = Root != null ? Root.Q(elementName) : null;
            if (element == null)
                return;
            RegisterClick(element, action);
        }

        /// <summary>
        /// Shared dismiss for every bottom-sheet / dialog. Do not reimplement in subclasses.
        /// </summary>
        /// <remarks>
        /// Full-screen <c>game-form-stage</c> uses PickingMode.Ignore, so empty taps often
        /// resolve to the form root instead of the sibling backdrop. One filtered listener on
        /// the form root covers root / backdrop / dim "view" layers without closing on chrome.
        /// </remarks>
        void BindBackdropDismiss()
        {
            VisualElement formRoot = Root.Q("root") ?? Root;
            formRoot.pickingMode = PickingMode.Position;

            VisualElement backdrop = formRoot.Q("formBackdrop");
            if (backdrop == null)
            {
                backdrop = new VisualElement { name = "formBackdrop" };
                backdrop.AddToClassList("game-form-backdrop");
                formRoot.Insert(0, backdrop);
            }

            backdrop.pickingMode = PickingMode.Position;

            foreach (VisualElement child in formRoot.Children())
            {
                if (child.ClassListContains("game-form-stage"))
                    child.pickingMode = PickingMode.Ignore;
            }

            EventCallback<ClickEvent> dismiss = evt =>
            {
                if (IsBackdropDismissTarget(evt.target as VisualElement, formRoot, backdrop))
                    CloseSelf();
            };
            formRoot.RegisterCallback(dismiss);
            _clicks.Add((formRoot, dismiss));

            foreach (VisualElement child in formRoot.Children())
            {
                if (child == backdrop || child.ClassListContains("game-form-stage"))
                    continue;
                string name = child.name ?? string.Empty;
                if (name.IndexOf("view", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                child.pickingMode = PickingMode.Position;
                child.RegisterCallback(dismiss);
                _clicks.Add((child, dismiss));
            }
        }

        /// <summary>
        /// Empty / dim areas only. Sheet chrome (dialog art, buttons, lists) must not dismiss.
        /// </summary>
        static bool IsBackdropDismissTarget(
            VisualElement target, VisualElement formRoot, VisualElement backdrop)
        {
            if (target == null)
                return false;

            if (target == formRoot || target == backdrop)
                return true;

            for (VisualElement n = target; n != null && n != formRoot; n = n.parent)
            {
                if (n == backdrop)
                    return true;
            }

            // Boluo full-screen dim layer clicked directly (name contains "view").
            string targetName = target.name ?? string.Empty;
            return targetName.IndexOf("view", StringComparison.OrdinalIgnoreCase) >= 0
                && !target.ClassListContains("game-form-stage");
        }

        void RegisterClick(VisualElement element, Action action)
        {
            if (element == null || action == null)
                return;

            element.pickingMode = PickingMode.Position;
            EventCallback<ClickEvent> callback = _ => action();
            element.RegisterCallback(callback);
            _clicks.Add((element, callback));
        }

        void ClearClicks()
        {
            for (int i = 0; i < _clicks.Count; i++)
                _clicks[i].element.UnregisterCallback(_clicks[i].callback);
            _clicks.Clear();
        }

        protected void CloseSelf()
        {
            if (_closing)
                return;

            if (!SlidesUpFromBottom || Root == null)
            {
                FinishClose();
                return;
            }

            _closing = true;
            PlaySlide(SlideDistance, FinishClose);
        }

        /// <summary>Close without the slide animation (e.g. preset teleport veil must cover immediately).</summary>
        protected void CloseSelfImmediate()
        {
            if (_closing)
                return;
            _closing = true;
            _slide?.Pause();
            _slide = null;
            FinishClose();
        }

        void FinishClose()
        {
            UIForm form = GetComponent<UIForm>();
            if (form != null)
                GameEntry.UI.CloseUIForm(form.SerialId);
        }

        void OnScreenChanged()
        {
            UIDocument document = GetComponent<UIDocument>();
            ScreenLayout.Apply(document);
        }

        void PlaySlide(float targetY, Action onComplete = null)
        {
            if (Root == null)
            {
                onComplete?.Invoke();
                return;
            }

            _slide?.Pause();
            float from = _slideY;
            float start = Time.unscaledTime;
            _slide = Root.schedule.Execute(() =>
            {
                if (Root == null)
                {
                    _slide?.Pause();
                    onComplete?.Invoke();
                    return;
                }

                float t = Mathf.Clamp01((Time.unscaledTime - start) / SlideDuration);
                float eased = targetY > from ? t * t * t : 1f - Mathf.Pow(1f - t, 3f);
                _slideY = Mathf.Lerp(from, targetY, eased);
                Root.style.translate = new Translate(0, _slideY);
                if (t < 1f)
                    return;

                _slide?.Pause();
                onComplete?.Invoke();
            }).Every(16);
        }
    }
}
