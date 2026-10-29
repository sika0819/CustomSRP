using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using Game.BoluoApi.Music;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    public class MusicForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;
        protected override bool HidesBottomHudIcons => true;

        /// <summary>Boluo InGameYinYueView <c>yinyuetanchuangkapian</c> anchoredY (bottom pivot).</summary>
        protected override float SheetBottomOffset => 120f;

        // -----------------------------------------------------------------------
        // Runtime state
        // -----------------------------------------------------------------------

        /// <summary>Music that the user intends to play after its bank finishes downloading.</summary>
        (int album, int track)? _pendingMusic;

        /// <summary>Bumps when a newer download click supersedes an in-flight play-after-download.</summary>
        int _playAfterDownloadGen;

        const int BankDecodePollMs = 50;
        const int BankDecodeWaitMs = 8000;

        /// <summary>Currently centred album index.</summary>
        int _centerAlbum;

        /// <summary>Track names for the current album (used as ListView itemsSource).</summary>
        List<string> _trackNames = new();

        // -----------------------------------------------------------------------
        // Scrollbar reveal timing (Boluo InGameYinYueView).
        // -----------------------------------------------------------------------

        const float ScrollbarIdleHideSeconds = 3f;
        const float ScrollbarFadeSeconds = 0.35f;
        const float ModeTipSeconds = 1.4f;
        const float ModeTipFadeSeconds = 0.25f;

        // -----------------------------------------------------------------------
        // Album strip: measure each name, pack with a fixed edge gap (no ellipsis).
        // -----------------------------------------------------------------------

        /// <summary>Fixed gap between one name's glyph end and the next name's glyph start.</summary>
        const float AlbumNameGap = 40f;
        const float AlbumFontUnselected = 48f;
        const float AlbumFontSelected = 72f;
        /// <summary>huadong viewport width — selected name centre sits at half of this.</summary>
        const float AlbumViewportWidth = 788f;
        const float AlbumViewportCenter = AlbumViewportWidth * 0.5f;
        const int AlbumStripCopies = 3;           // Triple strip for seamless loop.
        const string AlbumLabelClass = "game-music-form__album-label";
        const string AlbumLabelSelectedModifier = "game-music-form__album-label--selected";
        const string AlbumLabelUnselectedModifier = "game-music-form__album-label--unselected";

        /// <summary>Packed slot origins / widths (albumCount × copies). Frozen while dragging.</summary>
        readonly List<float> _albumSlotLeft = new List<float>();
        readonly List<float> _albumSlotWidth = new List<float>();
        /// <summary>Content distance from copy0 start to copy1 start (loop period).</summary>
        float _albumCopyWidth;

        // Track-title marquee (Boluo dual-copy horizontal scroll inside mask).
        const float TrackMarqueeGap = 160f;       // Space between copy A and copy B.
        const float TrackMarqueeSpeed = 80f;      // px/s leftward.
        const float TrackMarqueePauseSeconds = 3f;
        const long TrackMarqueeTickMs = 16;

        // -----------------------------------------------------------------------
        // Visual element caches
        // -----------------------------------------------------------------------

        ListView _list;
        ScrollView _scrollView;
        Scroller _vScroller;
        IVisualElementScheduledItem _hideSchedule;
        IVisualElementScheduledItem _fadeSchedule;
        float _scrollerAlpha;
        float _fadeFrom;
        float _fadeTo;
        float _fadeElapsed = -1f;
        bool _revealGate;
        float _ignoreProgrammaticUntil;

        // ListView disables touch-drag scrolling (m_TouchDraggingAllowed=false).
        // Drive vertical scrollOffset ourselves — same pattern as PresetForm.
        const float ListDragSlop = 8f;
        int _listPointerId = -1;
        bool _listPointerDown;
        bool _listDragging;
        bool _listCaptured;
        float _listDragStartY;
        float _listScrollStartY;
        bool _suppressTrackClick;
        /// <summary>「全部歌曲N首」— TextElement only; Label :hover washes grey to white.</summary>
        TextElement _title;

        VisualElement _albumContainer;
        VisualElement _albumContent;
        /// <summary>Host X offset via <see cref="IStyle.translate"/> (avoids layout recalc).</summary>
        float _albumOffsetX;
        /// <summary>Visual labels (albumCount × <see cref="AlbumStripCopies"/> for loop).</summary>
        /// <remarks>Use <see cref="TextElement"/> not <see cref="Label"/> — Unity's default
        /// Label theme washes :hover text to white on the white card (invisible).</remarks>
        readonly List<TextElement> _albumLabels = new List<TextElement>();
        readonly List<EventCallback<ClickEvent>> _albumClicks = new List<EventCallback<ClickEvent>>();
        static readonly Color AlbumTextColor = new Color(24f / 255f, 24f / 255f, 25f / 255f, 1f);
        // Grey subtitle under the album strip — must stay readable on the white card.
        static readonly Color TitleTextColor = new Color(135f / 255f, 135f / 255f, 145f / 255f, 1f);
        static readonly Color TrackTextColor = new Color(0.094f, 0.094f, 0.098f, 1f);
        static readonly Color TrackPlayingColor = new Color(0f, 0.639f, 0.459f, 1f);
        VisualElement _modeIcon;
        float _dragStartX;
        float _dragStartY;
        /// <summary>Content translate X at PointerDown — drag offsets accumulate from this.</summary>
        float _dragStartLeft;
        /// <summary>True once pointer has moved past the drag threshold during this press.</summary>
        bool _isDragging;
        /// <summary>True between PointerDown and the matching PointerUp so we don't snap on click.</summary>
        bool _pointerDownInStrip;
        int _dragPointerId = -1;
        const float DragActivationPixels = 6f;
        IVisualElementScheduledItem _albumSnapSchedule;

        // ICON_PLAYING equalizer: 00001–00059 @ ~30fps ≈ 2s loop.
        const int PlayingFrameCount = 59;
        const long PlayingFrameIntervalMs = 33;
        static Sprite[] _playingFrames;
        IVisualElementScheduledItem _playingAnimSchedule;
        int _playingFrameIndex;

        // Track-row status icons (Boluo MusicNotDownload / MusicLoading / MusicError / MusicRed).
        static Sprite _iconNotDownload;
        static Sprite _iconDownloading;
        static Sprite _iconDownloadError;
        static Sprite _iconDownloadSuccess;
        static bool _statusIconsLoaded;
        IVisualElementScheduledItem _downloadSpinSchedule;
        float _downloadSpinDegrees;
        const float DownloadSpinDegreesPerTick = 6f; // ~180°/s @ 33ms

        // -----------------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------------

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);

            DownloadedUnplayedManager.EnsureLoaded();

            // Force TextElement title — pooled forms may still carry a Label from an
            // older UXML, and slide-up under the cursor re-triggers :hover → white.
            EnsureTitleElement();

            // Album strip: drag with center snap (boundary stop, no wrap).
            _albumContainer = Root.Q("huadong");
            if (_albumContainer != null)
            {
                SetupAlbumDrag();
            }

            // Play mode button (Boluo InGameYinYueView MODE_BTN: yy_danqu/list/suiji).
            _modeIcon = Root.Q("modeIcon");
            VisualElement modeButton = Root.Q("modeButton");
            if (modeButton != null)
            {
                modeButton.pickingMode = PickingMode.Position;
                modeButton.BringToFront();
            }
            if (_modeIcon != null)
                _modeIcon.pickingMode = PickingMode.Ignore;
            OnClick("modeButton", OnClickPlayMode);
            UpdateModeButton(MusicPlaylist.Instance.PlayMode);

            // Music list (dialog card is picking Ignore — list must accept hits).
            _list = Root.Q<ListView>("musicList");
            if (_list != null)
                _list.pickingMode = PickingMode.Position;

            // Pooled form: drop a stale handler before re-subscribing.
            MusicPlaylist.Instance.TrackChanged -= OnPlaylistTrackChanged;
            MusicPlaylist.Instance.TrackChanged += OnPlaylistTrackChanged;

            EnsurePlayingFramesLoaded();
            EnsureStatusIconsLoaded();
            StartPlayingAnimLoop();
            StartDownloadSpinLoop();

            if (MusicCatalog.IsReady)
                SyncOpenToNowPlaying();
            else
                MusicCatalog.OnCatalogReady += OnCatalogReady;

            BindScrollbarReveal();

            // Slide-up lands the card under the pointer; re-lock colours after the
            // animation so a stale :hover resolve cannot leave white text.
            Root.schedule.Execute(RefreshTitle).StartingIn(50);
            Root.schedule.Execute(RefreshTitle).StartingIn(360);
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            MusicPlaylist.Instance.TrackChanged -= OnPlaylistTrackChanged;
            MusicCatalog.OnCatalogReady -= OnCatalogReady;
            StopPlayingAnimLoop();
            StopDownloadSpinLoop();
            UnbindScrollbarReveal();
            UnbindAlbumDrag();
            ClearAlbumClicks();
            StopAllTrackMarquees();
            _title = null;
            base.OnClose(isShutdown, userData);
        }

        void StopAllTrackMarquees()
        {
            if (_list == null)
                return;
            _list.Query("titleMask").ForEach(mask =>
            {
                if (mask.userData is TrackTitleMarquee m)
                    StopTrackMarquee(m);
            });
        }

        void ClearAlbumClicks()
        {
            for (int i = 0; i < _albumLabels.Count && i < _albumClicks.Count; i++)
            {
                _albumLabels[i]?.UnregisterCallback(_albumClicks[i]);
            }
            _albumClicks.Clear();
        }

        /// <summary>
        /// Drop pooled strip visuals so a reopen cannot keep detached TextElements
        /// or a wrong content host (labels off-screen → "专辑名看不到").
        /// </summary>
        void ClearAlbumStripVisuals()
        {
            ClearAlbumClicks();
            for (int i = 0; i < _albumLabels.Count; i++)
                _albumLabels[i]?.RemoveFromHierarchy();
            _albumLabels.Clear();
            _albumSlotLeft.Clear();
            _albumSlotWidth.Clear();
            _albumCopyWidth = 0f;
            _albumContent?.Clear();
        }

        static void MarkHeard(string albumName, string musicName) =>
            DownloadedUnplayedManager.MarkPlayed(DownloadedUnplayedManager.MusicKey(albumName, musicName));

        /// <summary>True when red-dot should show (unplayed + within 24h first-show).</summary>
        static bool ShouldShowRedDot(string albumName, string musicName) =>
            DownloadedUnplayedManager.ShouldShowRedDot(
                DownloadedUnplayedManager.MusicKey(albumName, musicName));

        // -----------------------------------------------------------------------
        // Catalog ready callback
        // -----------------------------------------------------------------------

        void OnCatalogReady()
        {
            MusicCatalog.OnCatalogReady -= OnCatalogReady;
            Root.schedule.Execute(SyncOpenToNowPlaying);
        }

        /// <summary>
        /// F1: align album strip + track list to the current playback session
        /// (or album 0 / track 0 when nothing has been selected yet).
        /// </summary>
        void SyncOpenToNowPlaying()
        {
            var music = GameEntry.Music;
            int albumCount = MusicCatalog.AlbumCount;
            if (albumCount <= 0)
            {
                _centerAlbum = 0;
                RefreshAll();
                return;
            }

            int album = music != null && music.CurrentAlbumIndex >= 0
                ? music.CurrentAlbumIndex
                : 0;
            _centerAlbum = ((album % albumCount) + albumCount) % albumCount;
            RefreshAll();

            int track = music != null ? music.CurrentTrackIndex : -1;
            ScrollTrackListTo(track);
        }

        // -----------------------------------------------------------------------
        // Refresh helpers
        // -----------------------------------------------------------------------

        void RefreshAll()
        {
            int count = MusicCatalog.AlbumCount;
            if (count > 0)
                _centerAlbum = ((_centerAlbum % count) + count) % count;
            else
                _centerAlbum = 0;
            // Place strip BEFORE highlight — never highlight from a stale
            // offset (pooled reopen left the visible centre at opacity 0.3,
            // which reads as "white" on the card).
            if (_albumContent != null && count > 0)
                SetAlbumOffsetX(TargetLeftForAlbum(_centerAlbum, count));
            RefreshAlbumStrip();
            RefreshTrackList();
            RefreshTitle();
        }

        void ScrollTrackListTo(int trackIndex)
        {
            if (_list == null || trackIndex < 0 || trackIndex >= _trackNames.Count)
                return;
            // Rebuild may not have laid out yet — scroll on next frame.
            int index = trackIndex;
            _list.schedule.Execute(() =>
            {
                if (_list == null || index < 0 || index >= _trackNames.Count)
                    return;
                _list.ScrollToItem(index);
            }).StartingIn(16);
        }

        /// <summary>
        /// Replace a pooled <see cref="Label"/> title with <see cref="TextElement"/> so
        /// Unity's default <c>.unity-label:hover</c> cannot wash grey text to white.
        /// </summary>
        void EnsureTitleElement()
        {
            if (Root == null)
                return;

            VisualElement existing = Root.Q("title");
            if (existing is TextElement te && existing is not Label)
            {
                _title = te;
                ApplyTitleChrome(_title);
                return;
            }

            VisualElement parent = existing?.parent ?? Root.Q("musicDialogkapian");
            if (parent == null)
                return;

            int index = existing != null ? parent.IndexOf(existing) : parent.childCount;
            existing?.RemoveFromHierarchy();

            _title = new TextElement { name = "title" };
            ApplyTitleChrome(_title);
            parent.Insert(Mathf.Clamp(index, 0, parent.childCount), _title);
        }

        /// <summary>
        /// Lock InGameYinYueView title look (42px grey PingFang, no letter-spacing)
        /// after pooled reopen / hover wash.
        /// </summary>
        void ApplyTitleChrome(TextElement title)
        {
            if (title == null)
                return;
            title.AddToClassList("game-music-form__title");
            title.pickingMode = PickingMode.Ignore;
            title.style.color = TitleTextColor;
            title.style.fontSize = 42f;
            title.style.letterSpacing = 0f;
            title.style.unityTextAlign = TextAnchor.MiddleLeft;
            title.style.whiteSpace = WhiteSpace.NoWrap;
        }

        void RefreshTitle()
        {
            if (_title == null || _title.panel == null)
                EnsureTitleElement();
            if (_title == null)
                return;

            ApplyTitleChrome(_title);

            if (!MusicCatalog.IsReady)
            {
                _title.text = "加载中...";
                return;
            }

            // Per InGameYinYueView.RefreshList: title reports the size of the
            // currently-visible album, not the whole catalog sum.
            int count = MusicCatalog.GetTracks(_centerAlbum).Count;
            _title.text = $"全部歌曲{count}首";
        }

        void RefreshAlbumStrip()
        {
            if (!MusicCatalog.IsReady || _albumContent == null)
                return;

            string[] names = MusicCatalog.GetAlbumNames();
            int albumCount = names.Length;
            if (albumCount <= 0)
                return;

            // Triple the strip so left/right drag always has neighbours (loop).
            int slotCount = albumCount * AlbumStripCopies;
            EnsureAlbumLabelSlots(slotCount);

            // Highlight: while dragging, follow the pointer; otherwise ALWAYS the
            // middle-copy slot for _centerAlbum.
            int highlightSlot;
            if (_isDragging && _albumSlotLeft.Count == slotCount)
            {
                highlightSlot = SlotIndexFromLeft(ReadAlbumContentLeft());
                if (highlightSlot < 0 || highlightSlot >= slotCount)
                    highlightSlot = albumCount + _centerAlbum;
            }
            else
            {
                highlightSlot = albumCount + _centerAlbum;
            }

            // Drag: freeze packed positions (only opacity/size chrome updates).
            // Settle / open / click: remeasure and repack so edge gap stays AlbumNameGap.
            if (!_isDragging || _albumSlotLeft.Count != slotCount)
                RelayoutAlbumSlots(names, albumCount, highlightSlot);

            ApplyAlbumSlotVisuals(names, albumCount, highlightSlot);
        }

        void EnsureAlbumLabelSlots(int slotCount)
        {
            while (_albumLabels.Count < slotCount)
            {
                var label = new TextElement();
                label.AddToClassList(AlbumLabelClass);
                label.pickingMode = PickingMode.Position;
                label.style.color = AlbumTextColor;
                label.style.position = Position.Absolute;
                label.style.top = 0;
                label.style.marginRight = 0;
                label.style.overflow = Overflow.Visible;
                label.style.whiteSpace = WhiteSpace.NoWrap;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                _albumContent.Add(label);
                _albumLabels.Add(label);
            }
            while (_albumLabels.Count > slotCount)
            {
                int last = _albumLabels.Count - 1;
                var stale = _albumLabels[last];
                if (stale != null && last < _albumClicks.Count)
                {
                    stale.UnregisterCallback(_albumClicks[last]);
                    _albumClicks.RemoveAt(last);
                }
                _albumLabels.RemoveAt(last);
                stale?.RemoveFromHierarchy();
            }

            while (_albumClicks.Count < _albumLabels.Count)
            {
                int slot = _albumClicks.Count;
                TextElement label = _albumLabels[slot];
                EventCallback<ClickEvent> click = evt =>
                {
                    if (_isDragging)
                        return;
                    if (evt.currentTarget is TextElement tapped && tapped.userData is int logical)
                        OnAlbumClicked(logical);
                };
                label?.RegisterCallback(click);
                _albumClicks.Add(click);
            }
        }

        /// <summary>
        /// Pack labels by measured glyph width with a constant <see cref="AlbumNameGap"/>
        /// between one name's end and the next name's start. No ellipsis / clip.
        /// </summary>
        void RelayoutAlbumSlots(string[] names, int albumCount, int highlightSlot)
        {
            int slotCount = albumCount * AlbumStripCopies;
            _albumSlotLeft.Clear();
            _albumSlotWidth.Clear();
            _albumContent.style.height = 101f;
            _albumContent.style.overflow = Overflow.Visible;

            float x = 0f;
            for (int i = 0; i < slotCount; i++)
            {
                var label = _albumLabels[i];
                int logical = i % albumCount;
                string name = names[logical];
                bool isSelected = i == highlightSlot;
                float fontSize = isSelected ? AlbumFontSelected : AlbumFontUnselected;

                label.text = name;
                label.userData = logical;
                label.style.fontSize = fontSize;

                float textW = MeasureAlbumNameWidth(label, name, fontSize);
                _albumSlotLeft.Add(x);
                _albumSlotWidth.Add(textW);

                label.style.left = x;
                label.style.width = textW;
                label.style.minWidth = textW;
                label.style.maxWidth = textW;
                label.style.overflow = Overflow.Visible;

                x += textW + AlbumNameGap;
            }

            _albumCopyWidth = albumCount > 0 && _albumSlotLeft.Count > albumCount
                ? _albumSlotLeft[albumCount] - _albumSlotLeft[0]
                : Mathf.Max(0f, x - AlbumNameGap);
            _albumContent.style.width = Mathf.Max(0f, x - AlbumNameGap);
        }

        void ApplyAlbumSlotVisuals(string[] names, int albumCount, int highlightSlot)
        {
            for (int i = 0; i < _albumLabels.Count; i++)
            {
                var label = _albumLabels[i];
                if (label == null)
                    continue;
                int logical = i % albumCount;
                label.text = names[logical];
                label.userData = logical;
                label.style.color = AlbumTextColor;
                bool isSelected = i == highlightSlot;
                label.style.opacity = isSelected ? 1f : 0.3f;
                // During drag widths stay frozen; font may grow but must not reflow.
                if (!_isDragging)
                    label.style.fontSize = isSelected ? AlbumFontSelected : AlbumFontUnselected;
                else if (i < _albumSlotWidth.Count)
                {
                    // Keep box width; allow selected glyphs to paint outside the box.
                    label.style.fontSize = isSelected ? AlbumFontSelected : AlbumFontUnselected;
                    label.style.width = _albumSlotWidth[i];
                    label.style.minWidth = _albumSlotWidth[i];
                    label.style.maxWidth = _albumSlotWidth[i];
                    label.style.left = _albumSlotLeft[i];
                    label.style.overflow = Overflow.Visible;
                }
                label.EnableInClassList(AlbumLabelSelectedModifier, isSelected);
                label.EnableInClassList(AlbumLabelUnselectedModifier, !isSelected);
            }
        }

        static float MeasureAlbumNameWidth(TextElement el, string text, float fontSize)
        {
            if (el == null || string.IsNullOrEmpty(text))
                return AlbumNameGap;

            el.style.fontSize = fontSize;
            Vector2 size = el.MeasureTextSize(
                text,
                0f, VisualElement.MeasureMode.Undefined,
                0f, VisualElement.MeasureMode.Undefined);

            float w = size.x;
            if (w < 1f)
                w = text.Length * fontSize; // CJK ≈ 1em fallback
            return Mathf.Max(w, 1f);
        }

        /// <summary>Current strip host X (translate); kept in <see cref="_albumOffsetX"/>.</summary>
        float ReadAlbumContentLeft() => _albumOffsetX;

        void SetAlbumOffsetX(float x)
        {
            _albumOffsetX = x;
            if (_albumContent == null)
                return;
            _albumContent.style.translate = new Translate(x, 0f);
        }

        void OnAlbumClicked(int albumIndex)
        {
            if (!MusicCatalog.IsReady)
                return;
            if (_isDragging)
                return;
            int count = MusicCatalog.AlbumCount;
            if (count <= 0)
                return;
            albumIndex = ((albumIndex % count) + count) % count;
            if (albumIndex == _centerAlbum)
            {
                SnapAlbumContent(albumIndex);
                return;
            }
            _centerAlbum = albumIndex;
            RefreshAlbumStrip();
            RefreshTrackList();
            RefreshTitle();
            SnapAlbumContent(albumIndex);
        }

        /// <summary>One copy width in content space — used to wrap the triple strip.</summary>
        float AlbumCircumference(int albumCount)
        {
            if (_albumCopyWidth > 1f)
                return _albumCopyWidth;
            // Fallback before first layout: rough estimate from gap packing.
            return Mathf.Max(albumCount, 1) * (AlbumFontUnselected * 2f + AlbumNameGap);
        }

        /// <summary>
        /// Keep content.left inside the middle-copy neighbourhood so the strip loops.
        /// </summary>
        void NormalizeAlbumLoop(ref float left, int albumCount)
        {
            if (albumCount <= 0)
                return;
            float circ = AlbumCircumference(albumCount);
            if (circ < 1f)
                return;
            float anchor = TargetLeftForAlbum(0, albumCount);
            while (left > anchor + circ * 0.5f)
                left -= circ;
            while (left < anchor - circ * 1.5f)
                left += circ;
        }

        /// <summary>Absolute slot index whose centre is closest to the viewport centre.</summary>
        int SlotIndexFromLeft(float contentLeft)
        {
            if (_albumSlotLeft.Count == 0 || _albumSlotWidth.Count == 0)
                return 0;
            float viewCenterInContent = AlbumViewportCenter - contentLeft;
            int best = 0;
            float bestDist = float.MaxValue;
            int n = Mathf.Min(_albumSlotLeft.Count, _albumSlotWidth.Count);
            for (int i = 0; i < n; i++)
            {
                float center = _albumSlotLeft[i] + _albumSlotWidth[i] * 0.5f;
                float d = Mathf.Abs(center - viewCenterInContent);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        int LogicalAlbumFromLeft(float left, int albumCount)
        {
            if (albumCount <= 0)
                return 0;
            int slot = SlotIndexFromLeft(left);
            return ((slot % albumCount) + albumCount) % albumCount;
        }

        /// <summary>
        /// Content.left that puts <paramref name="albumIndex"/>'s name centre on the
        /// huadong viewport centre (middle copy of the triple strip).
        /// </summary>
        float TargetLeftForAlbum(int albumIndex, int albumCount)
        {
            if (albumCount <= 0)
                return 0f;
            albumIndex = ((albumIndex % albumCount) + albumCount) % albumCount;
            int slot = albumCount + albumIndex; // middle copy
            if (slot >= _albumSlotLeft.Count || slot >= _albumSlotWidth.Count)
            {
                // Layout not ready — force a pack around the requested centre.
                if (MusicCatalog.IsReady && _albumContent != null)
                {
                    string[] names = MusicCatalog.GetAlbumNames();
                    if (names != null && names.Length == albumCount)
                    {
                        EnsureAlbumLabelSlots(albumCount * AlbumStripCopies);
                        RelayoutAlbumSlots(names, albumCount, albumCount + albumIndex);
                    }
                }
            }
            if (slot >= _albumSlotLeft.Count || slot >= _albumSlotWidth.Count)
                return 0f;
            float nameCenter = _albumSlotLeft[slot] + _albumSlotWidth[slot] * 0.5f;
            return AlbumViewportCenter - nameCenter;
        }

        void RefreshTrackList()
        {
            _trackNames.Clear();
            if (MusicCatalog.IsReady)
            {
                var tracks = MusicCatalog.GetTracks(_centerAlbum);
                for (int i = 0; i < tracks.Count; i++)
                    _trackNames.Add(tracks[i].MusicName);
            }

            if (_list == null)
                return;

            _list.fixedItemHeight = 144;
            _list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            _list.selectionType = SelectionType.None;
            _list.makeItem = MakeItem;
            _list.bindItem = BindItem;
            _list.unbindItem = UnbindItem;
            // Drop any recycled Label rows from a previous open (pooled form) so
            // MakeItem's TextElement path is what actually binds.
            _list.itemsSource = null;
            _list.Rebuild();
            _list.itemsSource = _trackNames;
            _list.Rebuild();

            // Keep the now-playing row in view when the open album matches playback.
            var music = GameEntry.Music;
            if (music != null
                && music.CurrentAlbumIndex == _centerAlbum
                && music.CurrentTrackIndex >= 0
                && music.CurrentTrackIndex < _trackNames.Count)
            {
                ScrollTrackListTo(music.CurrentTrackIndex);
            }
        }

        // -----------------------------------------------------------------------
        // Album strip — infinite-scroll drag
        // -----------------------------------------------------------------------

        void SetupAlbumDrag()
        {
            if (_albumContainer == null)
                return;

            // Must be the named host — NEVER the first non-Label child.
            // TextElement album labels are not Label; picking the first child
            // after a bad pool state made style.left move an empty node while
            // real labels sat off-screen (overflow:hidden → invisible).
            _albumContent = _albumContainer.Q("albumContent");
            if (_albumContent == null || _albumContent is TextElement)
            {
                _albumContainer.Clear();
                _albumContent = new VisualElement { name = "albumContent" };
                _albumContainer.Add(_albumContent);
            }

            // Packed by glyph width + AlbumNameGap; host moves via style.translate.
            _albumContent.style.position = Position.Relative;
            _albumContent.style.left = 0f;
            _albumContent.style.height = 101f;
            _albumContent.style.paddingLeft = 0;
            _albumContent.pickingMode = PickingMode.Ignore;
            _albumContent.usageHints = UsageHints.DynamicTransform;
            SetAlbumOffsetX(_albumOffsetX);

            // Pooled reopen: rebuild labels into this host every open.
            ClearAlbumStripVisuals();

            _albumContainer.pickingMode = PickingMode.Position;
            // Listen on the strip itself with BubbleUp so we don't intercept
            // pointer events meant for sibling UI (list rows, mode button,
            // GameFormLogic backdrop dismiss). TrickleDown + CapturePointer
            // were the original bug: every click in the popup got swallowed,
            // backdrop dismiss never saw its ClickEvent, and label clicks
            // never resolved.
            _albumContainer.RegisterCallback<PointerDownEvent>(OnAlbumPointerDown);
            _albumContainer.RegisterCallback<PointerMoveEvent>(OnAlbumPointerMove);
            _albumContainer.RegisterCallback<PointerUpEvent>(OnAlbumPointerUp);
            _albumContainer.RegisterCallback<PointerCancelEvent>(OnAlbumPointerCancel);
            _albumContainer.RegisterCallback<PointerCaptureOutEvent>(OnAlbumPointerCaptureOut);

            // Layout may not have resolved on the first frame after slide-up.
            Root.schedule.Execute(RefreshAlbumLayout).StartingIn(0);
            Root.schedule.Execute(RefreshAlbumLayout).StartingIn(16);
            Root.schedule.Execute(RefreshAlbumLayout).StartingIn(50);
        }

        void UnbindAlbumDrag()
        {
            if (_albumContainer == null)
                return;
            // Do NOT SnapAlbumContent on close — that leaves an in-flight left
            // animation on a pooled tree and the next open starts off-screen.
            _albumSnapSchedule?.Pause();
            _albumSnapSchedule = null;
            _pointerDownInStrip = false;
            _isDragging = false;
            _dragPointerId = -1;
            _albumContainer.UnregisterCallback<PointerDownEvent>(OnAlbumPointerDown);
            _albumContainer.UnregisterCallback<PointerMoveEvent>(OnAlbumPointerMove);
            _albumContainer.UnregisterCallback<PointerUpEvent>(OnAlbumPointerUp);
            _albumContainer.UnregisterCallback<PointerCancelEvent>(OnAlbumPointerCancel);
            _albumContainer.UnregisterCallback<PointerCaptureOutEvent>(OnAlbumPointerCaptureOut);
        }

        void OnAlbumPointerDown(PointerDownEvent evt)
        {
            if (_albumContainer == null || !MusicCatalog.IsReady)
                return;
            if (evt.button != 0)
                return;

            _albumSnapSchedule?.Pause();
            _albumSnapSchedule = null;

            _pointerDownInStrip = true;
            _isDragging = false;
            _dragPointerId = evt.pointerId;
            _dragStartX = evt.position.x;
            _dragStartY = evt.position.y;
            _dragStartLeft = _albumContent != null
                ? ReadAlbumContentLeft()
                : TargetLeftForAlbum(_centerAlbum, MusicCatalog.AlbumCount);
            // Do NOT CapturePointer here — capturing on every Down steals
            // subsequent list clicks/scrolls if Up is missed. Capture only
            // after the drag threshold is crossed (see Move).
        }

        void OnAlbumPointerMove(PointerMoveEvent evt)
        {
            if (!_pointerDownInStrip || evt.pointerId != _dragPointerId)
                return;
            if (_albumContent == null)
                return;
            // Button released without Up (e.g. alt-tab) — abort so hover can't keep dragging.
            if ((evt.pressedButtons & 1) == 0)
            {
                EndAlbumPointerGesture(commit: _isDragging);
                return;
            }

            float dx = evt.position.x - _dragStartX;
            float dy = evt.position.y - _dragStartY;

            if (!_isDragging)
            {
                if (Mathf.Abs(dx) < DragActivationPixels && Mathf.Abs(dy) < DragActivationPixels)
                    return;
                _isDragging = true;
                // Capture only once drag is real so PointerUp still arrives if
                // the cursor leaves the strip — without blocking the track list
                // for a simple press that never becomes a drag.
                _albumContainer.CapturePointer(evt.pointerId);
            }

            if (Mathf.Abs(dy) > Mathf.Abs(dx) * 1.5f)
                return;

            int albumCount = MusicCatalog.AlbumCount;
            float nextLeft = _dragStartLeft + dx;
            NormalizeAlbumLoop(ref nextLeft, albumCount);
            _dragStartLeft = nextLeft - dx;
            SetAlbumOffsetX(nextLeft);

            // Visual highlight only — do NOT commit album / rebuild track list until Up.
            RefreshAlbumStrip();
        }

        void OnAlbumPointerUp(PointerUpEvent evt)
        {
            if (!_pointerDownInStrip || evt.pointerId != _dragPointerId)
                return;
            EndAlbumPointerGesture(commit: _isDragging);
        }

        void OnAlbumPointerCancel(PointerCancelEvent evt)
        {
            if (!_pointerDownInStrip || evt.pointerId != _dragPointerId)
                return;
            EndAlbumPointerGesture(commit: false);
        }

        void OnAlbumPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (!_pointerDownInStrip || evt.pointerId != _dragPointerId)
                return;
            EndAlbumPointerGesture(commit: _isDragging);
        }

        /// <summary>
        /// Finish the current strip gesture. When <paramref name="commit"/> is true,
        /// snap and switch the track list; otherwise restore the previous album.
        /// </summary>
        void EndAlbumPointerGesture(bool commit)
        {
            int pointerId = _dragPointerId;
            bool wasDragging = _isDragging;
            _pointerDownInStrip = false;
            _isDragging = false;
            _dragPointerId = -1;

            if (_albumContainer != null && pointerId >= 0 && _albumContainer.HasPointerCapture(pointerId))
                _albumContainer.ReleasePointer(pointerId);

            int albumCount = MusicCatalog.AlbumCount;
            if (albumCount <= 0 || _albumContent == null)
                return;

            if (!commit || !wasDragging)
            {
                // Click (no drag) or cancel — keep committed album; click handler may still fire.
                SnapAlbumContent(_centerAlbum);
                return;
            }

            float left = ReadAlbumContentLeft();
            NormalizeAlbumLoop(ref left, albumCount);
            int targetIndex = LogicalAlbumFromLeft(left, albumCount);

            if (targetIndex != _centerAlbum)
            {
                _centerAlbum = targetIndex;
                RefreshAlbumStrip();
                RefreshTrackList();
                RefreshTitle();
            }
            else
            {
                RefreshAlbumStrip();
            }

            SnapAlbumContent(targetIndex);
        }

        void RefreshAlbumLayout()
        {
            if (_albumContent == null)
                return;
            RefreshAlbumStrip();
            int albumCount = MusicCatalog.AlbumCount;
            SetAlbumOffsetX(TargetLeftForAlbum(_centerAlbum, albumCount));
        }

        void SnapAlbumContent(int albumIndex)
        {
            if (_albumContent == null || !MusicCatalog.IsReady)
                return;

            int albumCount = MusicCatalog.AlbumCount;
            if (albumCount <= 0)
                return;

            albumIndex = ((albumIndex % albumCount) + albumCount) % albumCount;
            float to = TargetLeftForAlbum(albumIndex, albumCount);
            float startLeft = ReadAlbumContentLeft();
            NormalizeAlbumLoop(ref startLeft, albumCount);

            // Shortest path within the middle-copy neighbourhood.
            float circ = AlbumCircumference(albumCount);
            float delta = to - startLeft;
            if (delta > circ * 0.5f)
                startLeft += circ;
            else if (delta < -circ * 0.5f)
                startLeft -= circ;

            SetAlbumOffsetX(startLeft);
            _albumSnapSchedule?.Pause();
            float start = Time.unscaledTime;
            const float Duration = 0.2f;
            _albumSnapSchedule = _albumContent.schedule.Execute(() =>
            {
                if (_albumContent == null)
                    return;
                float t = Mathf.Clamp01((Time.unscaledTime - start) / Duration);
                float eased = t * t * (3f - 2f * t);
                float left = Mathf.Lerp(startLeft, to, eased);
                SetAlbumOffsetX(left);
                if (t >= 1f)
                {
                    SetAlbumOffsetX(TargetLeftForAlbum(albumIndex, albumCount));
                    RefreshAlbumStrip();
                    _albumSnapSchedule?.Pause();
                    _albumSnapSchedule = null;
                }
            }).Every(16);
        }

        // -----------------------------------------------------------------------
        // Play mode tip
        // -----------------------------------------------------------------------

        void OnClickPlayMode()
        {
            if (GameEntry.Music == null)
                return;

            int mode = GameEntry.Music.CyclePlayMode();
            ShowModeTip(MusicPlayMode.TipText(mode));

            // Update mode button appearance.
            UpdateModeButton(mode);
        }

        void ShowModeTip(string text)
        {
            UiTipToast.ShowMessage(text, ModeTipSeconds + ModeTipFadeSeconds);
        }

        // Sprite names for the 3 play modes (yy_danqu / yy_list / yy_suiji).
        // Loaded from `Assets/GameMain/Resources/MusicForm/` (Texture2D fallback —
        // importer may still be Default, same as status icons).
        static readonly string[] ModeSpriteNames = { "yy_danqu", "yy_list", "yy_suiji" };
        static Sprite[] _modeSprites;

        void UpdateModeButton(int mode)
        {
            if (_modeIcon == null)
                return;
            EnsureModeSpritesLoaded();
            int idx = mode switch
            {
                MusicPlayMode.Single => 0,
                MusicPlayMode.Loop => 1,
                MusicPlayMode.Shuffle => 2,
                _ => 0,
            };
            var sprite = _modeSprites != null && idx < _modeSprites.Length
                ? _modeSprites[idx]
                : null;
            if (sprite != null)
                _modeIcon.style.backgroundImage = new StyleBackground(sprite);
        }

        static void EnsureModeSpritesLoaded()
        {
            if (_modeSprites != null && _modeSprites.Length == ModeSpriteNames.Length)
                return;
            _modeSprites = new Sprite[ModeSpriteNames.Length];
            int found = 0;
            for (int i = 0; i < ModeSpriteNames.Length; i++)
            {
                _modeSprites[i] = LoadMusicStatusSprite(ModeSpriteNames[i]);
                if (_modeSprites[i] != null)
                    found++;
            }
            if (found == 0)
            {
                Log.Warning("[MusicForm] Play-mode icons missing under Resources/MusicForm/ "
                    + "(yy_danqu / yy_list / yy_suiji)");
            }
        }

        // -----------------------------------------------------------------------
        // Scrollbar reveal
        // -----------------------------------------------------------------------

        void BindScrollbarReveal()
        {
            if (_list == null)
                return;
            _scrollView = _list.Q<ScrollView>();
            if (_scrollView == null)
                return;

            _scrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _scrollView.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            _scrollView.mode = ScrollViewMode.Vertical;
            _scrollView.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
            _vScroller = _scrollView.verticalScroller;
            if (_vScroller == null)
                return;

            SetScrollerAlpha(0f);
            _revealGate = false;
            _ignoreProgrammaticUntil = 0f;
            _vScroller.valueChanged += OnScrollerValueChanged;
            BindListDragScroll();
            _list.schedule.Execute(EnableRevealGate).StartingIn(32);
        }

        void UnbindScrollbarReveal()
        {
            UnbindListDragScroll();
            if (_vScroller != null)
                _vScroller.valueChanged -= OnScrollerValueChanged;
            _hideSchedule?.Pause();
            _hideSchedule = null;
            _fadeSchedule?.Pause();
            _fadeSchedule = null;
            _vScroller = null;
            _scrollView = null;
            _revealGate = false;
        }

        // -----------------------------------------------------------------------
        // List touch/mouse drag scroll (ListView blocks native touch pan)
        // -----------------------------------------------------------------------

        void BindListDragScroll()
        {
            if (_list == null)
                return;
            // Listen on ListView so empty viewport chrome still receives the gesture.
            _list.RegisterCallback<PointerDownEvent>(OnListPointerDown);
            _list.RegisterCallback<PointerMoveEvent>(OnListPointerMove);
            _list.RegisterCallback<PointerUpEvent>(OnListPointerUp);
            _list.RegisterCallback<PointerCancelEvent>(OnListPointerCancel);
            _list.RegisterCallback<PointerCaptureOutEvent>(OnListPointerCaptureOut);
        }

        void UnbindListDragScroll()
        {
            EndListPointerGesture(commitScroll: false);
            if (_list == null)
                return;
            _list.UnregisterCallback<PointerDownEvent>(OnListPointerDown);
            _list.UnregisterCallback<PointerMoveEvent>(OnListPointerMove);
            _list.UnregisterCallback<PointerUpEvent>(OnListPointerUp);
            _list.UnregisterCallback<PointerCancelEvent>(OnListPointerCancel);
            _list.UnregisterCallback<PointerCaptureOutEvent>(OnListPointerCaptureOut);
        }

        static bool IsUnderVerticalScroller(VisualElement target)
        {
            for (VisualElement ve = target; ve != null; ve = ve.parent)
            {
                if (ve.ClassListContains("unity-scroller")
                    || ve.ClassListContains("unity-base-slider")
                    || ve is Scroller)
                    return true;
            }
            return false;
        }

        void OnListPointerDown(PointerDownEvent evt)
        {
            if (_list == null || _scrollView == null || _listPointerDown)
                return;
            // Mouse: primary button only. Touch/pen report button 0.
            if (evt.button != 0)
                return;
            if (IsUnderVerticalScroller(evt.target as VisualElement))
                return;

            _listPointerDown = true;
            _listDragging = false;
            _listCaptured = false;
            _listPointerId = evt.pointerId;
            _listDragStartY = evt.position.y;
            _listScrollStartY = _scrollView.scrollOffset.y;
            _suppressTrackClick = false;
        }

        void OnListPointerMove(PointerMoveEvent evt)
        {
            if (!_listPointerDown || evt.pointerId != _listPointerId || _scrollView == null)
                return;

            float dy = evt.position.y - _listDragStartY;
            if (!_listDragging && Mathf.Abs(dy) >= ListDragSlop)
            {
                _listDragging = true;
                _suppressTrackClick = true;
                if (!_listCaptured)
                {
                    _list.CapturePointer(evt.pointerId);
                    _listCaptured = true;
                }
            }

            if (!_listDragging)
                return;

            float max = ListScrollMax();
            float next = Mathf.Clamp(_listScrollStartY - dy, 0f, max);
            _scrollView.scrollOffset = new Vector2(_scrollView.scrollOffset.x, next);
            NotifyScrollbarUserInput(bypassProgrammaticCooldown: true);
            evt.StopPropagation();
        }

        void OnListPointerUp(PointerUpEvent evt)
        {
            if (!_listPointerDown || evt.pointerId != _listPointerId)
                return;
            EndListPointerGesture(commitScroll: _listDragging);
        }

        void OnListPointerCancel(PointerCancelEvent evt)
        {
            if (!_listPointerDown || evt.pointerId != _listPointerId)
                return;
            EndListPointerGesture(commitScroll: false);
        }

        void OnListPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (!_listPointerDown || evt.pointerId != _listPointerId)
                return;
            EndListPointerGesture(commitScroll: _listDragging);
        }

        void EndListPointerGesture(bool commitScroll)
        {
            int pointerId = _listPointerId;
            bool wasDragging = _listDragging;
            _listPointerDown = false;
            _listDragging = false;
            _listPointerId = -1;

            if (_listCaptured && _list != null && pointerId >= 0 && _list.HasPointerCapture(pointerId))
                _list.ReleasePointer(pointerId);
            _listCaptured = false;

            if (wasDragging || commitScroll)
            {
                // Keep click suppressed through the synthesized ClickEvent on this Up.
                _suppressTrackClick = true;
                _list?.schedule.Execute(() => { _suppressTrackClick = false; }).StartingIn(0);
            }
        }

        float ListScrollMax()
        {
            if (_vScroller != null && !float.IsNaN(_vScroller.highValue))
                return Mathf.Max(0f, _vScroller.highValue);
            if (_scrollView == null)
                return 0f;
            VisualElement content = _scrollView.contentContainer;
            VisualElement viewport = _scrollView.contentViewport;
            if (content == null || viewport == null)
                return 0f;
            float contentH = content.layout.height;
            float viewH = viewport.layout.height;
            if (float.IsNaN(contentH) || float.IsNaN(viewH))
                return 0f;
            return Mathf.Max(0f, contentH - viewH);
        }

        void EnableRevealGate()
        {
            _revealGate = true;
            _ignoreProgrammaticUntil = Time.unscaledTime + 0.5f;
        }

        void OnScrollerValueChanged(float _)
        {
            NotifyScrollbarUserInput(bypassProgrammaticCooldown: false);
        }

        void NotifyScrollbarUserInput(bool bypassProgrammaticCooldown)
        {
            if (_vScroller == null || !_revealGate)
                return;
            if (!bypassProgrammaticCooldown && Time.unscaledTime < _ignoreProgrammaticUntil)
                return;

            FadeScrollerTo(1f);
            _hideSchedule?.Pause();
            _hideSchedule = _list.schedule.Execute(BeginIdleHide)
                .StartingIn((long)(ScrollbarIdleHideSeconds * 1000f));
        }

        void BeginIdleHide()
        {
            _hideSchedule = null;
            FadeScrollerTo(0f);
        }

        void FadeScrollerTo(float target)
        {
            _fadeFrom = _scrollerAlpha;
            _fadeTo = Mathf.Clamp01(target);
            if (Mathf.Abs(_fadeFrom - _fadeTo) < 0.01f)
            {
                SetScrollerAlpha(_fadeTo);
                _fadeElapsed = -1f;
                _fadeSchedule?.Pause();
                _fadeSchedule = null;
                return;
            }

            _fadeElapsed = 0f;
            _fadeSchedule?.Pause();
            _fadeSchedule = _list.schedule.Execute(TickFade).Every(16);
        }

        void TickFade()
        {
            if (_fadeElapsed < 0f || _vScroller == null)
            {
                _fadeSchedule?.Pause();
                _fadeSchedule = null;
                return;
            }

            _fadeElapsed += 0.016f;
            float t = Mathf.Clamp01(_fadeElapsed / ScrollbarFadeSeconds);
            SetScrollerAlpha(Mathf.Lerp(_fadeFrom, _fadeTo, t));
            if (t < 1f)
                return;

            _fadeElapsed = -1f;
            _fadeSchedule?.Pause();
            _fadeSchedule = null;
        }

        void SetScrollerAlpha(float alpha)
        {
            _scrollerAlpha = Mathf.Clamp01(alpha);
            if (_vScroller == null)
                return;
            _vScroller.style.opacity = _scrollerAlpha;
            _vScroller.pickingMode = _scrollerAlpha > 0.01f
                ? PickingMode.Position
                : PickingMode.Ignore;
        }

        // -----------------------------------------------------------------------
        // Track-title marquee (dual copy inside mask)
        // -----------------------------------------------------------------------

        enum TrackMarqueePhase
        {
            Idle,
            Scrolling,
            Pausing,
        }

        /// <summary>Per-row marquee state; lives on the title mask's userData.</summary>
        sealed class TrackTitleMarquee
        {
            public VisualElement Mask;
            public VisualElement Mover;
            public TextElement CopyA;
            public TextElement CopyB;
            public VisualElement Badge;
            public IVisualElementScheduledItem Tick;
            public EventCallback<GeometryChangedEvent> OnGeom;
            public string Text = string.Empty;
            public Color TitleColor = TrackTextColor;
            public float TextWidth;
            public float Offset;
            public float PauseLeft;
            public TrackMarqueePhase Phase;
        }

        /// <summary>ListView row bind payload (click + marquee handle).</summary>
        sealed class TrackRowBind
        {
            public EventCallback<ClickEvent> Click;
            public TrackTitleMarquee Marquee;
        }

        static void StopTrackMarquee(TrackTitleMarquee m)
        {
            if (m == null)
                return;
            m.Tick?.Pause();
            m.Tick = null;
            m.Phase = TrackMarqueePhase.Idle;
            m.Offset = 0f;
            m.PauseLeft = 0f;
            if (m.Mover != null)
                m.Mover.style.translate = new Translate(0f, 0f);
            if (m.CopyB != null)
                m.CopyB.style.display = DisplayStyle.None;
        }

        void EnsureTrackMarqueeTick(TrackTitleMarquee m)
        {
            if (m == null || m.Mask == null)
                return;
            if (m.Tick != null)
                return;
            m.Tick = m.Mask.schedule.Execute(() => TickTrackMarquee(m))
                .Every(TrackMarqueeTickMs);
        }

        void TickTrackMarquee(TrackTitleMarquee m)
        {
            if (m == null || m.Phase == TrackMarqueePhase.Idle || m.Mover == null)
                return;

            float dt = TrackMarqueeTickMs / 1000f;
            if (m.Phase == TrackMarqueePhase.Pausing)
            {
                m.PauseLeft -= dt;
                if (m.PauseLeft > 0f)
                    return;
                m.Offset = 0f;
                m.Mover.style.translate = new Translate(0f, 0f);
                m.Phase = TrackMarqueePhase.Scrolling;
                return;
            }

            // Scrolling left until copy B's left edge aligns with the mask left.
            m.Offset -= TrackMarqueeSpeed * dt;
            float loop = m.TextWidth + TrackMarqueeGap;
            if (m.Offset <= -loop)
            {
                m.Offset = 0f;
                m.Mover.style.translate = new Translate(0f, 0f);
                m.Phase = TrackMarqueePhase.Pausing;
                m.PauseLeft = TrackMarqueePauseSeconds;
            }
            else
            {
                m.Mover.style.translate = new Translate(m.Offset, 0f);
            }
        }

        void RefreshTrackMarquee(TrackTitleMarquee m, string text, Color color)
        {
            if (m?.Mask == null || m.CopyA == null || m.CopyB == null || m.Mover == null)
                return;

            m.Text = text ?? string.Empty;
            m.TitleColor = color;
            m.CopyA.text = m.Text;
            m.CopyB.text = m.Text;
            m.CopyA.style.color = color;
            m.CopyB.style.color = color;

            float textW = MeasureTrackTitleWidth(m.CopyA, m.Text);
            m.TextWidth = textW;
            m.CopyA.style.left = 0f;
            m.CopyB.style.left = textW + TrackMarqueeGap;

            // Available width = panel minus badge (when shown). Do not use the
            // mask's current width — short-name shrink would starve the compare.
            VisualElement panel = m.Mask.parent;
            float panelW = panel != null ? panel.resolvedStyle.width : float.NaN;
            if (float.IsNaN(panelW) || panelW <= 1f)
            {
                StopTrackMarquee(m);
                m.Mask.style.width = StyleKeyword.Auto;
                m.Mask.style.flexGrow = 1;
                m.Mask.style.flexShrink = 1;
                return;
            }

            float badgeW = 0f;
            if (m.Badge != null && m.Badge.resolvedStyle.display != DisplayStyle.None)
            {
                float bw = m.Badge.resolvedStyle.width;
                float ml = m.Badge.resolvedStyle.marginLeft;
                if (!float.IsNaN(bw))
                    badgeW += bw;
                if (!float.IsNaN(ml))
                    badgeW += ml;
            }

            float available = Mathf.Max(0f, panelW - badgeW);
            bool needScroll = textW > available + 0.5f;
            if (!needScroll)
            {
                StopTrackMarquee(m);
                // Shrink mask to text so the red badge sits on the name Rect's right edge.
                m.Mask.style.flexGrow = 0;
                m.Mask.style.flexShrink = 0;
                m.Mask.style.width = Mathf.Max(0f, textW);
                m.Mover.style.width = textW;
                return;
            }

            // Overflow: mask fills remaining row; dual copy scrolls inside.
            m.Mask.style.flexGrow = 1;
            m.Mask.style.flexShrink = 1;
            m.Mask.style.width = StyleKeyword.Auto;
            m.Mover.style.width = textW * 2f + TrackMarqueeGap;
            m.CopyB.style.display = DisplayStyle.Flex;
            if (m.Phase == TrackMarqueePhase.Idle)
            {
                m.Offset = 0f;
                m.Mover.style.translate = new Translate(0f, 0f);
                m.Phase = TrackMarqueePhase.Scrolling;
            }
            EnsureTrackMarqueeTick(m);
        }

        static float MeasureTrackTitleWidth(TextElement el, string text)
        {
            if (el == null || string.IsNullOrEmpty(text))
                return 0f;
            // VisualElement.MeasureMode — nested enum; bare MeasureMode fails CS0103 on Unity 6.
            Vector2 size = el.MeasureTextSize(
                text,
                0f, VisualElement.MeasureMode.Undefined,
                0f, VisualElement.MeasureMode.Undefined);
            return Mathf.Max(0f, size.x);
        }

        static void SetTrackTitleColor(TrackTitleMarquee m, Color color)
        {
            if (m?.CopyA != null)
                m.CopyA.style.color = color;
            if (m?.CopyB != null)
                m.CopyB.style.color = color;
        }

        // -----------------------------------------------------------------------
        // ListView item callbacks
        // -----------------------------------------------------------------------

        VisualElement MakeItem()
        {
            // Row root receives clicks. Do NOT use Button — UITK Button captures
            // the pointer and blocks ListView vertical scrolling.
            var root = new VisualElement();
            root.name = "trackRow";
            root.style.position = Position.Relative;
            root.style.width = new Length(100, LengthUnit.Percent);
            root.style.height = 144;
            root.style.backgroundColor = new Color(0, 0, 0, 0);
            root.pickingMode = PickingMode.Position;

            // Title row: masked marquee name + Boluo DownloadSuccess red badge
            // after the name Rect (outside the mask so it never blocks scroll).
            var panel = new VisualElement();
            panel.name = "panel";
            panel.style.position = Position.Absolute;
            panel.style.left = 85; panel.style.top = 0; panel.style.bottom = 0; panel.style.right = 160;
            panel.style.flexDirection = FlexDirection.Row;
            panel.style.alignItems = Align.Center;
            panel.pickingMode = PickingMode.Ignore;

            var mask = new VisualElement();
            mask.name = "titleMask";
            mask.AddToClassList("game-music-form__track-title-mask");
            mask.pickingMode = PickingMode.Ignore;

            var mover = new VisualElement();
            mover.name = "titleMover";
            mover.AddToClassList("game-music-form__track-title-mover");
            mover.pickingMode = PickingMode.Ignore;
            mover.usageHints = UsageHints.DynamicTransform;

            // Typography matches Musicitem Text_0 (45px + TMP characterSpacing 4.5
            // → 4.5/100 em ≈ 2.025px letter-spacing). TextElement avoids hover-wash.
            TextElement MakeTitleCopy(string name)
            {
                var label = new TextElement();
                label.name = name;
                label.AddToClassList("game-music-form__track-title");
                label.style.fontSize = 45f;
                label.style.letterSpacing = 45f * 4.5f / 100f;
                label.style.color = TrackTextColor;
                label.pickingMode = PickingMode.Ignore;
                return label;
            }

            var copyA = MakeTitleCopy("label0");
            var copyB = MakeTitleCopy("label1");
            copyB.style.display = DisplayStyle.None;
            mover.Add(copyA);
            mover.Add(copyB);
            mask.Add(mover);
            panel.Add(mask);

            // Red-dot badge — Boluo InGameYinYueDownloadSuccess (16×16 on Text_0).
            // Sits after the mask so short names get badge on text right edge;
            // long names keep badge outside the clip (does not block marquee).
            var redBadge = new VisualElement();
            redBadge.name = "downloadsuccess";
            redBadge.style.width = 16;
            redBadge.style.height = 16;
            redBadge.style.marginLeft = 6;
            redBadge.style.flexShrink = 0;
            redBadge.style.display = DisplayStyle.None;
            redBadge.style.backgroundColor = new Color(1f, 59f / 255f, 48f / 255f, 1f);
            redBadge.style.borderTopLeftRadius = 8;
            redBadge.style.borderTopRightRadius = 8;
            redBadge.style.borderBottomLeftRadius = 8;
            redBadge.style.borderBottomRightRadius = 8;
            redBadge.pickingMode = PickingMode.Ignore;
            panel.Add(redBadge);

            var marquee = new TrackTitleMarquee
            {
                Mask = mask,
                Mover = mover,
                CopyA = copyA,
                CopyB = copyB,
                Badge = redBadge,
            };
            marquee.OnGeom = _ =>
            {
                if (mask.userData is TrackTitleMarquee live)
                    RefreshTrackMarquee(live, live.Text, live.TitleColor);
            };
            mask.RegisterCallback(marquee.OnGeom);
            mask.userData = marquee;

            // Right-side status icons (Boluo NotDownloaded / Downloading / DownloadError / Playing).
            EnsureStatusIconsLoaded();
            (string name, Sprite sprite)[] rightIcons =
            {
                ("notdownload", _iconNotDownload),
                ("downloading", _iconDownloading),
                ("downloaderror", _iconDownloadError),
                ("playinganima", null),
            };
            foreach (var (iname, sprite) in rightIcons)
            {
                var icon = new VisualElement();
                icon.name = iname;
                icon.style.position = Position.Absolute;
                icon.style.right = 84; icon.style.top = 42;
                icon.style.width = 60; icon.style.height = 60;
                icon.style.display = DisplayStyle.None;
                icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                icon.pickingMode = PickingMode.Ignore;
                if (sprite != null)
                    icon.style.backgroundImage = new StyleBackground(sprite);
                root.Add(icon);
            }

            // ICON_PLAYING: 60×60 equalizer sequence (Resources/MusicForm/Playing).
            var playingAnima = root.Q("playinganima");
            if (playingAnima != null)
            {
                playingAnima.style.backgroundColor = StyleKeyword.None;
                playingAnima.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                ApplyPlayingFrame(playingAnima, _playingFrameIndex);
            }

            root.Add(panel);
            return root;
        }

        void BindItem(VisualElement element, int index)
        {
            string name = index >= 0 && index < _trackNames.Count
                ? _trackNames[index]
                : string.Empty;

            int album = _centerAlbum;
            int track = index;

            MusicItemUiState state = DetermineRowState(album, track);
            ApplyRowState(element, state);

            var music = GameEntry.Music;
            bool isPlaying = music != null
                && music.IsPlaying
                && music.CurrentAlbumIndex == _centerAlbum
                && music.CurrentTrackIndex == index;

            Color titleColor = isPlaying ? TrackPlayingColor : TrackTextColor;
            if (isPlaying)
                ShowIcon(element, "playinganima");

            TrackTitleMarquee marquee = element.Q("titleMask")?.userData as TrackTitleMarquee;
            if (marquee != null)
            {
                // ApplyRowState may have toggled the badge; remasure after layout.
                RefreshTrackMarquee(marquee, name, titleColor);
                element.schedule.Execute(() =>
                {
                    if (element.Q("titleMask")?.userData is TrackTitleMarquee live)
                        RefreshTrackMarquee(live, name, titleColor);
                }).StartingIn(0);
            }

            // Unregister previous bind callback before attaching a new one.
            if (element.userData is TrackRowBind prevBind && prevBind.Click != null)
                element.UnregisterCallback(prevBind.Click);
            else if (element.userData is EventCallback<ClickEvent> legacy)
                element.UnregisterCallback(legacy);

            EventCallback<ClickEvent> callback = evt =>
            {
                // Drag-scroll synthesized a Click on Up — ignore it.
                if (_suppressTrackClick)
                {
                    _suppressTrackClick = false;
                    evt.StopPropagation();
                    return;
                }
                HandleTrackClick(album, track);
                evt.StopPropagation();
            };
            element.userData = new TrackRowBind { Click = callback, Marquee = marquee };
            element.RegisterCallback(callback);
        }

        void UnbindItem(VisualElement element, int index)
        {
            if (element.userData is TrackRowBind bind)
            {
                if (bind.Click != null)
                    element.UnregisterCallback(bind.Click);
                StopTrackMarquee(bind.Marquee);
                element.userData = null;
                return;
            }

            if (element.userData is EventCallback<ClickEvent> cb)
            {
                element.UnregisterCallback(cb);
                element.userData = null;
            }

            if (element.Q("titleMask")?.userData is TrackTitleMarquee m)
                StopTrackMarquee(m);
        }

        // -----------------------------------------------------------------------
        // Row state machine
        // -----------------------------------------------------------------------

        /// <summary>
        /// Determines the UI state for a given track by checking
        /// local readiness, active downloads, and playback status.
        /// Matches Boluo <c>InGameYinYueItem</c>: queue Pending shows as NotDownloaded;
        /// only the actively Downloading bank (or the user-clicked pending row) spins.
        /// </summary>
        MusicItemUiState DetermineRowState(int album, int track)
        {
            var tracks = MusicCatalog.GetTracks(album);
            if (track < 0 || track >= tracks.Count)
                return MusicItemUiState.Failed;

            var trackData = tracks[track];

            // Is this track currently playing?
            var music = GameEntry.Music;
            if (music != null
                && music.IsPlaying
                && music.CurrentAlbumIndex == album
                && music.CurrentTrackIndex == track)
            {
                return MusicItemUiState.Playing;
            }

            string bankName = trackData.BankName;

            bool userPending = PendingDownloadIntent.IsMusicMatch(album, track)
                || (_pendingMusic.HasValue
                    && _pendingMusic.Value.album == album
                    && _pendingMusic.Value.track == track);

            // Check active download queue entry.
            if (BankDownloader.TryGetActiveItem(bankName, out var item))
            {
                switch (item.State)
                {
                    case BankDownloadState.Downloading:
                        return MusicItemUiState.Downloading;
                    case BankDownloadState.Failed:
                        return MusicItemUiState.Failed;
                    case BankDownloadState.Pending:
                        // Boluo: queued Pending keeps NotDownloaded chrome; only the
                        // user-clicked row stays on the spinner until HTTP starts.
                        return userPending
                            ? MusicItemUiState.Downloading
                            : MusicItemUiState.NotDownloaded;
                }
            }

            // User just tapped download and metadata/queue has not caught up yet.
            if (userPending)
                return MusicItemUiState.Downloading;

            // Check local bank readiness.
            if (BankDownloader.IsLocalReady(bankName))
            {
                // Has the user already heard this track?
                var albumData = MusicCatalog.GetAlbum(album);
                if (albumData != null && ShouldShowRedDot(albumData.Name, trackData.MusicName))
                    return MusicItemUiState.Succeeded;
                return MusicItemUiState.Ready;
            }

            return MusicItemUiState.NotDownloaded;
        }

        /// <summary>
        /// Drives the five icon slots to reflect the given state.
        /// All five slots are updated on every call — no state carryover.
        /// </summary>
        void ApplyRowState(VisualElement element, MusicItemUiState state)
        {
            // Reset all icon slots to hidden.
            ShowIcon(element, "notdownload", false);
            ShowIcon(element, "downloading", false);
            ShowIcon(element, "downloaderror", false);
            ShowIcon(element, "downloadsuccess", false);
            ShowIcon(element, "playinganima", false);

            TrackTitleMarquee marquee = element.Q("titleMask")?.userData as TrackTitleMarquee;
            SetTrackTitleColor(marquee, TrackTextColor);

            switch (state)
            {
                case MusicItemUiState.Failed:
                    ShowIcon(element, "downloaderror", true);
                    break;

                case MusicItemUiState.NotDownloaded:
                    ShowIcon(element, "notdownload", true);
                    break;

                case MusicItemUiState.Downloading:
                    ShowIcon(element, "downloading", true);
                    break;

                case MusicItemUiState.Succeeded:
                    ShowIcon(element, "downloadsuccess", true);
                    break;

                case MusicItemUiState.Ready:
                    // No icon — label stays default colour.
                    break;

                case MusicItemUiState.Playing:
                    ShowIcon(element, "playinganima", true);
                    ApplyPlayingFrame(element.Q("playinganima"), _playingFrameIndex);
                    SetTrackTitleColor(marquee, TrackPlayingColor);
                    break;
            }
        }

        void ShowIcon(VisualElement element, string iconName, bool show = true)
        {
            var icon = element.Q(iconName);
            if (icon == null)
                return;
            icon.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show && iconName == "playinganima")
                ApplyPlayingFrame(icon, _playingFrameIndex);
            if (show && iconName == "downloading")
                icon.style.rotate = new Rotate(Angle.Degrees(_downloadSpinDegrees));
            if (!show && iconName == "downloading")
                icon.style.rotate = new Rotate(Angle.Degrees(0));
        }

        // -----------------------------------------------------------------------
        // Track click handling
        // -----------------------------------------------------------------------

        void HandleTrackClick(int album, int track)
        {
            if (GameEntry.Music == null)
                return;

            var state = DetermineRowState(album, track);

            switch (state)
            {
                case MusicItemUiState.Ready:
                case MusicItemUiState.Succeeded:
                    ClearPendingMusic();
                    PendingDownloadIntent.Clear();
                    // Mark as heard (suppress red dot).
                    var albumData = MusicCatalog.GetAlbum(album);
                    if (albumData != null)
                    {
                        var tracks = MusicCatalog.GetTracks(album);
                        if (track < tracks.Count)
                            MarkHeard(albumData.Name, tracks[track].MusicName);
                    }
                    GameEntry.Music.PlayIndex(album, track);
                    RefreshAll();
                    break;

                case MusicItemUiState.NotDownloaded:
                case MusicItemUiState.Failed:
                    TriggerMusicDownload(album, track);
                    break;

                // Downloading / Playing — no action.
            }
        }

        // -----------------------------------------------------------------------
        // Download gates & pending music
        // -----------------------------------------------------------------------

        /// <summary>
        /// F5 download gate checks + pending music enqueue.
        /// 无网 → 存储 → 流量 → 开下；拒流量清 PendingDownloadIntent。
        /// </summary>
        void TriggerMusicDownload(int album, int track)
        {
            var tracks = MusicCatalog.GetTracks(album);
            if (track < 0 || track >= tracks.Count)
                return;

            string bankName = tracks[track].BankName;
            string[] banks = { bankName };

            if (!DownloadNetworkGate.TryPassDownloadGates(
                    banks,
                    onAllowAfterMobile: () => DoDownload(album, track, bankName),
                    onWifiOnly: () =>
                    {
                        ClearPendingMusic();
                        _list?.schedule.Execute(() => _list?.RefreshItems());
                    },
                    onBlockedRetreat: null))
                return;

            DoDownload(album, track, bankName);
        }

        void DoDownload(int album, int track, string bankName)
        {
            // Shared with PresetForm — mutual overwrite (Boluo PendingDownloadIntent).
            _playAfterDownloadGen++;
            _pendingMusic = (album, track);
            PendingDownloadIntent.SetMusic(album, track);
            // Next frame: rebinding inside this click would unregister the callback mid-dispatch.
            _list?.schedule.Execute(() => _list?.RefreshItems());

            // Initiate priority download.
            if (PresetBankCatalog.TryGet(bankName, out var remote))
            {
                IslandPlayerPosSave.PushSaveSuppress();
                DownloadBankAsync(remote).Forget();
            }
            else
            {
                Log.Warning($"[MusicForm] Bank not found in PresetBankCatalog: {bankName}");
                ClearPendingMusic();
                _list?.schedule.Execute(() => _list?.RefreshItems());
            }
        }

        /// <summary>
        /// Runs the bank download and guarantees the save-suppress counter is popped exactly once,
        /// even if the download throws before the completion callback fires.
        /// </summary>
        async UniTaskVoid DownloadBankAsync(BankRemoteInfo remote)
        {
            try
            {
                await BankDownloader.DownloadBank(
                    remote,
                    loadIntoEngine: true,
                    onDone: OnBankDownloadDone,
                    prioritize: true,
                    preempt: true);
            }
            catch (Exception e)
            {
                Log.Warning($"[MusicForm] Bank download failed: {e.Message}");
                ClearPendingMusic();
            }
            finally
            {
                IslandPlayerPosSave.PopSaveSuppress();
            }
        }

        /// <summary>Called when the pending bank download completes (success or failure).</summary>
        void OnBankDownloadDone(BankDownloadItem item)
        {
            // 注意：保存抑制的出栈由 DownloadBankAsync 的 finally 统一负责，这里不要重复 Pop。
            if (item == null || string.IsNullOrEmpty(item.BankName))
            {
                ClearPendingMusic();
                _list?.RefreshItems();
                return;
            }

            bool wasPending = PendingDownloadIntent.IsMusicMatch(
                _pendingMusic?.album ?? -1,
                _pendingMusic?.track ?? -1);
            if (!wasPending && _pendingMusic.HasValue)
            {
                var pending = _pendingMusic.Value;
                var tracks = MusicCatalog.GetTracks(pending.album);
                if (pending.track >= 0 && pending.track < tracks.Count)
                {
                    string pendingBank = tracks[pending.track].BankName;
                    wasPending = string.Equals(pendingBank, item.BankName, StringComparison.OrdinalIgnoreCase)
                        && PendingDownloadIntent.Current == PendingDownloadIntent.Kind.Music;
                }
            }

            if (wasPending && PendingDownloadIntent.Current == PendingDownloadIntent.Kind.Music)
            {
                int album = PendingDownloadIntent.MusicAlbum;
                int track = PendingDownloadIntent.MusicTrack;
                if (item.State == BankDownloadState.Ready || item.State == BankDownloadState.Skipped)
                {
                    // Keep the spinner up until the bank is decoded. PlayIndex stops the
                    // current song first; posting before decode leaves that stop in place.
                    int gen = _playAfterDownloadGen;
                    string guid = !string.IsNullOrEmpty(item.Guid)
                        ? item.Guid
                        : MusicCatalog.GetTrack(album, track)?.BankGuid;
                    PlayDownloadedTrackAsync(gen, album, track, item.BankName, guid).Forget();
                    _list?.RefreshItems();
                    return;
                }

                ClearPendingMusic();
            }

            _list?.RefreshItems();
        }

        bool StillPendingDownload(int gen, int album, int track) =>
            gen == _playAfterDownloadGen && PendingDownloadIntent.IsMusicMatch(album, track);

        /// <summary>
        /// Wait until the freshly loaded bank can accept PostEvent, then switch playback to it.
        /// Current BGM keeps playing during the wait.
        /// </summary>
        async UniTaskVoid PlayDownloadedTrackAsync(int gen, int album, int track, string bankName, string guidText)
        {
            try
            {
                bool still = await WaitUntilBankPlayable(gen, album, track, bankName, guidText);
                if (!still || !StillPendingDownload(gen, album, track))
                    return;

                if (!string.IsNullOrEmpty(bankName)
                    && !WanosBankStore.IsLoaded(bankName)
                    && !WanosBankStore.Load(bankName))
                {
                    Log.Warning($"[MusicForm] downloaded bank not in engine: {bankName}");
                    return;
                }

                if (!WanosEngine.IsPlaybackEnabled)
                {
                    WanosEngine.EnablePlayback("music-download");
                    WanosEngine.StartEngine("music-download");
                    WanosEngine.Unmute("music-download");
                }

                var albumData = MusicCatalog.GetAlbum(album);
                var tracks = MusicCatalog.GetTracks(album);
                if (albumData != null && track >= 0 && track < tracks.Count)
                    MarkHeard(albumData.Name, tracks[track].MusicName);

                Log.Info($"[MusicForm] download ready → play album={album} track={track} bank={bankName}");
                GameEntry.Music?.PlayIndex(album, track, restart: true);
                if (GameEntry.Music != null && !GameEntry.Music.IsPlaying && StillPendingDownload(gen, album, track))
                {
                    await UniTask.Delay(150, DelayType.UnscaledDeltaTime);
                    if (StillPendingDownload(gen, album, track))
                        GameEntry.Music.PlayIndex(album, track, restart: true);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[MusicForm] play after download failed: {e.Message}");
            }
            finally
            {
                if (StillPendingDownload(gen, album, track))
                    ClearPendingMusic();
                _list?.RefreshItems();
            }
        }

        async UniTask<bool> WaitUntilBankPlayable(int gen, int album, int track, string bankName, string guidText)
        {
            if (!TryParseBankGuid(guidText, out ulong bankId) || !WanosNative.Instance.BankDecodeQueryAvailable)
            {
                await UniTask.Delay(200, DelayType.UnscaledDeltaTime);
                return StillPendingDownload(gen, album, track);
            }

            int waited = 0;
            while (waited < BankDecodeWaitMs)
            {
                if (!StillPendingDownload(gen, album, track))
                    return false;
                if (WanosNative.Instance.IsBankDecoded(bankId))
                    return true;
                if (!WanosNative.Instance.BankDecodeQueryAvailable)
                    return StillPendingDownload(gen, album, track);
                await UniTask.Delay(BankDecodePollMs, DelayType.UnscaledDeltaTime);
                waited += BankDecodePollMs;
            }

            Log.Warning($"[MusicForm] bank decode wait timed out bank={bankName} id={bankId} — play anyway");
            return StillPendingDownload(gen, album, track);
        }

        static bool TryParseBankGuid(string text, out ulong guid)
        {
            guid = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            return ulong.TryParse(text, out guid);
        }

        void ClearPendingMusic()
        {
            _pendingMusic = null;
            if (PendingDownloadIntent.Current == PendingDownloadIntent.Kind.Music)
                PendingDownloadIntent.Clear();
        }

        // -----------------------------------------------------------------------
        // Status icons (Boluo MusicNotDownload / MusicLoading / MusicError / MusicRed)
        // -----------------------------------------------------------------------

        static void EnsureStatusIconsLoaded()
        {
            if (_statusIconsLoaded)
                return;
            _statusIconsLoaded = true;
            _iconNotDownload = LoadMusicStatusSprite("MusicNotDownload");
            _iconDownloading = LoadMusicStatusSprite("MusicLoading");
            _iconDownloadError = LoadMusicStatusSprite("MusicError");
            _iconDownloadSuccess = LoadMusicStatusSprite("MusicRed");
            if (_iconNotDownload == null || _iconDownloading == null
                || _iconDownloadError == null || _iconDownloadSuccess == null)
            {
                Log.Warning("[MusicForm] Status icons missing under Resources/MusicForm/ "
                    + "(MusicNotDownload / MusicLoading / MusicError / MusicRed)");
            }
        }

        static Sprite LoadMusicStatusSprite(string resourceName)
        {
            var sprite = Resources.Load<Sprite>($"MusicForm/{resourceName}");
            if (sprite != null)
                return sprite;
            var tex = Resources.Load<Texture2D>($"MusicForm/{resourceName}");
            if (tex == null)
                return null;
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        void StartDownloadSpinLoop()
        {
            StopDownloadSpinLoop();
            if (Root == null)
                return;
            _downloadSpinSchedule = Root.schedule.Execute(TickDownloadSpin).Every(PlayingFrameIntervalMs);
        }

        void StopDownloadSpinLoop()
        {
            _downloadSpinSchedule?.Pause();
            _downloadSpinSchedule = null;
        }

        void TickDownloadSpin()
        {
            if (_list == null)
                return;
            _downloadSpinDegrees = (_downloadSpinDegrees - DownloadSpinDegreesPerTick) % 360f;
            _list.Query("downloading").ForEach(icon =>
            {
                if (icon.resolvedStyle.display == DisplayStyle.None)
                    return;
                icon.style.rotate = new Rotate(Angle.Degrees(_downloadSpinDegrees));
            });
        }

        // -----------------------------------------------------------------------
        // ICON_PLAYING — equalizer sequence frames
        // -----------------------------------------------------------------------

        static void EnsurePlayingFramesLoaded()
        {
            if (_playingFrames != null && _playingFrames.Length == PlayingFrameCount)
                return;

            var loaded = new Sprite[PlayingFrameCount];
            int found = 0;
            for (int i = 1; i <= PlayingFrameCount; i++)
            {
                var sprite = Resources.Load<Sprite>($"MusicForm/Playing/{i:D5}");
                if (sprite == null)
                {
                    // Fallback: Texture2D → Sprite wrapper for UITK background.
                    var tex = Resources.Load<Texture2D>($"MusicForm/Playing/{i:D5}");
                    if (tex != null)
                        sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
                loaded[i - 1] = sprite;
                if (sprite != null)
                    found++;
            }

            _playingFrames = loaded;
            if (found == 0)
                Log.Warning("[MusicForm] ICON_PLAYING frames missing under Resources/MusicForm/Playing/");
        }

        void StartPlayingAnimLoop()
        {
            StopPlayingAnimLoop();
            if (Root == null)
                return;
            _playingAnimSchedule = Root.schedule.Execute(TickPlayingAnim).Every(PlayingFrameIntervalMs);
        }

        void StopPlayingAnimLoop()
        {
            _playingAnimSchedule?.Pause();
            _playingAnimSchedule = null;
        }

        void TickPlayingAnim()
        {
            if (_playingFrames == null || _playingFrames.Length == 0 || _list == null)
                return;

            _playingFrameIndex = (_playingFrameIndex + 1) % PlayingFrameCount;

            // ListView recycles rows — walk visible hierarchy for playinganima slots.
            _list.Query("playinganima").ForEach(icon =>
            {
                if (icon.resolvedStyle.display == DisplayStyle.None)
                    return;
                ApplyPlayingFrame(icon, _playingFrameIndex);
            });
        }

        static void ApplyPlayingFrame(VisualElement icon, int frameIndex)
        {
            if (icon == null || _playingFrames == null || _playingFrames.Length == 0)
                return;
            int idx = frameIndex % _playingFrames.Length;
            var sprite = _playingFrames[idx];
            if (sprite != null)
                icon.style.backgroundImage = new StyleBackground(sprite);
        }

        // -----------------------------------------------------------------------
        // Post-play refresh helper (kept for compatibility)
        // -----------------------------------------------------------------------

        void RefreshAfterPlay()
        {
            RefreshAlbumStrip();
            _list?.RefreshItems();
        }

        /// <summary>
        /// Loop/Shuffle auto-advance (and any other PlayIndex) updates playlist
        /// indices without going through a row click. Rebind visible rows so the
        /// playing marker moves; do not rebuild, so the user's scroll stays.
        /// A shuffle jump to another album only clears the stale marker here.
        /// </summary>
        void OnPlaylistTrackChanged()
        {
            if (_list == null || Root == null || Root.panel == null)
                return;

            _list.RefreshItems();

            var music = GameEntry.Music;
            if (music != null
                && music.CurrentAlbumIndex == _centerAlbum
                && music.CurrentTrackIndex >= 0
                && music.CurrentTrackIndex < _trackNames.Count)
            {
                ScrollTrackListTo(music.CurrentTrackIndex);
            }
        }
    }
}
