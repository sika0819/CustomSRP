namespace Game
{
    /// <summary>
    /// 后台不提交 Painter2D Fill。只决定画不画均衡器条，不改帧率、不改音频闸。
    /// </summary>
    public static class BackgroundVectorUi
    {
        public static bool ShouldAnimateVectorUi(bool inBackground) => !inBackground;
    }
}
