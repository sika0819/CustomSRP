using System.Collections.Generic;
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
    /// Spawn 时注册氛围节点，避免 AtmospherePropService 全场景 Find。
    /// </summary>
    public static class AtmospherePropRegistry
    {
        static readonly Dictionary<AtmospherePropKind, List<Transform>> Entries =
            new Dictionary<AtmospherePropKind, List<Transform>>(8);

        public static int Count
        {
            get
            {
                int n = 0;
                foreach (var kv in Entries)
                    n += kv.Value.Count;
                return n;
            }
        }

        public static void Register(AtmospherePropKind kind, Transform t)
        {
            if (t == null)
                return;
            if (!Entries.TryGetValue(kind, out List<Transform> list))
            {
                list = new List<Transform>(8);
                Entries[kind] = list;
            }

            if (!list.Contains(t))
                list.Add(t);
        }

        public static void Unregister(Transform t)
        {
            if (t == null)
                return;
            foreach (var kv in Entries)
                kv.Value.Remove(t);
        }

        public static void Clear()
        {
            Entries.Clear();
        }

        public static void ForEach(AtmospherePropKind kind, System.Action<Transform> action)
        {
            if (action == null || !Entries.TryGetValue(kind, out List<Transform> list))
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

        public static void SetActive(AtmospherePropKind kind, bool active)
        {
            ForEach(kind, t =>
            {
                if (t.gameObject.activeSelf != active)
                    t.gameObject.SetActive(active);
            });
        }

        /// <summary>按实例名推断并注册（MapItem extras 生成后调用）。</summary>
        public static void RegisterByName(Transform t)
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
    }
}
