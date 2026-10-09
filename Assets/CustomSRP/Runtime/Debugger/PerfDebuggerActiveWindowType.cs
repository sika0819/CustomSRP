namespace CustomSRP.Debugger
{
    public enum PerfDebuggerActiveWindowType : byte
    {
        AlwaysOpen = 0,
        OnlyOpenWhenDevelopment = 1,
        OnlyOpenInEditor = 2,
        AlwaysClose = 3,
    }
}
