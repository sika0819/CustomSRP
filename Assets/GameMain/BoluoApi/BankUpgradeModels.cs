using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Game.BoluoApi
{
    [Serializable]
    public class BankUpgradeResponse
    {
        public int code;
        public string msg;
        public BankUpgradeData data;
    }

    [Serializable]
    public class BankUpgradeData
    {
        public string appVersion;
        public string bankVersion;
        public string projectName;
        public string projectGuid;
        public string projectDirectory;
        public string nativeVersion;
        public string studioVersion;
        public string runMode;
        /// <summary>后台开关：true=一树全景 SkyDome，false=进 3D 岛。缺省时看 yishu / runMode。</summary>
        public bool? panorama;
        /// <summary>后台开关别名，与 panorama 二选一。</summary>
        public bool? yishu;
        public string lastModifiedTimestamp;
        public string buildTimestamp;
        public string buildGuid;
        public string builderAccountId;
        public List<BankRemoteInfo> banks;
        [JsonProperty("musicLists")]
        public List<MusicListServerData> musicLists;
        [JsonProperty("MusicLists")]
        public List<MusicListServerData> MusicLists
        {
            get => musicLists;
            set
            {
                if (value != null && (musicLists == null || musicLists.Count == 0))
                    musicLists = value;
            }
        }
    }

    [Serializable]
    public class BankRemoteInfo
    {
        public string guid;
        public string bankName;
        public int type;
        public string lastModifiedTimestamp;
        public string version;
        public string author;
        public string url;
        public string md5;
        /// <summary>大小（KB，字符串）。</summary>
        public string size;
    }

    [Serializable]
    public class MusicListServerData
    {
        public string musicsName;
        public List<MusicServerData> musics;
    }

    [Serializable]
    public class MusicServerData
    {
        public string bankName;
        public string bankGuid;
        public string eventName;
        public string musicName;
    }
}

/// <summary>
/// 本地音乐目录数据结构（独立于服务端 JSON 模型）。
/// </summary>
namespace Game.BoluoApi.Music
{
    using Game.BoluoApi;

    public readonly struct MusicTrack
    {
        public readonly string MusicName;
        public readonly string BankName;
        public readonly string EventName;
        public readonly string BankGuid;

        public MusicTrack(string musicName, string bankName, string eventName, string bankGuid = "")
        {
            MusicName = musicName ?? "";
            BankName = bankName ?? "";
            EventName = eventName ?? "";
            BankGuid = bankGuid ?? "";
        }

        /// <summary>从服务端 MusicServerData 构造。</summary>
        public MusicTrack(MusicServerData src)
        {
            MusicName = src.musicName ?? "";
            BankName = src.bankName ?? "";
            EventName = src.eventName ?? (string.IsNullOrEmpty(src.bankName) ? "" : "Play_" + src.bankName);
            BankGuid = src.bankGuid ?? "";
        }
    }

    public sealed class MusicAlbum
    {
        public readonly string Name;
        public readonly MusicTrack[] Tracks;

        public MusicAlbum(string name, MusicTrack[] tracks)
        {
            Name = name ?? "";
            Tracks = tracks ?? Array.Empty<MusicTrack>();
        }

        public int TrackCount => Tracks?.Length ?? 0;
    }

    /// <summary>UI 显示用曲目下载/播放状态。</summary>
    public enum MusicItemUiState
    {
        Failed = 0,
        NotDownloaded = 1,
        Downloading = 2,
        Succeeded = 3,
        Ready = 4,
        Playing = 5,
    }
}
