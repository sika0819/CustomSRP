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
