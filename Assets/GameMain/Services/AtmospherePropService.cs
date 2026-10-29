using System.Collections.Generic;
using GameFramework.Event;
using UnityEngine;

namespace Game
{
    public enum AtmospherePropKind
    {
        DayRoot,
        NightRoot,
        SunBeam,
        Butterflies,
        Fireflies,
        Campfire,
    }

    /// <summary>
    /// 订天气、时段和世界就绪，显隐氛围节点，并写篝火门闩。
    /// 挂在 Island 场景里，场景卸载时 OnDisable 退订，OnDestroy 清名单和门闩。
    /// </summary>
    public sealed class AtmospherePropService : MonoBehaviour
    {
        readonly Dictionary<AtmospherePropKind, List<Transform>> _entries =
            new Dictionary<AtmospherePropKind, List<Transform>>(8);
        readonly HashSet<EntityId> _visualIgnited = new HashSet<EntityId>();

        bool _subscribed;
        bool _suppressIgnite = true;

        public static AtmospherePropService Instance { get; private set; }

        public bool SuppressIgnite => _suppressIgnite;

        public bool IsVisuallyIgnited => _visualIgnited.Count > 0;

        public int PropCount
        {
            get
            {
                int n = 0;
                foreach (var kv in _entries)
                    n += kv.Value.Count;
                return n;
            }
        }

        public static AtmospherePropService Ensure()
        {
            AtmospherePropService service = IslandServiceHost.Ensure(Instance, "AtmospherePropService");
            Instance = service;
            return service;
        }

        void Awake()
        {
            Instance = this;
            GameEntry.AtmosphereProps = this;
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

            SetActive(AtmospherePropKind.DayRoot, day);
            SetActive(AtmospherePropKind.NightRoot, night);

            SetActive(AtmospherePropKind.SunBeam, day && !weatherOn);
            SetActive(AtmospherePropKind.Butterflies, day && !heavyRain);
            SetActive(AtmospherePropKind.Fireflies, night && !heavyRain);

            bool allowCampfire = night && !heavyRain;
            SetSuppressIgnite(!allowCampfire);
            if (allowCampfire)
            {
                ForEach(AtmospherePropKind.Campfire, t =>
                {
                    if (t != null && t.TryGetComponent(out FireController fire))
                        fire.Ignite();
                });
            }
        }

        public void Register(AtmospherePropKind kind, Transform t)
        {
            if (t == null)
                return;
            if (!_entries.TryGetValue(kind, out List<Transform> list))
            {
                list = new List<Transform>(8);
                _entries[kind] = list;
            }

            if (!list.Contains(t))
                list.Add(t);
        }

        public void RegisterByName(Transform t)
        {
            if (t == null)
                return;
            string n = t.name;
            if (n == "FX_Day")
            {
                Register(AtmospherePropKind.DayRoot, t);
                return;
            }

            if (n == "FX_Night")
            {
                Register(AtmospherePropKind.NightRoot, t);
                return;
            }

            if (n.IndexOf("FX_SunBeam", System.StringComparison.OrdinalIgnoreCase) >= 0)
                Register(AtmospherePropKind.SunBeam, t);
            else if (n.IndexOf("FX_Butterflies", System.StringComparison.OrdinalIgnoreCase) >= 0)
                Register(AtmospherePropKind.Butterflies, t);
            else if (n.IndexOf("FX_Fireflies", System.StringComparison.OrdinalIgnoreCase) >= 0)
                Register(AtmospherePropKind.Fireflies, t);
            else if (n.IndexOf("Huodui", System.StringComparison.OrdinalIgnoreCase) >= 0
                     || n.IndexOf("Campfire", System.StringComparison.OrdinalIgnoreCase) >= 0)
                Register(AtmospherePropKind.Campfire, t);
        }

        public void ForEach(AtmospherePropKind kind, System.Action<Transform> action)
        {
            if (action == null || !_entries.TryGetValue(kind, out List<Transform> list))
                return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Transform t = list[i];
                if (t == null)
                {
                    list.RemoveAt(i);
                    continue;
                }

                action(t);
            }
        }

        public void SetActive(AtmospherePropKind kind, bool active)
        {
            ForEach(kind, t =>
            {
                if (t.gameObject.activeSelf != active)
                    t.gameObject.SetActive(active);
            });
        }

        public void SetSuppressIgnite(bool suppress)
        {
            if (_suppressIgnite == suppress)
                return;
            _suppressIgnite = suppress;
            if (GameEntry.Event != null)
                GameEntry.Event.Fire(null, CampfireSuppressChangedEventArgs.Create(suppress));
        }

        public void SetVisualIgnited(EntityId controllerEntityId, bool ignited)
        {
            bool was = IsVisuallyIgnited;
            if (ignited)
                _visualIgnited.Add(controllerEntityId);
            else
                _visualIgnited.Remove(controllerEntityId);
            if (was != IsVisuallyIgnited && GameEntry.Event != null)
                GameEntry.Event.Fire(null, CampfireVisualIgnitedChangedEventArgs.Create(IsVisuallyIgnited));
        }

        public void ClearVisualIgnited()
        {
            if (_visualIgnited.Count == 0)
                return;
            _visualIgnited.Clear();
            if (GameEntry.Event != null)
                GameEntry.Event.Fire(null, CampfireVisualIgnitedChangedEventArgs.Create(false));
        }

        /// <summary>加载失败或离岛：丢掉节点引用，并熄灭篝火门闩。</summary>
        public void ClearRuntime()
        {
            _entries.Clear();
            ClearVisualIgnited();
            SetSuppressIgnite(true);
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (GameEntry.AtmosphereProps == this)
                GameEntry.AtmosphereProps = null;
            Unsubscribe();
            ClearRuntime();
        }
    }
}
