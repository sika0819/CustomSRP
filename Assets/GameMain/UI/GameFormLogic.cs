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
        /// Design-px gap from stage bottom to sheet card bottom when open (Boluo
        /// <c>anchoredPosition.y</c> with bottom pivot). USS cards are authored for
        /// <see cref="UssAuthoredSheetBottom"/>; override per form for a different rest gap
        /// without rewriting every <c>margin-top: -(height + N)</c>. Applied as a
        /// <c>translate</c> on <c>.game-form-stage</c> so the full-screen backdrop stays put.
        /// </summary>
        protected virtual float SheetBottomOffset => UssAuthoredSheetBottom;

        /// <summary>
        /// When true, hide HUD preset + health icons while this form is open
        /// (Boluo <c>OpenPopupWithIconHide</c>). Volume / Weather / Music override.
        /// </summary>
        protected virtual bool HidesBottomHudIcons => false;

        /// <summary>
        /// Tap empty area (game visible, no panel chrome) to dismiss. Default: all bottom sheets.
        /// </summary>
        protected virtual bool CloseOnBackdropClick => SlidesUpFromBottom;

        const float SlideDuration = 0.32f;
        const float SlideDistance = 2436f;

        /// <summary>
        /// Bottom inset baked into sheet USS (<c>top: 100%; margin-top: -(height + 120)</c>),
        /// matching Boluo volume / weather / music / preset cards.
        /// </summary>
        protected const float UssAuthoredSheetBottom = 120f;

        readonly List<(VisualElement element, EventCallback<ClickEvent> callback)> _clicks = new();
        // PointerDown / PointerUp gate callbacks aren't ClickEvents so they
        // can't share _clicks. Track them separately so ClearClicks still
        // unregisters everything on close.
        readonly List<(VisualElement element, Delegate callback)> _trackingCallbacks = new();
        IVisualElementScheduledItem _slide;
        float _slideY;
        bool _closing;
        bool _hidBottomIcons;
        // Track whether the pointer-down that *could* trigger backdrop dismiss
        // started on the backdrop itself. Without this, a press that begins on
        // panel chrome (a list row, a button, a draggable strip) and ends with
        // the pointer released on the backdrop would be misread as a backdrop
        // click and dismiss the popup. We require the down *and* the up to land
        // on backdrop regions before we close.
        bool _backdropDownActive;
        int _backdropDownPointerId = -1;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            // Forms are pooled: clear close/slide state from the previous life.
            _closing = false;
            _hidBottomIcons = false;
            _slide?.Pause();
            _slide = null;
            _backdropDownActive = false;
            _backdropDownPointerId = -1;

            UIDocument document = GetComponent<UIDocument>();
            ScreenLayout.Apply(document);
            Root = document.rootVisualElement;
            ScreenLayout.Changed += OnScreenChanged;
            ApplySheetBottomOffset();

            if (HidesBottomHudIcons)
            {
                HudBottomIcons.PushHide();
                _hidBottomIcons = true;
            }

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
            if (_hidBottomIcons)
            {
                _hidBottomIcons = false;
                HudBottomIcons.PopShow();
            }

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
                if (!_backdropDownActive)
                {
                    // Press started on panel chrome (a list row, a button, a
                    // draggable strip) — even if the up landed on the
                    // backdrop, this is not a backdrop dismiss.
                    _backdropDownActive = false;
                    _backdropDownPointerId = -1;
                    return;
                }
                if (IsBackdropDismissTarget(evt.target as VisualElement, formRoot, backdrop))
                    CloseSelf();
                _backdropDownActive = false;
                _backdropDownPointerId = -1;
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

            // We need the *pointer-down* origin to gate the dismiss. The
            // default ClickEvent handler only sees the *up* target, so a
            // press that starts inside panel chrome (a list row, a button, a
            // draggable strip) and ends on the backdrop would otherwise be
            // misread as a backdrop click. Listening on TrickleDown lets us
            // record the down target before any child handler can StopPropagation.
            EventCallback<PointerDownEvent> trackDown = evt =>
            {
                if (evt.button != 0)
                {
                    _backdropDownActive = false;
                    _backdropDownPointerId = -1;
                    return;
                }
                _backdropDownActive = IsBackdropDismissTarget(
                    evt.target as VisualElement, formRoot, backdrop);
                _backdropDownPointerId = _backdropDownActive ? evt.pointerId : -1;
            };
            EventCallback<PointerUpEvent> trackUp = evt =>
            {
                if (_backdropDownActive
                    && evt.pointerId != _backdropDownPointerId)
                {
                    // Different pointer finished — invalidate the gate so the
                    // active ClickEvent cannot dismiss.
                    _backdropDownActive = false;
                }
            };
            formRoot.RegisterCallback(trackDown, TrickleDown.TrickleDown);
            formRoot.RegisterCallback(trackUp, TrickleDown.TrickleDown);
            _trackingCallbacks.Add((formRoot, trackDown));
            _trackingCallbacks.Add((formRoot, trackUp));
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

            for (int i = 0; i < _trackingCallbacks.Count; i++)
            {
                var (element, cb) = _trackingCallbacks[i];
                if (cb is EventCallback<PointerDownEvent> pd)
                    element.UnregisterCallback(pd, TrickleDown.TrickleDown);
                else if (cb is EventCallback<PointerUpEvent> pu)
                    element.UnregisterCallback(pu, TrickleDown.TrickleDown);
            }
            _trackingCallbacks.Clear();
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
            ApplySheetBottomOffset();
        }

        /// <summary>
        /// Lift / lower <c>.game-form-stage</c> so the open sheet gap matches
        /// <see cref="SheetBottomOffset"/>. USS stays authored at
        /// <see cref="UssAuthoredSheetBottom"/> (Boluo 120).
        /// </summary>
        void ApplySheetBottomOffset()
        {
            if (Root == null)
                return;

            float ty = UssAuthoredSheetBottom - SheetBottomOffset;
            Root.Query(className: "game-form-stage").ForEach(stage =>
            {
                if (Mathf.Approximately(ty, 0f))
                    stage.style.translate = StyleKeyword.Null;
                else
                    stage.style.translate = new Translate(0, ty);
            });
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
