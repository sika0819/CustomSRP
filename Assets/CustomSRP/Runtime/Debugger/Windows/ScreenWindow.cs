using UnityEngine;
using UnityEngine.UIElements;

namespace CustomSRP.Debugger
{
    sealed class ScreenWindow : PerfDebuggerWindowBase
    {
        Label _res;
        Label _w;
        Label _h;
        Label _dpi;
        Label _orient;
        Label _fs;
        Label _safe;
        Label _refresh;

        public override string Title => "Screen";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("Screen Information");
            box.Add(Row("Current Resolution", out _res));
            box.Add(Row("Width", out _w));
            box.Add(Row("Height", out _h));
            box.Add(Row("DPI", out _dpi));
            box.Add(Row("Orientation", out _orient));
            box.Add(Row("Full Screen", out _fs));
            box.Add(Row("Safe Area", out _safe));
            box.Add(Row("Refresh Rate", out _refresh));
            root.Add(box);
        }

        public override void Refresh()
        {
            Resolution r = Screen.currentResolution;
            Set(_res, $"{r.width}×{r.height}");
            Set(_w, $"{Screen.width} px");
            Set(_h, $"{Screen.height} px");
            Set(_dpi, Screen.dpi.ToString("F1"));
            Set(_orient, Screen.orientation.ToString());
            Set(_fs, $"{Screen.fullScreen} ({Screen.fullScreenMode})");
            Set(_safe, Screen.safeArea.ToString());
            Set(_refresh, $"{r.refreshRateRatio.value:F1} Hz");
        }
    }
}
