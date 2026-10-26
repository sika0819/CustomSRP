namespace Game
{
    /// <summary>
    /// Coarse low-end gate for preset transition FX (walk during exit, black-hold length).
    /// Delegates to <see cref="DeviceGraphicsPolicy"/> (<see cref="DeviceGraphicsClass.Low"/>).
    /// </summary>
    public static class IslandGraphicsTier
    {
        /// <summary>
        /// Phone Low tier — skip exit walk, shorten black hold.
        /// Editor / desktop High / Mid / Gmj are not low-end.
        /// </summary>
        public static bool IsLowEnd =>
            DeviceGraphicsPolicy.Current == DeviceGraphicsClass.Low;
    }
}
