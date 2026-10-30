using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// 防回归：无主存档 + 有旧音量键时，Load 内迁移写盘不得再经 Persist→EnsureLoaded 重入 Load（真机栈溢出）。
    /// </summary>
    public sealed class BoluoGameSaveVolumeMigrateTests
    {
        const string UserId = "vol_migrate_user";

        static readonly string[] SessionKeys =
        {
            "Boluo.UserToken",
            "Boluo.UserId",
            "Boluo.IsGuest",
            "Boluo.SaveLocal",
            "Game.UserToken",
            "Game.UserId",
            "Game.IsGuest",
        };

        Dictionary<string, string> _stringBackup;
        Dictionary<string, int> _intBackup;
        Dictionary<string, float> _floatBackup;
        bool _hadMusicKey;
        bool _hadEffectKey;
        bool _hadSaveBucket;

        [SetUp]
        public void SetUp()
        {
            _stringBackup = new Dictionary<string, string>();
            _intBackup = new Dictionary<string, int>();
            foreach (string key in SessionKeys)
            {
                if (!PlayerPrefs.HasKey(key))
                    continue;
                if (key.EndsWith("IsGuest") || key.EndsWith("SaveLocal") || key == "Game.IsGuest")
                    _intBackup[key] = PlayerPrefs.GetInt(key, 0);
                else
                    _stringBackup[key] = PlayerPrefs.GetString(key, "");
            }

            _floatBackup = new Dictionary<string, float>();
            _hadMusicKey = PlayerPrefs.HasKey(VolumeForm.MusicVolumeKey);
            if (_hadMusicKey)
                _floatBackup[VolumeForm.MusicVolumeKey] = PlayerPrefs.GetFloat(VolumeForm.MusicVolumeKey, 0.8f);
            _hadEffectKey = PlayerPrefs.HasKey(VolumeForm.EffectVolumeKey);
            if (_hadEffectKey)
                _floatBackup[VolumeForm.EffectVolumeKey] = PlayerPrefs.GetFloat(VolumeForm.EffectVolumeKey, 0.8f);

            string bucket = "BoluoSaveData." + UserId;
            _hadSaveBucket = PlayerPrefs.HasKey(bucket);
            if (_hadSaveBucket)
                _stringBackup[bucket] = PlayerPrefs.GetString(bucket, "");

            ClearFixturePrefs();
            GuestSession.Clear();
            BoluoGameSave.ResetSession();
        }

        [TearDown]
        public void TearDown()
        {
            ClearFixturePrefs();
            GuestSession.Clear();
            BoluoGameSave.ResetSession();

            foreach (string key in SessionKeys)
                PlayerPrefs.DeleteKey(key);
            PlayerPrefs.DeleteKey(VolumeForm.MusicVolumeKey);
            PlayerPrefs.DeleteKey(VolumeForm.EffectVolumeKey);
            PlayerPrefs.DeleteKey("BoluoSaveData." + UserId);

            foreach (var kv in _stringBackup)
                PlayerPrefs.SetString(kv.Key, kv.Value);
            foreach (var kv in _intBackup)
                PlayerPrefs.SetInt(kv.Key, kv.Value);
            foreach (var kv in _floatBackup)
                PlayerPrefs.SetFloat(kv.Key, kv.Value);

            if (!_hadMusicKey)
                PlayerPrefs.DeleteKey(VolumeForm.MusicVolumeKey);
            if (!_hadEffectKey)
                PlayerPrefs.DeleteKey(VolumeForm.EffectVolumeKey);
            if (!_hadSaveBucket)
                PlayerPrefs.DeleteKey("BoluoSaveData." + UserId);

            PlayerPrefs.Save();
        }

        [Test]
        public void Load_EmptySave_WithLegacyVolumePrefs_MigratesWithoutStackOverflow()
        {
            GuestSession.ApplyUserSession("tok_" + UserId, UserId, isGuest: false, saveLocal: true);
            Assert.IsTrue(BoluoGamePersistence.AllowsAccountProgressDisk);

            PlayerPrefs.DeleteKey("BoluoSaveData." + UserId);
            PlayerPrefs.SetFloat(VolumeForm.MusicVolumeKey, 0.42f);
            PlayerPrefs.SetFloat(VolumeForm.EffectVolumeKey, 0.55f);
            PlayerPrefs.Save();
            BoluoGameSave.ResetSession();

            // 旧实现会在此处 Load↔Persist 无限递归，真机 SIGSEGV stack overflow。
            BoluoGameSave.Load();

            Assert.IsTrue(BoluoGameSave.IsLoaded);
            Assert.AreEqual(42, BoluoGameSave.Data.bgmVolume);
            Assert.AreEqual(55, BoluoGameSave.Data.voiceVolume);

            string raw = PlayerPrefs.GetString("BoluoSaveData." + UserId, "");
            Assert.IsFalse(string.IsNullOrEmpty(raw), "migrated volumes must be persisted to account bucket");
            Assert.IsTrue(raw.Contains("\"bgmVolume\":42"), raw);
            Assert.IsTrue(raw.Contains("\"voiceVolume\":55"), raw);
        }

        static void ClearFixturePrefs()
        {
            PlayerPrefs.DeleteKey("BoluoSaveData." + UserId);
            PlayerPrefs.DeleteKey(VolumeForm.MusicVolumeKey);
            PlayerPrefs.DeleteKey(VolumeForm.EffectVolumeKey);
            foreach (string key in SessionKeys)
                PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }
}
