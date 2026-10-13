using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class MusicForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        public VisualTreeAsset TrackTemplate;

        // Boluo InGameYinYueView scrollbar reveal.
        const float ScrollbarIdleHideSeconds = 3f;
        const float ScrollbarFadeSeconds = 0.35f;

        ListView _list;
        ScrollView _scrollView;
        Scroller _vScroller;
        IVisualElementScheduledItem _hideSchedule;
        IVisualElementScheduledItem _fadeSchedule;
        float _scrollerAlpha;
        float _fadeFrom;
        float _fadeTo;
        float _fadeElapsed = -1f;
        bool _revealGate;
        float _ignoreProgrammaticUntil;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);

            _list = Root.Q<ListView>("musicList");
            if (_list == null)
                return;

            string[] tracks = MusicService.TrackNames;
            _list.fixedItemHeight = 144;
            _list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            _list.selectionType = SelectionType.None;
            _list.itemsSource = new List<string>(tracks);
            _list.makeItem = MakeItem;
            _list.bindItem = BindItem;
            _list.unbindItem = UnbindItem;
            _list.Rebuild();

            BindScrollbarReveal();
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            UnbindScrollbarReveal();
            base.OnClose(isShutdown, userData);
        }

        void BindScrollbarReveal()
        {
            // ListView.scrollView is internal; query the nested ScrollView instead.
            _scrollView = _list.Q<ScrollView>();
            if (_scrollView == null)
                return;

            _scrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _scrollView.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            _vScroller = _scrollView.verticalScroller;
            if (_vScroller == null)
                return;

            SetScrollerAlpha(0f);
            _revealGate = false;
            _ignoreProgrammaticUntil = 0f;
            _vScroller.valueChanged += OnScrollerValueChanged;
            _list.schedule.Execute(EnableRevealGate).StartingIn(32);
        }

        void UnbindScrollbarReveal()
        {
            if (_vScroller != null)
                _vScroller.valueChanged -= OnScrollerValueChanged;

            _hideSchedule?.Pause();
            _hideSchedule = null;
            _fadeSchedule?.Pause();
            _fadeSchedule = null;
            _vScroller = null;
            _scrollView = null;
            _revealGate = false;
        }

        void EnableRevealGate()
        {
            _revealGate = true;
            _ignoreProgrammaticUntil = Time.unscaledTime + 0.5f;
        }

        void OnScrollerValueChanged(float _)
        {
            NotifyScrollbarUserInput(bypassProgrammaticCooldown: false);
        }

        void NotifyScrollbarUserInput(bool bypassProgrammaticCooldown)
        {
            if (_vScroller == null || !_revealGate)
                return;
            if (!bypassProgrammaticCooldown && Time.unscaledTime < _ignoreProgrammaticUntil)
                return;

            FadeScrollerTo(1f);
            _hideSchedule?.Pause();
            _hideSchedule = _list.schedule.Execute(BeginIdleHide)
                .StartingIn((long)(ScrollbarIdleHideSeconds * 1000f));
        }

        void BeginIdleHide()
        {
            _hideSchedule = null;
            FadeScrollerTo(0f);
        }

        void FadeScrollerTo(float target)
        {
            _fadeFrom = _scrollerAlpha;
            _fadeTo = Mathf.Clamp01(target);
            if (Mathf.Abs(_fadeFrom - _fadeTo) < 0.01f)
            {
                SetScrollerAlpha(_fadeTo);
                _fadeElapsed = -1f;
                _fadeSchedule?.Pause();
                _fadeSchedule = null;
                return;
            }

            _fadeElapsed = 0f;
            _fadeSchedule?.Pause();
            _fadeSchedule = _list.schedule.Execute(TickFade).Every(16);
        }

        void TickFade()
        {
            if (_fadeElapsed < 0f || _vScroller == null)
            {
                _fadeSchedule?.Pause();
                _fadeSchedule = null;
                return;
            }

            _fadeElapsed += 0.016f;
            float t = Mathf.Clamp01(_fadeElapsed / ScrollbarFadeSeconds);
            SetScrollerAlpha(Mathf.Lerp(_fadeFrom, _fadeTo, t));
            if (t < 1f)
                return;

            _fadeElapsed = -1f;
            _fadeSchedule?.Pause();
            _fadeSchedule = null;
        }

        void SetScrollerAlpha(float alpha)
        {
            _scrollerAlpha = Mathf.Clamp01(alpha);
            if (_vScroller == null)
                return;
            _vScroller.style.opacity = _scrollerAlpha;
            _vScroller.pickingMode = _scrollerAlpha > 0.01f
                ? PickingMode.Position
                : PickingMode.Ignore;
        }

        VisualElement MakeItem()
        {
            if (TrackTemplate != null)
                return TrackTemplate.Instantiate();

            Label fallback = new Label();
            fallback.name = "label0";
            fallback.style.height = 144;
            fallback.style.fontSize = 36;
            fallback.style.color = new StyleColor(Color.white);
            return fallback;
        }

        void BindItem(VisualElement element, int index)
        {
            string[] tracks = MusicService.TrackNames;
            Label title = element.Q<Label>("label0") ?? element as Label;
            if (title != null && index >= 0 && index < tracks.Length)
                title.text = tracks[index];

            int playing = GameEntry.Music != null ? GameEntry.Music.CurrentIndex : 0;
            VisualElement playingMark = element.Q("playinganima");
            if (playingMark != null)
            {
                playingMark.usageHints = UsageHints.DynamicTransform;
                playingMark.style.display = index == playing ? DisplayStyle.Flex : DisplayStyle.None;
            }

            Button button = element.Q<Button>("musicButton") ?? element.Q<Button>();
            if (button == null)
                return;

            EventCallback<ClickEvent> callback = _ =>
            {
                GameEntry.Music?.PlayIndex(index);
                _list.RefreshItems();
            };
            button.userData = callback;
            button.RegisterCallback(callback);
        }

        void UnbindItem(VisualElement element, int index)
        {
            Button button = element.Q<Button>("musicButton") ?? element.Q<Button>();
            if (button?.userData is EventCallback<ClickEvent> callback)
                button.UnregisterCallback(callback);
        }
    }
}
