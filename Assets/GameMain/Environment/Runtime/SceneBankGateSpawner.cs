using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 进岛时按 BanksChecker 摆放和 <see cref="SceneBankGateCatalog"/> 生成区域门。
    /// 不加载缺失的 BanksChecker 预制体。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneBankGateSpawner : MonoBehaviour
    {
        readonly List<SceneBankGate> _gates = new List<SceneBankGate>(8);
        int _life = 1;
        bool _busy;

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器里 bank 往往已经在磁盘上，进度窗闪一下就关。
        /// 打开后先当没下完，确认下载再把进度从 0 拉到 100。真机不走这里。
        /// </summary>
        const bool EditorSimulateSlowDownload = false;
        const float EditorSimulateSeconds = 5f;
        static readonly HashSet<string> EditorSimFinished =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#endif

        public static IEnumerator Build(
            Dictionary<string, Transform> categoryParents,
            IReadOnlyList<MapItemData> items)
        {
            if (categoryParents == null
                || !categoryParents.TryGetValue(SceneBankGateCatalog.Category, out Transform parent)
                || parent == null)
            {
                yield break;
            }

            SceneBankGateSpawner spawner = parent.GetComponent<SceneBankGateSpawner>();
            if (spawner == null)
                spawner = parent.gameObject.AddComponent<SceneBankGateSpawner>();
            yield return spawner.LoadAndBuild(items);
        }

        IEnumerator LoadAndBuild(IReadOnlyList<MapItemData> items)
        {
            _life++;
            _busy = false;
            ClearChildren();
#if UNITY_EDITOR
            EditorSimFinished.Clear();
#endif

            SceneBankGateCatalog catalog = null;
            if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(
                    SceneAssets.SceneBankGateCatalogLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<SceneBankGateCatalog>(
                    SceneAssets.SceneBankGateCatalogLocation,
                    (asset, _) => catalog = asset,
                    trackLeave: true);
            }

#if UNITY_EDITOR
            if (catalog == null)
            {
                catalog = UnityEditor.AssetDatabase.LoadAssetAtPath<SceneBankGateCatalog>(
                    SceneAssets.SceneBankGateCatalogAssetPath);
            }
#endif

            var used = new HashSet<int>();
            int mapRows = 0;
            int skippedScale = 0;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    MapItemData data = items[i];
                    if (data == null || data.Parent != SceneBankGateCatalog.Category)
                        continue;
                    mapRows++;
                    used.Add(data.Index);
                    if (!data.IsActive)
                        continue;

                    SceneBankGateEntry entry = catalog != null ? catalog.Find(data.Index) : null;
                    TryCreate(
                        data.Index,
                        string.IsNullOrEmpty(data.ObjName) ? data.PrefabName : data.ObjName,
                        data.Pos,
                        data.Rotation,
                        data.Scale,
                        entry != null ? entry.BoxSize : Vector3.zero,
                        entry != null ? entry.Shape : SceneBankGateShape.Box,
                        entry != null ? entry.Radius : 0f,
                        entry != null ? entry.BankNames : null,
                        ref skippedScale);
                }
            }

            SceneBankGateEntry[] entries = catalog != null ? catalog.Entries : null;
            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    SceneBankGateEntry entry = entries[i];
                    if (entry == null)
                        continue;
                    if (entry.MapItemIndex >= 0 && used.Contains(entry.MapItemIndex))
                        continue;
                    TryCreate(
                        entry.MapItemIndex,
                        entry.ObjectName,
                        entry.Position,
                        entry.Rotation,
                        entry.Scale,
                        entry.BoxSize,
                        entry.Shape,
                        entry.Radius,
                        entry.BankNames,
                        ref skippedScale);
                }
            }

#if UNITY_EDITOR
            TryCreateEditorProbe();
