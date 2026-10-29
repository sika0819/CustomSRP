namespace Game
{
    /// <summary>
    /// Assembly-CSharp 的 OilWarpDriver 实现。登记在 IslandSceneComponent 上，
    /// Game 不直接引用渲染程序集。
    /// </summary>
    public interface IOilWarpVisualDriver
    {
        /// <param name="breath01">IslandBreathHint envelope 0..1.</param>
        /// <param name="weight01">Immersive fade 0..1.</param>
        /// <param name="phaseRadians">Breath phase for flow animation.</param>
        void SetImmersiveWarp(float breath01, float weight01, float phaseRadians);
    }
}
