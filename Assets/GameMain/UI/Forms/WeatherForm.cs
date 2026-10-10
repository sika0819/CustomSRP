using UnityEngine.UIElements;

namespace Game
{
    public class WeatherForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        static readonly string[] LevelButtons = { "fengButton", "wuButton", "yuButton" };

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            OnClick("dragHandle", CloseSelf);
            for (int i = 0; i < LevelButtons.Length; i++)
            {
                string name = LevelButtons[i];
                OnClick(name, () => Select(name));
            }
            Select(LevelButtons[0]);
        }

        void Select(string selected)
        {
            for (int i = 0; i < LevelButtons.Length; i++)
            {
                Button button = Root.Q<Button>(LevelButtons[i]);
                if (button == null)
                    continue;
                bool on = LevelButtons[i] == selected;
                button.style.scale = new Scale(on ? new UnityEngine.Vector3(1.08f, 1.08f, 1f) : UnityEngine.Vector3.one);
                button.style.opacity = on ? 1f : 0.72f;
            }
        }
    }
}
