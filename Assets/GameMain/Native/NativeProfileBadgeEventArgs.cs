using GameFramework;
using GameFramework.Event;

namespace Game.Native
{
    /// <summary>
    /// N→U: 原生侧要 Unity 显/隐 HUD「我的」红点（Imp 102）。
    /// iOS：未绑 Apple ID → Visible；Android：恒 false。由 IslandMainForm 消费。
    /// </summary>
    public sealed class NativeProfileBadgeEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(NativeProfileBadgeEventArgs).GetHashCode();
        public override int Id => EventId;

        public bool Visible { get; private set; }

        public static NativeProfileBadgeEventArgs Create(bool visible)
        {
            var e = ReferencePool.Acquire<NativeProfileBadgeEventArgs>();
            e.Visible = visible;
            return e;
        }

        public override void Clear()
        {
            Visible = false;
        }
    }
}
