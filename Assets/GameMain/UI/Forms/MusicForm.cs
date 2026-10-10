using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Game
{
    public class MusicForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        public VisualTreeAsset TrackTemplate;

        static readonly string[] Tracks =
        {
            "散步", "旅行", "工作", "驾车", "学习", "清晨", "午后", "夜晚"
        };

        ListView _list;
        int _playing;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            CloseWhenBackdropClicked();

            _list = Root.Q<ListView>("musicList");
            if (_list == null)
                return;

            _list.fixedItemHeight = 144;
            _list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            _list.selectionType = SelectionType.None;
            _list.itemsSource = new List<string>(Tracks);
            _list.makeItem = MakeItem;
            _list.bindItem = BindItem;
            _list.unbindItem = UnbindItem;
            _list.Rebuild();
        }

        VisualElement MakeItem()
        {
            if (TrackTemplate != null)
                return TrackTemplate.Instantiate();

            Label fallback = new Label();
            fallback.name = "label0";
            fallback.style.height = 144;
            fallback.style.fontSize = 36;
            fallback.style.color = new StyleColor(UnityEngine.Color.white);
            return fallback;
        }

        void BindItem(VisualElement element, int index)
        {
            Label title = element.Q<Label>("label0") ?? element as Label;
            if (title != null)
                title.text = Tracks[index];

            VisualElement playing = element.Q("playinganima");
            if (playing != null)
            {
                playing.usageHints = UsageHints.DynamicTransform;
                playing.style.display = index == _playing ? DisplayStyle.Flex : DisplayStyle.None;
            }

            Button button = element.Q<Button>("musicButton") ?? element.Q<Button>();
            if (button == null)
                return;

            EventCallback<ClickEvent> callback = _ =>
            {
                _playing = index;
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
