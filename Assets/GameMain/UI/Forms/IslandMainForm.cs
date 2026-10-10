using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public class IslandMainForm : GameFormLogic
    {
        VisualElement _timeHand;
        float _angle;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            OnClick("weatherBackground", () => UiAssets.OpenPanel(UiAssets.WeatherForm));
            OnClick("musicBackground", () => UiAssets.OpenPanel(UiAssets.MusicForm));
            OnClick("volumeBackground", () => UiAssets.OpenPanel(UiAssets.VolumeForm));
            OnClick("presetIcon", () => UiAssets.OpenPanel(UiAssets.PresetForm));
            OnClick("timerBackground", () => UiAssets.OpenDialog(UiAssets.TimerPauseForm));

            _timeHand = Root.Q("timeHand");
            if (_timeHand != null)
                _timeHand.usageHints = UsageHints.DynamicTransform;

            VisualElement playing = Root.Q("musicPlaying");
            if (playing != null)
                playing.usageHints = UsageHints.DynamicTransform;

            VisualElement tool = Root.Q("toolButton");
            if (tool != null)
                tool.style.display = DisplayStyle.None;

            VisualElement pauseHint = Root.Q("pauseHint");
            if (pauseHint != null)
                pauseHint.style.opacity = 0;
        }

        public override void OnUpdate(float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(elapseSeconds, realElapseSeconds);
            if (_timeHand == null)
                return;

            _angle = Mathf.Repeat(_angle + elapseSeconds * 6f, 360f);
            _timeHand.style.rotate = new Rotate(Angle.Degrees(_angle));
        }
    }
}
