using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// 预设播放态按 userId 分桶：退登只清内存，再进岛读当前账号的 curSchemeID。
    /// </summary>
    public sealed class PresetPlaybackAccountTests
    {
        const string UserA = "scheme_acct_a";
        const string UserB = "scheme_acct_b";
        const int SchemeId = 4;

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

            DeleteBuckets();
            GuestSession.Clear();
            BoluoGameSave.ResetSession();
        }

        [TearDown]
        public void TearDown()
        {
            DeleteBuckets();
            GuestSession.Clear();
            BoluoGameSave.ResetSession();

            foreach (string key in SessionKeys)
                PlayerPrefs.DeleteKey(key);
            foreach (var kv in _stringBackup)
                PlayerPrefs.SetString(kv.Key, kv.Value);
            foreach (var kv in _intBackup)
                PlayerPrefs.SetInt(kv.Key, kv.Value);
            PlayerPrefs.Save();
        }

        [Test]
        public void Logout_ClearsMemory_Relogin_RestoresThatAccountsScheme()
        {
            var asset = ScriptableObject.CreateInstance<IslandPresetAsset>();
            asset.Id = SchemeId;
            var go = new GameObject("PresetPlaybackAccountTests");
            PresetService service = go.AddComponent<PresetService>();
            try
            {
                service.SetPresets(new[] { asset });
                Login(UserA);

                service.Apply(SchemeId, teleportPlayer: false);
                Assert.AreEqual(SchemeId, service.CurrentId);
                Assert.AreEqual(2, service.SchemeState);
                Assert.AreEqual(SchemeId, BoluoGameSave.PresetSchemeId);
                Assert.IsFalse(BoluoGameSave.Recommend);

                service.ClearPlaybackForAccountSwitch();
                BoluoGameSave.ResetSession();
                Assert.AreEqual(0, service.CurrentId);
                Assert.AreEqual(0, service.SchemeState);

                Login(UserB);
                BoluoGameSave.Load();
                service.RestorePlaybackFromAccount();
                Assert.AreEqual(0, BoluoGameSave.PresetSchemeId);
                Assert.AreEqual(0, service.CurrentId);
                Assert.AreEqual(0, service.SchemeState);

                GuestSession.Clear();
                BoluoGameSave.ResetSession();
                Login(UserA);
                BoluoGameSave.Load();
                service.RestorePlaybackFromAccount();
                Assert.AreEqual(SchemeId, BoluoGameSave.PresetSchemeId);
                Assert.AreEqual(SchemeId, service.CurrentId);
                Assert.AreEqual(2, service.SchemeState);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(asset);
                BoluoGameSave.ResetSession();
            }
        }

        static void Login(string userId)
        {
            GuestSession.ApplyUserSession("tok_" + userId, userId, isGuest: false, saveLocal: true);
        }

        static void DeleteBuckets()
        {
            PlayerPrefs.DeleteKey("BoluoSaveData." + UserA);
            PlayerPrefs.DeleteKey("BoluoSaveData." + UserB);
            PlayerPrefs.Save();
        }
    }
}
