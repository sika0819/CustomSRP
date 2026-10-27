using System;

namespace Game
{
    /// <summary>
    /// Minimal world hook used by OilShoreWetDriver. Template stub — fire the event after water spawns.
    /// </summary>
    public static class IslandWorldService
    {
        public static event Action<bool> OnInlandWaterReady;

        public static bool InlandWaterReady { get; private set; }

        public static void NotifyInlandWaterReady(bool hasWater)
        {
            InlandWaterReady = hasWater;
            OnInlandWaterReady?.Invoke(hasWater);
        }
    }
}
