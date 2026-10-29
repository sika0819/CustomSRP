using System.Collections.Generic;
using Game.BoluoApi;
using Game.Native;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// Edit Mode：游客落票 → 退登清用户票 → 再游客换票进会话（不进 Procedure / 不进岛）。
    /// 对齐 Android GuestRelogin 契约：退登必须清本地用户票；再登可复用同 JWT，也可换新票；
    /// deviceToken 跨退登保留。不打真实 touristLogin（无网 / 无宿主）。
    /// 真实进岛 / 退登 / 换游客再进见 Play Mode <c>GuestSessionLifecyclePlayModeTests</c>。
    /// </summary>
    public sealed class GuestSessionLifecycleTests
    {
        static readonly string[] PrefKeys =
        {
            "Boluo.DeviceToken",
            "Boluo.DeviceTokenExpire",
            "Boluo.DeviceTokenVersion",
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
            foreach (string key in PrefKeys)
            {
                if (!PlayerPrefs.HasKey(key))
                    continue;
                // device / token 多为 string；IsGuest / SaveLocal 为 int。
                if (key.EndsWith("IsGuest") || key.EndsWith("SaveLocal") || key == "Game.IsGuest")
                    _intBackup[key] = PlayerPrefs.GetInt(key, 0);
                else
                    _stringBackup[key] = PlayerPrefs.GetString(key, "");
            }

            GuestSession.Clear();
            BoluoSession.ClearDeviceToken();
            HostIslandEntryGate.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            GuestSession.Clear();
            BoluoSession.ClearDeviceToken();
            HostIslandEntryGate.Reset();

            foreach (string key in PrefKeys)
                PlayerPrefs.DeleteKey(key);

            foreach (var kv in _stringBackup)
                PlayerPrefs.SetString(kv.Key, kv.Value);
            foreach (var kv in _intBackup)
                PlayerPrefs.SetInt(kv.Key, kv.Value);

            // 原先没有的键保持删除；原先有的已写回。
            PlayerPrefs.Save();
            BoluoSession.LoadFromPrefs();
        }

        [Test]
        public void GuestLogin_Logout_Relogin_WithNewToken()
        {
            const string token1 = "guest_jwt_first";
            const string token2 = "guest_jwt_second";
            const string userId1 = "guest_u1";
            const string userId2 = "guest_u2";

            SeedDeviceToken("device_tok_keep");

            GuestSession.ApplyUserSession(token1, userId1, isGuest: true, saveLocal: false);
            Assert.IsTrue(GuestSession.IsLoggedIn);
            Assert.IsTrue(GuestSession.IsGuest);
            Assert.AreEqual(token1, GuestSession.UserToken);
            Assert.AreEqual(userId1, GuestSession.UserId);

            // 退登：只清用户票，deviceToken 保留（对齐 BoluoLogout.ExecuteLeave → GuestSession.Clear）。
            GuestSession.Clear();
            Assert.IsFalse(GuestSession.IsLoggedIn);
            Assert.IsFalse(GuestSession.IsGuest);
            Assert.IsTrue(string.IsNullOrEmpty(GuestSession.UserToken));
            Assert.IsTrue(string.IsNullOrEmpty(GuestSession.UserId));
            BoluoSession.LoadFromPrefs();
            Assert.AreEqual("device_tok_keep", BoluoSession.DeviceToken);

            // 再游客：换新票（服务端也可能回同一 JWT；两侧都合法，本例验换新路径）。
            GuestSession.ApplyUserSession(token2, userId2, isGuest: true, saveLocal: false);
            GuestSession.SyncFromBoluo();

            Assert.IsTrue(GuestSession.IsLoggedIn);
            Assert.IsTrue(GuestSession.IsGuest);
            Assert.AreEqual(token2, GuestSession.UserToken);
            Assert.AreEqual(userId2, GuestSession.UserId);
            Assert.AreNotEqual(token1, GuestSession.UserToken);
            BoluoSession.LoadFromPrefs();
            Assert.AreEqual("device_tok_keep", BoluoSession.DeviceToken);
        }

        [Test]
        public void GuestLogin_Logout_Relogin_SameJwtStillHydrates()
        {
            // 同 deviceToken 再 touristLogin 常回同一 JWT（Android 真机已见）；
            // 关键是退登清空后再落票，不是「必须换串」。
            const string jwt = "guest_jwt_same_device";
            const string userId = "guest_same";

            GuestSession.ApplyUserSession(jwt, userId, isGuest: true, saveLocal: false);
            GuestSession.Clear();
            Assert.IsFalse(GuestSession.IsLoggedIn);

            GuestSession.ApplyUserSession(jwt, userId, isGuest: true, saveLocal: false);
            Assert.IsTrue(GuestSession.IsLoggedIn);
            Assert.AreEqual(jwt, GuestSession.UserToken);
            Assert.AreEqual(userId, GuestSession.UserId);
            Assert.IsTrue(GuestSession.IsGuest);
        }

        [Test]
        public void ApplyLocalEditorGuest_ReusesToken_UntilCleared()
        {
            GuestSession.ApplyLocalEditorGuest();
            Assert.IsTrue(GuestSession.IsLoggedIn);
            Assert.IsTrue(GuestSession.IsGuest);
            string firstToken = GuestSession.UserToken;
            string firstId = GuestSession.UserId;
            Assert.IsTrue(firstToken.StartsWith("editor_guest_"));

            GuestSession.ApplyLocalEditorGuest();
            Assert.AreEqual(firstToken, GuestSession.UserToken);
            Assert.AreEqual(firstId, GuestSession.UserId);

            GuestSession.Clear();
            Assert.IsFalse(GuestSession.IsLoggedIn);

            GuestSession.ApplyLocalEditorGuest();
            Assert.IsTrue(GuestSession.IsLoggedIn);
            Assert.AreEqual(firstToken, GuestSession.UserToken);
            Assert.AreEqual(firstId, GuestSession.UserId);
        }

        [Test]
        public void HostGate_Arm_Reset_AllowsReArm()
        {
            // Edit Mode 不调 Leave（会扯 Procedure / 音频 runtime）；门闩拆装用 Reset 覆盖再登前须能再 Arm。
            int gen0 = HostIslandEntryGate.Generation;
            Assert.IsFalse(HostIslandEntryGate.IsArmed);

            HostIslandEntryGate.Arm();
            Assert.IsTrue(HostIslandEntryGate.IsArmed);
            Assert.AreEqual(gen0 + 1, HostIslandEntryGate.Generation);

            // 已武装再 Arm：忽略，不加重代。
            HostIslandEntryGate.Arm();
            Assert.IsTrue(HostIslandEntryGate.IsArmed);
            Assert.AreEqual(gen0 + 1, HostIslandEntryGate.Generation);

            HostIslandEntryGate.Reset();
            Assert.IsFalse(HostIslandEntryGate.IsArmed);

            HostIslandEntryGate.Arm();
            Assert.IsTrue(HostIslandEntryGate.IsArmed);
            Assert.AreEqual(gen0 + 2, HostIslandEntryGate.Generation);
        }

        [Test]
        public void NativeSession_ParseUserBlob_HydrateFields()
        {
            Assert.IsTrue(
                NativeSession.TryParseUserSessionBlob(
                    "1700000000\tjwt_abc\tuid_9\t0",
                    out uint expire,
                    out string token,
                    out bool isGuest,
                    out string userId,
                    out bool saveLocal));
            Assert.AreEqual(1700000000u, expire);
            Assert.AreEqual("jwt_abc", token);
            Assert.AreEqual("uid_9", userId);
            Assert.IsFalse(saveLocal);
            Assert.IsFalse(isGuest);

            Assert.IsFalse(
                NativeSession.TryParseUserSessionBlob(
                    "",
                    out _,
                    out _,
                    out _,
                    out _,
                    out _));
        }

        static void SeedDeviceToken(string token)
        {
            uint expire = (uint)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400u;
            BoluoSession.SetDeviceToken(token, expire);
            BoluoSession.LoadFromPrefs();
            Assert.AreEqual(token, BoluoSession.DeviceToken);
        }
    }
}
