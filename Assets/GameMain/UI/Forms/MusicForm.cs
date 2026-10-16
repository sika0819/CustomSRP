using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // Persistence keys
        // -----------------------------------------------------------------------

        /// <summary>Format: "m:{AlbumName}|{MusicName}" — case-insensitive set.</summary>
        static readonly HashSet<string> _heardBanks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string HeardMarkerFile = "music_heard.dat";

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

        VisualElement _albumContainer;
        VisualElement _albumContent;
        /// <summary>Visual labels (albumCount × <see cref="AlbumStripCopies"/> for loop).</summary>
        /// <remarks>Use <see cref="TextElement"/> not <see cref="Label"/> — Unity's default
        /// Label theme washes :hover text to white on the white card (invisible).</remarks>
        readonly List<TextElement> _albumLabels = new List<TextElement>();
        readonly List<EventCallback<ClickEvent>> _albumClicks = new List<EventCallback<ClickEvent>>();
        static readonly Color AlbumTextColor = new Color(24f / 255f, 24f / 255f, 25f / 255f, 1f);
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

            // Load heard markers from disk.
            LoadHeardMarkers();

            // Cache frequently used elements.
            _hintWindows = Root.Q("hintwindows");
            _hintLabel = Root.Q<Label>("labeltmp");
            if (_hintWindows != null)
                _hintWindows.style.opacity = 0f;

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

            // Persist heard markers.
            SaveHeardMarkers();

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

        // -----------------------------------------------------------------------
        // Heard markers persistence
        // -----------------------------------------------------------------------

        static string HeardMarkerPath =>
            Path.Combine(Application.persistentDataPath, HeardMarkerFile);

        static void LoadHeardMarkers()
        {
            _heardBanks.Clear();
            string path = HeardMarkerPath;
            if (!File.Exists(path))
                return;
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (!string.IsNullOrWhiteSpace(raw))
                        _heardBanks.Add(raw.Trim());
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[MusicForm] LoadHeardMarkers failed: {ex.Message}");
            }
        }

        static void SaveHeardMarkers()
        {
            try
            {
                string dir = Path.GetDirectoryName(HeardMarkerPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var lines = _heardBanks.ToArray();
                Array.Sort(lines, StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(HeardMarkerPath, lines);
            }
            catch (Exception ex)
            {
                Log.Warning($"[MusicForm] SaveHeardMarkers failed: {ex.Message}");
            }
        }

        /// <summary>Mark a track as heard (red-dot suppressed).</summary>
        static void MarkHeard(string albumName, string musicName)
        {
            _heardBanks.Add($"m:{albumName}|{musicName}");
        }

        /// <summary>Returns true if this track has been heard before.</summary>
        static bool HasHeard(string albumName, string musicName)
        {
            return _heardBanks.Contains($"m:{albumName}|{musicName}");
        }

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
            RefreshAlbumStrip();
            // Place strip on middle copy immediately so labels are under the indicator.
            if (_albumContent != null && count > 0)
                _albumContent.style.left = TargetLeftForAlbum(_centerAlbum, count);
            RefreshAlbumStrip(); // re-apply selected highlight for middle copy
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

        void RefreshTitle()
        {
            Label title = Root.Q<Label>("title");
            if (title == null)
                return;

            if (!MusicCatalog.IsReady)
            {
                title.text = "加载中...";
                return;
            }

            // Per InGameYinYueView.RefreshList: title reports the size of the
            // currently-visible album, not the whole catalog sum.
            int count = MusicCatalog.GetTracks(_centerAlbum).Count;
            title.text = $"全部歌曲{count}首";
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
                EventCallback<ClickEvent> click = evt =>
                {
                    // Click switches only when it was not a drag.
                    if (_isDragging)
                        return;
                    if (evt.currentTarget is TextElement tapped && tapped.userData is int logical)
                        OnAlbumClicked(logical);
                };
                label.RegisterCallback(click);
                _albumClicks.Add(click);
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

            // Highlight: during drag use live slot under center; otherwise committed album.
            float left = _albumContent.resolvedStyle.left;
            int highlightSlot = Mathf.RoundToInt(-left / AlbumStep);
            if (_isDragging && highlightSlot >= 0 && highlightSlot < slotCount)
            {
                // live visual only
            }
            else if (highlightSlot < 0 || highlightSlot >= slotCount
                     || highlightSlot % albumCount != _centerAlbum)
            {
                highlightSlot = albumCount + _centerAlbum; // middle copy
            }

            for (int i = 0; i < _albumLabels.Count; i++)
            {
                var label = _albumLabels[i];
                if (label == null)
                    continue;
                int logical = i % albumCount;
                label.text = names[logical];
                label.userData = logical;
                // Inline color beats any leftover theme wash.
                label.style.color = AlbumTextColor;
                bool isSelected = i == highlightSlot;
                label.EnableInClassList(AlbumLabelSelectedModifier, isSelected);
                label.EnableInClassList(AlbumLabelUnselectedModifier, !isSelected);
            }
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
            _list.itemsSource = _trackNames;
            _list.makeItem = MakeItem;
            _list.bindItem = BindItem;
            _list.unbindItem = UnbindItem;
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

            // Locate the existing content container (kept as a non-Label
            // child of _albumContainer so its `left` is independent from the
            // viewport clip). If UXML ever ships without one, create it on the
            // fly so we don't drag the viewport itself.
            _albumContent = null;
            foreach (var child in _albumContainer.Children())
            {
                if (child is Label)
                    continue;
                _albumContent = child;
                break;
            }
            if (_albumContent == null)
            {
                _albumContent = new VisualElement();
                _albumContent.name = "albumContent";
                _albumContainer.Add(_albumContent);
            }

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

            Root.schedule.Execute(RefreshAlbumLayout).StartingIn(16);
        }

        void UnbindAlbumDrag()
        {
            if (_albumContainer == null)
                return;
            EndAlbumPointerGesture(commit: false);
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
                ? _albumContent.resolvedStyle.left
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

            float left = _albumContent.resolvedStyle.left;
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
            float startLeft = _albumContent.resolvedStyle.left;
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
            if (_hintWindows == null)
                return;
            if (_hintLabel != null)
                _hintLabel.text = text;
            _hintWindows.style.opacity = 1f;
            _tipHide?.Pause();
            _tipHide = Root.schedule.Execute(() =>
            {
                if (_hintWindows != null)
                    _hintWindows.style.opacity = 0f;
                _tipHide = null;
            }).StartingIn((long)((ModeTipSeconds + ModeTipFadeSeconds) * 1000f));
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

            var label = new Label();
            label.name = "label0";
            label.style.position = Position.Absolute;
            label.style.left = 0; label.style.top = 0; label.style.bottom = 0; label.style.right = 0;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.fontSize = 45;
            label.style.color = new Color(0.094f, 0.094f, 0.098f, 1f);
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
            Label title = element.Q<Label>("label0");
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
                    title.style.color = new Color(0, 0.639f, 0.459f, 1f);
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
            if (_pendingMusic.HasValue
                && _pendingMusic.Value.album == album
                && _pendingMusic.Value.track == track)
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
                if (albumData != null && !HasHeard(albumData.Name, trackData.MusicName))
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
            Label title = element.Q<Label>("label0");
            if (title != null)
                title.style.color = new Color(0.094f, 0.094f, 0.098f, 1f);

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
                        title.style.color = new Color(0, 0.639f, 0.459f, 1f);
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
        /// Shows a brief tip message in the hint window.
        /// </summary>
        void ShowGateTip(string message)
        {
            if (_hintLabel != null)
                _hintLabel.text = message;
            if (_hintWindows != null)
            {
                _hintWindows.style.opacity = 1f;
                _tipHide?.Pause();
                _tipHide = Root.schedule.Execute(() =>
                {
                    if (_hintWindows != null)
                        _hintWindows.style.opacity = 0f;
                    _tipHide = null;
                }).StartingIn(2500);
            }
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
            // Register pending intent.
            _pendingMusic = (album, track);

            // Initiate priority download.
            if (PresetBankCatalog.TryGet(bankName, out var remote))
            {
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
            if (item == null || string.IsNullOrEmpty(item.BankName))
            {
                ClearPendingMusic();
                _list?.RefreshItems();
                return;
            }

            bool wasPending = false;
            if (_pendingMusic.HasValue)
            {
                var pending = _pendingMusic.Value;
                var tracks = MusicCatalog.GetTracks(pending.album);
                if (pending.track >= 0 && pending.track < tracks.Count)
                {
                    string pendingBank = tracks[pending.track].BankName;
                    wasPending = string.Equals(pendingBank, item.BankName, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (wasPending)
            {
                if (item.State == BankDownloadState.Ready || item.State == BankDownloadState.Skipped)
                {
                    // Download succeeded — auto-play.
                    GameEntry.Music?.PlayIndex(_pendingMusic.Value.album, _pendingMusic.Value.track);
                }
                // Download failed — do not auto-play; leave the Failed state visible.
                ClearPendingMusic();
            }

            _list?.RefreshItems();
        }

        void ClearPendingMusic()
        {
            _pendingMusic = null;
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
