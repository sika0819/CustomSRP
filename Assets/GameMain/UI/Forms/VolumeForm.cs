using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class VolumeForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        readonly List<(VisualElement element, EventCallback<GeometryChangedEvent> callback)> _layout = new();
        readonly List<(VisualElement element, EventCallback<PointerDownEvent> down, EventCallback<PointerMoveEvent> move)> _pointers = new();
        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            CloseWhenBackdropClicked();
            BindTrack("presetdian1", "backgroundtiao1", "caption11");
            BindTrack("presetdian2", "backgroundtiao2", "caption21");
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            for (int i = 0; i < _layout.Count; i++)
                _layout[i].element.UnregisterCallback(_layout[i].callback);
            _layout.Clear();
            for (int i = 0; i < _pointers.Count; i++)
            {
                _pointers[i].element.UnregisterCallback(_pointers[i].down);
                _pointers[i].element.UnregisterCallback(_pointers[i].move);
            }
            _pointers.Clear();
            base.OnClose(isShutdown, userData);
        }

        void BindTrack(string knobName, string trackName, string labelName)
        {
            VisualElement knob = Root.Q(knobName);
            VisualElement track = Root.Q(trackName);
            Label label = Root.Q<Label>(labelName);
            if (knob == null || track == null)
                return;

            knob.pickingMode = PickingMode.Position;
            knob.usageHints = UsageHints.DynamicTransform;
            float value = 1f;
            void Apply() => SetValue(knob, track, label, value);
            EventCallback<GeometryChangedEvent> layout = _ => Apply();
            track.RegisterCallback(layout);
            _layout.Add((track, layout));
            Apply();

            EventCallback<PointerDownEvent> down = evt => knob.CapturePointer(evt.pointerId);
            EventCallback<PointerMoveEvent> move = evt =>
            {
                if (!knob.HasPointerCapture(evt.pointerId))
                    return;
                float width = track.layout.width;
                if (width < 1f)
                    return;
                Vector2 local = track.WorldToLocal(evt.position);
                value = Mathf.Clamp01(local.x / width);
                Apply();
            };
            knob.RegisterCallback(down);
            knob.RegisterCallback(move);
            _pointers.Add((knob, down, move));
        }

        static void SetValue(VisualElement knob, VisualElement track, Label label, float value)
        {
            if (track.layout.width < 1f)
                return;

            knob.style.left = track.layout.x + track.layout.width * value - knob.layout.width * 0.5f;
            if (label != null)
                label.text = Mathf.RoundToInt(value * 100f).ToString();
        }
    }
}
