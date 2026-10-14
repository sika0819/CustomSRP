namespace Game.Native
{
    /// <summary>IMP 错误码（与原生约定，冻结）。</summary>
    public static class BridgeCode
    {
        public const int Ok = 0;
        public const int ErrGeneric = -1;
        public const int ErrNotReady = -2;
        public const int ErrUnsupported = -3;
        public const int ErrBadArgs = -4;
        public const int ErrBufferTooSmall = -5;
        public const int ErrTimeout = -6;
    }
}
