#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Game.BoluoApi;
using Game.Native;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEditor;

namespace Game.Tests
{
    /// <summary>
    /// Play Mode：对齐真机壳时序。先在壳上游客登录，再启动 Unity；
    /// 交票后 Unity 才 Query(20) 进岛。退登走 <see cref="EditorNativeHost.Logout"/>（只入队 107），
    /// 加载罩「登录中」出现后再交第二次游客票。不写 Prefs 冒充宿主，也不同帧 Drain。
    /// </summary>
    public sealed class GuestSessionLifecyclePlayModeTests
    {
        const float BootTimeoutSec = 120f;
        const float IslandEnterTimeoutSec = 240f;
        const float LeaveTimeoutSec = 120f;
        const float StayOnIslandSec = 10f;

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
        bool _remoteGuestBackup;
        bool _hadRemoteGuestBackup;

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            BackupPrefs();
            _hadRemoteGuestBackup = true;
            _remoteGuestBackup = EditorPrefs.GetBool(GuestLoginClient.EditorTryRemoteKey, false);
            // 确定性：本地 Editor 游客票，不打 touristLogin。
            EditorPrefs.SetBool(GuestLoginClient.EditorTryRemoteKey, false);

            ResetSessionRuntime();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (Application.isPlaying
                && GameEntry.Procedure != null
                && GameEntry.Procedure.CurrentProcedure is ProcedureIsland)
            {
                EditorNativeHost.Logout();
                float t0 = Time.realtimeSinceStartup;
                while (GameEntry.Procedure.CurrentProcedure is ProcedureIsland
                       && Time.realtimeSinceStartup - t0 < 30f)
                    yield return null;
            }

            ResetSessionRuntime();
            RestorePrefs();
            if (_hadRemoteGuestBackup)
                EditorPrefs.SetBool(GuestLoginClient.EditorTryRemoteKey, _remoteGuestBackup);
            yield return null;
        }

        /// <summary>
        /// 壳先游客登录 → 启动 Unity → 交票进岛 → Logout → 加载罩后再交另一张游客票进岛。
        /// 票是壳侧占位，进岛走本地 bank，不打 upgrade/bank。
        /// </summary>
        [UnityTest]
        [NUnit.Framework.Timeout(600000)]
        [Category("SessionLifecycle")]
        public IEnumerator GuestLogin_EnterIsland_Leave_ReloginOtherGuest_EnterAgain()
        {
            const string deviceToken = "pm_device_tok_keep";
            const string token1 = "pm_guest_jwt_first";
            const string token2 = "pm_guest_jwt_second";
            const string userId1 = "pm_guest_u1";
            const string userId2 = "pm_guest_u2";
            uint deviceExpire = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400u;

            int enterBaseline = ProcedureIsland.EnterCount;

            // 壳先落票。此时还没启动 Unity，Prefs 里没有用户票。
            EditorNativeHost.CompleteGuestLogin(deviceToken, deviceExpire, token1, userId1);
            Assert.IsTrue(string.IsNullOrEmpty(GuestSession.UserToken));
            Assert.IsTrue(string.IsNullOrEmpty(GuestSession.UserId));

            yield return ReloadLaunchScene();
            yield return WaitUntil(
                () => GameEntry.Procedure != null && GameEntry.Base != null,
                BootTimeoutSec,
                "GameEntry.Procedure / Base");

            // 壳上已有票：Tick 会自动交 106，不必再点编辑器登录窗。
            yield return WaitUntil(
                () => HostIslandEntryGate.IsArmed,
                BootTimeoutSec,
                "HostIslandEntryGate Arm (106) for first guest");

            yield return WaitUntil(
                () => ProcedureIsland.EnterCount >= enterBaseline + 1
                    && IsIslandSceneLoaded(),
                IslandEnterTimeoutSec,
                "first ProcedureIsland + Island scene");

            yield return WaitUntil(
                () => GameEntry.World != null && GameEntry.World.IsNearReady
                    && !BootLoadingService.IsVisible,
                IslandEnterTimeoutSec,
                "first island uncovered");

            Assert.IsTrue(GuestSession.IsLoggedIn);
            Assert.AreEqual(userId1, GuestSession.UserId);
            Assert.AreEqual(token1, GuestSession.UserToken);
            Assert.IsFalse(GuestSession.SaveLocal);
            Assert.IsInstanceOf<ProcedureIsland>(GameEntry.Procedure.CurrentProcedure);
            BoluoSession.LoadFromPrefs();
            Assert.AreEqual(deviceToken, BoluoSession.DeviceToken);

            // 退登只入队 107。等回到 GuestLogin 且用户票已清，再交第二张票。
            // 不能等文案「登录中」：回到 GuestLogin 后若壳上还没票，罩会被改成编辑器登录提示。
            EditorNativeHost.Logout();

            yield return WaitUntil(
                () => ProcedureGuestLogin.IsCurrent
                    && !GuestSession.IsLoggedIn
                    && !HostIslandEntryGate.IsArmed
                    && BootLoadingService.IsVisible,
                LeaveTimeoutSec,
                "GuestLogin cover after leave, user ticket cleared");

            BoluoSession.LoadFromPrefs();
            Assert.AreEqual(deviceToken, BoluoSession.DeviceToken);
            Assert.IsTrue(string.IsNullOrEmpty(GuestSession.UserToken));
            Assert.IsTrue(string.IsNullOrEmpty(GuestSession.UserId));

            int enterAfterLeave = ProcedureIsland.EnterCount;
            EditorNativeHost.CompleteGuestLogin(deviceToken, deviceExpire, token2, userId2);
            EditorNativeHost.PassGuestLoginToUnity();

            yield return WaitUntil(
                () => HostIslandEntryGate.IsArmed,
                BootTimeoutSec,
                "HostIslandEntryGate Arm (106) for second guest");

            yield return WaitUntil(
                () => ProcedureIsland.EnterCount >= enterAfterLeave + 1
                    && IsIslandSceneLoaded(),
                IslandEnterTimeoutSec,
                "second ProcedureIsland + Island scene");

            yield return WaitUntil(
                () => GameEntry.World != null && GameEntry.World.IsNearReady
                    && !BootLoadingService.IsVisible,
                IslandEnterTimeoutSec,
                "second island uncovered");

            Assert.IsTrue(GuestSession.IsLoggedIn);
            Assert.AreEqual(userId2, GuestSession.UserId);
            Assert.AreEqual(token2, GuestSession.UserToken);
            Assert.AreNotEqual(token1, GuestSession.UserToken);
            Assert.IsFalse(GuestSession.SaveLocal);
            Assert.GreaterOrEqual(ProcedureIsland.EnterCount, enterBaseline + 2);
            Assert.IsInstanceOf<ProcedureIsland>(GameEntry.Procedure.CurrentProcedure);
            BoluoSession.LoadFromPrefs();
            Assert.AreEqual(deviceToken, BoluoSession.DeviceToken);

            yield return new WaitForSecondsRealtime(StayOnIslandSec);
        }

