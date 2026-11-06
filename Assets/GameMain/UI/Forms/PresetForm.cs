using System;
using System.Collections.Generic;
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
        protected override bool HidesBottomHudIcons => true;

        /// <summary>Boluo InGameYuSheView sheet card anchoredY (bottom pivot).</summary>
        protected override float SheetBottomOffset => 120f;
        public VisualTreeAsset PresetTemplate;

        /// <summary>面板开着时相机用 <see cref="IslandCameraSrpSetup.PresetPanelRenderScale"/>。</summary>
        public static bool IsOpen { get; private set; }

        const float ItemWidth = 264f;
        const float ItemGap = 36f;
        /// <summary>
        /// Beat UITK ScrollView's built-in touch threshold (≈10px) so we own the
        /// gesture and drive scrollOffset 1:1 with the finger.
        /// </summary>
        const float ClickSlop = 8f;
        /// <summary>Coast after release: linear px/s². Higher = shorter glide.</summary>
        const float CoastDeceleration = 3200f;
        const float CoastVelocityScale = 0.85f;
        const float MinCoastStartSpeed = 140f;
        const float MinCoastStopSpeed = 35f;
        const float MaxCoastSpeed = 2000f;
        const float CoastTickMs = 16f;
        /// <summary>Release velocity from trailing window — avoids single-frame spikes.</summary>
        const float VelocitySampleWindow = 0.07f;
        const float MinVelocitySampleDt = 0.012f;
        const float TrackWidth = 240f;
        const float ThumbWidth = 63f;
        const float SidePad = 60f;
        const float ScrollbarIdleHideSeconds = 3f;
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
        IVisualElementScheduledItem _scrollbarHide;
        IVisualElementScheduledItem _progressTick;
        IVisualElementScheduledItem _playingAnimTick;
        IVisualElementScheduledItem _coastTick;
        int _selected;
        bool _dragging;
        bool _moved;
        bool _captured;
        int _pointerId = -1;
        float _dragStartX;
        float _scrollStartX;
        float _lastPointerX;
        float _lastPointerTime;
        float _velAnchorX;
        float _velAnchorTime;
        float _scrollVelocity;
        float _coastVelocity;
        float _coastX;
        float _coastLastTime;
        bool _drivingScroll;
        int _pressIndex = -1;
        int _lastProgressEpoch = -1;
        int _lastSeenCurrentId = -1;
        int _lastSeenSchemeState = -1;
        bool _lastDownloaderBusy;
        bool _anyPlaying;

        public override void OnOpen(object userData)
        {
            IsOpen = true;
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

            // Thumb travel via translate (not marginLeft) so drag ticks don't dirty layout.
            if (_scrollThumb != null)
                _scrollThumb.style.marginLeft = -TrackWidth * 0.5f;

            _list.mode = ScrollViewMode.Horizontal;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            // Own the drag (starve UITK pan). Coast after release is custom — see TickCoast.
            _list.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
            _list.scrollDecelerationRate = 0f;
            _list.elasticity = 0f;
            _list.contentContainer.style.flexDirection = FlexDirection.Row;
            _list.contentContainer.style.flexWrap = Wrap.NoWrap;
            _list.contentContainer.style.alignItems = Align.FlexStart;
            _list.pickingMode = PickingMode.Position;
            _list.RegisterCallback<PointerDownEvent>(OnListPointerDown, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerMoveEvent>(OnListPointerMove, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerUpEvent>(OnListPointerUp, TrickleDown.TrickleDown);
            _list.RegisterCallback<PointerCancelEvent>(OnListPointerCancel, TrickleDown.TrickleDown);
            _list.horizontalScroller.valueChanged += OnScrollValueChanged;

            if (GameEntry.Presets != null && GameEntry.Presets.CurrentId > 0)
            {
                IslandPresetAsset[] presets = GameEntry.Presets.Presets;
                for (int i = 0; i < presets.Length; i++)
                {
                    if (presets[i] != null && presets[i].Id == GameEntry.Presets.CurrentId)
                        _selected = i;
                }
            }

            _lastDownloaderBusy = BankDownloader.HasBusyWork;
            Rebuild();
            Root.schedule.Execute(ScrollSelectedIntoView).StartingIn(0);
            BankDownloader.OnBankLocalReady -= OnBankLocalReadyForUi;
            BankDownloader.OnBankLocalReady += OnBankLocalReadyForUi;
            StartProgressTick();
            StartPlayingAnimTick();
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            IsOpen = false;
            BankDownloader.OnBankLocalReady -= OnBankLocalReadyForUi;
            // F2: closing must NOT cancel downloads or CancelOtherState.
            StopCoast();
            StopProgressTick();
            StopPlayingAnimTick();
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

        /// <summary>静默 Rest/Critical 落盘：立刻全量翻牌（含红点），不等人点下载。</summary>
        void OnBankLocalReadyForUi(string bankName)
        {
            if (!IsOpen || Root == null || string.IsNullOrEmpty(bankName))
                return;
            Root.schedule.Execute(() =>
            {
                if (IsOpen)
                    RefreshAllStates(force: true);
            });
        }

        void RefreshAllStates(bool force = false)
        {
            int epoch = BankDownloader.ProgressEpoch;
            int currentId = GameEntry.Presets != null ? GameEntry.Presets.CurrentId : 0;
            int schemeState = GameEntry.Presets != null ? GameEntry.Presets.SchemeState : 0;
            bool busy = BankDownloader.HasBusyWork;
            bool busyCleared = _lastDownloaderBusy && !busy;
            _lastDownloaderBusy = busy;
            bool hasUser = _userTriggered.Count > 0;
            bool idsChanged = currentId != _lastSeenCurrentId
                || schemeState != _lastSeenSchemeState;

            // 就绪翻牌只靠 force（含 OnBankLocalReady）。禁止 epoch 全扫——
            // 后台弹窗开 / 用户主动下时 ProgressEpoch 极频，全扫会掉帧（闺蜜机已踩过）。
            // busy 清除再 force 一次：末包若当时探针失败，避免 !busy 早退永不再刷。
            if (force || busyCleared)
            {
                _lastProgressEpoch = epoch;
                _lastSeenCurrentId = currentId;
                _lastSeenSchemeState = schemeState;
                for (int i = 0; i < _items.Count; i++)
                    ApplyItemState(i, force: true);
                SyncPlayingAnimTick();
                return;
            }

            // 闲置时若 MarkLocalReady 只 bump epoch（无 OnBankLocalReady），
            // 粘住的未下载/失败/转圈卡靠局部自愈翻到红点，不全表扫。
            if (!busy && !hasUser && !idsChanged)
            {
                if (epoch == _lastProgressEpoch)
                    return;
                _lastProgressEpoch = epoch;
                for (int i = 0; i < _items.Count; i++)
                {
                    ItemBinding stuck = _items[i];
                    if (!stuck.HasAppliedState)
                        continue;
                    if (stuck.AppliedState != BankUiState.NotDownloaded
                        && stuck.AppliedState != BankUiState.Failed
                        && stuck.AppliedState != BankUiState.Downloading)
                        continue;
                    ApplyItemState(i, force: true);
                }

                SyncPlayingAnimTick();
                return;
            }

            _lastProgressEpoch = epoch;
            _lastSeenCurrentId = currentId;
            _lastSeenSchemeState = schemeState;

            for (int i = 0; i < _items.Count; i++)
            {
                ItemBinding item = _items[i];
                if (busy || hasUser)
                {
                    bool relevant = !item.HasAppliedState
                        || item.PresetId == currentId
                        || (item.HasAppliedState && item.AppliedState == BankUiState.Playing)
                        || (item.HasAppliedState && item.AppliedState == BankUiState.Downloading)
                        || _userTriggered.Contains(item.PresetId)
                        || ItemHasActiveDownload(item);
                    if (!relevant)
                        continue;
                }
                else
                {
                    // 仅方案/当前预设变化：刷 Playing 相关卡。
                    if (item.PresetId != currentId
                        && !(item.HasAppliedState && item.AppliedState == BankUiState.Playing))
                        continue;
                }

                ApplyItemState(i, force: false);
            }

            SyncPlayingAnimTick();
        }

        static bool ItemHasActiveDownload(in ItemBinding item)
        {
            string[] banks = item.BankNames;
            if (banks == null)
                return false;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (string.IsNullOrEmpty(bn) || BankDownloader.IsLocalReady(bn))
                    continue;
                if (BankDownloader.TryGetActiveItem(bn, out BankDownloadItem active)
                    && active != null
                    && (active.State == BankDownloadState.Downloading
                        || active.State == BankDownloadState.Pending))
                    return true;
            }

            return false;
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
                    if (active.State == BankDownloadState.Skipped
                        && string.Equals(
                            active.Error,
                            BankDownloadItem.UrlEmptyError,
                            System.StringComparison.Ordinal))
                        anyNoUrl = true;
                    else if (active.State == BankDownloadState.Downloading
                             && string.IsNullOrEmpty(active.Url))
                        anyNoUrl = true;
                    else if (active.State == BankDownloadState.Pending
                             && string.IsNullOrEmpty(active.Url))
                        anyNoUrl = true;
                    continue;
                }

                if (PresetBankCatalogGaps.IsUndownloadable(bn))
                {
                    // 无 catalog / url 空：不可下 → 重试，勿假 NotDownloaded 空点。
                    anyNoUrl = true;
                }
            }

            if (allReady)
                return BankUiState.AllReady;

            bool userTriggered = item.UserTriggered || _userTriggered.Contains(item.PresetId);
            bool userAttemptLive = CardUserAttemptLive(item.BankNames);
            bool unresolved = anyNoUrl || CardHasUnresolvedFailure(item.BankNames);
            switch (PresetCardUiState.Resolve(false, userTriggered, userAttemptLive, unresolved))
            {
                case PresetDownloadVisual.Downloading:
                    return BankUiState.Downloading;
                case PresetDownloadVisual.Retry:
                    return BankUiState.Failed;
                default:
                    return BankUiState.NotDownloaded;
            }
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

            // F4 switching gate. Stale SchemeState=1 with no iris → heal then allow click.
            if (presets != null && presets.IsTransitioning)
            {
                IslandMainForm.Current?.HealStalePresetTransitionLatch();
                if (presets.IsTransitioning)
                {
                    UiDialogService.ShowToast(BusyToastText, anchoredY: 0f);
                    return;
                }
            }

            if (GameEntry.Weather != null && GameEntry.Weather.IsWeatherBusy)
            {
                UiDialogService.ShowToast(BusyToastText, anchoredY: 0f);
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
                    // 同一预设已在下：只暖优先队列，避免 Start+abortOther 重入。
                    if (PendingDownloadIntent.IsPresetMatch(item.PresetId))
                    {
                        BankDownloader.PriorityBanks(item.BankNames);
                        break;
                    }

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

            // 区域进度窗硬占用：先判再钉意图，避免环闪一下。
            if (UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: true))
            {
                UiDialogService.ShowToast(UserBankDownloadFlow.BusyToastText, anchoredY: 0f);
                return;
            }

            ItemBinding captured = item;
            int presetId = captured.PresetId;
            bool saveSuppressPushed = false;

            // 先清失败标记再翻进度环，否则卡片会停在重试图标上，看起来点了没反应。
            BankDownloader.ArmUserRetry(banks);
            _userTriggered.Add(presetId);
            PendingDownloadIntent.SetPreset(
                presetId,
                PendingDownloadIntent.EnterSource.UserClick);
            RefreshAllStates(force: true);

            UserBankDownloadFlow.Start(
                banks,
                new DownloadUiOptions
                {
                    // 预设：蜂窝才出流量窗；不开「下载进度」弹窗，进度走卡片环。
                    SuppressProgressDialog = true,
                    OnCancel = () =>
                    {
                        if (PendingDownloadIntent.IsPresetMatch(presetId))
                            PendingDownloadIntent.Clear();
                        _userTriggered.Remove(presetId);
                        ApplyItemState(captured.Index, force: true);
                    },
                },
                onComplete: () =>
                {
                    if (saveSuppressPushed)
                        IslandPlayerPosSave.PopSaveSuppress();
                },
                onBanksDone: result => OnPresetBanksDone(presetId, result),
                onDownloadStarting: () =>
                {
                    // 意图已在点击时写入；这里只做保存抑制，避免同意链晚到覆盖已点的下一张。
                    IslandPlayerPosSave.PushSaveSuppress();
                    saveSuppressPushed = true;
                    RefreshAllStates(force: true);
                },
                prioritize: false,
                preempt: true,
                // 未下载点击：打断其它 User，本卡优先出进度。
                abortOtherUserDownloads: true,
                preemptBusyBatch: true);
        }

        void OnPresetBanksDone(int presetId, IReadOnlyList<BankDownloadItem> result) =>
            OnPresetBanksDoneAsync(presetId, result).Forget();

        async UniTaskVoid OnPresetBanksDoneAsync(
            int presetId,
            IReadOnlyList<BankDownloadItem> result)
        {
            // 注意：保存抑制的出栈由 DownloadBanksAsync 的 finally 统一负责，这里不要重复 Pop。
            IslandPresetAsset preset = GameEntry.Presets?.GetById(presetId);
            string[] banks = preset != null ? preset.BankNames : null;

            // 双保险：Flow 已 MarkResultLocalReady；这里再钉 result 就绪项，
            // 避免 Entries.Remove 后探针窗导致同步段 AreBanksLocallyReady=false。
            // 勿对本卡未齐 bank 盲 Mark——会把仍在静默队列的包假成已下载。
            MarkReadyBanksFromResult(result);

            // 本路 pending 可能先结束，同包其它 bank 仍在后台队列；
            // 等本卡 banks 齐或队列空闲，勿过早清 _userTriggered 导致图标闪回未下载。
            bool allLocal = AreBanksLocallyReady(banks);
            if (!allLocal && CardUserAttemptLive(banks))
                allLocal = await WaitPresetBanksSettled(banks);

            if (!allLocal)
            {
                if (CardUserAttemptLive(banks))
                {
                    // 仍在用户槽：保持转圈，等这轮出结果。
                    if (Root != null)
                        RefreshAllStates(force: true);
                    return;
                }

                // 这轮失败或已降回静默：收掉进度环，失败卡显示重试。
                _userTriggered.Remove(presetId);
                if (PendingDownloadIntent.IsPresetMatch(presetId))
                    PendingDownloadIntent.Clear();
                if (Root != null)
                    RefreshAllStates(force: true);
                return;
            }

            // 本卡齐了：正向缓存 + 先 force 翻红点/Ready，再清 _userTriggered。
            if (banks != null && banks.Length > 0)
                BankDownloader.MarkLocalReady(banks);
            if (Root != null)
                RefreshAllStates(force: true);
            _userTriggered.Remove(presetId);

            // 仅用户点卡 / 健康智慧推荐才自动进场；静默 KickSilentAll 落盘不得切预设。
            if (!PendingDownloadIntent.ShouldAutoEnterPreset(presetId))
            {
                if (PendingDownloadIntent.IsPresetMatch(presetId))
                    PendingDownloadIntent.Clear();
                if (Root != null)
                    RefreshAllStates(force: true);
                return;
            }

            PendingDownloadIntent.Clear();
            if (preset == null)
            {
                if (Root != null)
                    RefreshAllStates(force: true);
                return;
            }

            // 已跳转：记已听，不显示红点。
            MarkHeard(preset.DisplayName);
            if (Root != null)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].PresetId == presetId)
                        _selected = i;
                }

                RefreshAllStates(force: true);
                CloseSelfImmediate();
            }

            GameEntry.Presets?.RequestTransition(presetId);
        }

        /// <summary>
        /// 等到本预设 banks 全部 IsLocalReady，或相关 bank 不再 busy（失败/无包）后停止。
        /// </summary>
        static async UniTask<bool> WaitPresetBanksSettled(string[] banks)
        {
            if (banks == null || banks.Length == 0)
                return true;

            const float idleGraceSeconds = 0.4f;
            const float maxWaitSeconds = 180f;
            float deadline = Time.realtimeSinceStartup + maxWaitSeconds;
            float lastBusyAt = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (AreBanksLocallyReady(banks))
                    return true;

                if (AnyPresetBankBusy(banks))
                    lastBusyAt = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - lastBusyAt >= idleGraceSeconds)
                    return AreBanksLocallyReady(banks);

                await UniTask.Delay(100, DelayType.UnscaledDeltaTime);
            }

            return AreBanksLocallyReady(banks);
        }

        static bool CardUserAttemptLive(string[] banks)
        {
            if (banks == null)
                return false;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (!string.IsNullOrEmpty(bn) && BankDownloader.IsUserAttemptLive(bn))
                    return true;
            }

            return false;
        }

        static bool CardHasUnresolvedFailure(string[] banks)
        {
            if (banks == null)
                return false;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (string.IsNullOrEmpty(bn) || BankDownloader.IsLocalReady(bn))
                    continue;
                if (BankDownloader.HasUnresolvedFailure(bn))
                    return true;
            }

            return false;
        }

        static bool AnyPresetBankBusy(string[] banks)
        {
            if (banks == null)
                return false;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (!string.IsNullOrEmpty(bn) && BankDownloader.IsBankBusy(bn))
                    return true;
            }

            return false;
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
                if (!BankDownloader.IsLocalReady(bn)
                    && !BankLoadGate.CanEnterWithoutOfficialFile(bn))
                    return false;
            }
            return true;
        }

        static void MarkReadyBanksFromResult(IReadOnlyList<BankDownloadItem> result)
        {
            if (result == null || result.Count == 0)
                return;
            for (int i = 0; i < result.Count; i++)
            {
                BankDownloadItem item = result[i];
                if (item == null || string.IsNullOrEmpty(item.BankName))
                    continue;
                if (item.State == BankDownloadState.Ready
                    || (item.State == BankDownloadState.Skipped
                        && !string.Equals(
                            item.Error,
                            BankDownloadItem.UrlEmptyError,
                            StringComparison.Ordinal)))
                    BankDownloader.MarkLocalReady(item.BankName);
            }
        }

        // -----------------------------------------------------------------------
        // Pointer / scroll
        // -----------------------------------------------------------------------

        /// <summary>
        /// Device touch also synthesizes compatibility mouse events (pointerId 0).
        /// Tracking the mouse copy while a finger is down desyncs scroll from the finger.
        /// </summary>
        static bool IsPrimaryFingerOrMouse(IPointerEvent evt)
        {
            if (evt.pointerType == UnityEngine.UIElements.PointerType.touch || evt.pointerType == UnityEngine.UIElements.PointerType.pen)
                return true;
            if (evt.pointerType != UnityEngine.UIElements.PointerType.mouse)
                return false;
            return Input.touchCount == 0;
        }

        void OnListPointerDown(PointerDownEvent evt)
        {
            if (_list == null)
                return;
            if (!IsPrimaryFingerOrMouse(evt))
                return;

            // Compatibility mouse often wins the race; retarget to the real finger.
            if (_pointerId >= 0)
            {
                if (_pointerId == PointerId.mousePointerId
                    && evt.pointerType != UnityEngine.UIElements.PointerType.mouse)
                {
                    if (_captured && _list.HasPointerCapture(_pointerId))
                        _list.ReleasePointer(_pointerId);
                    _pointerId = evt.pointerId;
                    _captured = false;
                    _dragStartX = evt.position.x;
                    _scrollStartX = _list.scrollOffset.x;
                    ResetVelocitySample(evt.position.x);
                    _pressIndex = FindItemIndexAt(evt.position);
                }
                return;
            }

            StopCoast();
            _pointerId = evt.pointerId;
            _dragging = true;
            _moved = false;
            _captured = false;
            // Panel coords — same space as scrollOffset (Unity ScrollView formula).
            _dragStartX = evt.position.x;
            _scrollStartX = _list.scrollOffset.x;
            ResetVelocitySample(evt.position.x);
            _pressIndex = FindItemIndexAt(evt.position);
            RevealScrollbar();
        }

        void OnListPointerMove(PointerMoveEvent evt)
        {
            if (!_dragging || evt.pointerId != _pointerId || _list == null)
                return;

            // Starve ScrollView's contentContainer touch pan for this press.
            evt.StopImmediatePropagation();

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

            float now = Time.unscaledTime;
            if (_moved)
                UpdateVelocitySample(evt.position.x, now);
            else
            {
                _lastPointerX = evt.position.x;
                _lastPointerTime = now;
            }

            if (!_moved)
                return;

            // scrollOffset = start − fingerΔ → content translate stays under the finger.
            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            float next = Mathf.Clamp(_scrollStartX - dx, 0f, max);
            SetScrollX(next);
            RevealScrollbar();
        }

        void OnListPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != _pointerId)
                return;

            bool wasMoved = _moved;
            int pressIndex = _pressIndex;
            float releaseVelocity = wasMoved
                ? FinalizeReleaseVelocity(evt.position.x, Time.unscaledTime)
                : 0f;

            if (_captured && _list != null && _list.HasPointerCapture(evt.pointerId))
                _list.ReleasePointer(evt.pointerId);

            _dragging = false;
            _pointerId = -1;
            _captured = false;
            _pressIndex = -1;
            _moved = false;
            _scrollVelocity = 0f;
            SyncScrollTrack();

            if (wasMoved && Mathf.Abs(releaseVelocity) >= MinCoastStartSpeed)
                StartCoast(releaseVelocity * CoastVelocityScale);
            else
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
            _scrollVelocity = 0f;
            StopCoast();
            SyncScrollTrack();
            ScheduleScrollbarHide();
        }

        void ResetVelocitySample(float pointerX)
        {
            float now = Time.unscaledTime;
            _lastPointerX = pointerX;
            _lastPointerTime = now;
            _velAnchorX = pointerX;
            _velAnchorTime = now;
            _scrollVelocity = 0f;
        }

        void UpdateVelocitySample(float pointerX, float now)
        {
            _lastPointerX = pointerX;
            _lastPointerTime = now;
            float window = now - _velAnchorTime;
            if (window < VelocitySampleWindow)
                return;

            // Trailing-window velocity; scrollOffset moves opposite the finger.
            _scrollVelocity = -((pointerX - _velAnchorX) / window);
            _velAnchorX = pointerX;
            _velAnchorTime = now;
        }

        float FinalizeReleaseVelocity(float pointerX, float now)
        {
            float window = now - _velAnchorTime;
            if (window >= MinVelocitySampleDt)
                _scrollVelocity = -((pointerX - _velAnchorX) / window);
            return Mathf.Clamp(_scrollVelocity, -MaxCoastSpeed, MaxCoastSpeed);
        }

        void StartCoast(float velocity)
        {
            if (_list == null || Root == null)
                return;

            _coastVelocity = Mathf.Clamp(velocity, -MaxCoastSpeed, MaxCoastSpeed);
            if (Mathf.Abs(_coastVelocity) < MinCoastStartSpeed)
            {
                ScheduleScrollbarHide();
                return;
            }

            _coastTick?.Pause();
            _coastX = _list.scrollOffset.x;
            _coastLastTime = Time.unscaledTime;
            RevealScrollbar();
            _coastTick = Root.schedule.Execute(TickCoast).Every((long)CoastTickMs);
        }

        void TickCoast()
        {
            if (_list == null || _dragging)
            {
                StopCoast();
                return;
            }

            float now = Time.unscaledTime;
            float dt = Mathf.Clamp(now - _coastLastTime, 0.001f, 0.05f);
            _coastLastTime = now;

            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            float prevV = _coastVelocity;
            float decel = CoastDeceleration * Mathf.Sign(_coastVelocity);
            _coastVelocity -= decel * dt;
            // Crossed zero → stop (no reverse bounce).
            if (prevV * _coastVelocity <= 0f || Mathf.Abs(_coastVelocity) < MinCoastStopSpeed)
            {
                StopCoast();
                ScheduleScrollbarHide();
                return;
            }

            _coastX = Mathf.Clamp(_coastX + prevV * dt, 0f, max);
            SetScrollX(_coastX);

            if (max <= 0.01f || _coastX <= 0f || _coastX >= max)
            {
                StopCoast();
                ScheduleScrollbarHide();
            }
        }

        void StopCoast()
        {
            _coastTick?.Pause();
            _coastTick = null;
            _coastVelocity = 0f;
        }

        void SetScrollX(float x)
        {
            if (_list == null)
                return;
            _drivingScroll = true;
            _list.scrollOffset = new Vector2(x, 0f);
            _drivingScroll = false;
            SyncScrollTrack();
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
            // Our SetScrollX already syncs the thumb; skip hide-timer churn while driving.
            if (_drivingScroll)
                return;
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
            SetScrollX(Mathf.Clamp(target, 0f, max));
        }

        void SyncScrollTrack()
        {
            if (_scrollThumb == null || _list == null)
                return;

            float max = Mathf.Max(0f, ContentWidth() - ViewportWidth());
            float t = max > 0.01f ? Mathf.Clamp01(_list.scrollOffset.x / max) : 0f;
            float travel = TrackWidth - ThumbWidth;
            // translate avoids Layout Recalculation (marginLeft was dirtying the sheet).
            _scrollThumb.style.translate = new Translate(t * travel, 0f);
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
            // 后台直接返回，不 Pause schedule。停掉之后要等下一次 Playing 才会再挂上。
            if (!BackgroundVectorUi.ShouldAnimateVectorUi(AudioAppLifecycle.IsInBackground))
                return;

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

        static void MarkHeard(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return;
            DownloadedUnplayedManager.MarkPlayed(DownloadedUnplayedManager.PresetKey(displayName));
        }

    }
}
