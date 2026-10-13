using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    sealed class TimeWindow : DebuggerPage
    {
        Label _time;
        Label _unscaled;
        Label _fixed;
        Label _delta;
        Label _smooth;
        Label _scale;
        Label _frame;
        Label _realtime;

        public override string Title => "Time";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("Time Information");
            box.Add(Row("Time", out _time));
            box.Add(Row("Unscaled Time", out _unscaled));
            box.Add(Row("Fixed Time", out _fixed));
            box.Add(Row("Delta Time", out _delta));
            box.Add(Row("Smooth Delta", out _smooth));
            box.Add(Row("Time Scale", out _scale));
            box.Add(Row("Frame Count", out _frame));
            box.Add(Row("Realtime Since Startup", out _realtime));
            root.Add(box);
        }

        public override void Refresh()
        {
            Set(_time, Time.time.ToString("F3"));
            Set(_unscaled, Time.unscaledTime.ToString("F3"));
            Set(_fixed, Time.fixedTime.ToString("F3"));
            Set(_delta, $"{Time.deltaTime * 1000f:F2} ms");
            Set(_smooth, $"{Time.smoothDeltaTime * 1000f:F2} ms");
            Set(_scale, Time.timeScale.ToString("F2"));
            Set(_frame, Time.frameCount.ToString());
            Set(_realtime, Time.realtimeSinceStartup.ToString("F3"));
        }
    }
}
