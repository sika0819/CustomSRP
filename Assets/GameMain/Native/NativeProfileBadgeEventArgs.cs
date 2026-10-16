using GameFramework;
using GameFramework.Event;

namespace Game.Native
{
    /// <summary>
    /// N→U: 原生侧要 Unity 显/隐 HUD 上的"我的"红点。
    /// 当前业务未直接消费；保留 EventArgs 以便 UI 层后续 Subscribe。
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
