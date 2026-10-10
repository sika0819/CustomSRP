using GameFramework;
using GameFramework.Event;

namespace Game
{
    public sealed class EnterIslandEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(EnterIslandEventArgs).GetHashCode();

        public override int Id => EventId;

        public static EnterIslandEventArgs Create()
        {
            return ReferencePool.Acquire<EnterIslandEventArgs>();
        }

        public override void Clear()
        {
        }
    }
}
