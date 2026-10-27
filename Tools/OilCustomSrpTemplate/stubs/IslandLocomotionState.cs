namespace Game
{
    /// <summary>
    /// Locomotion / buffer hints read by CustomSRP. Template stub — set from your camera if needed.
    /// </summary>
    public static class IslandLocomotionState
    {
        public static bool IsWalking { get; set; }
        public static int BufferWidth { get; set; }
        public static int BufferHeight { get; set; }
        public static bool MirrorRenderedThisFrame { get; set; }
    }
}
