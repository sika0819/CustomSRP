using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Lays every form out in its own phone frame so the screens can be viewed together.
    /// </summary>
    public sealed class UiPreview : MonoBehaviour
    {
        const int DesignWidth = 1125;
        const int DesignHeight = 2436;
        const float FrameWidth = 520f;

        [SerializeField]
        FormSlot[] forms;

        [SerializeField]
        VisualTreeAsset musicTrack;

        [SerializeField]
        VisualTreeAsset presetItem;

        [SerializeField]
        Texture2D loadingStill;

        void Start()
        {
            UIDocument document = GetComponent<UIDocument>();
            if (document == null)
                return;

            VisualElement root = document.rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = new Color(0.11f, 0.12f, 0.14f, 1f);

            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            VisualElement grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.paddingLeft = 24;
            grid.style.paddingTop = 24;
            grid.style.paddingBottom = 24;
            scroll.Add(grid);

            if (forms == null)
                return;

            for (int i = 0; i < forms.Length; i++)
            {
                if (forms[i].Tree == null)
                    continue;
                grid.Add(BuildCard(forms[i]));
            }
        }

        VisualElement BuildCard(FormSlot slot)
        {
            VisualElement card = new VisualElement();
            card.style.marginRight = 24;
            card.style.marginBottom = 24;
            card.style.width = FrameWidth;

            Label title = new Label(string.IsNullOrEmpty(slot.Title) ? slot.Tree.name : slot.Title);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 22;
            title.style.color = new Color(0.92f, 0.94f, 0.96f, 1f);
            title.style.marginBottom = 8;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            card.Add(title);

            float scale = FrameWidth / DesignWidth;
            float frameHeight = DesignHeight * scale;

            VisualElement window = new VisualElement();
            window.style.width = FrameWidth;
            window.style.height = frameHeight;
            window.style.overflow = Overflow.Hidden;
            window.style.backgroundColor = new Color(0.04f, 0.05f, 0.06f, 1f);
            window.style.borderTopLeftRadius = 18;
            window.style.borderTopRightRadius = 18;
            window.style.borderBottomLeftRadius = 18;
            window.style.borderBottomRightRadius = 18;
            card.Add(window);

            VisualElement page = new VisualElement();
            page.style.position = Position.Absolute;
            page.style.left = 0;
            page.style.top = 0;
            page.style.width = DesignWidth;
            page.style.height = DesignHeight;
            page.style.transformOrigin = new TransformOrigin(Length.Percent(0), Length.Percent(0));
            page.style.scale = new Scale(new Vector3(scale, scale, 1f));
            window.Add(page);

            TemplateContainer tree = slot.Tree.Instantiate();
            tree.style.position = Position.Absolute;
            tree.style.left = 0;
            tree.style.right = 0;
            tree.style.top = 0;
            tree.style.bottom = 0;
            page.Add(tree);
            Dress(slot.Title, tree);
            return card;
        }

        void Dress(string title, VisualElement tree)
        {
            if (title == "Loading" && loadingStill != null)
            {
                VisualElement still = tree.Q("campfireStill");
                if (still != null)
                    still.style.backgroundImage = new StyleBackground(loadingStill);
            }
            else if (title == "Music")
            {
                FillList(tree, "musicList", musicTrack, 144f, "label0",
                    "散步", "旅行", "工作", "驾车", "学习", "清晨", "午后", "夜晚");
            }
            else if (title == "Preset")
            {
                FillList(tree, "presetList", presetItem, 337f, "presetitem",
                    "焦虑袭来", "清晨海边", "午后树荫", "傍晚篝火", "雨夜");
            }
        }

        static void FillList(VisualElement tree, string listName, VisualTreeAsset item, float itemHeight, string labelName, params string[] labels)
        {
            ListView list = tree.Q<ListView>(listName);
            if (list == null || item == null)
                return;

            list.fixedItemHeight = itemHeight;
            list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            list.selectionType = SelectionType.None;
            list.itemsSource = new List<string>(labels);
            list.makeItem = () => item.Instantiate();
            list.bindItem = (element, index) =>
            {
                Label label = element.Q<Label>(labelName);
                if (label != null)
                    label.text = labels[index];
            };
            list.Rebuild();
        }

        [Serializable]
        public struct FormSlot
        {
            public string Title;
            public VisualTreeAsset Tree;
        }
    }
}
