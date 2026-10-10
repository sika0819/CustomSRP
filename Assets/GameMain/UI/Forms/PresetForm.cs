using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Game
{
    public class PresetForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        public VisualTreeAsset PresetTemplate;

        static readonly string[] Presets = { "焦虑袭来", "清晨海边", "午后树荫", "傍晚篝火", "雨夜" };

        ListView _list;
        int _selected;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            CloseWhenBackdropClicked();

            _list = Root.Q<ListView>("presetList");
            if (_list == null)
                return;

            _list.fixedItemHeight = 337;
            _list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            _list.selectionType = SelectionType.None;
            _list.itemsSource = new List<string>(Presets);
            _list.makeItem = MakeItem;
            _list.bindItem = BindItem;
            _list.unbindItem = UnbindItem;
            _list.Rebuild();
        }

        VisualElement MakeItem()
        {
            if (PresetTemplate != null)
                return PresetTemplate.Instantiate();

            Label fallback = new Label();
            fallback.name = "presetitem";
            fallback.style.height = 337;
            fallback.style.fontSize = 32;
            fallback.style.color = new StyleColor(UnityEngine.Color.white);
            return fallback;
        }

        void BindItem(VisualElement element, int index)
        {
            Label title = element.Q<Label>("presetitem") ?? element as Label;
            if (title != null)
                title.text = Presets[index];

            element.style.opacity = index == _selected ? 1f : 0.72f;
            Button button = element.Q<Button>();
            if (button == null)
                return;

            EventCallback<ClickEvent> callback = _ =>
            {
                _selected = index;
                _list.RefreshItems();
            };
            button.userData = callback;
            button.RegisterCallback(callback);
        }

        void UnbindItem(VisualElement element, int index)
        {
            Button button = element.Q<Button>();
            if (button?.userData is EventCallback<ClickEvent> callback)
                button.UnregisterCallback(callback);
        }
    }
}
