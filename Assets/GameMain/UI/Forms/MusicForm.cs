using System;
using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using Game.BoluoApi.Music;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    public class MusicForm : GameFormLogic
    {
        protected override bool SlidesUpFromBottom => true;

        // -----------------------------------------------------------------------
        // Runtime state
        // -----------------------------------------------------------------------

        /// <summary>Music that the user intends to play after its bank finishes downloading.</summary>
        (int album, int track)? _pendingMusic;

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
        // Album strip layout (Boluo HorizontalDragComponent: fixed step + loop).
        // -----------------------------------------------------------------------

        const float AlbumStep = 140f;              // 120px label + 20px gap (fixed).
        const int AlbumStripCopies = 3;           // Triple strip for seamless loop.
        const string AlbumLabelClass = "game-music-form__album-label";
        const string AlbumLabelSelectedModifier = "game-music-form__album-label--selected";
        const string AlbumLabelUnselectedModifier = "game-music-form__album-label--unselected";

        // -----------------------------------------------------------------------
        // Visual element caches
        // -----------------------------------------------------------------------

        ListView _list;
        ScrollView _scrollView;
        Scroller _vScroller;
        IVisualElementScheduledItem _hideSchedule;
        IVisualElementScheduledItem _fadeSchedule;
        IVisualElementScheduledItem _tipHide;
        float _scrollerAlpha;
        float _fadeFrom;
        float _fadeTo;
        float _fadeElapsed = -1f;
        bool _revealGate;
        float _ignoreProgrammaticUntil;
        VisualElement _hintWindows;
        Label _hintLabel;
        /// <summary>「全部歌曲N首」— TextElement only; Label :hover washes grey to white.</summary>
        TextElement _title;

        VisualElement _albumContainer;
        VisualElement _albumContent;
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
        /// <summary>Content <c>left</c> at PointerDown — drag offsets accumulate from this.</summary>
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

        // -----------------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------------

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);

            DownloadedUnplayedManager.EnsureLoaded();

            // Cache frequently used elements.
            _hintWindows = Root.Q("hintwindows");
            _hintLabel = Root.Q<Label>("labeltmp");
            if (_hintWindows != null)
                _hintWindows.style.opacity = 0f;

            // Force TextElement title — pooled forms may still carry a Label from an
            // older UXML, and slide-up under the cursor re-triggers :hover → white.
            EnsureTitleElement();

            // Album strip: drag with center snap (boundary stop, no wrap).
            _albumContainer = Root.Q("huadong");
            if (_albumContainer != null)
            {
                SetupAlbumDrag();
            }

            // Play mode button.
            _modeIcon = Root.Q("modeIcon");
            OnClick("modeButton", OnClickPlayMode);
            UpdateModeButton(MusicPlaylist.Instance.PlayMode);

            // Music list (dialog card is picking Ignore — list must accept hits).
            _list = Root.Q<ListView>("musicList");
            if (_list != null)
                _list.pickingMode = PickingMode.Position;

            EnsurePlayingFramesLoaded();
            StartPlayingAnimLoop();

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
            MusicCatalog.OnCatalogReady -= OnCatalogReady;
            _tipHide?.Pause();
            _tipHide = null;
            StopPlayingAnimLoop();
            UnbindScrollbarReveal();
            UnbindAlbumDrag();
            ClearAlbumClicks();
            _title = null;
            base.OnClose(isShutdown, userData);
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
            // resolvedStyle.left (pooled reopen left the visible centre at
            // opacity 0.3, which reads as "white" on the card).
            if (_albumContent != null && count > 0)
                _albumContent.style.left = TargetLeftForAlbum(_centerAlbum, count);
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
                _title.pickingMode = PickingMode.Ignore;
                _title.AddToClassList("game-music-form__title");
                _title.style.color = TitleTextColor;
                return;
            }

            VisualElement parent = existing?.parent ?? Root.Q("musicDialogkapian");
            if (parent == null)
                return;

            int index = existing != null ? parent.IndexOf(existing) : parent.childCount;
            existing?.RemoveFromHierarchy();

            _title = new TextElement { name = "title" };
            _title.AddToClassList("game-music-form__title");
            _title.pickingMode = PickingMode.Ignore;
            _title.style.color = TitleTextColor;
            parent.Insert(Mathf.Clamp(index, 0, parent.childCount), _title);
        }

        void RefreshTitle()
        {
            if (_title == null || _title.panel == null)
                EnsureTitleElement();
            if (_title == null)
                return;

            _title.pickingMode = PickingMode.Ignore;
            _title.style.color = TitleTextColor;

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

            while (_albumLabels.Count < slotCount)
            {
                // TextElement avoids .unity-label:hover → white (invisible on white card).
                var label = new TextElement();
                label.AddToClassList(AlbumLabelClass);
                label.pickingMode = PickingMode.Position;
                label.style.color = AlbumTextColor;
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

            // Forms are pooled: OnClose clears clicks but keeps labels. Re-bind
            // any slots that lost their callback so reopen stays clickable.
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

            // Highlight: while dragging, follow the pointer; otherwise ALWAYS the
            // middle-copy slot for _centerAlbum. Using resolvedStyle.left here was
            // the reopen bug — layout had not resolved yet, so the selected class
            // landed on an off-screen copy and the visible name stayed at opacity
            // 0.3 (dark×0.3 on white ≈ washed / "white").
            int highlightSlot;
            if (_isDragging)
            {
                float left = ReadAlbumContentLeft();
                highlightSlot = Mathf.RoundToInt(-left / AlbumStep);
                if (highlightSlot < 0 || highlightSlot >= slotCount)
                    highlightSlot = albumCount + _centerAlbum;
            }
            else
            {
                highlightSlot = albumCount + _centerAlbum;
            }

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
                // Inline opacity/size so a missed USS class cannot leave the
                // centred name washed out after a pooled reopen.
                label.style.opacity = isSelected ? 1f : 0.3f;
                label.style.fontSize = isSelected ? 72f : 48f;
                label.EnableInClassList(AlbumLabelSelectedModifier, isSelected);
                label.EnableInClassList(AlbumLabelUnselectedModifier, !isSelected);
            }
        }

        /// <summary>Prefer written style.left; fall back to resolved when unset.</summary>
        float ReadAlbumContentLeft()
        {
            if (_albumContent == null)
                return 0f;
            StyleLength left = _albumContent.style.left;
            if (left.keyword == StyleKeyword.Null || left.keyword == StyleKeyword.Auto)
                return _albumContent.resolvedStyle.left;
            return left.value.value;
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
        float AlbumCircumference(int albumCount) => albumCount * AlbumStep;

        /// <summary>
        /// Keep content.left inside the middle copy range so labels never scroll
        /// off into empty space (seamless infinite loop).
        /// </summary>
        void NormalizeAlbumLoop(ref float left, int albumCount)
        {
            if (albumCount <= 0)
                return;
            float circ = AlbumCircumference(albumCount);
            // Middle copy covers left in [-2*circ, -circ] when padding centres slot 0 of copy 0...
            // Slot layout: copies 0,1,2. Center on copy 1 → left = -(albumCount + index)*step.
            // Valid window for continuous drag without blank: roughly (-2*circ, 0).
            // When left drifts past either edge by a full circ, jump by ±circ.
            while (left > -circ * 0.5f)
                left -= circ;
            while (left < -circ * 2.5f)
                left += circ;
        }

        int LogicalAlbumFromLeft(float left, int albumCount)
        {
            if (albumCount <= 0)
                return 0;
            int slot = Mathf.RoundToInt(-left / AlbumStep);
            return ((slot % albumCount) + albumCount) % albumCount;
        }

        /// <summary>Target left that centres <paramref name="albumIndex"/> in the middle copy.</summary>
        float TargetLeftForAlbum(int albumIndex, int albumCount)
        {
            if (albumCount <= 0)
                return 0f;
            albumIndex = ((albumIndex % albumCount) + albumCount) % albumCount;
            int middleBase = albumCount; // start of copy 1
            return -(middleBase + albumIndex) * AlbumStep;
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

            // Match .game-music-form__huadong > VisualElement even if USS lags.
            _albumContent.style.flexDirection = FlexDirection.Row;
            _albumContent.style.flexWrap = Wrap.NoWrap;
            _albumContent.pickingMode = PickingMode.Ignore;

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
            _albumContent.style.left = nextLeft;

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
            int albumCount = MusicCatalog.AlbumCount;
            float left = TargetLeftForAlbum(_centerAlbum, albumCount);
            _albumContent.style.left = left;
            RefreshAlbumStrip();
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

            _albumContent.style.left = startLeft;
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
                _albumContent.style.left = left;
                if (t >= 1f)
                {
                    _albumContent.style.left = TargetLeftForAlbum(albumIndex, albumCount);
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
            ShowHintToast(text, (long)((ModeTipSeconds + ModeTipFadeSeconds) * 1000f));
        }

        void ShowHintToast(string text, long hideAfterMs)
        {
            if (_hintWindows == null)
                return;
            if (_hintLabel != null)
                _hintLabel.text = text ?? string.Empty;

            // TipWindows sizing: TipWindow.ShowTip pad + max width.
            if (_hintLabel != null)
            {
                _hintLabel.style.whiteSpace = WhiteSpace.NoWrap;
                float fontSize = _hintLabel.resolvedStyle.fontSize;
                if (fontSize < 1f)
                    fontSize = 36f;
                float textW = (_hintLabel.text != null ? _hintLabel.text.Length : 0) * fontSize * 0.62f;
                float width = Mathf.Clamp(
                    textW + UiTipToast.PadHorizontal * 2f,
                    UiTipToast.MinWidth,
                    UiTipToast.MaxWidth);
                _hintWindows.style.width = width;
                _hintWindows.style.marginLeft = -width * 0.5f;
                _hintLabel.style.whiteSpace = WhiteSpace.Normal;
                _hintLabel.style.maxWidth = width - UiTipToast.PadHorizontal * 2f;
            }

            _hintWindows.style.opacity = 1f;
            _hintWindows.BringToFront();
            _tipHide?.Pause();
            _tipHide = Root.schedule.Execute(() =>
            {
                if (_hintWindows != null)
                    _hintWindows.style.opacity = 0f;
                _tipHide = null;
            }).StartingIn(hideAfterMs);
        }

        // Sprite names for the 3 play modes (yy_danqu / yy_list / yy_suiji).
        // Loaded from `Assets/GameMain/Resources/MusicForm/` so they survive
        // the runtime build without needing an Addressables or AssetDatabase
        // dependency.
        static readonly string[] ModeSpriteNames = { "yy_danqu", "yy_list", "yy_suiji" };

        void UpdateModeButton(int mode)
        {
            if (_modeIcon == null)
                return;
            int idx = mode switch
            {
                (int)MusicPlayMode.Single => 0,
                (int)MusicPlayMode.Loop => 1,
                (int)MusicPlayMode.Shuffle => 2,
                _ => 0,
            };
            var sprite = Resources.Load<Sprite>($"MusicForm/{ModeSpriteNames[idx]}");
            if (sprite != null)
                _modeIcon.style.backgroundImage = new StyleBackground(sprite);
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
            _vScroller = _scrollView.verticalScroller;
            if (_vScroller == null)
                return;

            SetScrollerAlpha(0f);
            _revealGate = false;
            _ignoreProgrammaticUntil = 0f;
            _vScroller.valueChanged += OnScrollerValueChanged;
            _list.schedule.Execute(EnableRevealGate).StartingIn(32);
        }

        void UnbindScrollbarReveal()
        {
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

            var panel = new VisualElement();
            panel.name = "panel";
            panel.style.position = Position.Absolute;
            panel.style.left = 85; panel.style.top = 0; panel.style.bottom = 0; panel.style.right = 160;
            panel.pickingMode = PickingMode.Ignore;

            // TextElement (not Label): ListView rows under the cursor on reopen
            // would otherwise get .unity-label:hover → white on the white card.
            var label = new TextElement();
            label.name = "label0";
            label.style.position = Position.Absolute;
            label.style.left = 0; label.style.top = 0; label.style.bottom = 0; label.style.right = 0;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.fontSize = 45;
            label.style.color = TrackTextColor;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            label.pickingMode = PickingMode.Ignore;
            panel.Add(label);

            // Five icon slots — all hidden by default.
            string[] iconNames = { "notdownload", "downloading", "downloaderror", "downloadsuccess", "playinganima" };
            foreach (var iname in iconNames)
            {
                var icon = new VisualElement();
                icon.name = iname;
                icon.style.position = Position.Absolute;
                icon.style.right = 84; icon.style.top = 42;
                icon.style.width = 60; icon.style.height = 60;
                icon.style.display = DisplayStyle.None;
                icon.pickingMode = PickingMode.Ignore;
                root.Add(icon);
            }

            // ICON_PLAYING: 60×60 equalizer sequence (Resources/MusicForm/Playing).
            var playingAnima = root.Q("playinganima");
            if (playingAnima != null)
            {
                playingAnima.style.backgroundColor = StyleKeyword.None;
                playingAnima.style.unityBackgroundScaleMode = ScaleMode.StretchToFill;
                ApplyPlayingFrame(playingAnima, _playingFrameIndex);
            }

            root.Add(panel);
            return root;
        }

        void BindItem(VisualElement element, int index)
        {
            TextElement title = element.Q<TextElement>("label0");
            if (title != null && index >= 0 && index < _trackNames.Count)
                title.text = _trackNames[index];

            int album = _centerAlbum;
            int track = index;

            MusicItemUiState state = DetermineRowState(album, track);
            ApplyRowState(element, state);

            var music = GameEntry.Music;
            bool isPlaying = music != null
                && music.IsPlaying
                && music.CurrentAlbumIndex == _centerAlbum
                && music.CurrentTrackIndex == index;

            if (isPlaying)
            {
                ShowIcon(element, "playinganima");
                if (title != null)
                    title.style.color = TrackPlayingColor;
            }

            // Unregister previous bind callback before attaching a new one.
            if (element.userData is EventCallback<ClickEvent> prev)
                element.UnregisterCallback(prev);

            EventCallback<ClickEvent> callback = evt =>
            {
                HandleTrackClick(album, track);
                evt.StopPropagation();
            };
            element.userData = callback;
            element.RegisterCallback(callback);
        }

        void UnbindItem(VisualElement element, int index)
        {
            if (element.userData is EventCallback<ClickEvent> cb)
            {
                element.UnregisterCallback(cb);
                element.userData = null;
            }
        }

        // -----------------------------------------------------------------------
        // Row state machine
        // -----------------------------------------------------------------------

        /// <summary>
        /// Determines the UI state for a given track by checking
        /// local readiness, active downloads, and playback status.
        /// Priority: Playing > Downloading > Pending > Ready > Succeeded > NotDownloaded > Failed.
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

            // Pending (user clicked, waiting for download to finish).
            if (PendingDownloadIntent.IsMusicMatch(album, track)
                || (_pendingMusic.HasValue
                    && _pendingMusic.Value.album == album
                    && _pendingMusic.Value.track == track))
            {
                return MusicItemUiState.Downloading;
            }

            // Check active download queue entry.
            if (BankDownloader.TryGetActiveItem(bankName, out var item))
            {
                switch (item.State)
                {
                    case BankDownloadState.Downloading:
                    case BankDownloadState.Pending:
                        return MusicItemUiState.Downloading;
                    case BankDownloadState.Failed:
                        return MusicItemUiState.Failed;
                }
            }

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

            // Reset label colour.
            TextElement title = element.Q<TextElement>("label0");
            if (title != null)
                title.style.color = TrackTextColor;

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
                    if (title != null)
                        title.style.color = TrackPlayingColor;
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
        /// Shows a brief tip message in the hint window (TipWindows / UICanvasTip).
        /// </summary>
        void ShowGateTip(string message)
        {
            ShowHintToast(message, 2500);
        }

        /// <summary>
        /// F5 download gate checks + pending music enqueue.
        /// </summary>
        void TriggerMusicDownload(int album, int track)
        {
            var tracks = MusicCatalog.GetTracks(album);
            if (track < 0 || track >= tracks.Count)
                return;

            string bankName = tracks[track].BankName;

            // Gate 1: No network.
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                ShowGateTip("需要网络连接");
                return;
            }

            // Gate 2: Insufficient storage (rough estimate).
            long requiredKb = 0;
            if (PresetBankCatalog.TryGet(bankName, out var remoteInfo) && remoteInfo != null)
            {
                if (!string.IsNullOrEmpty(remoteInfo.size) && long.TryParse(remoteInfo.size, out long sizeKb))
                    requiredKb = sizeKb;
            }

            if (requiredKb > 0)
            {
                long requiredBytes = requiredKb * 1024L;
                if (GetAvailableStorageBytes() < requiredBytes * 2)
                {
                    ShowGateTip("存储空间不足");
                    return;
                }
            }

            // Gate 3: Mobile data without user consent.
            // TODO: Wire up a user-consent dialog. For now, allow.
            // if (Application.internetReachability == NetworkReachability.ReachableViaCarrierData
            //     && !UserHasConsentedToMobileDownload)
            // {
            //     ShowMobileDownloadConsentDialog(() => DoDownload(album, track, bankName));
            //     return;
            // }

            DoDownload(album, track, bankName);
        }

        void DoDownload(int album, int track, string bankName)
        {
            // Shared with PresetForm — mutual overwrite (Boluo PendingDownloadIntent).
            _pendingMusic = (album, track);
            PendingDownloadIntent.SetMusic(album, track);

            // Initiate priority download.
            if (PresetBankCatalog.TryGet(bankName, out var remote))
            {
                IslandPlayerPosSave.PushSaveSuppress();
                BankDownloader.DownloadBank(
                    remote,
                    loadIntoEngine: true,
                    onDone: OnBankDownloadDone,
                    prioritize: true,
                    preempt: true);
            }
            else
            {
                Log.Warning($"[MusicForm] Bank not found in PresetBankCatalog: {bankName}");
                ClearPendingMusic();
            }
        }

        /// <summary>Called when the pending bank download completes (success or failure).</summary>
        void OnBankDownloadDone(BankDownloadItem item)
        {
            IslandPlayerPosSave.PopSaveSuppress();
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
                    GameEntry.Music?.PlayIndex(album, track);
                ClearPendingMusic();
            }

            _list?.RefreshItems();
        }

        void ClearPendingMusic()
        {
            _pendingMusic = null;
            if (PendingDownloadIntent.Current == PendingDownloadIntent.Kind.Music)
                PendingDownloadIntent.Clear();
        }

        /// <summary>
        /// Rough estimate of available storage in bytes ( PersistentDataPath drive).
        /// Returns <c>long.MaxValue</c> on failure (no gate triggered).
        /// </summary>
        static long GetAvailableStorageBytes()
        {
            try
            {
                string path = Application.persistentDataPath;
                var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
                if (drive.IsReady)
                    return drive.AvailableFreeSpace;
            }
            catch { /* ignore */ }
            return long.MaxValue;
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
    }
}
