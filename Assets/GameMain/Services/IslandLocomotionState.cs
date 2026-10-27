namespace Game
{
    /// <summary>
    /// Cross-assembly locomotion hints for CustomSRP (Assembly-CSharp can read Game).
    /// Set by <see cref="IslandJoystickCamera"/> each LateUpdate.
    /// </summary>
    public static class IslandLocomotionState
    {
        /// <summary>True while the stick is driving forward/back beyond the walk threshold.</summary>
        public static bool IsWalking { get; set; }

        /// <summary>Main camera CRP buffer size after the last render (SRP writes this).</summary>
        public static int BufferWidth { get; set; }

        public static int BufferHeight { get; set; }

        /// <summary>True if OilPlanarReflection actually re-rendered the mirror this frame.</summary>
        public static bool MirrorRenderedThisFrame { get; set; }
    }
}
