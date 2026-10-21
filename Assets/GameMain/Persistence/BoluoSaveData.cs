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
    }
}
