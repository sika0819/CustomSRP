using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class PresetForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        public VisualTreeAsset PresetTemplate;

        const float ItemWidth = 264f;
        const float ItemGap = 24f;
        const float ClickSlop = 14f;
        const float TrackWidth = 240f;
        const float ThumbWidth = 63f;

        struct ItemBinding
        {
            public VisualElement Root;
            public int Index;
            public int PresetId;
        }

        readonly List<ItemBinding> _items = new();
        ScrollView _list;
        VisualElement _scrollThumb;
        int _selected;
        bool _dragging;
        bool _moved;
        bool _captured;
        int _pointerId = -1;
        float _dragStartX;
        float _scrollStartX;
        int _pressIndex = -1;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);

            _list = Root.Q<ScrollView>("presetList");
            _scrollThumb = Root.Q("scrollTrackActive");
            if (_list == null)
                return;

            _list.mode = ScrollViewMode.Horizontal;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.contentContainer.style.flexDirection = FlexDirection.Row;
            _list.contentContainer.style.flexWrap = Wrap.NoWrap;
            _list.contentContainer.style.alignItems = Align.FlexStart;
            _list.RegisterCallback<PointerDownEvent>(OnListPointerDown, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerMoveEvent>(OnListPointerMove, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerUpEvent>(OnListPointerUp, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerCancelEvent>(OnListPointerCancel, TrickleDown.TrickleDown);
            _list.horizontalScroller.valueChanged += OnScrollValueChanged;

            if (GameEntry.Presets != null && GameEntry.Presets.CurrentId > 0)
            {
                IslandPresetAsset[] presets = GameEntry.Presets.Presets;
                for (int i = 0; i < presets.Length; i++)
                {
                    if (presets[i] != null && presets[i].Id == GameEntry.Presets.CurrentId)
                        _selected = i;
                }
            }

            Rebuild();
            Root.schedule.Execute(ScrollSelectedIntoView).StartingIn(0);
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            if (_list != null)
            {
                _list.UnregisterCallback<PointerDownEvent>(OnListPointerDown, TrickleDown.TrickleDown);
                _list.UnregisterCallback<PointerMoveEvent>(OnListPointerMove, TrickleDown.TrickleDown);
                _list.UnregisterCallback<PointerUpEvent>(OnListPointerUp, TrickleDown.TrickleDown);
                _list.UnregisterCallback<PointerCancelEvent>(OnListPointerCancel, TrickleDown.TrickleDown);
                _list.horizontalScroller.valueChanged -= OnScrollValueChanged;
            }

            _items.Clear();
            _list?.contentContainer.Clear();
            _list = null;
            _scrollThumb = null;
            base.OnClose(isShutdown, userData);
        }

        void Rebuild()
        {
            _items.Clear();
            _list.contentContainer.Clear();

            IslandPresetAsset[] presets = GameEntry.Presets != null
                ? GameEntry.Presets.Presets
                : System.Array.Empty<IslandPresetAsset>();

            int count = GameEntry.Presets != null
                ? GameEntry.Presets.GetUiCount()
                : 0;

            if (count <= 0)
            {
                string[] fallback =
                {
                    "清晨起床", "独自旅行", "午后放松", "委屈泛滥", "专注工作",
                    "放空发呆", "灵感唤醒", "假日躺平", "学习冲刺", "准备入睡",
                };
                for (int i = 0; i < fallback.Length; i++)
                {
                    VisualElement element = MakeItem();
                    BindFallback(element, i, fallback[i]);
                    _list.Add(element);
                }

                SyncScrollTrack();
                return;
            }

            for (int i = 0; i < count; i++)
            {
                VisualElement element = MakeItem();
                BindItem(element, i, presets[i]);
                _list.Add(element);
            }

            SyncScrollTrack();
        }

        VisualElement MakeItem()
        {
            if (PresetTemplate != null)
            {
                TemplateContainer item = PresetTemplate.Instantiate();
                item.style.width = ItemWidth;
                item.style.height = 337;
                item.style.flexShrink = 0;
                item.style.marginRight = ItemGap;
                return item;
            }

            Label fallback = new Label();
            fallback.name = "presetitem";
            fallback.style.width = ItemWidth;
            fallback.style.height = 337;
            fallback.style.flexShrink = 0;
            fallback.style.marginRight = ItemGap;
            fallback.style.fontSize = 32;
            fallback.style.color = new StyleColor(Color.white);
            return fallback;
        }

        void BindItem(VisualElement element, int index, IslandPresetAsset preset)
        {
            Label title = element.Q<Label>("presetitem") ?? element as Label;
            if (title != null)
                title.text = preset != null ? preset.DisplayName : ("预设 " + (index + 1));

            VisualElement cover = element.Q("cover") ?? element.Q("zhanweitu2Button");
            ApplyCover(cover, preset, index);
            element.style.opacity = index == _selected ? 1f : 0.72f;
            if (cover != null)
                cover.pickingMode = PickingMode.Position;
            element.pickingMode = PickingMode.Position;

            _items.Add(new ItemBinding
            {
                Root = element,
                Index = index,
                PresetId = preset != null ? preset.Id : index + 1,
            });
        }

        void BindFallback(VisualElement element, int index, string name)
        {
            Label title = element.Q<Label>("presetitem") ?? element as Label;
            if (title != null)
                title.text = name;

            VisualElement cover = element.Q("cover") ?? element.Q("zhanweitu2Button");
            ApplyCover(cover, null, index);
            element.style.opacity = index == _selected ? 1f : 0.72f;
            if (cover != null)
                cover.pickingMode = PickingMode.Position;
            element.pickingMode = PickingMode.Position;

            _items.Add(new ItemBinding
            {
                Root = element,
                Index = index,
                PresetId = index + 1,
            });
        }

        static void ApplyCover(VisualElement cover, IslandPresetAsset preset, int index)
        {
            if (cover == null)
                return;

            Texture2D tex = preset != null ? preset.Cover : null;
            if (tex == null)
            {
                int coverId = Mathf.Clamp(index + 1, 1, PresetService.UiPresetCount);
                tex = Resources.Load<Texture2D>($"PresetCovers/zhanweitu{coverId}");
            }

            if (tex != null)
                cover.style.backgroundImage = new StyleBackground(tex);
        }

        void SelectPreset(int index, int presetId)
        {
            _selected = index;
            // Instant close so IslandMainForm black veil is not covered by this panel.
            CloseSelfImmediate();
            GameEntry.Presets?.RequestTransition(presetId);
        }

        void OnListPointerDown(PointerDownEvent evt)
        {
            if (_list == null || _pointerId >= 0)
                return;

            _pointerId = evt.pointerId;
            _dragging = true;
            _moved = false;
            _captured = false;
            _dragStartX = evt.position.x;
            _scrollStartX = _list.scrollOffset.x;
            _pressIndex = FindItemIndexAt(evt.position);
        }

        void OnListPointerMove(PointerMoveEvent evt)
        {
            if (!_dragging || evt.pointerId != _pointerId || _list == null)
                return;

            float dx = evt.position.x - _dragStartX;
            if (!_moved && Mathf.Abs(dx) >= ClickSlop)
            {
                _moved = true;
                if (!_captured)
                {
                    _list.CapturePointer(evt.pointerId);
                    _captured = true;
                }
            }

            if (!_moved)
                return;

            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            float next = Mathf.Clamp(_scrollStartX - dx, 0f, max);
            _list.scrollOffset = new Vector2(next, 0f);
            SyncScrollTrack();
            evt.StopPropagation();
        }

        void OnListPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != _pointerId)
                return;

            bool wasMoved = _moved;
            int pressIndex = _pressIndex;

            if (_captured && _list != null && _list.HasPointerCapture(evt.pointerId))
                _list.ReleasePointer(evt.pointerId);

            _dragging = false;
            _pointerId = -1;
            _captured = false;
            _pressIndex = -1;
            _moved = false;
            SyncScrollTrack();

            if (!wasMoved && pressIndex >= 0 && pressIndex < _items.Count)
            {
                ItemBinding item = _items[pressIndex];
                SelectPreset(item.Index, item.PresetId);
                evt.StopPropagation();
            }
        }

        void OnListPointerCancel(PointerCancelEvent evt)
        {
            if (evt.pointerId != _pointerId)
                return;

            if (_captured && _list != null && _list.HasPointerCapture(evt.pointerId))
                _list.ReleasePointer(evt.pointerId);

            _dragging = false;
            _pointerId = -1;
            _captured = false;
            _pressIndex = -1;
            _moved = false;
            SyncScrollTrack();
        }

        int FindItemIndexAt(Vector2 panelPosition)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                VisualElement root = _items[i].Root;
                if (root == null)
                    continue;
                if (root.worldBound.Contains(panelPosition))
                    return i;
            }

            return -1;
        }

        void OnScrollValueChanged(float _) => SyncScrollTrack();

        void ScrollSelectedIntoView()
        {
            if (_list == null || _selected < 0)
                return;

            float target = _selected * (ItemWidth + ItemGap);
            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            _list.scrollOffset = new Vector2(Mathf.Clamp(target, 0f, max), 0f);
            SyncScrollTrack();
        }

        void SyncScrollTrack()
        {
            if (_scrollThumb == null || _list == null)
                return;

            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            float t = max > 0.01f ? Mathf.Clamp01(_list.scrollOffset.x / max) : 0f;
            float travel = TrackWidth - ThumbWidth;
            _scrollThumb.style.marginLeft = -TrackWidth * 0.5f + t * travel;
            _scrollThumb.style.opacity = _dragging && _moved ? 1f : 0.5f;
        }

        float ViewportWidth()
        {
            float w = _list != null ? _list.contentViewport.layout.width : 0f;
            return w > 1f ? w : 900f;
        }

        float ContentWidth()
        {
            int n = _list != null ? _list.contentContainer.childCount : 0;
            if (n <= 0)
                return 0f;
            return n * ItemWidth + Mathf.Max(0, n - 1) * ItemGap + 24f;
        }
    }
}
