using System.Collections.Generic;
using System.Text;
using Game.BoluoApi.Music;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// 后台传输计划：所有 mode 推当前专辑曲序（含未就绪占位；可播才有 path）。
    /// 负一手动：邻接未就绪 → 原生重播当前；已就绪 → 切过去。
    /// mode 仍写入 JSON，供曲终 auto（只落可播曲）使用。
    /// iOS 无对等 API → 三方法全部 no-op。
    /// </summary>
    public static class BackgroundTransportSync
    {
        /// <summary>
        /// 推后台传输计划：当前专辑 bank 已就绪曲。无曲目 / 当前曲未就绪 → Clear。
        /// </summary>
        public static void PublishPlan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PublishPlanAndroid();
#endif
        }

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
#if UNITY_ANDROID && !UNITY_EDITOR
            ResyncFromNativeAndroid();
#endif
        }

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

        static void PublishPlanAndroid()
        {
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
            int catalogInAlbum = MusicCatalog.GetTracks(album).Count;

            // 任意 mode 只推本专辑（含未就绪曲，path 空）：负一手动邻接未下完由原生重播当前；
            // 已就绪则切过去。曲终 auto 仍只落在可播曲。
            var tracks = MusicCatalog.GetTracks(album);
            for (int i = 0; i < tracks.Count; i++)
            {
                if (!TryAppendPlanTrack(tracks[i], out int addedAt))
                    continue;
                if (i == track)
                    currentInPlan = addedAt;
            }

            if (Scratch.Count == 0 || currentInPlan < 0
                || string.IsNullOrEmpty(Scratch[currentInPlan].Path))
            {
                Log.Warning(
                    $"[BackgroundTransportSync] ClearPlan album={album} track={track} " +
                    $"catalogInAlbum={catalogInAlbum} plan={Scratch.Count} currentInPlan={currentInPlan}");
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
        }

        static bool TryAppendPlanTrack(MusicTrack t, out int indexInScratch)
        {
            indexInScratch = -1;
            if (string.IsNullOrEmpty(t.EventName) || string.IsNullOrEmpty(t.BankName))
                return false;

            // 未下载完也占位：计划下标 = 专辑曲序，负一手动才能对「下一首」判就绪。
            string path = "";
            bool loaded = false;
            if (WanosBankPaths.Instance.TryResolve(t.BankName, out string resolved)
                && WanosBankStore.IsLikelyValidBankFile(resolved))
            {
                path = resolved;
                loaded = WanosBankStore.IsLoaded(t.BankName);
            }

            indexInScratch = Scratch.Count;
            Scratch.Add(new PlanEntry
            {
                Name = t.MusicName ?? "",
                Event = t.EventName,
                Bank = t.BankName,
                Guid = t.BankGuid ?? "",
                Path = path,
                Loaded = loaded,
            });
            return true;
        }

        static void ResyncFromNativeAndroid()
        {
            string json = AndroidBackgroundTransport.GetStateJson();
            if (string.IsNullOrEmpty(json))
                return;

            if (!TryParseState(json, out string eventName, out bool paused, out bool suspended, out bool valid))
                return;

            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            // 到点闩不被原生 suspended 盖掉。其余用写入前的 Silence 合并，suspended 不降级用户暂停。
            if (NowPlayingService.PausedForTimerEnd)
                PlaybackGate.SetSilence(SilenceReason.TimerEnd, "resync-native");
            else
            {
                bool resumeInFlight = music != null && music.HardReplayPending;
                PlaybackGate.AdoptFromNative(paused, suspended, "resync-native", resumeInFlight);
            }
            NowPlayingService.MirrorSilenceLatch();

            if (music == null)
                return;

            bool latchPaused = paused || suspended;
            if (!valid || string.IsNullOrEmpty(eventName))
            {
                if (latchPaused)
                    music.ApplyNativePausedLatch(true);
                return;
            }

            if (!MusicCatalog.TryFindByEventName(eventName, out int albumIdx, out int trackIdx))
            {
                Log.Warning($"[BackgroundTransportSync] Resync: unknown event={eventName}");
                music.ApplyNativePausedLatch(latchPaused);
                return;
            }

            music.AdoptTrack(albumIdx, trackIdx, latchPaused);
            music.ApplyNativePausedLatch(latchPaused);
            Log.Info(
                $"[BackgroundTransportSync] Resync adopted album={albumIdx} track={trackIdx}"
                + $" event={eventName} paused={paused} suspended={suspended}");
        }

        static bool TryParseState(
            string json, out string eventName, out bool paused, out bool suspended, out bool valid)
        {
            eventName = "";
            paused = false;
            suspended = false;
            valid = false;
            try
            {
                eventName = ExtractString(json, "event") ?? "";
                paused = ExtractBool(json, "paused");
                suspended = ExtractBool(json, "suspended");
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
