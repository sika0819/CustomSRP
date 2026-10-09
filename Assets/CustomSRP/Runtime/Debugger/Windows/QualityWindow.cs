using UnityEngine;
using UnityEngine.UIElements;

namespace CustomSRP.Debugger
{
    sealed class QualityWindow : PerfDebuggerWindowBase
    {
        Label _level;
        Label _color;
        Label _aa;
        Label _vSync;
        Label _shadows;
        Label _shadowDist;
        Label _lod;
        Label _aniso;

        public override string Title => "Quality";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("Quality Information");
            box.Add(Row("Quality Level", out _level));
            box.Add(Row("Active Color Space", out _color));
            box.Add(Row("Anti Aliasing", out _aa));
            box.Add(Row("vSync", out _vSync));
            box.Add(Row("Shadows", out _shadows));
            box.Add(Row("Shadow Distance", out _shadowDist));
            box.Add(Row("LOD Bias", out _lod));
            box.Add(Row("Anisotropic", out _aniso));
            root.Add(box);

            string[] names = QualitySettings.names;
            if (names != null && names.Length > 0)
            {
                var levels = Box("Set Quality Level");
                for (int i = 0; i < names.Length; i++)
                {
                    int index = i;
                    var btn = new Button(() =>
                    {
                        QualitySettings.SetQualityLevel(index, true);
                        Refresh();
                    })
                    {
                        text = names[i]
                    };
                    btn.AddToClassList("perf-button");
                    levels.Add(btn);
                }

                root.Add(levels);
            }
        }

        public override void Refresh()
        {
            int q = QualitySettings.GetQualityLevel();
            string[] names = QualitySettings.names;
            Set(_level, names != null && q >= 0 && q < names.Length ? names[q] : q.ToString());
            Set(_color, QualitySettings.activeColorSpace.ToString());
            Set(_aa, QualitySettings.antiAliasing.ToString());
            Set(_vSync, QualitySettings.vSyncCount.ToString());
            Set(_shadows, QualitySettings.shadows.ToString());
            Set(_shadowDist, QualitySettings.shadowDistance.ToString("F1"));
            Set(_lod, QualitySettings.lodBias.ToString("F2"));
            Set(_aniso, QualitySettings.anisotropicFiltering.ToString());
        }
    }
}
