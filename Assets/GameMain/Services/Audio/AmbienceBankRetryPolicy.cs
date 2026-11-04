namespace Game
{
    /// <summary>
    /// 边走边下 bank：落盘后立刻重评开播的判定（纯逻辑，供 EditMode 测）。
    /// </summary>
    public static class AmbienceBankRetryPolicy
    {
        /// <summary>
        /// listener-still 是否应跳过本 tick。pending bank 已可解析 / 落盘强制窗内不得跳过。
        /// </summary>
        public static bool ShouldSkipListenerStillEval(
            bool listenerMovedEnough,
            bool softStopping,
            bool gainSlewPending,
            bool hasResolvablePendingRetry,
            bool forceEvalWindow = false)
        {
            if (listenerMovedEnough
                || softStopping
                || gainSlewPending
                || hasResolvablePendingRetry
                || forceEvalWindow)
                return false;
            return true;
        }

        /// <summary>
        /// 落盘 / 灌引擎信号是否应对该 bank 触发重评。
        /// 目录环境音 / 已有 source / 曾 Retain 失败均可——禁止「必须先 Retain 失败」才重评
        /// （人在圈外下完再走进、或 pending 被 Prune 清掉后会永远卡住）。
        /// </summary>
        public static bool ShouldHandleAmbienceBankReady(
            bool isAmbienceCatalogBank,
            bool anySourceUsesBank,
            bool pendingContainsBank)
            => isAmbienceCatalogBank || anySourceUsesBank || pendingContainsBank;

        /// <summary>落盘 / 灌引擎信号是否应对该 bank 触发重评（须已在 pending）。</summary>
        public static bool ShouldRetryOnBankSignal(bool pendingContainsBank)
            => pendingContainsBank;
    }
}
