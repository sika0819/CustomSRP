using System.Collections.Generic;
using System.Text;
using Game.BoluoApi.Music;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// 后台传输计划：Single/Loop 推当前专辑，Shuffle 推全部专辑；回前台对账原生可能切过的曲。
    /// iOS 无对等 API → 三方法全部 no-op。
    /// </summary>
    public static class BackgroundTransportSync
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        static readonly StringBuilder Sb = new StringBuilder(1024);
        static readonly List<PlanEntry> Scratch = new List<PlanEntry>(32);

        struct PlanEntry
        {
            public string Name;
            public string Event;
            public string Bank;
            public string Guid;
            public string Path;
            public bool Loaded;
        }
#endif

        /// <summary>
        /// 推后台传输计划：Single/Loop = 当前专辑；Shuffle = 全部专辑可播曲。
        /// 无曲目 / 当前曲 bank 未就绪 → Clear。
        /// </summary>
        public static void PublishPlan()
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            return;
#else
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            if (music == null || !MusicCatalog.IsReady)
            {
                ClearPlan();
                return;
            }

            int album = music.CurrentAlbumIndex;
            int track = music.CurrentTrackIndex;
            if (album < 0 || track < 0)
            {
                ClearPlan();
                return;
            }

            Scratch.Clear();
            int currentInPlan = -1;
            bool shuffleAll = music.PlayMode == MusicPlayMode.Shuffle;

            if (shuffleAll)
            {
                for (int a = 0; a < MusicCatalog.AlbumCount; a++)
                {
                    var albumTracks = MusicCatalog.GetTracks(a);
                    for (int i = 0; i < albumTracks.Count; i++)
                    {
                        if (!TryAppendPlanTrack(albumTracks[i], out int addedAt))
                            continue;
                        if (a == album && i == track)
                            currentInPlan = addedAt;
                    }
                }
            }
            else
            {
                var tracks = MusicCatalog.GetTracks(album);
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (!TryAppendPlanTrack(tracks[i], out int addedAt))
                        continue;
                    if (i == track)
                        currentInPlan = addedAt;
                }
            }

            if (Scratch.Count == 0 || currentInPlan < 0)
            {
                ClearPlan();
                return;
            }

            Sb.Length = 0;
            Sb.Append("{\"mode\":").Append(music.PlayMode)
                .Append(",\"current\":").Append(currentInPlan)
                .Append(",\"tracks\":[");
            for (int i = 0; i < Scratch.Count; i++)
            {
                if (i > 0)
                    Sb.Append(',');
                PlanEntry e = Scratch[i];
                Sb.Append("{\"name\":\"").Append(Escape(e.Name))
                    .Append("\",\"event\":\"").Append(Escape(e.Event))
                    .Append("\",\"bank\":\"").Append(Escape(e.Bank))
                    .Append("\",\"guid\":\"").Append(Escape(e.Guid))
                    .Append("\",\"path\":\"").Append(Escape(e.Path))
                    .Append("\",\"loaded\":").Append(e.Loaded ? "true" : "false")
                    .Append('}');
            }
            Sb.Append("]}");

            AndroidBackgroundTransport.SetPlan(Sb.ToString());
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <returns>false if track skipped; <paramref name="indexInScratch"/> is Scratch index when added.</returns>
        static bool TryAppendPlanTrack(MusicTrack t, out int indexInScratch)
        {
            indexInScratch = -1;
            if (string.IsNullOrEmpty(t.EventName) || string.IsNullOrEmpty(t.BankName))
                return false;
            if (!WanosBankPaths.Instance.TryResolve(t.BankName, out string path))
                return false;
            // 与 Java looksLikeWanosBank / 原生 loadBank 门禁对齐：坏文件不进计划
            if (!WanosBankStore.IsLikelyValidBankFile(path))
                return false;

            indexInScratch = Scratch.Count;
            Scratch.Add(new PlanEntry
            {
                Name = t.MusicName ?? "",
                Event = t.EventName,
                Bank = t.BankName,
                Guid = t.BankGuid ?? "",
                Path = path,
                Loaded = WanosBankStore.IsLoaded(t.BankName),
            });
            return true;
        }
#endif

        public static void ClearPlan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidBackgroundTransport.ClearPlan();
#endif
        }

        /// <summary>
        /// 回前台对账：原生可能在后台切过歌 / 改过暂停态。
        /// 用 event 名对齐 C# 当前曲；paused 同步到 <see cref="NowPlayingService"/>.
        /// </summary>
        public static void ResyncFromNative()
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            return;
#else
            string json = AndroidBackgroundTransport.GetStateJson();
            if (string.IsNullOrEmpty(json))
                return;

            if (!TryParseState(json, out string eventName, out bool paused, out bool valid))
                return;
            if (!valid || string.IsNullOrEmpty(eventName))
                return;

            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            if (music == null)
                return;

            // 暂停态先同步给 C#（AdoptTrack 路径可能提前 return）；
            // RestoreForegroundAudio 在 Resync 之后会再清一次（覆盖负一屏暂停）。
            NowPlayingService.NoteUserPaused(paused);

            if (!MusicCatalog.TryFindByEventName(eventName, out int albumIdx, out int trackIdx))
            {
                Log.Warning($"[BackgroundTransportSync] Resync: unknown event={eventName}");
                return;
            }

            // 同曲也对账：重挂 voice + Loop/Shuffle Break（等本曲播完再自动下一首）。
            music.AdoptTrack(albumIdx, trackIdx);
            Log.Info($"[BackgroundTransportSync] Resync adopted album={albumIdx} track={trackIdx} event={eventName} paused={paused}");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static bool TryParseState(string json, out string eventName, out bool paused, out bool valid)
        {
            eventName = "";
            paused = false;
            valid = false;
            try
            {
                eventName = ExtractString(json, "event") ?? "";
                paused = ExtractBool(json, "paused");
                valid = ExtractBool(json, "valid");
                return true;
            }
            catch (System.Exception e)
            {
                Log.Warning($"[BackgroundTransportSync] parse state failed: {e.Message}");
                return false;
            }
        }

        static string ExtractString(string json, string key)
        {
            string needle = "\"" + key + "\":\"";
            int i = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (i < 0)
                return null;
            int start = i + needle.Length;
            int end = json.IndexOf('"', start);
            if (end < 0)
                return null;
            return json.Substring(start, end - start);
        }

        static bool ExtractBool(string json, string key)
        {
            string needle = "\"" + key + "\":";
            int i = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (i < 0)
                return false;
            int start = i + needle.Length;
            while (start < json.Length && json[start] == ' ')
                start++;
            return start + 4 <= json.Length
                   && string.Compare(json, start, "true", 0, 4, System.StringComparison.OrdinalIgnoreCase) == 0;
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
#endif
    }
}
