using GameFramework;
using GameFramework.Event;

namespace Game
{
    public sealed class IslandWorldReadyEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(IslandWorldReadyEventArgs).GetHashCode();

        public override int Id => EventId;

        public int MapItemCount { get; private set; }
        public int SpawnedCount { get; private set; }
        public int ExtrasCount { get; private set; }

        public static IslandWorldReadyEventArgs Create(int mapItemCount, int spawnedCount, int extrasCount)
        {
            IslandWorldReadyEventArgs e = ReferencePool.Acquire<IslandWorldReadyEventArgs>();
            e.MapItemCount = mapItemCount;
            e.SpawnedCount = spawnedCount;
            e.ExtrasCount = extrasCount;
            return e;
        }

        public override void Clear()
        {
            MapItemCount = 0;
            SpawnedCount = 0;
            ExtrasCount = 0;
        }
    }
}
