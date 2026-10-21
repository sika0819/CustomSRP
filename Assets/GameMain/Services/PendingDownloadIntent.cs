namespace Game
{
    /// <summary>
    /// Shared pending intent after the user taps an unready preset / music row.
    /// Preset and music overwrite each other (Boluo PendingDownloadIntent).
    /// </summary>
    public static class PendingDownloadIntent
    {
        public enum Kind
        {
            None = 0,
            Preset = 1,
            Music = 2,
        }

        public static Kind Current { get; private set; }
        public static int PresetId { get; private set; }
        public static int MusicAlbum { get; private set; }
        public static int MusicTrack { get; private set; }

        public static void SetPreset(int presetId)
        {
            Current = Kind.Preset;
            PresetId = presetId;
            MusicAlbum = 0;
            MusicTrack = 0;
        }

        public static void SetMusic(int album, int track)
        {
            Current = Kind.Music;
            MusicAlbum = album;
            MusicTrack = track;
            PresetId = 0;
        }

        public static void Clear()
        {
            Current = Kind.None;
            PresetId = 0;
            MusicAlbum = 0;
            MusicTrack = 0;
        }

        public static bool IsPresetMatch(int presetId) =>
            Current == Kind.Preset && PresetId == presetId;

        public static bool IsMusicMatch(int album, int track) =>
            Current == Kind.Music && MusicAlbum == album && MusicTrack == track;
    }
}
