namespace Game
{
    /// <summary>
    /// Implemented by Assembly-CSharp OilWarpDriver so Game can drive immersive
    /// oil UV warp without an asmdef cycle.
    /// </summary>
    public interface IOilWarpVisualDriver
    {
        /// <param name="breath01">IslandBreathHint envelope 0..1.</param>
        /// <param name="weight01">Immersive fade 0..1.</param>
        /// <param name="phaseRadians">Breath phase for flow animation.</param>
        void SetImmersiveWarp(float breath01, float weight01, float phaseRadians);
    }

    public static class OilWarpVisual
    {
        public static IOilWarpVisualDriver Driver { get; set; }

        public static void SetImmersiveWarp(float breath01, float weight01, float phaseRadians = 0f)
        {
            Driver?.SetImmersiveWarp(breath01, weight01, phaseRadians);
        }
    }
}
