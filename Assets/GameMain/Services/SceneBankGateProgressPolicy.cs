namespace Game
{
    /// <summary>
    /// 区域门进度窗开/丢/再开判定（纯逻辑，EditMode 可测）。
    /// </summary>
    public static class SceneBankGateProgressPolicy
    {
        /// <summary>
        /// 是否先弹「前方区域未下载」。
        /// 传 <c>silentBackgroundRest=true</c>（后台弹窗关）→ 否：直接门禁 + 进度窗并优先插队。
        /// FeatureOn 请用 <see cref="BackgroundDownloadPolicy.ShouldShowGateRequestFirst"/>。
        /// </summary>
        public static bool ShouldShowRequestFirst(bool silentBackgroundRest) =>
            !silentBackgroundRest;

        /// <summary>
        /// 本地认为进度开着，但票对应窗与任意进度窗都已不在，且没有其它弹窗 → 应清 open 标记以便再开。
        /// </summary>
        public static bool ShouldClearOrphanedProgressOpen(
            bool progressOpenFlag,
            bool uiOpenForTicket,
            bool anyActiveProgressForm,
            bool hasOtherPopup)
        {
            if (!progressOpenFlag)
                return false;
            if (uiOpenForTicket || anyActiveProgressForm || hasOtherPopup)
                return false;
            return true;
        }

        /// <summary>
        /// 下载仍在跑、人在门里、进度 UI 不在时是否应 Reopen（忽略 PromptLatched）。
        /// </summary>
        public static bool ShouldAttemptReopen(
            bool playerInsideBlockedGate,
            bool progressOpenFlag,
            bool uiOpenForTicket,
            bool ticketValidAndNotRetired,
            bool hasOtherPopup)
        {
            if (!playerInsideBlockedGate || !ticketValidAndNotRetired)
                return false;
            if (uiOpenForTicket || progressOpenFlag || hasOtherPopup)
                return false;
            return true;
        }

        /// <summary>
        /// LateUpdate：走「重开进度」还是「新门禁」。
        /// Flow 正忙但 SuppressProgress（无票）时必须走新门禁，否则门口永远不弹窗。
        /// </summary>
        public static bool ShouldPreferReopenOverNewPrompt(
            bool spawnerBusy,
            bool flowBusy,
            int activeProgressTicket)
        {
            if (spawnerBusy)
                return true;
            return flowBusy && activeProgressTicket > 0;
        }

        /// <summary>
        /// Spawner 占 busy、同意链/Flow 已空闲、无进度票、无其它弹窗 →
        /// 清 orphan busy，否则 PreferReopen 一直空转、再进门不出进度
        /// （流量切 Wi‑Fi 后 §4.6 关窗与 Progress Open 竞态时会踩到）。
        /// 同意链 phase 非 Idle 时禁止清（异步 Open 申请/§4.6 时 HasPopup 可能仍为 false）。
        /// </summary>
        public static bool ShouldClearOrphanedSpawnerBusy(
            bool spawnerBusy,
            bool flowBusy,
            int activeProgressTicket,
            bool hasOtherPopup,
            bool consentPromptActive = false)
        {
            if (!spawnerBusy || flowBusy || hasOtherPopup || consentPromptActive)
                return false;
            return activeProgressTicket <= 0;
        }

        /// <summary>
        /// 关票 B 不得使票 A 退役（旧高水位缺陷）。
        /// </summary>
        public static bool ClosingShouldRetireOnlySelf(int closedTicket, int otherTicket)
        {
            if (closedTicket <= 0 || otherTicket <= 0)
                return true;
            return closedTicket != otherTicket;
        }

        /// <summary>
        /// 区域门固墙：仅未齐时挡；盘齐后即使 Spawner/Flow 仍 busy（MD5 收尾）也放行。
        /// </summary>
        public static bool ShouldForceBlockWhileBusy(bool spawnerBusyDownloadingGate, bool banksSatisfied) =>
            spawnerBusyDownloadingGate && !banksSatisfied;

        /// <summary>
        /// 传输完成能否立刻把进度票标成结束。
        /// 百分比还没套到可见窗上，或套上后还没留出可见时间（上滑未结束 / 同一帧）时不能退役，
        /// 否则晚到的 OnOpen 会在画出百分比之前把窗丢掉。用户点关闭不走这里。
        /// </summary>
        public static bool ShouldRetireOnTransferComplete(
            bool progressPresentedOnForm,
            bool visibleLongEnough) =>
            progressPresentedOnForm && visibleLongEnough;
    }
}
