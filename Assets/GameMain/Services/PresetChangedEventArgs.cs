using GameFramework;
using GameFramework.Event;

namespace Game
{
    public sealed class PresetChangedEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(PresetChangedEventArgs).GetHashCode();

        public int PresetId { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }

        public override int Id => EventId;

        public static PresetChangedEventArgs Create(IslandPresetAsset preset)
        {
            PresetChangedEventArgs e = ReferencePool.Acquire<PresetChangedEventArgs>();
            e.PresetId = preset != null ? preset.Id : 0;
            e.DisplayName = preset != null ? preset.DisplayName : null;
            e.Description = preset != null ? preset.Description : null;
            return e;
        }

        public override void Clear()
        {
            PresetId = 0;
            DisplayName = null;
            Description = null;
        }
    }
}
