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

        const float SlideDuration = 0.32f;
        const float SlideDistance = 2436f;

        readonly List<(VisualElement element, EventCallback<ClickEvent> callback)> _clicks = new();
        IVisualElementScheduledItem _slide;
        float _slideY;
        bool _closing;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
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

            if (CloseWhenAnyButtonClicked)
            {
                Root.Query<Button>().ForEach(button =>
                {
                    EventCallback<ClickEvent> callback = _ => CloseSelf();
                    button.RegisterCallback(callback);
                    _clicks.Add((button, callback));
                });
            }
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            ScreenLayout.Changed -= OnScreenChanged;
            for (int i = 0; i < _clicks.Count; i++)
                _clicks[i].element.UnregisterCallback(_clicks[i].callback);
            _clicks.Clear();
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
            VisualElement element = Root.Q(elementName);
            if (element == null)
                return;

            element.pickingMode = PickingMode.Position;
            EventCallback<ClickEvent> callback = _ => action();
            element.RegisterCallback(callback);
            _clicks.Add((element, callback));
        }

        protected void CloseWhenBackdropClicked()
        {
            VisualElement backdrop = Root.Q("root") ?? Root;
            backdrop.pickingMode = PickingMode.Position;
            EventCallback<ClickEvent> callback = evt =>
            {
                if (evt.target == evt.currentTarget)
                    CloseSelf();
            };
            backdrop.RegisterCallback(callback);
            _clicks.Add((backdrop, callback));
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
