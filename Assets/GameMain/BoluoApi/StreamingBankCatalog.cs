using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// 首包 <c>StreamingAssets/WanosAudio/ProjectMetadata.json</c> 里记录的 bank 版本 / MD5。
    /// 解压首包 bank 到 persistent 后，下载侧用版本比对决定是否跳过 HTTP。
    /// </summary>
    public static class StreamingBankCatalog
    {
        public const string MetadataRelativePath = "WanosAudio/ProjectMetadata.json";

        static readonly Dictionary<string, Entry> ByName =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        static int _loadState; // 0 idle, 1 loading, 2 done
        static UniTaskCompletionSource _loadTcs;

        sealed class Entry
        {
            public string Version;
            public string Md5;
            public string SizeKb;
        }

        [Serializable]
        sealed class MetadataDto
        {
            [JsonProperty("Banks")]
            public List<BankDto> Banks;
        }

        [Serializable]
        sealed class BankDto
        {
            [JsonProperty("BankName")]
            public string BankName;

            [JsonProperty("Version")]
            public string Version;

            [JsonProperty("Md5")]
            public string Md5;

            [JsonProperty("Size")]
            public string Size;
        }

        public static void ResetSession()
        {
            _loadState = 0;
            _loadTcs = null;
            ByName.Clear();
        }

        public static UniTask EnsureLoadedAsync()
        {
            if (_loadState >= 2)
                return UniTask.CompletedTask;
            if (_loadState == 1 && _loadTcs != null)
                return _loadTcs.Task;

            _loadState = 1;
            _loadTcs = new UniTaskCompletionSource();
            LoadAsync().Forget();
            return _loadTcs.Task;
        }

        public static bool TryGetVersion(string bankName, out string version)
        {
            version = null;
            if (!TryGet(bankName, out var e) || string.IsNullOrEmpty(e.Version))
                return false;
            version = e.Version;
            return true;
        }

        public static bool TryGetMd5(string bankName, out string md5)
        {
            md5 = null;
            if (!TryGet(bankName, out var e) || string.IsNullOrEmpty(e.Md5))
                return false;
            md5 = e.Md5;
            return true;
        }

        /// <summary>
        /// 首包已解压到盘且 catalog 版本与服务端一致 → 无需重下。
        /// </summary>
        public static bool IsSameVersionAsStreaming(string bankName, string remoteVersion)
        {
            if (string.IsNullOrEmpty(bankName) || string.IsNullOrEmpty(remoteVersion))
                return false;
            return TryGetVersion(bankName, out string local)
                   && string.Equals(local, remoteVersion, StringComparison.OrdinalIgnoreCase);
        }

        static bool TryGet(string bankName, out Entry entry)
        {
            entry = null;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;
            return ByName.TryGetValue(stem, out entry);
        }

        static async UniTaskVoid LoadAsync()
        {
            try
            {
                var (ok, text) = await Game.StreamingAssetsUtil.ReadTextAsync(MetadataRelativePath);
                if (!ok || string.IsNullOrEmpty(text))
                {
                    Log.Warning($"[StreamingBankCatalog] metadata missing: {MetadataRelativePath}");
                    return;
                }

                MetadataDto dto;
                try
                {
                    dto = JsonConvert.DeserializeObject<MetadataDto>(text);
                }
                catch (Exception e)
                {
                    Log.Warning($"[StreamingBankCatalog] parse failed: {e.Message}");
                    return;
                }

                ByName.Clear();
                if (dto?.Banks == null)
                    return;

                for (int i = 0; i < dto.Banks.Count; i++)
                {
                    var b = dto.Banks[i];
                    if (b == null || string.IsNullOrEmpty(b.BankName))
                        continue;
                    string stem = AudioEnginePaths.StripBankExtension(b.BankName);
                    if (string.IsNullOrEmpty(stem))
                        continue;
                    ByName[stem] = new Entry
                    {
                        Version = b.Version ?? "",
                        Md5 = b.Md5 ?? "",
                        SizeKb = b.Size ?? "",
                    };
                }

                Log.Info($"[StreamingBankCatalog] loaded banks={ByName.Count} from {MetadataRelativePath}");
            }
            catch (Exception e)
            {
                Log.Warning($"[StreamingBankCatalog] load failed: {e.Message}");
            }
            finally
            {
                _loadState = 2;
                _loadTcs?.TrySetResult();
            }
        }
    }
}
