using System;
using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using Game.BoluoApi.Music;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Music catalog sourced from (1) online upgrade/bank API first, (2) local
    /// <c>StreamingAssets/WanosAudio/MusicLists.json</c> as fallback.
    /// No hard-coded album or track names.
    ///
    /// Startup flow:
    ///   BankUpgradeBootstrap.RunAsync()
    ///     └─ BankUpgradeClient.RequestUpgradeBank()  →  BankUpgradeData.musicLists
    ///          └─ MusicCatalog.SetServerData(musicLists)   ← 优先用线上
    ///   MusicForm.OnOpen()  （服务未就绪时）
    ///     └─ MusicCatalog.LoadAsync()  ← 本地兜底（线上没有/失败时）
    /// </summary>
    public static class MusicCatalog
    {
        /// <summary>Fired once catalog data is available (from server or local).</summary>
        public static event Action OnCatalogReady;

        /// <summary>True after catalog data has been populated.</summary>
        public static bool IsReady => _albums != null;

        /// <summary>Number of albums. Zero before the catalog is ready.</summary>
        public static int AlbumCount => _albums?.Count ?? 0;

        /// <summary>All loaded albums. Empty before the catalog is ready.</summary>
        public static IReadOnlyList<MusicAlbum> Albums => _albums;

        static List<MusicAlbum> _albums;
        static bool _loading;

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Inject catalog data from the online upgrade/bank API.
        /// Takes priority over any local JSON; subsequent calls are ignored.
        /// </summary>
        public static void SetServerData(List<MusicListServerData> serverData)
        {
            if (_albums != null)
                return; // Already populated — server data arrived earlier or local already loaded.
            if (serverData == null || serverData.Count == 0)
            {
                Debug.LogWarning("[MusicCatalog] Server musicLists empty — will fall back to local JSON.");
                LoadAsync();
                return;
            }
            _albums = BuildAlbums(serverData);
            Debug.Log($"[MusicCatalog] Set {AlbumCount} albums from server.");
            BroadcastReady();
        }

        /// <summary>
        /// Load catalog from the local <c>WanosAudio/MusicLists.json</c> file.
        /// No-op if already loaded or currently loading.
        /// Safe to call multiple times (e.g. as fallback from UI).
        /// </summary>
        public static void LoadAsync()
        {
            if (_albums != null || _loading)
                return;
            _loading = true;

            string path = JsonPath();
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[MusicCatalog] JsonPath returned null.");
                _loading = false;
                return;
            }

#if UNITY_EDITOR
            LoadJsonFromEditorPath(path);
#else
            LoadJsonFromPlayer(path);
#endif
        }

        /// <summary>Returns album names in load order. Empty array before ready.</summary>
        public static string[] GetAlbumNames()
        {
            if (_albums == null)
                return Array.Empty<string>();
            var names = new string[_albums.Count];
            for (int i = 0; i < _albums.Count; i++)
                names[i] = _albums[i].Name;
            return names;
        }

        /// <summary>Returns the album at <paramref name="albumIndex"/> or null if out of range.</summary>
        public static MusicAlbum GetAlbum(int albumIndex)
        {
            if (_albums == null || (uint)albumIndex >= (uint)_albums.Count)
                return null;
            return _albums[albumIndex];
        }

        /// <summary>Returns the track list of the given album, or an empty list if not found.</summary>
        public static IReadOnlyList<MusicTrack> GetTracks(int albumIndex)
        {
            var album = GetAlbum(albumIndex);
            return album?.Tracks ?? EmptyTracks;
        }

        /// <summary>Returns a specific track or null if either index is out of range.</summary>
        public static MusicTrack? GetTrack(int albumIndex, int trackIndex)
        {
            var album = GetAlbum(albumIndex);
            if (album == null || (uint)trackIndex >= (uint)album.Tracks.Length)
                return null;
            return album.Tracks[trackIndex];
        }

        /// <summary>
        /// Finds a track by its <c>BankName</c>. Returns true and sets indices if found.
        /// </summary>
        public static bool TryFindByBankName(string bankName, out int albumIdx, out int trackIdx)
        {
            albumIdx = -1;
            trackIdx = -1;
            if (_albums == null || string.IsNullOrEmpty(bankName))
                return false;
            for (int a = 0; a < _albums.Count; a++)
            {
                var tracks = _albums[a].Tracks;
                for (int t = 0; t < tracks.Length; t++)
                {
                    if (string.Equals(tracks[t].BankName, bankName, StringComparison.OrdinalIgnoreCase))
                    {
                        albumIdx = a;
                        trackIdx = t;
                        return true;
                    }
                }
            }
            return false;
        }

        // -----------------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Converts server list data into in-memory albums.
        /// Exposed so <see cref="BankUpgradeBootstrap"/> can also use this path.
        /// </summary>
        static List<MusicAlbum> BuildAlbums(List<MusicListServerData> src)
        {
            if (src == null || src.Count == 0)
                return new List<MusicAlbum>();

            var albums = new List<MusicAlbum>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i] == null)
                    continue;
                var trackList = new List<MusicTrack>();
                if (src[i].musics != null)
                {
                    for (int j = 0; j < src[i].musics.Count; j++)
                    {
                        if (src[i].musics[j] != null)
                            trackList.Add(new MusicTrack(src[i].musics[j]));
                    }
                }
                albums.Add(new MusicAlbum(src[i].musicsName ?? $"专辑{i + 1}", trackList.ToArray()));
            }
            return albums;
        }

        /// <summary>
        /// Parse a JSON string (from local file) into in-memory albums.
        /// Called from the local file loading path only.
        /// </summary>
        static void ParseJson(string text)
        {
            _loading = false;
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[MusicCatalog] Local JSON is empty — catalog will stay empty.");
                BroadcastReady();
                return;
            }
            try
            {
                var raw = Newtonsoft.Json.JsonConvert.DeserializeObject<MusicListsRaw>(text);
                if (raw?.musicLists == null || raw.musicLists.Count == 0)
                {
                    Debug.LogWarning("[MusicCatalog] Local JSON has no musicLists.");
                    BroadcastReady();
                    return;
                }
                _albums = BuildAlbums(raw.musicLists);
                Debug.Log($"[MusicCatalog] Loaded {AlbumCount} albums from local JSON.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicCatalog] Local JSON parse failed: {ex.Message}");
            }
            BroadcastReady();
        }

        static void BroadcastReady()
        {
            _loading = false;
            try
            {
                OnCatalogReady?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicCatalog] OnCatalogReady threw: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Platform-specific loading
        // -----------------------------------------------------------------------

#if UNITY_EDITOR
        static void LoadJsonFromEditorPath(string path)
        {
            Debug.Log($"[MusicCatalog] Loading local JSON (Editor): {path}");
            try
            {
                string text = File.ReadAllText(path);
                ParseJson(text);
            }
            catch (Exception ex)
            {
                _loading = false;
                Debug.LogWarning($"[MusicCatalog] Failed to read local JSON: {ex.Message}");
                BroadcastReady();
            }
        }
#else
        static void LoadJsonFromPlayer(string path)
        {
            Debug.Log($"[MusicCatalog] Loading local JSON (Player): {path}");
            // Extract relative path for StreamingAssetsUtil.
            string relative = path;
            string sa = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(sa) && path.StartsWith(sa, StringComparison.OrdinalIgnoreCase))
                relative = path.Substring(sa.Length).TrimStart('/', '\\');

            StreamingAssetsUtil.ReadTextCoroutine(relative, (ok, text) =>
            {
                if (ok && !string.IsNullOrEmpty(text))
                    ParseJson(text);
                else
                {
                    _loading = false;
                    Debug.LogWarning($"[MusicCatalog] Failed to load local JSON via StreamingAssetsUtil.");
                    BroadcastReady();
                }
            });
        }
#endif

        static string JsonPath()
        {
#if UNITY_EDITOR
            // Prefer the AssetRaw copy (matches BoluoUnity editor layout).
            var editorPath = Path.Combine(
                Application.dataPath,
                "AssetRaw",
                "Configs",
                "MusicLists.json");
            if (File.Exists(editorPath))
                return editorPath;
            // Fallback: StreamingAssets (used at runtime in custom SRP layout).
            return Path.Combine(Application.streamingAssetsPath, "WanosAudio", "MusicLists.json");
#else
            return Path.Combine(Application.streamingAssetsPath, "WanosAudio", "MusicLists.json");
#endif
        }

        // -----------------------------------------------------------------------
        // Internal JSON model (matches the local .json file schema)
        // -----------------------------------------------------------------------

        [Serializable]
        sealed class MusicListsRaw
        {
            public List<MusicListServerData> musicLists;
        }

        static readonly MusicTrack[] EmptyTracks = Array.Empty<MusicTrack>();
    }
}
