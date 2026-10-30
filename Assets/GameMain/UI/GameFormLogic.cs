using System;
using System.Collections.Generic;
using Game.Native;
using GameFramework.UI;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    public class GameFormLogic : UIFormLogic
    {
        protected VisualElement Root { get; private set; }

        /// <summary>Document 重建后换上新的视觉树根。setter 不能再标 protected（CS0273）。</summary>
        protected void BindRoot(VisualElement root)
        {
            Root = root;
        }
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

        /// <summary>
        /// 下载提示窗要盖住主界面摇杆。只对这几张开：加载罩关闭时改层级会把树又挂回去。
        /// </summary>
        protected virtual bool PinDocumentOrder => false;

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
        // UIDocument 根的 translate 归文档自己（LateUpdate 里 SetTransform / ClearTransform）。
        // 滑 UXML 页面根，打开那一帧的长卡顿也不能把 0.32s 一次吃完。
        const float MaxSlideStep = 0.05f;
        VisualElement _slideTarget;
        float _slideFrom;
        float _slideTo;
        float _slideElapsed;
        float _slideY;
        bool _slidePlaying;
        Action _slideComplete;
        bool _closing;
        bool _hidBottomIcons;
        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            // Forms are pooled: clear close/slide state from the previous life.
            _closing = false;
            _hidBottomIcons = false;
            StopSlide();

            UIDocument document = GetComponent<UIDocument>();
            ScreenLayout.Apply(document);
            Root = document.rootVisualElement;
            ScreenLayout.Changed += OnScreenChanged;
            ApplySheetBottomOffset();

            if (HidesBottomHudIcons)
            {
                IslandMainForm.Current?.PushBottomHide();
                _hidBottomIcons = true;
            }

            if (SlidesUpFromBottom)
            {
                _slideY = SlideDistance;
                BeginSlide(0f, null);
            }
            else
            {
                _slideY = 0f;
                _slideTarget = PageRoot();
                ApplySlideTranslate(0f);
            }

            if (CloseOnBackdropClick)
                BindBackdropDismiss();
            else
                // 不点空白关闭时，stage 的 USS Ignore 会让子树命中不稳定；
                // 下载弹窗等要把舞台和卡片改成可点。
                EnableStagePicking();

            if (CloseWhenAnyButtonClicked)
            {
                Root.Query<Button>().ForEach(button =>
                    RegisterClick(button, CloseSelf));
            }

            if (PinDocumentOrder)
                EnsureDocumentOrder();

            // 打开的这一刻就把摇杆命中层让开，不等下一帧 Tick。
            if (IsOverlayForm())
                IslandMainForm.Current?.SetJoystickModal(true);
        }

        void EnableStagePicking()
        {
            if (Root == null)
                return;
            // stage USS 默认 Ignore（给底部 sheet 点空白关闭用）。下载弹窗等不走
            // BindBackdropDismiss，必须把舞台和 NoticeCard 改回可点，否则按钮看起来点不了。
            Root.Query(className: "game-form-stage").ForEach(stage =>
                stage.pickingMode = PickingMode.Position);
            Root.Query(className: "notice-card").ForEach(card =>
                card.pickingMode = PickingMode.Position);
            VisualElement dialog = Root.Q("dialog");
            if (dialog != null)
                dialog.pickingMode = PickingMode.Position;
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            if (_hidBottomIcons)
            {
                _hidBottomIcons = false;
                IslandMainForm.Current?.PopBottomShow();
            }

            ScreenLayout.Changed -= OnScreenChanged;
            ClearClicks();
            StopSlide();
            _slideTarget = null;
            Root = null;
            base.OnClose(isShutdown, userData);
            // 关掉的这一刻按「去掉自己」恢复命中层，不等下一帧。
            IslandMainForm.Current?.SetJoystickModal(JoystickShouldBlock());
        }

        public override void OnUpdate(float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(elapseSeconds, realElapseSeconds);
            TickSlide(realElapseSeconds);
        }

        public override void OnDepthChanged(int uiGroupDepth, int depthInUIGroup)
        {
            base.OnDepthChanged(uiGroupDepth, depthInUIGroup);
            UIDocument document = GetComponent<UIDocument>();
            if (document != null)
                document.sortingOrder = uiGroupDepth * 100 + depthInUIGroup;
            if (PinDocumentOrder)
                EnsureDocumentOrder();
        }

        /// <summary>
        /// 相同 sortingOrder 不会重新插入。先错开再写回，保证这张窗盖住主界面摇杆。
        /// 不要放进每个界面的 OnOpen：加载罩关闭时改层级会把已经摘掉的树又挂回去。
        /// </summary>
        protected void EnsureDocumentOrder()
        {
            UIDocument document = GetComponent<UIDocument>();
            UIForm form = GetComponent<UIForm>();
            if (document == null || form == null || form.UIGroup == null)
                return;

            float order = form.UIGroup.Depth * 100f + form.DepthInUIGroup;
            if (Mathf.Approximately(document.sortingOrder, order))
                document.sortingOrder = order + 1f;
            document.sortingOrder = order;
        }

        protected void OnClick(string elementName, Action action)
        {
            VisualElement element = Root != null ? Root.Q(elementName) : null;
            if (element == null)
                return;
            RegisterClick(element, action);
        }

        /// <summary>空白处按下。子类可先跑「返回」再关。</summary>
        protected virtual void OnBackdropDismiss()
        {
            CloseSelf();
        }

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

            // 舞台保持 USS 的 Ignore。点卡片外会落到背后的空白层；
            // 把舞台改成可点会把卡片上的按下当成点空白关掉。
            formRoot.Query(className: "notice-card").ForEach(card =>
                card.pickingMode = PickingMode.Position);
            VisualElement dialog = formRoot.Q("dialog");
            if (dialog != null)
                dialog.pickingMode = PickingMode.Position;

            // 按下即关。ClickEvent 要等抬起还落在同一块上，真机抬起经常被摇杆丢掉。
            // 目标就是空白层时才关，卡片和按钮不是这个目标。
            EventCallback<PointerDownEvent> dismiss = evt =>
            {
                if (_closing || evt.button > 0)
                    return;
                if (!IsBackdropDismissTarget(evt.target as VisualElement, formRoot, backdrop))
                    return;
                OnBackdropDismiss();
                evt.StopPropagation();
            };
            formRoot.RegisterCallback(dismiss);
            _trackingCallbacks.Add((formRoot, dismiss));

            foreach (VisualElement child in formRoot.Children())
            {
                if (child == backdrop || child.ClassListContains("game-form-stage"))
                    continue;
                string name = child.name ?? string.Empty;
                if (name.IndexOf("view", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                child.pickingMode = PickingMode.Position;
                child.RegisterCallback(dismiss);
                _trackingCallbacks.Add((child, dismiss));
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

            // 舞台铺满设计画布。点在卡片外会命中舞台本身，不命中它的子节点。
            if (target.ClassListContains("game-form-stage"))
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

            // Button 自带的 Clickable 在冒泡阶段对 PointerDown 调 StopImmediatePropagation。
            // 后挂的按下回调不会执行，点「立即下载」就是没反应、下一张窗也出不来。
            // 拿掉 Clickable，并在 TrickleDown 接住，赶在它之前。
            if (element is Button button)
                button.clickable = null;

            // 按下就触发。真机会把同一次触摸再发成兼容鼠标，那一次丢掉。
            // 只记住这一根手指，松开或取消后才能再点。button > 0 才丢掉：
            // 部分触摸的 button 是 -1，写成 != 0 会把这次按下整段扔掉。
            element.pickingMode = PickingMode.Position;
            element.focusable = false;
            int activePointer = -1;
            int firedFrame = -1;
            EventCallback<PointerDownEvent> callback = evt =>
            {
                if (evt.button > 0)
                    return;
                if (activePointer >= 0 && !IsPointerHeld(activePointer))
                    activePointer = -1;
                // 同一次触摸会再来一支兼容鼠标。手指还在，或这一帧已经触发过，都丢掉。
                if (evt.pointerId == PointerId.mousePointerId
                    && (Input.touchCount > 0 || firedFrame == Time.frameCount))
                {
                    evt.StopImmediatePropagation();
                    return;
                }
                if (activePointer == evt.pointerId)
                {
                    evt.StopImmediatePropagation();
                    return;
                }
                activePointer = evt.pointerId;
                firedFrame = Time.frameCount;
                action();
                evt.StopImmediatePropagation();
            };
            element.RegisterCallback(callback, TrickleDown.TrickleDown);
            _trackingCallbacks.Add((element, callback));

            EventCallback<PointerUpEvent> up = evt =>
            {
                if (evt.pointerId == activePointer)
                    activePointer = -1;
            };
            element.RegisterCallback(up);
            _trackingCallbacks.Add((element, up));

            EventCallback<PointerCancelEvent> cancel = evt =>
            {
                if (evt.pointerId == activePointer)
                    activePointer = -1;
            };
            element.RegisterCallback(cancel);
            _trackingCallbacks.Add((element, cancel));

            // 按下已经执行。拦住随后冒泡的 ClickEvent，避免点到按钮又被父级当成点空白处关掉。
            EventCallback<ClickEvent> swallow = evt => evt.StopPropagation();
            element.RegisterCallback(swallow);
            _clicks.Add((element, swallow));
        }

        static bool IsPointerHeld(int pointerId)
        {
            if (pointerId < 0)
                return false;

            if (Input.touchCount > 0)
            {
                if (pointerId == PointerId.mousePointerId)
                    return false;

                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch touch = Input.GetTouch(i);
                    if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                        continue;
                    if (touch.fingerId + PointerId.touchPointerIdBase == pointerId)
                        return true;
                }

                return false;
            }

            return pointerId == PointerId.mousePointerId && Input.GetMouseButton(0);
        }

        bool IsOverlayForm()
        {
            UIForm form = GetComponent<UIForm>();
            if (form == null || form.UIGroup == null)
                return false;
            string name = form.UIGroup.Name;
            return name == "Panel" || name == "Dialog";
        }

        /// <summary>
        /// 摇杆命中层是否该让开。关闭过程中本窗可能还挂在组上，先把它从计数里拿掉。
        /// </summary>
        bool JoystickShouldBlock()
        {
            if (NativeOverlayGate.BlocksLocomotion)
                return true;
            if (GameEntry.UI == null)
                return false;

            int panel = GroupCount("Panel");
            int dialog = GroupCount("Dialog");
            UIForm self = GetComponent<UIForm>();
            if (self != null && GameEntry.UI.HasUIForm(self.SerialId) && self.UIGroup != null)
            {
                if (self.UIGroup.Name == "Panel")
                    panel = Mathf.Max(0, panel - 1);
                else if (self.UIGroup.Name == "Dialog")
                    dialog = Mathf.Max(0, dialog - 1);
            }

            return panel > 0 || dialog > 0;
        }

        static int GroupCount(string groupName)
        {
            if (GameEntry.UI == null)
                return 0;
            IUIGroup group = GameEntry.UI.GetUIGroup(groupName);
            return group != null ? group.UIFormCount : 0;
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
                {
                    element.UnregisterCallback(pd, TrickleDown.TrickleDown);
                    element.UnregisterCallback(pd);
                }
                else if (cb is EventCallback<PointerUpEvent> pu)
                {
                    element.UnregisterCallback(pu, TrickleDown.TrickleDown);
                    element.UnregisterCallback(pu);
                }
                else if (cb is EventCallback<PointerCancelEvent> pc)
                {
                    element.UnregisterCallback(pc, TrickleDown.TrickleDown);
                    element.UnregisterCallback(pc);
                }
            }
            _trackingCallbacks.Clear();
        }

        protected void CloseSelf()
        {
            if (_closing)
                return;

            if (!SlidesUpFromBottom || Root == null)
            {
                _closing = true;
                FinishClose();
                return;
            }

            _closing = true;
            BeginSlide(SlideDistance, FinishClose);
        }

        /// <summary>Close without the slide animation (e.g. preset teleport veil must cover immediately).</summary>
        protected void CloseSelfImmediate()
        {
            if (_closing)
                return;
            _closing = true;
            StopSlide();
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

        /// <summary>
        /// UXML 页面根。文档根的 translate 会被 <c>UIDocument</c> 写掉，滑它等于没动画。
        /// </summary>
        VisualElement PageRoot()
        {
            if (Root == null)
                return null;
            return Root.Q("root") ?? Root;
        }

        void BeginSlide(float targetY, Action onComplete)
        {
            _slideTarget = PageRoot();
            if (_slideTarget == null)
            {
                StopSlide();
                onComplete?.Invoke();
                return;
            }

            _slideFrom = _slideY;
            _slideTo = targetY;
            _slideElapsed = 0f;
            _slidePlaying = true;
            _slideComplete = onComplete;
            ApplySlideTranslate(_slideFrom);
        }

        void StopSlide()
        {
            _slidePlaying = false;
            _slideComplete = null;
        }

        void TickSlide(float realElapseSeconds)
        {
            if (!_slidePlaying || _slideTarget == null)
                return;

            _slideElapsed += Mathf.Min(Mathf.Max(0f, realElapseSeconds), MaxSlideStep);
            float t = Mathf.Clamp01(_slideElapsed / SlideDuration);
            // 向下收起 InCubic，向上弹出 OutCubic。对齐 Boluo ShowAnima / HideAnima。
            float eased = _slideTo > _slideFrom ? t * t * t : 1f - Mathf.Pow(1f - t, 3f);
            _slideY = Mathf.Lerp(_slideFrom, _slideTo, eased);
            ApplySlideTranslate(_slideY);
            if (t < 1f)
                return;

            Action done = _slideComplete;
            StopSlide();
            done?.Invoke();
        }

        void ApplySlideTranslate(float y)
        {
            if (_slideTarget == null)
                return;
            if (Mathf.Approximately(y, 0f))
                _slideTarget.style.translate = StyleKeyword.Null;
            else
                _slideTarget.style.translate = new Translate(0, y);
        }
    }
}
