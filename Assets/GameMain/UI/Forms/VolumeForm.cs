using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class VolumeForm : GameFormLogic
    {
        public const string MusicVolumeKey = "Volume.Music";
        public const string EffectVolumeKey = "Volume.Effect";

        /// <summary>Hit-box / solid white disk diameter (matches VolumeRow.uss).</summary>
        const float KnobSize = 144f;

        /// <summary>
        /// Boluo InGameTiaoJieView: fill grows from a full end-cap (height) to the track width;
        /// knob travel equals that growth (663 when track is 867).
        /// </summary>
        const float MinFillWidth = 204f;

        /// <summary>
        /// Boluo <c>InGameTiaoJieView</c> dragIcon anchoredX (center pivot) relative to track center.
        /// Do not park by aligning knob right edge to track right — that overshoots by ~29.5px.
        /// </summary>
        const float KnobCenterMin = -331f;
        const float KnobCenterMax = 332f;

        protected override bool SlidesUpFromBottom => true;
        protected override bool HidesBottomHudIcons => true;

        /// <summary>Boluo InGameTiaoJieView <c>bg</c> anchoredY (bottom pivot).</summary>
        protected override float SheetBottomOffset => 120f;
        readonly List<(VisualElement element, EventCallback<GeometryChangedEvent> callback)> _layout = new();
        readonly List<(VisualElement element, EventCallback<PointerDownEvent> down, EventCallback<PointerMoveEvent> move, EventCallback<PointerUpEvent> up, EventCallback<PointerCaptureOutEvent> captureOut)> _pointers = new();

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
                _pointers[i].element.UnregisterCallback(_pointers[i].captureOut);
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
            float dragStartLocalX = 0f;
            float dragStartValue = 0f;

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

            EventCallback<PointerDownEvent> down = evt =>
            {
                if (evt.button != 0)
                    return;
                dragStartLocalX = track.WorldToLocal(evt.position).x;
                dragStartValue = value;
                knob.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            };
            EventCallback<PointerMoveEvent> move = evt =>
            {
                if (!knob.HasPointerCapture(evt.pointerId))
                    return;
                float travel = Mathf.Max(KnobCenterMax - KnobCenterMin, 1f);
                float localX = track.WorldToLocal(evt.position).x;
                value = Mathf.Clamp01(dragStartValue + (localX - dragStartLocalX) / travel);
                PushVolume(settingKey, value);
                Apply();
            };
            EventCallback<PointerUpEvent> up = evt =>
            {
                if (!knob.HasPointerCapture(evt.pointerId))
                    return;
                knob.ReleasePointer(evt.pointerId);
                SaveVolume(settingKey, value);
            };
            EventCallback<PointerCaptureOutEvent> captureOut = _ => SaveVolume(settingKey, value);
            knob.RegisterCallback(down);
            knob.RegisterCallback(move);
            knob.RegisterCallback(up);
            knob.RegisterCallback(captureOut);
            _pointers.Add((knob, down, move, up, captureOut));
        }

        static float LoadVolume(string key)
        {
            // Prefer live MusicPlaylist so the panel matches what is actually audible.
            if (GameEntry.Music != null)
            {
                if (key == MusicVolumeKey)
                    return Mathf.Clamp01(GameEntry.Music.MusicVolume);
                if (key == EffectVolumeKey)
                    return Mathf.Clamp01(GameEntry.Music.EffectVolume);
            }

            if (GameEntry.Setting != null && GameEntry.Setting.HasSetting(key))
                return Mathf.Clamp01(GameEntry.Setting.GetFloat(key, 0.8f));

            BoluoGameSave.EnsureLoaded();
            if (key == MusicVolumeKey)
                return BoluoGameSave.MusicVolume01;
            if (key == EffectVolumeKey)
                return BoluoGameSave.EffectVolume01;
            return 0.8f;
        }

        static void SaveVolume(string key, float value)
        {
            PushVolume(key, value);
            GameEntry.Music?.PersistVolumeConstants();
        }

        static void PushVolume(string key, float value)
        {
            value = Mathf.Clamp01(value);
            if (key == MusicVolumeKey)
                GameEntry.Music?.SetMusicVolume(value);
            else if (key == EffectVolumeKey)
                GameEntry.Music?.SetEffectVolume(value);
            // Group volumes also pushed inside MusicPlaylist → WanosEngine parameters.
        }

        static void SetValue(VisualElement knob, VisualElement track, VisualElement fill, Label label, float value)
        {
            if (track.layout.width < 1f)
                return;

            float trackWidth = track.layout.width;
            // Boluo: knob is center-pivoted at KnobCenterMin/Max vs track center; UITK uses top-left.
            float trackCenterX = track.layout.x + trackWidth * 0.5f;
            float halfKnob = KnobSize * 0.5f;
            float knobLeftMin = trackCenterX + KnobCenterMin - halfKnob;
            float travel = KnobCenterMax - KnobCenterMin; // 663; equals trackWidth - MinFillWidth
            knob.style.left = knobLeftMin;
            knob.style.translate = new Translate(travel * value, 0f);

            if (fill != null)
            {
                // 9-slice fill: width change (not scale) so end-caps stay unscaled.
                // Event-driven only (drag / layout), not a per-frame tick.
                if (value <= 0.001f)
                {
                    fill.style.width = 0f;
                    fill.style.display = DisplayStyle.None;
                }
                else
                {
                    fill.style.display = DisplayStyle.Flex;
                    fill.style.width = Mathf.Min(MinFillWidth + travel * value, trackWidth);
                }
            }

            if (label != null)
                label.text = Mathf.RoundToInt(value * 100f).ToString();
        }
    }
}
