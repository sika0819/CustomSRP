using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game.BoluoApi;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 下载体积与剩余存储（纯计算）。
    /// 用户下载弹窗请走 <see cref="UserBankDownloadFlow"/> /
    /// <see cref="DownloadConsentSession.EnsureReady"/>。
    /// </summary>
    public static class DownloadNetworkGate
    {
        public static string FormatSizeMb(IList<string> bankNames)
        {
            long kb = PendingKilobytes(bankNames);
            if (kb <= 0)
                return "若干";
            float mb = kb / 1024f;
            return mb.ToString("0.#", CultureInfo.InvariantCulture);
        }

        public static long PendingKilobytes(IList<string> bankNames)
        {
            if (bankNames == null)
                return 0;
            long kb = 0;
            for (int i = 0; i < bankNames.Count; i++)
            {
                string name = bankNames[i];
                if (string.IsNullOrEmpty(name) || BankDownloader.IsLocalReady(name))
                    continue;
                if (PresetBankCatalog.TryGet(name, out BankRemoteInfo remote)
                    && remote != null
                    && long.TryParse(remote.size, out long parsed)
                    && parsed > 0)
                    kb += parsed;
            }

            return kb;
        }

        public static long AvailableStorageBytes()
        {
            try
            {
                string path = Application.persistentDataPath;
                var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
                if (drive.IsReady)
                    return drive.AvailableFreeSpace;
            }
            catch
            {
                // 读不到时不拦。
            }

            return long.MaxValue;
        }
    }
}
