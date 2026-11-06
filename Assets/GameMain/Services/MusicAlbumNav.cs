namespace Game
{
    /// <summary>
    /// 本专辑顺序切歌纯函数（负一手动 SkipAdjacent；与 PlayMode 无关）。
    /// </summary>
    public static class MusicAlbumNav
    {
        /// <summary>
        /// 本专辑顺序 ±1，越界取模。
        /// <paramref name="trackCount"/> ≤ 0 → -1；仅 1 首 → 0（重播）。
        /// </summary>
        public static int ResolveAdjacentIndex(int trackCount, int currentIndex, int direction)
        {
            if (trackCount <= 0)
                return -1;
            if (trackCount == 1)
                return 0;
            if (direction == 0)
                direction = 1;

            int cur = currentIndex;
            if (cur < 0 || cur >= trackCount)
                return direction > 0 ? 0 : trackCount - 1;

            return ((cur + direction) % trackCount + trackCount) % trackCount;
        }

        /// <summary>
        /// 负一手动切歌专用：邻接取模；未就绪 → 当前（重播）；已就绪 → 邻接。
        /// 音乐列表点下/切歌不要用本方法。
        /// </summary>
        /// <param name="isTrackReady">下标 → 是否可播（盘上就绪）。null 视为全部就绪。</param>
        public static int ResolveManualSkipIndex(
            int trackCount,
            int currentIndex,
            int direction,
            System.Func<int, bool> isTrackReady)
        {
            int next = ResolveAdjacentIndex(trackCount, currentIndex, direction);
            if (next < 0)
                return -1;
            if (next == currentIndex)
                return next;
            if (isTrackReady != null && !isTrackReady(next))
            {
                if (currentIndex >= 0 && currentIndex < trackCount)
                    return currentIndex;
                return next;
            }
            return next;
        }
    }
}
