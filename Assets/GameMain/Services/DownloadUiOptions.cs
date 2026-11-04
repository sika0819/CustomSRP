using System;

namespace Game
{
    /// <summary>下载同意会话的 UI 选项（岛门可锁移动、可先弹申请窗）。</summary>
    public struct DownloadUiOptions
    {
        /// <summary>
        /// 同意链期间边沿锁平移。区域门已改为仅后退瞬间由 Spawner 上锁，
        /// 通常传 false（固墙拦住即可，弹窗不冻走）。
        /// </summary>
        public bool LockMoveDuringPrompt;

        /// <summary>先弹「前方区域未下载」申请窗，点立即下载后再跑门禁。</summary>
        public bool ShowRequestFirst;

        /// <summary>申请窗体积文案；空则按 bank 估算。</summary>
        public string RequestSizeMb;

        /// <summary>返回 / 切 Wi‑Fi / 存储确认 / 无网后的收尾（如岛门 EndPromptChain）。</summary>
        public Action OnCancel;

        /// <summary>
        /// 后台 Rest Wi‑Fi 确认窗「稍后」/ 蒙层关闭（不含存储/无网取消）。
        /// 仅关窗后触发：闩本岛不再重弹，可传则静默；未关窗时必须先显示确认窗。
        /// </summary>
        public Action OnWifiConfirmLater;

        /// <summary>
        /// 为 true 时跳过「下载进度」弹窗（预设用卡片环、音乐用行内环）。
        /// 默认 false：区域门仍开进度窗。流量窗仍只由蜂窝 <c>NeedsMobileDataPrompt</c> 决定。
        /// </summary>
        public bool SuppressProgressDialog;
    }
}
