namespace Game.Native
{
    /// <summary>IMP MsgType（只追加不改语义；与 Android/iOS 数值对齐）。</summary>
    public static class MsgType
    {
        public const int ProtocolHandshake = 0;
        public const int UiHudIcon = 1;
        public const int EngineIslandReady = 2;
        public const int SessionGetToken = 20;
        /// <summary>U→N：宿主身份。载荷 <c>channel\\toriginEnd\\tappId\\tenv\\tdeviceId</c>。</summary>
        public const int HostGetIdentity = 21;
        public const int UiOverlay = 100;
        public const int HealthPressureLevel = 101;
        public const int UiProfileBadge = 102;
        /// <summary>N→U 定时进度：a=剩余秒 b=满圈基准(3600)；0,0 取消；0,-1 到点结束。</summary>
        public const int TimerProgress = 103;
        /// <summary>N→U 锁屏/负一屏：a=0 暂停 / 1 播放 / 2 下一首 / 3 上一首。</summary>
        public const int MediaCommand = 104;
        /// <summary>U→N Query 正在播放：a=playing 0/1，b=专辑，c=曲目，inBlob=title\talbum。</summary>
        public const int NowPlaying = 105;

        public const int MediaPause = 0;
        public const int MediaPlay = 1;
        public const int MediaNext = 2;
        public const int MediaPrev = 3;

        public const int HudIconProfile = 1;
        public const int HudIconTimer = 2;
        public const int HudIconHealth = 3;

        public const int TimerRingSeconds = 3600;

        public const int TokenKindDevice = 1;
        public const int TokenKindUser = 2;
        public const int FlagForceRefresh = 1;
    }
}
