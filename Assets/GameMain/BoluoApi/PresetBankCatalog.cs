using System;
using System.Collections.Generic;
using System.IO;

namespace Game.BoluoApi
{
    /// <summary>缓存 upgrade/bank 返回的 banks。</summary>
    public static class PresetBankCatalog
    {
        static readonly Dictionary<string, BankRemoteInfo> ByName =
            new Dictionary<string, BankRemoteInfo>(StringComparer.OrdinalIgnoreCase);

        public static bool HasAny => ByName.Count > 0;

        public static void Clear() => ByName.Clear();

        public static bool TryApplyUpgrade(BankUpgradeData data)
        {
            if (data?.banks == null || data.banks.Count == 0)
                return false;
            SetFromUpgrade(data);
            return true;
        }

        public static void SetFromUpgrade(BankUpgradeData data)
        {
            ByName.Clear();
            if (data?.banks == null)
                return;
            foreach (var b in data.banks)
            {
                if (b == null || string.IsNullOrEmpty(b.bankName))
                    continue;
                b.bankName = AudioEnginePaths.StripBankExtension(b.bankName);
                if (string.IsNullOrEmpty(b.bankName))
                    continue;
                ByName[b.bankName] = b;
            }
        }

        public static int SetFromLocalStreaming()
        {
            ByName.Clear();
            try
            {
                AddLocalFiles(AudioEnginePaths.StreamingBankPath, "*" + AudioEnginePaths.BankExtension);
                AddLocalFiles(AudioEnginePaths.StreamingBankPath, "*" + AudioEnginePaths.LegacyBankExtension);
                AddLocalFiles(AudioEnginePaths.PersistentBankPath, "*" + AudioEnginePaths.BankExtension);
                AddLocalFiles(AudioEnginePaths.PersistentBankPath, "*" + AudioEnginePaths.LegacyBankExtension);
            }
            catch
            {
                return ByName.Count;
            }
            return ByName.Count;
        }

        static void AddLocalFiles(string dir, string pattern)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            string[] files = Directory.GetFiles(dir, pattern);
            for (int i = 0; i < files.Length; i++)
            {
                string name = AudioEnginePaths.StripBankExtension(Path.GetFileName(files[i]));
                if (string.IsNullOrEmpty(name))
                    continue;
                ByName[name] = new BankRemoteInfo { bankName = name };
            }
        }

        public static bool TryGet(string bankName, out BankRemoteInfo info) =>
            ByName.TryGetValue(bankName ?? "", out info);

        public static List<BankRemoteInfo> ResolveRemotes(IList<string> bankNames)
        {
            var list = new List<BankRemoteInfo>();
            if (bankNames == null)
                return list;
            for (int i = 0; i < bankNames.Count; i++)
            {
                string name = bankNames[i];
                if (string.IsNullOrEmpty(name))
                    continue;
                if (TryGet(name, out var info))
                    list.Add(info);
                else
                {
                    list.Add(new BankRemoteInfo
                    {
                        bankName = name,
                        url = "",
                        md5 = "",
                    });
                }
            }
            return list;
        }

        public static List<BankRemoteInfo> AllRemotes()
        {
            return new List<BankRemoteInfo>(ByName.Values);
        }
    }
}
