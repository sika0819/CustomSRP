namespace Game
{
    public enum DeviceGraphicsClass
    {
        Low = 0,
        Mid = 1,
        High = 2,
        Gmj = 3,
    }

    /// <summary>
    /// Graphics tier hint for CustomSRP mobile clamps. Template defaults to High.
    /// </summary>
    public static class DeviceGraphicsPolicy
    {
        public static DeviceGraphicsClass Current { get; set; } = DeviceGraphicsClass.High;
    }
}
