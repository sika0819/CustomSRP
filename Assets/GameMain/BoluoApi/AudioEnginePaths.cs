using System.IO;
using UnityEngine;

namespace Game.BoluoApi
{
    /// <summary>Wanos bank 落盘 / 解析路径（GameMain → WanosBankStore）。</summary>
    public static class AudioEnginePaths
    {
        public const string BankFolderName = "BoluoAudioBanks";
        public const string BankExtension = ".wsbk";
        public const string LegacyBankExtension = ".wb";

        public static string PersistentBankPath =>
            Path.Combine(Application.persistentDataPath, BankFolderName);

        public static string StreamingBankPath =>
            Path.Combine(Application.streamingAssetsPath, BankFolderName);

        /// <summary>
        /// 备选目录：<c>StreamingAssets/WanosBank/</c>。保留旧工程目录布局的兼容性。
        /// 银行文件 (.wb / .wsbk) 可能由 Wanos 工具链直接放这里（不经过 BoluoAudioBanks）。
        /// </summary>
        public static string LegacyStreamingBankPath =>
            Path.Combine(Application.streamingAssetsPath, "WanosBank");

        public static void EnsureWritableDirs()
        {
            Directory.CreateDirectory(PersistentBankPath);
        }

        public static string StripBankExtension(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            if (name.EndsWith(BankExtension, System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - BankExtension.Length);
            if (name.EndsWith(LegacyBankExtension, System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - LegacyBankExtension.Length);
            return name;
        }

        public static string ResolveBankPath(string bankName)
        {
            if (TryFindLocalBank(bankName, out string existing))
                return existing;
            return Path.Combine(PersistentBankPath, StripBankExtension(bankName ?? "unknown") + BankExtension);
        }

        public static bool TryFindLocalBank(string bankName, out string path)
        {
            if (TryFindExistingBank(PersistentBankPath, bankName, out path))
                return true;
            if (TryFindExistingBank(StreamingBankPath, bankName, out path))
                return true;
            // 兼容旧工程：Wanos 工具链会把 Common_Init / Common_Music 等基础银行放在
            // StreamingAssets/WanosBank/ 下，这里兜底扫一下。
            return TryFindExistingBank(LegacyStreamingBankPath, bankName, out path);
        }

        static bool TryFindExistingBank(string dir, string bankName, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;
            string stem = StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;

            string[] candidates =
            {
                Path.Combine(dir, stem + BankExtension),
                Path.Combine(dir, stem + LegacyBankExtension),
                Path.Combine(dir, stem),
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    path = candidates[i];
                    return true;
                }
            }
            return false;
        }
    }
}
