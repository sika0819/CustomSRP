using GameFramework;
using GameFramework.Event;

namespace Game
{
    /// <summary>
    /// 篝火点燃政策门闩变更（雨雾/白天抑制点燃）。
    /// </summary>
    public sealed class CampfireSuppressChangedEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(CampfireSuppressChangedEventArgs).GetHashCode();

        public override int Id => EventId;

        public bool Suppress { get; private set; }

        public static CampfireSuppressChangedEventArgs Create(bool suppress)
        {
            CampfireSuppressChangedEventArgs e = ReferencePool.Acquire<CampfireSuppressChangedEventArgs>();
            e.Suppress = suppress;
            return e;
        }

        public override void Clear() => Suppress = false;
    }
}
