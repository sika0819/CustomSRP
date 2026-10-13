using GameFramework.Event;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 订 Weather/Time/WorldReady 事件，经 AtmospherePropRegistry 显隐；写篝火门闩。
    /// </summary>
    public sealed class AtmospherePropService : MonoBehaviour
    {
        bool _subscribed;

        public static AtmospherePropService Ensure()
        {
            AtmospherePropService existing = FindAnyObjectByType<AtmospherePropService>();
            if (existing != null)
                return existing;

            var go = new GameObject("AtmospherePropService");
            DontDestroyOnLoad(go);
            return go.AddComponent<AtmospherePropService>();
        }

        void OnEnable() => TrySubscribe();

        void Start() => TrySubscribe();

        void OnDisable() => Unsubscribe();

        void TrySubscribe()
        {
            if (_subscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Subscribe(WeatherChangedEventArgs.EventId, OnWeatherOrTime);
            GameEntry.Event.Subscribe(TimeOfDayChangedEventArgs.EventId, OnWeatherOrTime);
            GameEntry.Event.Subscribe(IslandWorldReadyEventArgs.EventId, OnWorldReady);
            _subscribed = true;
        }

        void Unsubscribe()
        {
            if (!_subscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Unsubscribe(WeatherChangedEventArgs.EventId, OnWeatherOrTime);
            GameEntry.Event.Unsubscribe(TimeOfDayChangedEventArgs.EventId, OnWeatherOrTime);
            GameEntry.Event.Unsubscribe(IslandWorldReadyEventArgs.EventId, OnWorldReady);
            _subscribed = false;
        }

        void OnWorldReady(object sender, GameEventArgs e) => Refresh();

        void OnWeatherOrTime(object sender, GameEventArgs e) => Refresh();

        public void Refresh()
        {
            WeatherService weather = GameEntry.Weather;
            if (weather == null)
                return;

            bool rainOrFog = weather.Kind == IslandWeatherKind.Rain || weather.Kind == IslandWeatherKind.Foggy;
            bool weatherOn = weather.Level > 0 && rainOrFog;
            bool heavyRain = weather.Kind == IslandWeatherKind.Rain && weather.Level >= 2;
            bool day = weather.Period == IslandTimePeriod.Morning || weather.Period == IslandTimePeriod.Noon;
            bool night = weather.Period == IslandTimePeriod.Evening || weather.Period == IslandTimePeriod.Night;

            AtmospherePropRegistry.SetActive(AtmospherePropKind.DayRoot, day);
            AtmospherePropRegistry.SetActive(AtmospherePropKind.NightRoot, night);

            // Always drive per-kind flags so day/night transitions do not leave stale activeSelf.
            AtmospherePropRegistry.SetActive(AtmospherePropKind.SunBeam, day && !weatherOn);
            AtmospherePropRegistry.SetActive(AtmospherePropKind.Butterflies, day && !heavyRain);
            AtmospherePropRegistry.SetActive(AtmospherePropKind.Fireflies, night && !heavyRain);

            bool allowCampfire = night && !heavyRain;
            CampfireWeatherGate.SetSuppressIgnite(!allowCampfire);
            // Gate early-outs when value unchanged; still nudge registered campfires after late spawn.
            if (allowCampfire)
            {
                AtmospherePropRegistry.ForEach(AtmospherePropKind.Campfire, t =>
                {
                    if (t == null)
                        return;
                    FireController fire = t.GetComponent<FireController>()
                                         ?? t.GetComponentInChildren<FireController>(true);
                    if (fire != null)
                        fire.Ignite();
                });
            }
        }

        void OnDestroy()
        {
            Unsubscribe();
            CampfireWeatherGate.ClearVisualIgnited();
            CampfireWeatherGate.SetSuppressIgnite(true);
        }
    }
}
