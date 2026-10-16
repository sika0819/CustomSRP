namespace Game
{
    /// <summary>
    /// 1 单曲循环 / 2 列表循环 / 3 随机。对齐 Boluo MusicPlayMode。
    /// </summary>
    public static class MusicPlayMode
    {
        public const int Single = 1;
        public const int Loop = 2;
        public const int Shuffle = 3;

        public static int Next(int mode) => mode switch
        {
            Single => Loop,
            Loop => Shuffle,
            _ => Single,
        };

        public static string TipText(int mode) => mode switch
        {
            Single => "单曲循环",
            Loop => "列表循环",
            Shuffle => "随机播放",
            _ => "单曲循环",
        };
    }
}
