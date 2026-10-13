using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    sealed class SettingsWindow : DebuggerPage
    {
        readonly DebuggerComponent _debugger;
        Slider _scale;

        public SettingsWindow(DebuggerComponent debugger) => _debugger = debugger;

        public override string Title => "Settings";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("Window Settings");
            _scale = new Slider("UI Scale", 0.75f, 2.5f)
            {
                value = _debugger != null ? _debugger.UiScale : 1f,
                showInputField = true
            };
            _scale.RegisterValueChangedCallback(evt =>
            {
                if (_debugger != null)
                {
                    _debugger.UiScale = evt.newValue;
                }
            });
            box.Add(_scale);

            var row = new VisualElement();
            row.AddToClassList("perf-toolbar");
            foreach (float s in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                float scale = s;
                var btn = new Button(() =>
                {
                    if (_debugger != null)
                    {
                        _debugger.UiScale = scale;
                    }

                    if (_scale != null)
                    {
                        _scale.SetValueWithoutNotify(scale);
                    }
                })
                {
                    text = $"{s:0.##}x"
                };
                btn.AddToClassList("perf-button");
                row.Add(btn);
            }

            box.Add(row);

            var unlock = new Button(() =>
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            })
            {
                text = "Unlock 60 FPS (mobile)"
            };
            unlock.AddToClassList("perf-button");
            box.Add(unlock);

            var reset = new Button(() => _debugger?.ResetLayout())
            {
                text = "Reset Layout"
            };
            reset.AddToClassList("perf-button");
            box.Add(reset);
            root.Add(box);
        }

        public override void Refresh()
        {
            if (_scale != null && _debugger != null)
            {
                _scale.SetValueWithoutNotify(_debugger.UiScale);
            }
        }
    }
}
