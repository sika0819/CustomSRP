using GameFramework;
using GameFramework.Event;

namespace Game.Native
{
    /// <summary>
    /// N→U: 原生健康页选了压力等级（0 放松 / 1 轻度 / 2 中度 / 3 偏高 / 4 高压）。
    /// <see cref="Game.HealthRecommendPresetBridge"/> 在智慧推荐开时消费并切预设。
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