        static IEnumerator ReloadLaunchScene()
        {
            AsyncOperation op = SceneManager.LoadSceneAsync(SceneAssets.Launch, LoadSceneMode.Single);
            Assert.IsNotNull(op, "LoadSceneAsync(Launch) returned null — is Launch in EditorBuildSettings?");
            while (op != null && !op.isDone)
                yield return null;
            // Domain 不重载时静态门闩可能残留；场景起来后再清一次。
            HostIslandEntryGate.Reset();
            EditorNativeHost.ResetRuntime();
            EditorLoginGate.ResetSessionFlags();
        }

        static bool IsIslandSceneLoaded()
        {
            if (GameEntry.Scene != null && GameEntry.Scene.SceneIsLoaded(SceneAssets.Island))
                return true;
            Scene byName = SceneManager.GetSceneByName("Island");
            if (byName.IsValid() && byName.isLoaded)
                return true;
            Scene byPath = SceneManager.GetSceneByPath(SceneAssets.Island);
            return byPath.IsValid() && byPath.isLoaded;
        }

        static IEnumerator WaitUntil(Func<bool> pred, float timeoutSec, string what)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!pred())
            {
                if (Time.realtimeSinceStartup - t0 > timeoutSec)
                {
                    Assert.Fail(
                        $"Timeout after {timeoutSec:0}s waiting for {what}. "
                        + $"proc={ProcName()} guestLogin={ProcedureGuestLogin.IsCurrent} "
                        + $"armed={HostIslandEntryGate.IsArmed} gen={HostIslandEntryGate.Generation} "
                        + $"enter={ProcedureIsland.EnterCount} loggedIn={GuestSession.IsLoggedIn} "
                        + $"guest={GuestSession.IsGuest} userId={GuestSession.UserId} "
                        + $"islandScene={IsIslandSceneLoaded()} "
                        + $"worldNear={(GameEntry.World != null && GameEntry.World.IsNearReady)} "
                        + $"cover={BootLoadingService.IsVisible} tip={BootLoadingService.Tip}");
                }

                yield return null;
            }
        }

        static string ProcName()
        {
            var p = GameEntry.Procedure?.CurrentProcedure;
            return p == null ? "null" : p.GetType().Name;
        }

        static void ResetSessionRuntime()
        {
            GuestSession.Clear();
            BoluoSession.ClearDeviceToken();
            HostIslandEntryGate.Reset();
            EditorNativeHost.ResetRuntime();
            EditorNativeHost.ClearShellSession();
            EditorLoginGate.ResetSessionFlags();
        }

        void BackupPrefs()
        {
            _stringBackup = new Dictionary<string, string>();
            _intBackup = new Dictionary<string, int>();
            foreach (string key in PrefKeys)
            {
                if (!PlayerPrefs.HasKey(key))
                    continue;
                if (key.EndsWith("IsGuest") || key.EndsWith("SaveLocal") || key == "Game.IsGuest")
                    _intBackup[key] = PlayerPrefs.GetInt(key, 0);
                else
                    _stringBackup[key] = PlayerPrefs.GetString(key, "");
            }
        }

        void RestorePrefs()
        {
            foreach (string key in PrefKeys)
                PlayerPrefs.DeleteKey(key);
            if (_stringBackup != null)
            {
                foreach (var kv in _stringBackup)
                    PlayerPrefs.SetString(kv.Key, kv.Value);
            }

            if (_intBackup != null)
            {
                foreach (var kv in _intBackup)
                    PlayerPrefs.SetInt(kv.Key, kv.Value);
            }

            PlayerPrefs.Save();
            BoluoSession.LoadFromPrefs();
        }
    }
}
#endif
