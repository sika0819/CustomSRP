using System.IO;
using UnityEngine;

namespace Game.BoluoApi
{
    /// <summary>Bank 落盘路径（无 Wwise 引擎；仅下载用）。</summary>
    public static class AudioEnginePaths
    {
        public const string BankFolderName = "BoluoAudioBanks";
        public const string BankExtension = ".bnk";
        public const string DefaultBankExtension = ".bnk";
        public const string LegacyBankExtension = ".wb";
        public const string InitBankName = "Init";

        public static string PersistentBankPath =>
            Path.Combine(Application.persistentDataPath, BankFolderName);

        public static string StreamingBankPath =>
            Path.Combine(Application.streamingAssetsPath, BankFolderName);

        public static string WwiseGeneratedBankPath =>
            Path.Combine(Application.streamingAssetsPath, "Audio", "GeneratedSoundBanks");

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
            if (name.EndsWith(".bnk", System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 4);
            if (name.EndsWith(".wsbk", System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 5);
            if (name.EndsWith(LegacyBankExtension, System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - LegacyBankExtension.Length);
            return name;
        }

        public static bool IsPackedWwiseInit(string bankName = null) => false;

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
            return TryFindExistingBank(StreamingBankPath, bankName, out path);
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
                Path.Combine(dir, stem + ".bnk"),
                Path.Combine(dir, stem + ".wsbk"),
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
