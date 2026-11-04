namespace Game
{
    /// <summary>
    /// 贴河/瀑/火时 bank 优先策略（纯函数，供 <see cref="AmbienceZoneService"/> 与单测）。
    /// </summary>
    public static class AmbienceProximityBankPolicy
    {
        public enum Action
        {
            None,
            LoadLocal,
            DownloadAndLoad,
        }

        public static bool IsPriorityKind(AmbienceEmitterKind kind)
        {
            return kind == AmbienceEmitterKind.River
                || kind == AmbienceEmitterKind.Waterfall
                || kind == AmbienceEmitterKind.Campfire;
        }

        /// <summary>
        /// 已在引擎 → None；盘上可解析 → LoadLocal；否则 DownloadAndLoad。
        /// </summary>
        public static Action Resolve(bool isPriorityKind, bool engineLoaded, bool canResolveOrLocalReady)
        {
            if (!isPriorityKind)
                return Action.None;
            if (engineLoaded)
                return Action.None;
            if (canResolveOrLocalReady)
                return Action.LoadLocal;
            return Action.DownloadAndLoad;
        }
    }
}
