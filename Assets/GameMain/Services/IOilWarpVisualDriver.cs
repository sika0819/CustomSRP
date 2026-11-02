namespace Game
{
    /// <summary>
    /// Assembly-CSharp 的 OilWarpDriver 实现。登记在 IslandSceneComponent 上，
    /// Game 不直接引用渲染程序集。沉浸已不再使用全屏扭曲；接口保留为空操作。
    /// </summary>
    public interface IOilWarpVisualDriver
    {
        /// <summary>No-op：沉浸不再驱动 UV 扭曲。</summary>
        void SetImmersiveWarp(float breath01, float weight01, float phaseRadians);
    }
}