#endif

            int blocked = 0;
            for (int i = 0; i < _gates.Count; i++)
            {
                if (_gates[i] != null && _gates[i].IsBlocked)
                    blocked++;
            }

            Log.Info(
                "[SceneBankGate] gates=" + _gates.Count +
                " blocked=" + blocked +
                " mapRows=" + mapRows +
                " catalog=" + (entries != null ? entries.Length : 0) +
                " skippedScale=" + skippedScale);
            if (_gates.Count == 0 && mapRows == 0 && (entries == null || entries.Length == 0))
            {
                Log.Info(
                    "[SceneBankGate] no gates — AllMapItem has no BanksChecker rows and catalog is empty");
            }
        }

        void TryCreate(
            int index,
            string objectName,
            Vector3 position,
            Vector3 rotation,
            Vector3 scale,
            Vector3 boxSize,
            SceneBankGateShape shape,
            float radius,
            string[] bankNames,
            ref int skippedScale)
        {
            Vector3 size = Vector3.zero;
            if (shape == SceneBankGateShape.Sphere)
            {
                if (radius < 0.5f)
                {
                    skippedScale++;
                    Log.Warning(
                        "[SceneBankGate] skip sphere index=" + index +
                        " name=" + objectName +
                        " — radius is empty");
                    return;
                }
            }
            else if (!SceneBankGateCatalog.TryResolveBoxSize(scale, boxSize, out size))
            {
                skippedScale++;
                if (HasBankNames(bankNames))
                {
                    Log.Warning(
                        "[SceneBankGate] skip gate index=" + index +
                        " name=" + objectName +
                        " — scale is near 1 and BoxSize is empty");
                }

                return;
            }

            var go = new GameObject("Gate");
            go.transform.SetParent(transform, false);
            go.transform.position = position;
            go.transform.localEulerAngles = rotation;
            go.transform.localScale = Vector3.one;

            string[] banks = CopyBanks(bankNames);
            bool blocked = HasBankNames(banks) && !BanksSatisfied(banks);
            SceneBankGate gate = go.AddComponent<SceneBankGate>();
            gate.Setup(index, objectName, banks, size, radius, shape, blocked);
            _gates.Add(gate);
        }

        void Update()
        {
            if (_gates.Count == 0)
                return;

            RefreshBlocked();
            SamplePlayerInside();
            if (_busy || HudOverlayGate.HasPopup())
                return;

            for (int i = 0; i < _gates.Count; i++)
            {
                SceneBankGate gate = _gates[i];
                if (gate == null || !gate.IsBlocked || !gate.PlayerInside || gate.PromptLatched)
                    continue;
                gate.PromptLatched = true;
                BeginPrompt(gate);
                break;
            }
        }

        void RefreshBlocked()
        {
            for (int i = 0; i < _gates.Count; i++)
            {
                SceneBankGate gate = _gates[i];
                if (gate == null || !gate.IsBlocked)
                    continue;
                if (BanksSatisfied(gate.BankNames))
                    gate.SetBlocked(false);
            }
        }

        void BeginPrompt(SceneBankGate gate)
        {
            if (gate == null || !gate.IsBlocked)
                return;
            if (GameEntry.UI == null)
            {
                gate.PromptLatched = false;
                return;
            }

            RetreatFromGate();

            string size = FormatResourceSize(gate.BankNames);
            Log.Info("[SceneBankGate] show download dialog size=" + size + " gate=" + gate.name);
            UiDialogService.ShowRequestDownload(
                size,
                () => OnRequestDownloadConfirmed(gate),
                RetreatFromGate);
        }

        /// <summary>申请窗「立即下载」：无网 → 存储 → 流量 → 进度。</summary>
        void OnRequestDownloadConfirmed(SceneBankGate gate)
        {
            if (gate == null)
                return;

            string[] banks = gate.BankNames;
            if (!DownloadNetworkGate.TryPassDownloadGates(
                    banks,
                    onAllowAfterMobile: () => BeginDownload(gate),
                    onWifiOnly: null,
                    onBlockedRetreat: RetreatFromGate))
                return;

            BeginDownload(gate);
        }

        static void RetreatFromGate()
        {
            IslandJoystickCamera.Current?.StepBack(1.25f);
        }

        void SamplePlayerInside()
        {
            IslandJoystickCamera cam = IslandJoystickCamera.Current;
            Vector3 eye = Vector3.zero;
            Vector3 foot = Vector3.zero;
            bool hasBody = cam != null;
            if (hasBody)
            {
                eye = cam.transform.position;
                foot = eye - Vector3.up * Mathf.Max(0.5f, cam.EyeHeight);
            }

            for (int i = 0; i < _gates.Count; i++)
            {
                SceneBankGate gate = _gates[i];
                if (gate == null)
                    continue;
                gate.SetSampledInside(hasBody && gate.ContainsBody(eye, foot));
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// 目录还是空的时候，在出生点前方放一扇编辑器测试门，方便看到下载窗。
        /// 填了正式门之后这扇不再生成。
        /// </summary>
        void TryCreateEditorProbe()
        {
            if (!EditorSimulateSlowDownload || _gates.Count > 0)
                return;

            Vector3 eye = IslandSpawnPose.DefaultPos;
            float yawRad = IslandSpawnPose.DefaultYawDegrees * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
            int skipped = 0;
            TryCreate(
                -1,
                "EditorProbe",
                eye + forward * 6f,
                new Vector3(0f, IslandSpawnPose.DefaultYawDegrees, 0f),
                Vector3.one,
                new Vector3(14f, 8f, 2f),
                SceneBankGateShape.Box,
                0f,
                new[] { "Music1_1" },
                ref skipped);
            Log.Info("[SceneBankGate] editor probe 6m ahead of spawn — walk forward to open the download dialog");
        }
#endif

        void BeginDownload(SceneBankGate gate)
        {
            if (_busy || gate == null)
                return;
            _busy = true;
            int life = _life;
            RunDownload(gate, life).Forget();
        }

        async UniTaskVoid RunDownload(SceneBankGate gate, int life)
        {
            int ticket = UiDialogService.OpenDownloadProgress();
            try
            {
#if UNITY_EDITOR
                if (EditorSimulateSlowDownload)
                {
                    await SimulateEditorProgress(ticket, life, gate.BankNames);
                    if (life != _life || this == null)
                        return;
                }
#endif
                List<BankRemoteInfo> remotes = PresetBankCatalog.ResolveRemotes(gate.BankNames);
                await BankDownloader.DownloadBanks(
                    remotes,
                    loadIntoEngine: true,
                    onItemProgress: _ =>
                    {
                        if (life != _life)
                            return;
                        PushDownloadProgress(ticket, gate.BankNames);
                    },
                    preempt: true);

                if (life != _life || this == null)
                    return;

                PushDownloadProgress(ticket, gate.BankNames);
                RefreshBlocked();
                if (!BanksSatisfied(gate.BankNames))
                    UiDialogService.ShowToast("下载失败");
            }
            catch (Exception e)
            {
                Log.Warning("[SceneBankGate] download failed: " + e.Message);
                if (life == _life && this != null)
                    UiDialogService.ShowToast("下载失败");
            }
            finally
            {
                UiDialogService.CloseDownloadProgress(ticket);
                if (life == _life && this != null)
                    _busy = false;
            }
        }

#if UNITY_EDITOR
        async UniTask SimulateEditorProgress(int ticket, int life, string[] banks)
        {
            Log.Info("[SceneBankGate] editor simulate unfinished download sec=" + EditorSimulateSeconds);
            float elapsed = 0f;
            while (elapsed < EditorSimulateSeconds)
            {
                if (life != _life || this == null)
                    return;
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / EditorSimulateSeconds);
                float shown = t >= 1f ? 100f : Mathf.Min(99f, t * 100f);
                UiDialogService.SetDownloadProgress(ticket, shown);
                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            if (life != _life || this == null)
                return;
            MarkEditorSimFinished(banks);
            UiDialogService.SetDownloadProgress(ticket, 100f);
        }

        static void MarkEditorSimFinished(string[] banks)
        {
            if (banks == null)
                return;
            for (int i = 0; i < banks.Length; i++)
            {
                if (!string.IsNullOrEmpty(banks[i]))
                    EditorSimFinished.Add(banks[i]);
            }
        }
#endif

        void OnDestroy()
        {
            _life++;
            _gates.Clear();
        }

        void ClearChildren()
        {
            _gates.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);
        }

        static bool HasBankNames(string[] banks)
        {
            if (banks == null)
                return false;
            for (int i = 0; i < banks.Length; i++)
            {
                if (!string.IsNullOrEmpty(banks[i]))
                    return true;
            }

            return false;
        }

        static string[] CopyBanks(string[] banks)
        {
            if (!HasBankNames(banks))
                return Array.Empty<string>();
            var list = new List<string>(banks.Length);
            for (int i = 0; i < banks.Length; i++)
            {
                if (!string.IsNullOrEmpty(banks[i]))
                    list.Add(banks[i]);
            }

            return list.ToArray();
        }

        /// <summary>
        /// 磁盘就绪，且不是「没有地址就被跳过」的条目。空地址的 Skipped 不能把门打开。
        /// 编辑器模拟未完成时，磁盘就绪也不算过门，直到这段假进度走完。
        /// </summary>
        static bool BankReady(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return true;
#if UNITY_EDITOR
            if (EditorSimulateSlowDownload && !EditorSimFinished.Contains(bankName))
                return false;
#endif
            return DiskReady(bankName);
        }

        static bool DiskReady(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return true;
            if (!BankDownloader.IsLocalReady(bankName))
                return false;
            if (BankDownloader.TryGetActiveItem(bankName, out BankDownloadItem item)
                && item != null
                && item.State == BankDownloadState.Skipped
                && string.IsNullOrEmpty(item.Url))
                return false;
            return true;
        }

        static bool BanksSatisfied(string[] banks)
        {
            if (!HasBankNames(banks))
                return true;
            for (int i = 0; i < banks.Length; i++)
            {
                if (!BankReady(banks[i]))
                    return false;
            }

            return true;
        }

        static void PushDownloadProgress(int ticket, string[] banks)
        {
            MeasureDownload(banks, out float percent, out float downloadedMb, out float totalMb);
            UiDialogService.SetDownloadProgress(ticket, percent, downloadedMb, totalMb);
        }

        static void MeasureDownload(string[] banks, out float percent, out float downloadedMb, out float totalMb)
        {
            downloadedMb = 0f;
            totalMb = 0f;
            percent = 100f;
            if (!HasBankNames(banks))
                return;

            int count = 0;
            for (int i = 0; i < banks.Length; i++)
            {
                string name = banks[i];
                if (string.IsNullOrEmpty(name))
                    continue;
                count++;
                float weight = 1f;
                if (PresetBankCatalog.TryGet(name, out BankRemoteInfo remote)
                    && remote != null
                    && long.TryParse(remote.size, out long kb)
                    && kb > 0)
                    weight = kb / 1024f;
                totalMb += weight;
                if (BankReady(name))
                    downloadedMb += weight;
                else if (BankDownloader.TryGetActiveItem(name, out BankDownloadItem active) && active != null)
                    downloadedMb += weight * Mathf.Clamp01(active.Progress);
            }

            if (count == 0 || totalMb <= 0.01f)
            {
                percent = 100f;
                downloadedMb = 0f;
                totalMb = 0f;
                return;
            }

            percent = Mathf.Clamp01(downloadedMb / totalMb) * 100f;
        }

        /// <summary>只回数字（或「若干」），单位由弹窗模板加 MB。</summary>
        static string FormatResourceSize(string[] banks)
        {
            long kb = PendingKilobytes(banks);
            if (kb <= 0)
                return "若干";
            return (kb / 1024f).ToString("0.#");
        }

        static long PendingKilobytes(string[] banks)
        {
            if (banks == null)
                return 0;
            long kb = 0;
            for (int i = 0; i < banks.Length; i++)
            {
                string name = banks[i];
                if (string.IsNullOrEmpty(name) || DiskReady(name))
                    continue;
                if (PresetBankCatalog.TryGet(name, out BankRemoteInfo remote)
                    && remote != null
                    && long.TryParse(remote.size, out long parsed)
                    && parsed > 0)
                    kb += parsed;
            }

            return kb;
        }

    }
}
