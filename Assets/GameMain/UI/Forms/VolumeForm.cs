using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class VolumeForm : GameFormLogic
    {
        public const string MusicVolumeKey = "Volume.Music";
        public const string EffectVolumeKey = "Volume.Effect";
        const float KnobSize = 144f;

        protected override bool SlidesUpFromBottom => true;
        readonly List<(VisualElement element, EventCallback<GeometryChangedEvent> callback)> _layout = new();
        readonly List<(VisualElement element, EventCallback<PointerDownEvent> down, EventCallback<PointerMoveEvent> move, EventCallback<PointerUpEvent> up)> _pointers = new();

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            BindRow("musicVolume", MusicVolumeKey);
            BindRow("effectVolume", EffectVolumeKey);
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
                _pointers[i].element.UnregisterCallback(_pointers[i].up);
            }
            _pointers.Clear();
            base.OnClose(isShutdown, userData);
        }

        void BindRow(string rowName, string settingKey)
        {
            VisualElement row = Root.Q(rowName);
            if (row == null)
                return;

            VisualElement knob = row.Q("knob");
            VisualElement track = row.Q("track");
            VisualElement fill = row.Q("fill");
            Label label = row.Q<Label>("value");
            if (knob == null || track == null)
                return;

            knob.pickingMode = PickingMode.Position;
            knob.usageHints = UsageHints.DynamicTransform;
            float value = LoadVolume(settingKey);
            void Apply()
            {
                if (track.layout.width < 1f)
                    return;
                SetValue(knob, track, fill, label, value);
            }
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
                float travel = Mathf.Max(width - KnobSize, 1f);
                Vector2 local = track.WorldToLocal(evt.position);
                value = Mathf.Clamp01((local.x - KnobSize * 0.5f) / travel);
                PushVolume(settingKey, value);
                Apply();
            };
            EventCallback<PointerUpEvent> up = evt =>
            {
                if (knob.HasPointerCapture(evt.pointerId))
                    knob.ReleasePointer(evt.pointerId);
                SaveVolume(settingKey, value);
            };
            knob.RegisterCallback(down);
            knob.RegisterCallback(move);
            knob.RegisterCallback(up);
            _pointers.Add((knob, down, move, up));
        }

        static float LoadVolume(string key)
        {
            if (GameEntry.Setting != null && GameEntry.Setting.HasSetting(key))
                return Mathf.Clamp01(GameEntry.Setting.GetFloat(key, 1f));
            return 1f;
        }

        static void SaveVolume(string key, float value)
        {
            PushVolume(key, value);
            if (GameEntry.Setting == null)
                return;
            GameEntry.Setting.SetFloat(key, Mathf.Clamp01(value));
            GameEntry.Setting.Save();
        }

        static void PushVolume(string key, float value)
        {
            value = Mathf.Clamp01(value);
            if (key == MusicVolumeKey)
                GameEntry.Music?.SetMusicVolume(value);
            else if (key == EffectVolumeKey)
                GameEntry.Music?.SetEffectVolume(value);
        }

        static void SetValue(VisualElement knob, VisualElement track, VisualElement fill, Label label, float value)
        {
            if (track.layout.width < 1f)
                return;

            float trackWidth = track.layout.width;
            float travel = Mathf.Max(trackWidth - KnobSize, 0f);
            float fillWidth = Mathf.Max(trackWidth * value, value > 0.001f ? 204f : 0f);
            fillWidth = Mathf.Min(fillWidth, trackWidth);
            knob.style.left = track.layout.x + travel * value;
            if (fill != null)
                fill.style.width = fillWidth;
            if (label != null)
                label.text = Mathf.RoundToInt(value * 100f).ToString();
        }
    }
}
