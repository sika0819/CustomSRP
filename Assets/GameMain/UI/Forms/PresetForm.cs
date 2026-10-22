using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Island YuShe preset panel — horizontal cards, bank download gate, pending auto-apply.
    /// Behaviour mirrors Boluo InGameYuSheView / InGameYuSheItem (see 预设面板迁移指南).
    /// </summary>
    public class PresetForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        public VisualTreeAsset PresetTemplate;

        const float ItemWidth = 264f;
        const float ItemGap = 36f;
        const float ClickSlop = 14f;
        const float TrackWidth = 240f;
        const float ThumbWidth = 63f;
        const float SidePad = 60f;
        const float ScrollbarIdleHideSeconds = 3f;
        const float ToastSeconds = 1.6f;
        const string ToastName = "presetBusyToast";
        const string BusyToastText = "天气正在改变,稍后再操作";
        const string HeardMarkerFile = "preset_heard_markers.txt";
        static readonly HashSet<string> HeardPresets = new HashSet<string>(StringComparer.Ordinal);

        enum BankUiState
        {
            AllReady = 0,
            NotDownloaded = 1,
            Downloading = 2,
            Failed = 3,
            Playing = 4,
            RedDot = 5,
        }

        struct ItemBinding
        {
            public VisualElement Root;
            public int Index;
            public int PresetId;
            public string DisplayName;
            public string[] BankNames;
            public bool UserTriggered;
            public BankUiState AppliedState;
            public float AppliedProgress;
            public bool HasAppliedState;
            // Cached chrome — avoid root.Q every progress / playing tick.
            public VisualElement IconWait;
            public VisualElement IconError;
            public VisualElement IconSuccess;
            public VisualElement IconDownloading;
            public VisualElement IconReady;
            public VisualElement IconPlaying;
            public ArcProgressRing ProgressRing;
            public PresetEqualizerBars Equalizer;
            public Label Title;
        }

        readonly List<ItemBinding> _items = new();
        readonly HashSet<int> _userTriggered = new();
        ScrollView _list;
        VisualElement _scrollThumb;
        VisualElement _scrollTrack;
        VisualElement _panel;
        VisualElement _toast;
        IVisualElementScheduledItem _toastHide;
        IVisualElementScheduledItem _scrollbarHide;
        IVisualElementScheduledItem _progressTick;
        IVisualElementScheduledItem _playingAnimTick;
        int _selected;
        bool _dragging;
        bool _moved;
        bool _captured;
        int _pointerId = -1;
        float _dragStartX;
        float _scrollStartX;
        int _pressIndex = -1;
        int _lastProgressEpoch = -1;
        int _lastSeenCurrentId = -1;
        int _lastSeenSchemeState = -1;
        bool _anyPlaying;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);

            DownloadedUnplayedManager.EnsureLoaded();

            _panel = Root.Q("presetButton");
            if (_panel != null)
                _panel.pickingMode = PickingMode.Position;

            _list = Root.Q<ScrollView>("presetList");
            _scrollThumb = Root.Q("scrollTrackActive");
            _scrollTrack = Root.Q("scrollTrack");
            if (_list == null)
                return;

            _list.mode = ScrollViewMode.Horizontal;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.contentContainer.style.flexDirection = FlexDirection.Row;
            _list.contentContainer.style.flexWrap = Wrap.NoWrap;
            _list.contentContainer.style.alignItems = Align.FlexStart;
            _list.pickingMode = PickingMode.Position;
            _list.RegisterCallback<PointerDownEvent>(OnListPointerDown, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerMoveEvent>(OnListPointerMove, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerUpEvent>(OnListPointerUp, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerCancelEvent>(OnListPointerCancel, TrickleDown.TrickleDown);
            _list.horizontalScroller.valueChanged += OnScrollValueChanged;

            EnsureToast();
            HideToast();

            if (GameEntry.Presets != null && GameEntry.Presets.CurrentId > 0)
            {
                IslandPresetAsset[] presets = GameEntry.Presets.Presets;
                for (int i = 0; i < presets.Length; i++)
                {
                    if (presets[i] != null && presets[i].Id == GameEntry.Presets.CurrentId)
                        _selected = i;
                }
            }

            Rebuild();
            Root.schedule.Execute(ScrollSelectedIntoView).StartingIn(0);
            StartProgressTick();
            StartPlayingAnimTick();
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            // F2: closing must NOT cancel downloads or CancelOtherState.
            StopProgressTick();
            StopPlayingAnimTick();
            _toastHide?.Pause();
            _toastHide = null;
            _scrollbarHide?.Pause();
            _scrollbarHide = null;

            if (_list != null)
            {
                _list.UnregisterCallback<PointerDownEvent>(OnListPointerDown, TrickleDown.TrickleDown);
                _list.UnregisterCallback<PointerMoveEvent>(OnListPointerMove, TrickleDown.TrickleDown);
                _list.UnregisterCallback<PointerUpEvent>(OnListPointerUp, TrickleDown.TrickleDown);
                _list.UnregisterCallback<PointerCancelEvent>(OnListPointerCancel, TrickleDown.TrickleDown);
                _list.horizontalScroller.valueChanged -= OnScrollValueChanged;
            }

            _items.Clear();
            _list?.contentContainer.Clear();
            _list = null;
            _scrollThumb = null;
            _scrollTrack = null;
            _panel = null;
            _toast = null;
            base.OnClose(isShutdown, userData);
        }

        void Rebuild()
        {
            _items.Clear();
            _list.contentContainer.Clear();

            IslandPresetAsset[] presets = GameEntry.Presets != null
                ? GameEntry.Presets.Presets
                : Array.Empty<IslandPresetAsset>();

            int count = GameEntry.Presets != null
                ? GameEntry.Presets.GetUiCount()
                : 0;

            if (count <= 0)
            {
                string[] fallback =
                {
                    "清晨起床", "独自旅行", "午后放松", "委屈泛滥", "专注工作",
                    "放空发呆", "灵感唤醒", "假日躺平", "学习冲刺", "准备入睡",
                };
                for (int i = 0; i < fallback.Length; i++)
                {
                    VisualElement element = MakeItem();
                    BindFallback(element, i, fallback[i]);
                    _list.Add(element);
                }

                SyncScrollTrack();
                return;
            }

            for (int i = 0; i < count; i++)
            {
                VisualElement element = MakeItem();
                BindItem(element, i, presets[i]);
                _list.Add(element);
            }

            RefreshAllStates(force: true);
            SyncScrollTrack();
        }

        VisualElement MakeItem()
        {
            if (PresetTemplate != null)
            {
                TemplateContainer item = PresetTemplate.Instantiate();
                item.style.width = ItemWidth;
                item.style.height = 337;
                item.style.flexShrink = 0;
                item.style.marginRight = ItemGap;
                return item;
            }

            Label fallback = new Label();
            fallback.name = "presetitem";
            fallback.style.width = ItemWidth;
            fallback.style.height = 337;
            fallback.style.flexShrink = 0;
            fallback.style.marginRight = ItemGap;
            fallback.style.fontSize = 32;
            fallback.style.color = new StyleColor(Color.white);
            return fallback;
        }

        void BindItem(VisualElement element, int index, IslandPresetAsset preset)
        {
            Label title = element.Q<Label>("presetitem") ?? element as Label;
            string name = preset != null ? preset.DisplayName : ("预设 " + (index + 1));
            if (title != null)
                title.text = name;

            VisualElement cover = element.Q("cover") ?? element.Q("zhanweitu2Button");
            ApplyCover(cover, preset);

            VisualElement vip = element.Q("vip");
            if (vip != null)
                vip.style.display = DisplayStyle.None;

            element.style.opacity = 1f;
            if (cover != null)
                cover.pickingMode = PickingMode.Position;
            element.pickingMode = PickingMode.Position;
            EnsureDownloadRing(element);

            var binding = new ItemBinding
            {
                Root = element,
                Index = index,
                PresetId = preset != null ? preset.Id : index + 1,
                DisplayName = name,
                BankNames = preset != null ? preset.BankNames : null,
                UserTriggered = _userTriggered.Contains(preset != null ? preset.Id : index + 1),
                Title = title,
            };
            CacheItemChrome(ref binding);
            _items.Add(binding);
        }

        static void CacheItemChrome(ref ItemBinding item)
        {
            VisualElement root = item.Root;
            if (root == null)
                return;

            item.IconWait = root.Q("statewaitdownload");
            item.IconError = root.Q("stateerror");
            item.IconSuccess = root.Q("statedownloadsuccess");
            item.IconDownloading = root.Q("statedownloading");
            item.IconReady = root.Q("stateready");
            item.IconPlaying = root.Q("stateplaying");
            item.ProgressRing = item.IconDownloading != null
                ? item.IconDownloading.Q<ArcProgressRing>("fill")
                : null;
            item.Equalizer = EnsureEqualizer(item.IconPlaying);
        }

        /// <summary>Replace legacy barLeft/Center/Right with mesh equalizer (no layout style anim).</summary>
        static PresetEqualizerBars EnsureEqualizer(VisualElement host)
        {
            if (host == null)
                return null;

            PresetEqualizerBars existing = host.Q<PresetEqualizerBars>("equalizer");
            if (existing != null)
                return existing;

            host.Clear();
            var eq = new PresetEqualizerBars { name = "equalizer" };
            eq.AddToClassList("game-preset-item__equalizer");
            host.Add(eq);
            return eq;
        }

        /// <summary>
        /// Ensure statedownloading/fill is an <see cref="ArcProgressRing"/> (Boluo Fill + ArcProgressBar).
        /// </summary>
        static void EnsureDownloadRing(VisualElement root)
        {
            VisualElement host = root.Q("statedownloading");
            if (host == null)
                return;

            ArcProgressRing existing = host.Q<ArcProgressRing>("fill");
            if (existing != null)
                return;

            VisualElement legacy = host.Q("fill");
            if (legacy != null)
                host.Remove(legacy);

            var ring = new ArcProgressRing { name = "fill" };
            ring.AddToClassList("game-preset-item__fill");
            host.Add(ring);
        }

        void BindFallback(VisualElement element, int index, string name)
        {
            Label title = element.Q<Label>("presetitem") ?? element as Label;
            if (title != null)
                title.text = name;

            VisualElement cover = element.Q("cover") ?? element.Q("zhanweitu2Button");
            ApplyCover(cover, null);
            VisualElement vip = element.Q("vip");
            if (vip != null)
                vip.style.display = DisplayStyle.None;

            element.style.opacity = 1f;
            if (cover != null)
                cover.pickingMode = PickingMode.Position;
            element.pickingMode = PickingMode.Position;
            EnsureDownloadRing(element);

            var binding = new ItemBinding
            {
                Root = element,
                Index = index,
                PresetId = index + 1,
                DisplayName = name,
                BankNames = null,
                UserTriggered = false,
                Title = title,
            };
            CacheItemChrome(ref binding);
            _items.Add(binding);
        }

        /// <summary>Bind-once cover from <see cref="IslandPresetAsset.Cover"/> — no Resources.Load.</summary>
        static void ApplyCover(VisualElement cover, IslandPresetAsset preset)
        {
            if (cover == null)
                return;

            Texture2D tex = preset != null ? preset.Cover : null;
            if (tex == null)
            {
                if (preset != null)
                    Log.Warning($"[PresetForm] Preset id={preset.Id} has no Cover assigned");
                return;
            }

            cover.style.backgroundImage = new StyleBackground(tex);
        }

        // -----------------------------------------------------------------------
        // Card state machine
        // -----------------------------------------------------------------------

        void RefreshAllStates(bool force = false)
        {
            int epoch = BankDownloader.ProgressEpoch;
            int currentId = GameEntry.Presets != null ? GameEntry.Presets.CurrentId : 0;
            int schemeState = GameEntry.Presets != null ? GameEntry.Presets.SchemeState : 0;
            bool busy = BankDownloader.HasBusyWork;
            bool hasUser = _userTriggered.Count > 0;

            if (!force
                && epoch == _lastProgressEpoch
                && currentId == _lastSeenCurrentId
                && schemeState == _lastSeenSchemeState
                && !hasUser
                && !busy)
            {
                return;
            }

            // Epoch + ids unchanged, no download: only sync Playing chrome for current / was-playing cards.
            bool playingOnly = !force
                && epoch == _lastProgressEpoch
                && !busy
                && !hasUser;

            // Download active: narrow to cards that can change bank/progress chrome.
            bool narrowBusy = !force && !playingOnly && (busy || hasUser);

            _lastProgressEpoch = epoch;
            _lastSeenCurrentId = currentId;
            _lastSeenSchemeState = schemeState;

            for (int i = 0; i < _items.Count; i++)
            {
                if (playingOnly || narrowBusy)
                {
                    ItemBinding item = _items[i];
                    bool relevant = !item.HasAppliedState
                        || item.PresetId == currentId
                        || (item.HasAppliedState && item.AppliedState == BankUiState.Playing)
                        || (item.HasAppliedState && item.AppliedState == BankUiState.Downloading)
                        || _userTriggered.Contains(item.PresetId);
                    if (!relevant)
                        continue;
                }

                ApplyItemState(i, force);
            }

            SyncPlayingAnimTick();
        }

        BankUiState ComputeState(in ItemBinding item)
        {
            var presets = GameEntry.Presets;
            bool isCurrent = presets != null
                && presets.CurrentId == item.PresetId
                && presets.SchemeState > 0;

            if (isCurrent && presets.SchemeState == 2)
                return BankUiState.Playing;

            BankUiState bank = ComputeBankState(item);
            if (bank == BankUiState.AllReady)
            {
                if (isCurrent && presets.SchemeState == 1)
                    return BankUiState.AllReady; // Ready icon spins via opacity; still ready chrome
                if (DownloadedUnplayedManager.ShouldShowRedDot(
                        DownloadedUnplayedManager.PresetKey(item.DisplayName)))
                    return BankUiState.RedDot;
                return BankUiState.AllReady;
            }

            return bank;
        }

        BankUiState ComputeBankState(in ItemBinding item)
        {
            string[] banks = item.BankNames;
            if (banks == null || banks.Length == 0)
                return BankUiState.AllReady;

            bool anyFailed = false;
            bool anyPending = false;
            bool anyDownloading = false;
            bool anyNoUrl = false;
            bool allReady = true;

            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (string.IsNullOrEmpty(bn))
                    continue;

                if (BankDownloader.IsLocalReady(bn))
                    continue;

                allReady = false;

                if (BankDownloader.TryGetActiveItem(bn, out var active) && active != null)
                {
                    switch (active.State)
                    {
                        case BankDownloadState.Ready:
                        case BankDownloadState.Skipped:
                            break;
                        case BankDownloadState.Failed:
                            anyFailed = true;
                            break;
                        case BankDownloadState.Downloading:
                            if (string.IsNullOrEmpty(active.Url))
                                anyNoUrl = true;
                            else
                                anyDownloading = true;
                            break;
                        case BankDownloadState.Pending:
                        default:
                            if (string.IsNullOrEmpty(active.Url))
                                anyNoUrl = true;
                            else
                                anyPending = true;
                            break;
                    }
                    continue;
                }

                if (PresetBankCatalog.TryGet(bn, out var remote) && remote != null)
                {
                    if (string.IsNullOrEmpty(remote.url))
                        anyNoUrl = true;
                    else
                        anyPending = true;
                }
                else
                {
                    // No catalog entry — treat as missing / not downloaded (retry later).
                    anyPending = true;
                }
            }

            if (allReady)
                return BankUiState.AllReady;
            if (anyFailed)
                return BankUiState.Failed;
            if (anyNoUrl && !anyDownloading && !anyPending)
                return BankUiState.Failed;

            bool userTriggered = item.UserTriggered || _userTriggered.Contains(item.PresetId);
            if (userTriggered && (anyDownloading || anyPending))
                return BankUiState.Downloading;

            return BankUiState.NotDownloaded;
        }

        void ApplyItemState(int index, bool force)
        {
            if (index < 0 || index >= _items.Count)
                return;

            ItemBinding item = _items[index];
            item.UserTriggered = _userTriggered.Contains(item.PresetId);

            BankUiState state = ComputeState(item);
            float progress = state == BankUiState.Downloading
                ? GetBanksProgress(item.BankNames)
                : item.AppliedProgress;

            // Progress-only: update ring mesh, never touch display/opacity (avoids cover repaint).
            if (!force
                && item.HasAppliedState
                && item.AppliedState == state)
            {
                if (state == BankUiState.Downloading
                    && Mathf.Abs(item.AppliedProgress - progress) >= 0.005f)
                {
                    if (item.ProgressRing != null)
                        item.ProgressRing.FillAmount = progress;
                    item.AppliedProgress = progress;
                    _items[index] = item;
                }
                else
                {
                    _items[index] = item;
                }

                return;
            }

            VisualElement root = item.Root;
            if (root == null)
            {
                _items[index] = item;
                return;
            }

            SetIconVisible(item.IconWait, false);
            SetIconVisible(item.IconError, false);
            SetIconVisible(item.IconSuccess, false);
            SetIconVisible(item.IconDownloading, false);
            SetIconVisible(item.IconReady, false);
            SetIconVisible(item.IconPlaying, false);

            if (item.Title != null)
                item.Title.style.color = new Color(25f / 255f, 25f / 255f, 26f / 255f, 1f);

            // Boluo YusheItem: cover Image stays white; select/unselect is Ready/Playing chrome only
            // (no root opacity / cover multiply). Phone card size stays 264 for both.
            root.style.opacity = 1f;

            switch (state)
            {
                case BankUiState.Playing:
                    SetIconVisible(item.IconPlaying, true);
                    break;
                case BankUiState.RedDot:
                    SetIconVisible(item.IconSuccess, true);
                    break;
                case BankUiState.AllReady:
                    SetIconVisible(item.IconReady, true);
                    break;
                case BankUiState.NotDownloaded:
                    SetIconVisible(item.IconWait, true);
                    break;
                case BankUiState.Downloading:
                    SetIconVisible(item.IconDownloading, true);
                    if (item.ProgressRing != null)
                    {
                        if (!item.HasAppliedState || item.AppliedState != BankUiState.Downloading)
                            item.ProgressRing.ResetProgress();
                        item.ProgressRing.FillAmount = progress;
                    }
                    break;
                case BankUiState.Failed:
                    SetIconVisible(item.IconError, true);
                    break;
            }

            item.AppliedState = state;
            item.AppliedProgress = progress;
            item.HasAppliedState = true;
            _items[index] = item;
        }

        static void SetIconVisible(VisualElement icon, bool show)
        {
            if (icon == null)
                return;
            icon.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        static float GetBanksProgress(string[] banks)
        {
            if (banks == null || banks.Length == 0)
                return 1f;

            float downloaded = 0f;
            float total = 0f;
            int count = 0;
            float completed = 0f;

            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (string.IsNullOrEmpty(bn))
                    continue;
                count++;

                float sizeMb = 1f;
                if (PresetBankCatalog.TryGet(bn, out var remote)
                    && remote != null
                    && !string.IsNullOrEmpty(remote.size)
                    && long.TryParse(remote.size, out long kb)
                    && kb > 0)
                {
                    sizeMb = kb / 1024f;
                }

                total += sizeMb;

                if (BankDownloader.IsLocalReady(bn))
                {
                    completed += 1f;
                    downloaded += sizeMb;
                    continue;
                }

                if (BankDownloader.TryGetActiveItem(bn, out var active) && active != null)
                {
                    downloaded += sizeMb * Mathf.Clamp01(active.Progress);
                    continue;
                }
            }

            if (count == 0)
                return 1f;
            if (total > 0.01f)
                return Mathf.Clamp01(downloaded / total);
            return Mathf.Clamp01(completed / count);
        }

        // -----------------------------------------------------------------------
        // Click / select
        // -----------------------------------------------------------------------

        void HandleItemClick(int index)
        {
            if (index < 0 || index >= _items.Count)
                return;

            ItemBinding item = _items[index];
            var presets = GameEntry.Presets;

            // F4 switching gate.
            if (presets != null && presets.IsTransitioning)
            {
                ShowToast(BusyToastText);
                return;
            }

            if (GameEntry.Weather != null && GameEntry.Weather.IsWeatherBusy)
            {
                ShowToast(BusyToastText);
                return;
            }

            // F3: already playing this card — no re-apply.
            if (presets != null
                && presets.CurrentId == item.PresetId
                && presets.SchemeState == 2)
            {
                return;
            }

            BankUiState bank = ComputeBankState(item);
            switch (bank)
            {
                case BankUiState.AllReady:
                case BankUiState.Playing:
                case BankUiState.RedDot:
                    ApplyReadyPreset(item);
                    break;
                case BankUiState.NotDownloaded:
                case BankUiState.Failed:
                    TriggerPresetDownload(item);
                    break;
                case BankUiState.Downloading:
                    // Already downloading — keep priority warm, no MoveTo.
                    TriggerPresetDownload(item);
                    break;
            }
        }

        void ApplyReadyPreset(in ItemBinding item)
        {
            MarkHeard(item.DisplayName);
            _selected = item.Index;
            PendingDownloadIntent.Clear();
            CloseSelfImmediate();
            GameEntry.Presets?.RequestTransition(item.PresetId);
        }

        void TriggerPresetDownload(in ItemBinding item)
        {
            string[] banks = item.BankNames;
            if (banks == null || banks.Length == 0)
            {
                ApplyReadyPreset(item);
                return;
            }

            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                ShowToast("需要网络连接");
                return;
            }

            long requiredKb = 0;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (string.IsNullOrEmpty(bn) || BankDownloader.IsLocalReady(bn))
                    continue;
                if (PresetBankCatalog.TryGet(bn, out var remote)
                    && remote != null
                    && !string.IsNullOrEmpty(remote.size)
                    && long.TryParse(remote.size, out long kb))
                {
                    requiredKb += kb;
                }
            }

            if (requiredKb > 0)
            {
                long requiredBytes = requiredKb * 1024L;
                if (GetAvailableStorageBytes() < requiredBytes * 2)
                {
                    ShowToast("存储空间不足");
                    return;
                }
            }

            // Gate 3 (mobile data consent) — same TODO as MusicForm; allow for now.

            _userTriggered.Add(item.PresetId);
            PendingDownloadIntent.SetPreset(item.PresetId);
            ApplyItemState(item.Index, force: true);

            var remotes = PresetBankCatalog.ResolveRemotes(banks);
            if (remotes == null || remotes.Count == 0)
            {
                Log.Warning($"[PresetForm] No catalog remotes for preset {item.PresetId}");
                ApplyItemState(item.Index, force: true);
                return;
            }

            int presetId = item.PresetId;
            IslandPlayerPosSave.PushSaveSuppress();
            // Fire-and-forget: UniTask continues on main thread via BankDownloader.
            DownloadBanksAsync(presetId, remotes).Forget();
        }

        /// <summary>
        /// Runs the preset bank download and guarantees the save-suppress counter is popped exactly once,
        /// even if the download throws before the completion callback fires
        /// (otherwise the player position would stay permanently unsaved).
        /// </summary>
        async UniTaskVoid DownloadBanksAsync(int presetId, List<BankRemoteInfo> remotes)
        {
            try
            {
                await BankDownloader.DownloadBanks(
                    remotes,
                    loadIntoEngine: true,
                    onComplete: list => OnPresetBanksDone(presetId, list),
                    prioritize: false,
                    preempt: true);
            }
            catch (Exception e)
            {
                Log.Warning($"[PresetForm] Bank download failed: {e.Message}");
                _userTriggered.Remove(presetId);
                if (PendingDownloadIntent.IsPresetMatch(presetId))
                    PendingDownloadIntent.Clear();
                if (Root != null)
                    RefreshAllStates();
            }
            finally
            {
                IslandPlayerPosSave.PopSaveSuppress();
            }
        }

        void OnPresetBanksDone(int presetId, IReadOnlyList<BankDownloadItem> _)
        {
            // 注意：保存抑制的出栈由 DownloadBanksAsync 的 finally 统一负责，这里不要重复 Pop。
            IslandPresetAsset preset = GameEntry.Presets?.GetById(presetId);
            string[] banks = preset != null ? preset.BankNames : null;

            // Empty-url Skipped must not count as ready (Boluo no-Url → Failed).
            bool allLocal = AreBanksLocallyReady(banks);
            if (!allLocal)
            {
                _userTriggered.Remove(presetId);
                if (PendingDownloadIntent.IsPresetMatch(presetId))
                    PendingDownloadIntent.Clear();
                if (Root != null)
                    RefreshAllStates();
                return;
            }

            _userTriggered.Remove(presetId);

            // F7: auto-enter only if pending still points at this preset.
            if (!PendingDownloadIntent.IsPresetMatch(presetId))
            {
                if (Root != null)
                    RefreshAllStates();
                return;
            }

            PendingDownloadIntent.Clear();
            if (preset == null)
            {
                if (Root != null)
                    RefreshAllStates();
                return;
            }

            MarkHeard(preset.DisplayName);
            if (Root != null)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].PresetId == presetId)
                        _selected = i;
                }

                CloseSelfImmediate();
            }

            GameEntry.Presets?.RequestTransition(presetId);
        }

        static bool AreBanksLocallyReady(string[] banks)
        {
            if (banks == null || banks.Length == 0)
                return true;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (string.IsNullOrEmpty(bn))
                    continue;
                if (!BankDownloader.IsLocalReady(bn))
                    return false;
            }
            return true;
        }

        // -----------------------------------------------------------------------
        // Pointer / scroll
        // -----------------------------------------------------------------------

        void OnListPointerDown(PointerDownEvent evt)
        {
            if (_list == null || _pointerId >= 0)
                return;

            _pointerId = evt.pointerId;
            _dragging = true;
            _moved = false;
            _captured = false;
            _dragStartX = evt.position.x;
            _scrollStartX = _list.scrollOffset.x;
            _pressIndex = FindItemIndexAt(evt.position);
            RevealScrollbar();
        }

        void OnListPointerMove(PointerMoveEvent evt)
        {
            if (!_dragging || evt.pointerId != _pointerId || _list == null)
                return;

            float dx = evt.position.x - _dragStartX;
            if (!_moved && Mathf.Abs(dx) >= ClickSlop)
            {
                _moved = true;
                if (!_captured)
                {
                    _list.CapturePointer(evt.pointerId);
                    _captured = true;
                }
            }

            if (!_moved)
                return;

            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            float next = Mathf.Clamp(_scrollStartX - dx, 0f, max);
            _list.scrollOffset = new Vector2(next, 0f);
            SyncScrollTrack();
            RevealScrollbar();
            evt.StopPropagation();
        }

        void OnListPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != _pointerId)
                return;

            bool wasMoved = _moved;
            int pressIndex = _pressIndex;

            if (_captured && _list != null && _list.HasPointerCapture(evt.pointerId))
                _list.ReleasePointer(evt.pointerId);

            _dragging = false;
            _pointerId = -1;
            _captured = false;
            _pressIndex = -1;
            _moved = false;
            SyncScrollTrack();
            ScheduleScrollbarHide();

            if (!wasMoved && pressIndex >= 0 && pressIndex < _items.Count)
            {
                HandleItemClick(pressIndex);
                evt.StopPropagation();
            }
        }

        void OnListPointerCancel(PointerCancelEvent evt)
        {
            if (evt.pointerId != _pointerId)
                return;

            if (_captured && _list != null && _list.HasPointerCapture(evt.pointerId))
                _list.ReleasePointer(evt.pointerId);

            _dragging = false;
            _pointerId = -1;
            _captured = false;
            _pressIndex = -1;
            _moved = false;
            SyncScrollTrack();
            ScheduleScrollbarHide();
        }

        int FindItemIndexAt(Vector2 panelPosition)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                VisualElement root = _items[i].Root;
                if (root == null)
                    continue;
                if (root.worldBound.Contains(panelPosition))
                    return i;
            }

            return -1;
        }

        void OnScrollValueChanged(float _)
        {
            SyncScrollTrack();
            RevealScrollbar();
            ScheduleScrollbarHide();
        }

        void ScrollSelectedIntoView()
        {
            if (_list == null || _selected < 0)
                return;

            // Center selected card in the viewport (Boluo ScrollCurrentPresetToCenter).
            float cardCenter = SidePad + _selected * (ItemWidth + ItemGap) + ItemWidth * 0.5f;
            float target = cardCenter - ViewportWidth() * 0.5f;
            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            _list.scrollOffset = new Vector2(Mathf.Clamp(target, 0f, max), 0f);
            SyncScrollTrack();
        }

        void SyncScrollTrack()
        {
            if (_scrollThumb == null || _list == null)
                return;

            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            float t = max > 0.01f ? Mathf.Clamp01(_list.scrollOffset.x / max) : 0f;
            float travel = TrackWidth - ThumbWidth;
            _scrollThumb.style.marginLeft = -TrackWidth * 0.5f + t * travel;
        }

        void RevealScrollbar()
        {
            if (_scrollThumb != null)
                _scrollThumb.style.opacity = 1f;
            if (_scrollTrack != null)
                _scrollTrack.style.opacity = 1f;
        }

        void ScheduleScrollbarHide()
        {
            _scrollbarHide?.Pause();
            if (Root == null)
                return;
            _scrollbarHide = Root.schedule.Execute(() =>
            {
                if (_scrollThumb != null)
                    _scrollThumb.style.opacity = 0.5f;
                if (_scrollTrack != null)
                    _scrollTrack.style.opacity = 0.5f;
                _scrollbarHide = null;
            }).StartingIn((long)(ScrollbarIdleHideSeconds * 1000f));
        }

        float ViewportWidth()
        {
            float w = _list != null ? _list.contentViewport.layout.width : 0f;
            return w > 1f ? w : 900f;
        }

        float ContentWidth()
        {
            int n = _list != null ? _list.contentContainer.childCount : 0;
            if (n <= 0)
                return 0f;
            return SidePad + n * ItemWidth + Mathf.Max(0, n - 1) * ItemGap + SidePad;
        }

        // -----------------------------------------------------------------------
        // Progress tick / toast / heard
        // -----------------------------------------------------------------------

        void StartProgressTick()
        {
            StopProgressTick();
            if (Root == null)
                return;
            // ≤10Hz — Boluo BankProgressVersion throttle. ApplyItemState skips unchanged chrome.
            // RefreshAllStates(bool force=false) 不能当 method group 传给 Action<TimerState>
            _progressTick = Root.schedule.Execute(_ => RefreshAllStates()).Every(100);
        }

        void StopProgressTick()
        {
            _progressTick?.Pause();
            _progressTick = null;
        }

        // Boluo InGameYuSheItemPlaying / state_Playing.prefab (cycleDuration=1).
        const float PlayingCycleDuration = 1f;

        void StartPlayingAnimTick()
        {
            SyncPlayingAnimTick();
        }

        void StopPlayingAnimTick()
        {
            _playingAnimTick?.Pause();
            _playingAnimTick = null;
            _anyPlaying = false;
        }

        void SyncPlayingAnimTick()
        {
            bool any = false;
            for (int i = 0; i < _items.Count; i++)
            {
                ItemBinding item = _items[i];
                if (item.HasAppliedState && item.AppliedState == BankUiState.Playing)
                {
                    any = true;
                    break;
                }
            }

            _anyPlaying = any;
            if (!any)
            {
                _playingAnimTick?.Pause();
                _playingAnimTick = null;
                return;
            }

            if (Root == null || _playingAnimTick != null)
                return;

            _playingAnimTick = Root.schedule.Execute(TickPlayingBars).Every(50);
        }

        void TickPlayingBars()
        {
            if (!_anyPlaying || _items.Count == 0 || PlayingCycleDuration <= 0f)
                return;

            float t = Time.unscaledTime * (Mathf.PI * 2f / PlayingCycleDuration);
            bool any = false;
            for (int i = 0; i < _items.Count; i++)
            {
                ItemBinding item = _items[i];
                if (!item.HasAppliedState || item.AppliedState != BankUiState.Playing)
                    continue;
                if (item.Equalizer == null)
                    continue;
                if (item.IconPlaying != null
                    && item.IconPlaying.resolvedStyle.display == DisplayStyle.None)
                    continue;

                item.Equalizer.Tick(t);
                any = true;
            }

            if (!any)
            {
                _anyPlaying = false;
                _playingAnimTick?.Pause();
                _playingAnimTick = null;
            }
        }

        void EnsureToast()
        {
            // Host on stage (not the sheet) so TipWindows sits above the panel chrome.
            VisualElement host = Root.Q("stage") ?? Root;
            _toast = UiTipToast.Ensure(host, ToastName);
        }

        void ShowToast(string message)
        {
            if (_toast == null)
                return;
            UiTipToast.Show(_toast, message ?? BusyToastText, anchoredY: 0f);
            _toastHide?.Pause();
            _toastHide = _toast.schedule.Execute(HideToast).StartingIn((long)(ToastSeconds * 1000f));
        }

        void HideToast()
        {
            _toastHide?.Pause();
            _toastHide = null;
            UiTipToast.Hide(_toast);
        }

        static void MarkHeard(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return;
            DownloadedUnplayedManager.MarkPlayed(DownloadedUnplayedManager.PresetKey(displayName));
        }

        static long GetAvailableStorageBytes()
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
                // ignore
            }

            return long.MaxValue;
        }
    }
}
