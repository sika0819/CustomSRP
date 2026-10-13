using UnityEngine.UIElements;

namespace Game
{
    public class WeatherForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        static readonly string[] WeatherButtons = { "fengButton", "yuButton", "wuButton" };
        static readonly IslandWeatherKind[] WeatherKinds =
        {
            IslandWeatherKind.Wind,
            IslandWeatherKind.Rain,
            IslandWeatherKind.Foggy,
        };

        int _selectedIndex;
        int _level = 1;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            OnClick("dragHandle", CloseSelf);
            OnClick("closeLevel", CloseSelf);

            WeatherService weather = GameEntry.Weather;
            if (weather != null)
            {
                _level = weather.Level > 0 ? weather.Level : 1;
                for (int i = 0; i < WeatherKinds.Length; i++)
                {
                    if (WeatherKinds[i] == weather.Kind)
                        _selectedIndex = i;
                }
            }

            for (int i = 0; i < WeatherButtons.Length; i++)
            {
                int index = i;
                string name = WeatherButtons[i];
                OnClick(name, () => Select(index));
            }

            // Level knobs if present
            OnClick("level1", () => SetLevel(1));
            OnClick("level2", () => SetLevel(2));
            OnClick("level3", () => SetLevel(3));

            Select(_selectedIndex);
        }

        void Select(int index)
        {
            _selectedIndex = index;
            VisualElement tab = Root.Q("tab");
            for (int i = 0; i < WeatherButtons.Length; i++)
            {
                Button button = Root.Q<Button>(WeatherButtons[i]);
                if (button == null)
                    continue;
                bool on = i == index;
                button.style.scale = new Scale(on ? new UnityEngine.Vector3(1.08f, 1.08f, 1f) : UnityEngine.Vector3.one);
                button.style.opacity = on ? 1f : 0.35f;
                if (on && tab != null && button.layout.width > 1f)
                {
                    float center = button.layout.x + button.layout.width * 0.5f;
                    tab.style.left = center - 21f;
                    tab.style.marginLeft = 0;
                }
            }

            ApplyWeather();
        }

        void SetLevel(int level)
        {
            _level = UnityEngine.Mathf.Clamp(level, 1, 3);
            ApplyWeather();
        }

        void ApplyWeather()
        {
            if (_selectedIndex < 0 || _selectedIndex >= WeatherKinds.Length)
                return;
            GameEntry.Weather?.SetWeather(WeatherKinds[_selectedIndex], _level);
        }
    }
}
