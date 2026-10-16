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
                string[] bankUpdate =
                {
                    "bankupdatebackground", "bankupdatevideo", "background2",
                    "bankupdatehint", "labeltmp", "lower", "upper"
                };
                for (int i = 0; i < bankUpdate.Length; i++)
                {
                    VisualElement layer = tree.Q(bankUpdate[i]);
                    if (layer != null)
                        layer.style.display = DisplayStyle.None;
                }

                VisualElement loading = tree.Q("uiloading");
                if (loading != null)
                    loading.style.display = DisplayStyle.Flex;
            }
            else if (title == "Island Main")
            {
                VisualElement pauseHint = tree.Q("pauseHint");
                if (pauseHint != null)
                    pauseHint.style.opacity = 0;
            }
            else if (title == "Music")
            {
                // MusicForm.cs builds row visuals entirely in code now;
                // the preview scene feeds fake labels so the layout can be
                // inspected without runtime MusicCatalog data.
                FillList(tree, "musicList", 144f, new[]
                {
                    "散步", "旅行", "工作", "驾车", "学习", "清晨", "午后", "夜晚"
                });
            }
            else if (title == "Preset")
            {
                FillPresetRow(tree, presetItem,
                    "清晨起床", "独自旅行", "午后放松", "委屈泛滥", "专注工作",
                    "放空发呆", "灵感唤醒", "假日躺平", "学习冲刺", "准备入睡");
            }
        }

        static void FillList(VisualElement tree, string listName, float itemHeight, System.Collections.IList labels)
        {
            ListView list = tree.Q<ListView>(listName);
            if (list == null)
                return;

            list.fixedItemHeight = itemHeight;
            list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            list.selectionType = SelectionType.None;
            list.itemsSource = labels;
            list.makeItem = () =>
            {
                // Mirror MusicForm.MakeItem — single Label per row, no per-item
                // template asset so the editor preview stays in lock-step with
                // the runtime layout.
                var root = new VisualElement();
                root.style.position = Position.Relative;
                root.style.width = new Length(100, LengthUnit.Percent);
                root.style.height = itemHeight;
                root.style.backgroundColor = new Color(0, 0, 0, 0);

                var label = new Label();
                label.style.position = Position.Absolute;
                label.style.left = 85;
                label.style.top = 0;
                label.style.bottom = 0;
                label.style.right = 160;
                label.style.unityTextAlign = TextAnchor.MiddleLeft;
                label.style.fontSize = 45;
                label.style.color = new Color(0.094f, 0.094f, 0.098f, 1f);
                label.style.whiteSpace = WhiteSpace.NoWrap;
                label.style.overflow = Overflow.Hidden;
                root.Add(label);
                return root;
            };
            list.bindItem = (element, index) =>
            {
                Label label = element.Q<Label>();
                if (label != null && index >= 0 && index < labels.Count)
                {
                    object raw = labels[index];
                    if (raw is string s)
                        label.text = s;
                }
            };
            list.Rebuild();
        }

        static void FillPresetRow(VisualElement tree, VisualTreeAsset item, params string[] labels)
        {
            ScrollView list = tree.Q<ScrollView>("presetList");
            if (list == null || item == null)
                return;

            list.mode = ScrollViewMode.Horizontal;
            list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            list.contentContainer.style.flexDirection = FlexDirection.Row;
            list.contentContainer.style.flexWrap = Wrap.NoWrap;
            list.contentContainer.style.alignItems = Align.FlexStart;
            list.contentContainer.Clear();
            for (int i = 0; i < labels.Length; i++)
            {
                TemplateContainer element = item.Instantiate();
                element.style.width = 264;
                element.style.height = 337;
                element.style.flexShrink = 0;
                element.style.marginRight = 24;
                Label label = element.Q<Label>("presetitem");
                if (label != null)
                    label.text = labels[i];
                VisualElement cover = element.Q("cover");
                if (cover != null)
                {
                    Texture2D tex = Resources.Load<Texture2D>($"PresetCovers/zhanweitu{i + 1}");
                    if (tex != null)
                        cover.style.backgroundImage = new StyleBackground(tex);
                }

                list.Add(element);
            }
        }

        [Serializable]
        public struct FormSlot
        {
            public string Title;
            public VisualTreeAsset Tree;
        }
    }
}
