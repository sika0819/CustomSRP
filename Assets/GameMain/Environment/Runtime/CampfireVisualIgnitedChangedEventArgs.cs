using GameFramework;
using GameFramework.Event;

namespace Game
{
    /// <summary>
    /// 场景中是否仍有视觉上点燃的篝火。
    /// </summary>
    public sealed class CampfireVisualIgnitedChangedEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(CampfireVisualIgnitedChangedEventArgs).GetHashCode();

        public override int Id => EventId;

        public bool IsVisuallyIgnited { get; private set; }

        public static CampfireVisualIgnitedChangedEventArgs Create(bool isVisuallyIgnited)
        {
            CampfireVisualIgnitedChangedEventArgs e =
                ReferencePool.Acquire<CampfireVisualIgnitedChangedEventArgs>();
            e.IsVisuallyIgnited = isVisuallyIgnited;
            return e;
        }

        public override void Clear() => IsVisuallyIgnited = false;
    }
}
