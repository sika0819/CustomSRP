using GameFramework;
using GameFramework.Event;

namespace Game.Native
{
    /// <summary>
    /// N→U: 原生健康页选了压力等级（0 放松 / 1 轻度 / 2 中度 / 3 偏高 / 4 高压）。
    /// 当前业务未直接消费；保留 EventArgs 以便 UI / 适配层后续 Subscribe。
    /// </summary>
    public sealed class NativeHealthPressureEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(NativeHealthPressureEventArgs).GetHashCode();
        public override int Id => EventId;

        public int Level { get; private set; }

        public static NativeHealthPressureEventArgs Create(int level)
        {
            var e = ReferencePool.Acquire<NativeHealthPressureEventArgs>();
            e.Level = level;
            return e;
        }

        public override void Clear()
        {
            Level = 0;
        }
    }
}
