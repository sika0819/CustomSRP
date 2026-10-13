using GameFramework;
using GameFramework.Event;

namespace Game
{
    /// <summary>
    /// Island HUD should run black veil → apply → reveal → show description.
    /// </summary>
    public sealed class PresetTransitionRequestEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(PresetTransitionRequestEventArgs).GetHashCode();

        public int PresetId { get; private set; }

        public override int Id => EventId;

        public static PresetTransitionRequestEventArgs Create(int presetId)
        {
            PresetTransitionRequestEventArgs e = ReferencePool.Acquire<PresetTransitionRequestEventArgs>();
            e.PresetId = presetId;
            return e;
        }

        public override void Clear()
        {
            PresetId = 0;
        }
    }
}
