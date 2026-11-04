using Game.BoluoApi;

namespace Game
{
    /// <summary>
    /// 预设 bank 是否「目录上不可下」：无 catalog 条目或 url 空。
    /// 与可下缺盘区分，避免 UI 假 NotDownloaded / 空点 Skipped。
    /// </summary>
    public static class PresetBankCatalogGaps
    {
        /// <summary>盘上未就绪，且 catalog 无条目或 url 空 → 不可 HTTP。</summary>
        public static bool IsUndownloadable(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            if (BankDownloader.IsLocalReady(bankName))
                return false;
            if (!PresetBankCatalog.TryGet(bankName, out BankRemoteInfo remote) || remote == null)
                return true;
            return string.IsNullOrEmpty(remote.url);
        }
    }
}
