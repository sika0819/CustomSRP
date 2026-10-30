using System;
using System.Collections;
using System.Collections.Generic;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 进岛时按 BanksChecker 摆放和 <see cref="SceneBankGateCatalog"/> 生成区域门。
    /// 不加载缺失的 BanksChecker 预制体。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class SceneBankGateSpawner : MonoBehaviour
    {
        readonly List<SceneBankGate> _gates = new List<SceneBankGate>(8);
        int _life = 1;
        bool _busy;
        bool _ownsCatalog;
        SceneBankGateCatalog _ownedCatalog;
        SceneBankGate _downloadGate;
        int _progressTicket;
        bool _progressOpen;
        bool _moveLocked;
        bool _unlockMoveAfterExit;
        float _retreatUntil;

        /// <summary>贴墙时胶囊中心可能落在 Trigger 壳外的余量。</summary>
        const float BodyProbeSlopMeters = 0.75f;

        /// <summary>下载弹窗弹出后，沿朝向后退的距离。</summary>
        const float PopupRetreatMeters = 1f;

        /// <summary>上面这段后退要在这一秒内走完。</summary>
        const float PopupRetreatSeconds = 1f;

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
            _downloadGate = null;
            _progressTicket = 0;
            _progressOpen = false;
            _unlockMoveAfterExit = false;
            _retreatUntil = 0f;
            SetDownloadMoveLock(false);
            DownloadConsentSession.ResetForLeaveIsland();
            if (_ownsCatalog && _ownedCatalog != null)
                Destroy(_ownedCatalog);
            _ownedCatalog = null;
            _ownsCatalog = false;
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
            if (catalog == null)
            {
                catalog = SceneBankGateCatalog.CreateBuiltin();
                _ownedCatalog = catalog;
                _ownsCatalog = true;
                Log.Warning(
                    "[SceneBankGate] SceneBankGateCatalog missing from package — using builtin zones");
            }

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

            RefreshBlocked();

            int blocked = 0;
            for (int i = 0; i < _gates.Count; i++)
            {
                SceneBankGate gate = _gates[i];
                if (gate == null || !gate.IsBlocked)
                    continue;
                blocked++;
                Log.Info(
                    "[SceneBankGate] blocked " + gate.name +
                    " banks=" + string.Join(",", gate.BankNames));
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

            // 门只守区域 bank；预设曲子由 PresetForm 另下。同区域 bank 就绪即可进。
            string[] zoneBanks = CopyBanks(bankNames);
            bool blocked = HasBankNames(zoneBanks) && !BanksSatisfied(zoneBanks);
            SceneBankGate gate = go.AddComponent<SceneBankGate>();
            gate.Setup(index, objectName, zoneBanks, size, radius, shape, blocked);
            _gates.Add(gate);
        }

        void LateUpdate()
        {
            if (_gates.Count == 0)
                return;

            RefreshBlocked();
            SamplePlayerInside();
            if (!HudOverlayGate.HasPopup())
            {
                if (_busy || UserBankDownloadFlow.IsBusy)
                    TryReopenActiveProgress();
                else
                    TryBeginPrompt();
            }

            IslandJoystickCamera cam = IslandJoystickCamera.Current;

            // 相机没走到移动分支时，稍过 1 秒也结束，避免移动锁一直不还。
            if (cam != null
                && cam.IsDownloadRetreatPlaying
                && _retreatUntil > 0f
                && Time.time >= _retreatUntil + 0.2f)
                cam.CancelDownloadRetreat();

            // 弹窗那一下的后退要播完再还移动，避免半秒后又走进去。
            bool retreatPlaying = cam != null && cam.IsDownloadRetreatPlaying;
            if (_unlockMoveAfterExit && !retreatPlaying && !_progressOpen)
            {
                _unlockMoveAfterExit = false;
                SetDownloadMoveLock(false);
            }

            // 人已经在门外时再采样一次，清掉弹窗锁，下次走进去才能再触发。
            if (cam == null || !StillInsideBlocked(cam))
                SamplePlayerInside();
        }

        void TryBeginPrompt()
        {
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

        /// <summary>
        /// 这次下载还在跑、进度条已被关掉时，再次走进同一扇门只把进度条打开。
        /// 不另起一次下载。其它未下完的门不并行第二段下载。
        /// </summary>
        void TryReopenActiveProgress()
        {
            SceneBankGate gate = _downloadGate;
            if (gate == null || !gate.IsBlocked || !gate.PlayerInside || gate.PromptLatched)
                return;
            int ticket = UserBankDownloadFlow.ActiveProgressTicket > 0
                ? UserBankDownloadFlow.ActiveProgressTicket
                : _progressTicket;
            if (_progressOpen || ticket <= 0 || UiDialogService.IsProgressRetired(ticket))
                return;
            if (UiDialogService.IsProgressOpen(ticket))
                return;

            if (!UiDialogService.ReopenDownloadProgress(ticket, OnProgressDismissed))
                return;

            gate.PromptLatched = true;
            _progressOpen = true;
            _progressTicket = ticket;
            _unlockMoveAfterExit = false;
            SetDownloadMoveLock(true);
            BeginPopupRetreat();
        }

        void RefreshBlocked()
        {
            for (int i = 0; i < _gates.Count; i++)
            {
                SceneBankGate gate = _gates[i];
                if (gate == null)
                    continue;
                // 只看区域 bank（ZoneBanks == BankNames）。
                bool blocked = HasBankNames(gate.ZoneBanks) && !BanksSatisfied(gate.ZoneBanks);
                // 下完的那一帧要先把人推出触发区，不能先拆掉碰撞，否则推距是 0。
                if (_busy && gate == _downloadGate && HasBankNames(gate.ZoneBanks))
                    blocked = true;
                if (gate.IsBlocked != blocked)
                    gate.SetBlocked(blocked);
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

            _unlockMoveAfterExit = false;
            SetDownloadMoveLock(true);
            BeginPopupRetreat();

            string[] zoneBanks = gate.ZoneBanks;
            string size = FormatResourceSize(zoneBanks);
            Log.Info("[SceneBankGate] UserBankDownloadFlow size=" + size + " gate=" + gate.name);
            SceneBankGate captured = gate;
            _busy = true;
            _downloadGate = captured;
            _progressOpen = true;

            UserBankDownloadFlow.Start(
                zoneBanks,
                new DownloadUiOptions
                {
                    LockMoveDuringPrompt = true,
                    ShowRequestFirst = true,
                    RequestSizeMb = size,
                    OnCancel = EndPromptChain,
                },
                onComplete: () => FinishDownloadFromFlow(captured),
                onBanksDone: _ =>
                {
                    if (!BanksSatisfied(captured.ZoneBanks))
                        UiDialogService.ShowToast("下载失败");
                },
                onProgressDismissed: OnProgressDismissed,
                prioritize: true,
                preempt: true);
        }

        /// <summary>返回、无网、存储不足、只等 Wi-Fi：链结束。后退播完再还移动。</summary>
        void EndPromptChain()
        {
            // 同意链取消时 Flow 不会跑 onComplete，这里清掉 BeginPrompt 预置的 busy。
            if (!UserBankDownloadFlow.IsBusy)
            {
                _busy = false;
                _downloadGate = null;
                _progressOpen = false;
                _progressTicket = 0;
            }

            _unlockMoveAfterExit = true;
        }

        /// <summary>弹窗出现时沿当前朝向，在 1 秒内后退大约 1 米。</summary>
        void BeginPopupRetreat()
        {
            IslandMainForm.Current?.CancelLocomotion();
            LocomotionInput.ClearAll();
            _retreatUntil = Time.time + PopupRetreatSeconds;
            IslandJoystickCamera.Current?.BeginDownloadRetreat(PopupRetreatMeters, PopupRetreatSeconds);
        }

        bool StillInsideBlocked(IslandJoystickCamera cam)
        {
            Vector3 eye = cam.transform.position;
            Vector3 foot = eye - Vector3.up * Mathf.Max(0.5f, cam.EyeHeight);
            for (int i = 0; i < _gates.Count; i++)
            {
                SceneBankGate gate = _gates[i];
                if (gate != null && gate.IsBlocked && gate.ContainsBody(eye, foot, BodyProbeSlopMeters))
                    return true;
            }

            return false;
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

            // 进度窗已关、没有别的弹窗时，人在门外就清锁。移动锁还在也要清：
            // 人已经被推出区外，锁却留着，再走进去不会出进度条。
            // 人还在门里时这里不会清（SetSampledInside 只在门外清），关窗这一帧不会立刻再弹。
            bool clearLatch = !HudOverlayGate.HasPopup() && !_progressOpen;
            for (int i = 0; i < _gates.Count; i++)
            {
                SceneBankGate gate = _gates[i];
                if (gate == null)
                    continue;
                gate.SetSampledInside(
                    hasBody && gate.ContainsBody(eye, foot, BodyProbeSlopMeters),
                    clearLatch);
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

        /// <summary>玩家关掉进度条。下载继续。后退播完再还移动。</summary>
        void OnProgressDismissed()
        {
            _progressOpen = false;
            if (!_busy && !UserBankDownloadFlow.IsBusy)
                return;
            _unlockMoveAfterExit = true;
        }

        /// <summary>Flow 已关进度窗并 Revoke；这里只清门状态与移动锁。</summary>
        void FinishDownloadFromFlow(SceneBankGate gate)
        {
            if (this == null)
                return;
            _busy = false;
            _downloadGate = null;
            _progressTicket = 0;
            _progressOpen = false;
            _unlockMoveAfterExit = false;
            RefreshBlocked();
            SetDownloadMoveLock(false);
            DownloadConsentSession.ReleaseSessionMoveLock();
        }

        void SetDownloadMoveLock(bool locked)
        {
            _moveLocked = locked;
            if (!locked)
                DownloadConsentSession.ReleaseSessionMoveLock();
            IslandJoystickCamera.Current?.SetDownloadMoveLock(locked);
            if (!locked)
                return;
            IslandMainForm.Current?.CancelLocomotion();
            LocomotionInput.ClearAll();
        }

        void OnDestroy()
        {
            _life++;
            _unlockMoveAfterExit = false;
            DownloadConsentSession.ResetForLeaveIsland();
            SetDownloadMoveLock(false);
            _gates.Clear();
            if (_ownsCatalog && _ownedCatalog != null)
                Destroy(_ownedCatalog);
            _ownedCatalog = null;
            _ownsCatalog = false;
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
