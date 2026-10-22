using System;

namespace Game
{
    /// <summary>
    /// 主游戏存档逻辑模型（PlayerPrefs JSON 键 <see cref="BoluoGamePersistence.Keys.BoluoSaveData"/>）。
    /// 音量为 0–100，与旧 Boluo 工程兼容。
    /// </summary>
    [Serializable]
    public class BoluoSaveData
    {
        public bool weatherLinkage;
        public bool healthTip = true;
        public bool recommend = true;
        public int bgmVolume = 80;
        public int voiceVolume = 80;
        public uint maxHealthLevelDate;
        public int maxHealthLevel;
        public int noEnquireHealthLevel;
        public int lastPressureLevel;
        public uint lastPressureLevelDate;

        /// <summary>
        /// 是否已写入过岛上大气快照。false（含老存档）→ 进岛跟墙钟；true → 恢复天气/时段。
        /// </summary>
        public bool hasIslandAtmosphere;
        /// <summary><see cref="IslandWeatherKind"/> 整型（Clear = -1）。</summary>
        public int weatherKind = (int)IslandWeatherKind.Cloud;
        public int weatherLevel = 1;
        public float hours = 12f;
        /// <summary><see cref="IslandTimePeriod"/> 整型。</summary>
        public int period = (int)IslandTimePeriod.Noon;
        /// <summary><see cref="IslandLookKind"/> 整型。</summary>
        public int lookKind = (int)IslandLookKind.Canonical;
        public float fogDensity = 0.001f;
    }
}
