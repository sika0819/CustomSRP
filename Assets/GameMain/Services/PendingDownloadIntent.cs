namespace Game
{
    /// <summary>
    /// 用户点未就绪预设 / 音乐，或 iOS 健康智慧推荐后的「下完做什么」。
    /// 预设与音乐互斥覆盖。静默 KickSilentAll 不得写入本意图。
    /// </summary>
    public static class PendingDownloadIntent
    {
        public enum Kind
        {
            None = 0,
            Preset = 1,
            Music = 2,
        }

        /// <summary>谁授权「下完后切预设」。静默下载完成时 Source=None → 禁止自动切。</summary>
        public enum EnterSource
        {
            None = 0,
            /// <summary>用户点了预设卡。</summary>
            UserClick = 1,
            /// <summary>iOS 健康智慧推荐 / 压力档联动。</summary>
            HealthRecommend = 2,
        }

        public static Kind Current { get; private set; }
        public static int PresetId { get; private set; }
        public static int MusicAlbum { get; private set; }
        public static int MusicTrack { get; private set; }
        public static EnterSource PresetEnterSource { get; private set; }

        public static void SetPreset(int presetId, EnterSource enterSource = EnterSource.UserClick)
        {
            Current = Kind.Preset;
            PresetId = presetId;
            MusicAlbum = 0;
            MusicTrack = 0;
            PresetEnterSource = enterSource;
        }

        public static void SetMusic(int album, int track)
        {
            Current = Kind.Music;
            MusicAlbum = album;
            MusicTrack = track;
            PresetId = 0;
            PresetEnterSource = EnterSource.None;
        }

        public static void Clear()
        {
            Current = Kind.None;
            PresetId = 0;
            MusicAlbum = 0;
            MusicTrack = 0;
            PresetEnterSource = EnterSource.None;
        }

        public static bool IsPresetMatch(int presetId) =>
            Current == Kind.Preset && PresetId == presetId;

        public static bool IsMusicMatch(int album, int track) =>
            Current == Kind.Music && MusicAlbum == album && MusicTrack == track;

        /// <summary>
        /// 下完是否自动切预设：须 Intent 命中且来源为用户点 / 健康推荐。
        /// 静默 KickSilentAll 落盘完成 → false。
        /// </summary>
        public static bool ShouldAutoEnterPreset(int presetId) =>
            IsPresetMatch(presetId)
            && (PresetEnterSource == EnterSource.UserClick
                || PresetEnterSource == EnterSource.HealthRecommend);
    }
}
