using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 篝火政策门闩 + 视觉点燃集合。状态变更经 <see cref="GameEntry.Event"/> 分发。
    /// </summary>
    public static class CampfireWeatherGate
    {
        public static bool SuppressIgnite { get; private set; } = true;

        public static bool IsVisuallyIgnited => VisualIgnitedIds.Count > 0;

        static readonly HashSet<EntityId> VisualIgnitedIds = new HashSet<EntityId>();

        public static void SetSuppressIgnite(bool suppress)
        {
            if (SuppressIgnite == suppress)
                return;
            SuppressIgnite = suppress;
            if (GameEntry.Event != null)
                GameEntry.Event.Fire(null, CampfireSuppressChangedEventArgs.Create(suppress));
        }

        public static void SetVisualIgnited(EntityId controllerEntityId, bool ignited)
        {
            bool was = IsVisuallyIgnited;
            if (ignited)
                VisualIgnitedIds.Add(controllerEntityId);
            else
                VisualIgnitedIds.Remove(controllerEntityId);
            if (was != IsVisuallyIgnited)
                FireVisualChanged();
        }

        public static void ClearVisualIgnited()
        {
            if (VisualIgnitedIds.Count == 0)
                return;
            VisualIgnitedIds.Clear();
            FireVisualChanged();
        }

        static void FireVisualChanged()
        {
            if (GameEntry.Event != null)
                GameEntry.Event.Fire(null, CampfireVisualIgnitedChangedEventArgs.Create(IsVisuallyIgnited));
        }
    }
}
