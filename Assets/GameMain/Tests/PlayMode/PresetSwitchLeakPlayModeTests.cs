#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Game.BoluoApi;
using Game.Native;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Tests
{
    /// <summary>
    /// 进岛后按界面预设连续切换。每一圈都等虹膜结束、流送安静、再卸掉没人引用的资源，然后采样。
    /// 第一圈只负责把各地点的工作集装进来，后面几圈相对这一圈不得再爬。
    /// 不进 Run All：在 Test Runner 里点这一条，或按 Category「PresetLeak」跑。
    /// </summary>
    public sealed class PresetSwitchLeakPlayModeTests
    {
        const float BootTimeoutSec = 120f;
        const float IslandEnterTimeoutSec = 240f;
        const float SwitchTimeoutSec = 30f;
        const float SettleTimeoutSec = 25f;
        const float QuietHoldSec = 2f;

        const int WarmupRounds = 1;
        const int MeasureRounds = 3;

        // 热身圈之后允许的增长。reserved 只打日志：Unity 堆只扩不还。
        const long UnityGrowthBudget = 96L * 1024 * 1024;
        const long MonoGrowthBudget = 24L * 1024 * 1024;
        const long GfxGrowthBudget = 64L * 1024 * 1024;

        // 热身之后每一圈都还在涨，并且单圈超过这个台阶，就当泄漏。
        const long UnityClimbStep = 8L * 1024 * 1024;
        const long MonoClimbStep = 4L * 1024 * 1024;

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
        bool _leakProbeBackup;

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            BackupPrefs();
            _hadRemoteGuestBackup = true;
            _remoteGuestBackup = EditorPrefs.GetBool(GuestLoginClient.EditorTryRemoteKey, false);
            EditorPrefs.SetBool(GuestLoginClient.EditorTryRemoteKey, false);

            _leakProbeBackup = IslandLeakProbe.Enabled;
            IslandLeakProbe.Enabled = false;

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
            IslandLeakProbe.Enabled = _leakProbeBackup;
            yield return null;
        }

        /// <summary>
        /// 游客票进岛，然后在 UI 预设之间连切。热身一圈，再量三圈。
        /// </summary>
        [UnityTest]
        [Explicit("多分钟浸泡，不进 Run All")]
        [NUnit.Framework.Timeout(900000)]
        [Category("PresetLeak")]
        public IEnumerator SwitchPresets_Repeats_MemoryStaysBounded()
        {
            Time.timeScale = 1f;
            yield return EnterIsland();

            int[] ids = CollectUiPresetIds();
            var samples = new List<MemSample>(2 + WarmupRounds + MeasureRounds);
            yield return SettleAndSample("enter", samples);

            int rounds = WarmupRounds + MeasureRounds;
            for (int round = 0; round < rounds; round++)
            {
                yield return CycleOnce(ids, round, rounds);
                string label = round < WarmupRounds
                    ? "warmup"
                    : "lap" + (round - WarmupRounds + 1);
                yield return SettleAndSample(label, samples);
            }

            AssertBounded(samples);
        }

        static IEnumerator EnterIsland()
        {
            const string deviceToken = "pm_preset_leak_device";
            const string token = "pm_preset_leak_jwt";
            const string userId = "pm_preset_leak_user";
            uint deviceExpire = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400u;
            int enterBaseline = ProcedureIsland.EnterCount;

            EditorNativeHost.CompleteGuestLogin(deviceToken, deviceExpire, token, userId);

            yield return ReloadLaunchScene();
            yield return WaitUntil(
                () => GameEntry.Procedure != null && GameEntry.Base != null,
                BootTimeoutSec,
                "GameEntry.Procedure / Base");

            yield return WaitUntil(
                () => HostIslandEntryGate.IsArmed,
                BootTimeoutSec,
                "HostIslandEntryGate Arm (106)");

            yield return WaitUntil(
                () => ProcedureIsland.EnterCount >= enterBaseline + 1
                    && IsIslandSceneLoaded(),
                IslandEnterTimeoutSec,
                "ProcedureIsland + Island scene");

            yield return WaitUntil(
                () => GameEntry.World != null && GameEntry.World.IsNearReady
                    && !BootLoadingService.IsVisible
                    && IslandMainForm.Current != null
                    && GameEntry.Presets != null
                    && GameEntry.Presets.GetUiCount() >= 2
                    && !GameEntry.Presets.IsTransitioning
                    && !IslandMainForm.IsPresetTransitionBusy,
                IslandEnterTimeoutSec,
                "island uncovered, preset list ready");
        }

        static int[] CollectUiPresetIds()
        {
            PresetService presets = GameEntry.Presets;
            Assert.IsNotNull(presets, "PresetService missing");
            int n = presets.GetUiCount();
            Assert.GreaterOrEqual(n, 2, "need at least 2 UI presets");

            var ids = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                IslandPresetAsset preset = presets.GetByIndex(i);
                if (preset != null && preset.Id > 0)
                    ids.Add(preset.Id);
            }

            Assert.GreaterOrEqual(ids.Count, 2, "UI preset ids");
            return ids.ToArray();
        }

        static IEnumerator CycleOnce(int[] ids, int round, int rounds)
        {
            int origin = 0;
            int current = GameEntry.Presets != null ? GameEntry.Presets.CurrentId : 0;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == current)
                {
                    origin = i;
                    break;
                }
            }

            for (int step = 0; step < ids.Length; step++)
            {
                int id = ids[(origin + 1 + step) % ids.Length];
                Report($"[PresetLeak] round {round + 1}/{rounds} -> preset {id}");
                yield return SwitchTo(id);
            }
        }

        static IEnumerator SwitchTo(int id)
        {
            Assert.IsNotNull(GameEntry.Presets, "PresetService missing before switch");
            yield return WaitUntil(
                () => !IslandMainForm.IsPresetTransitionBusy
                    && GameEntry.Presets != null
                    && !GameEntry.Presets.IsTransitioning,
                SwitchTimeoutSec,
                "idle before preset " + id);

            GameEntry.Presets.RequestTransition(id);
            yield return WaitUntil(
                () => GameEntry.Presets != null
                    && GameEntry.Presets.CurrentId == id
                    && !GameEntry.Presets.IsTransitioning
                    && !IslandMainForm.IsPresetTransitionBusy,
                SwitchTimeoutSec,
                "preset " + id + " revealed");
        }

        static IEnumerator SettleAndSample(string label, List<MemSample> samples)
        {
            float deadline = Time.realtimeSinceStartup + SettleTimeoutSec;
            float quietSince = -1f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (IsQuiet())
                {
                    if (quietSince < 0f)
                        quietSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - quietSince >= QuietHoldSec)
                        break;
                }
                else
                {
                    quietSince = -1f;
                }

                yield return null;
            }

            if (quietSince < 0f || Time.realtimeSinceStartup - quietSince < QuietHoldSec)
            {
                Report("[PresetLeak] streamer did not stay quiet; sampling anyway (" + label + ")");
            }

            AsyncOperation unload = Resources.UnloadUnusedAssets();
            if (unload != null)
            {
                while (!unload.isDone)
                    yield return null;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            yield return null;
            yield return null;

            MemSample sample = ReadSample(label);
            samples.Add(sample);
            Report(FormatSample(sample));
        }

        static bool IsQuiet()
        {
            if (IslandMainForm.IsPresetTransitionBusy)
                return false;
            if (GameEntry.Presets != null && GameEntry.Presets.IsTransitioning)
                return false;
            EnvItemStreamerLite streamer = EnvItemStreamerLite.Current;
            return streamer == null || !streamer.HasPendingSpawn;
        }

        static void AssertBounded(List<MemSample> samples)
        {
            int baseline = -1;
            int firstLap = -1;
            int last = -1;
            for (int i = 0; i < samples.Count; i++)
            {
                string label = samples[i].Label;
                if (label == "warmup")
                    baseline = i;
                else if (label.StartsWith("lap", StringComparison.Ordinal))
                {
                    if (firstLap < 0)
                        firstLap = i;
                    last = i;
                }
            }

            var report = new StringBuilder(512);
            for (int i = 0; i < samples.Count; i++)
            {
                if (i > 0)
                    report.Append('\n');
                report.Append(FormatSample(samples[i]));
            }

            Report("[PresetLeak] series\n" + report);
            Assert.GreaterOrEqual(baseline, 0, "missing warmup sample\n" + report);
            Assert.GreaterOrEqual(last, 0, "missing measure sample\n" + report);

            MemSample from = samples[baseline];
            MemSample to = samples[last];
            long unity = to.UnityAlloc - from.UnityAlloc;
            long mono = to.MonoUsed - from.MonoUsed;
            long gfx = to.Gfx - from.Gfx;
            string delta =
                "delta unity=" + SignedMb(unity)
                + " mono=" + SignedMb(mono)
                + " gfx=" + SignedMb(gfx)
                + " (budget unity=" + Mb(UnityGrowthBudget)
                + " mono=" + Mb(MonoGrowthBudget)
                + " gfx=" + Mb(GfxGrowthBudget) + ")";
            Report("[PresetLeak] " + delta);

            var failures = new List<string>(4);
            if (unity > UnityGrowthBudget)
                failures.Add("unity allocated grew " + SignedMb(unity));
            if (mono > MonoGrowthBudget)
                failures.Add("mono used grew " + SignedMb(mono));
            if (gfx > GfxGrowthBudget)
                failures.Add("gfx grew " + SignedMb(gfx));

            if (firstLap >= 0 && last > firstLap && Climbing(samples, firstLap, last, s => s.UnityAlloc, UnityClimbStep))
                failures.Add("unity allocated climbed every lap after warmup");
            if (firstLap >= 0 && last > firstLap && Climbing(samples, firstLap, last, s => s.MonoUsed, MonoClimbStep))
                failures.Add("mono used climbed every lap after warmup");

            if (failures.Count > 0)
            {
                Assert.Fail(
                    "preset switch retained memory\n"
                    + string.Join("\n", failures)
                    + "\n" + delta
                    + "\n" + report);
            }
        }

        static bool Climbing(
            List<MemSample> samples, int from, int to, Func<MemSample, long> pick, long step)
        {
            for (int i = from + 1; i <= to; i++)
            {
                if (pick(samples[i]) - pick(samples[i - 1]) <= step)
                    return false;
            }

            return true;
        }

        static MemSample ReadSample(string label)
        {
            long gfx = 0;
            try
            {
                gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
            }
            catch (Exception)
            {
                gfx = 0;
            }

            EnvItemStreamerLite streamer = EnvItemStreamerLite.Current;
            return new MemSample
            {
                Label = label,
                UnityAlloc = Profiler.GetTotalAllocatedMemoryLong(),
                MonoUsed = Profiler.GetMonoUsedSizeLong(),
                Gfx = gfx,
                Reserved = Profiler.GetTotalReservedMemoryLong(),
                Spawned = streamer != null ? streamer.SpawnedCount : -1,
            };
        }

        static string FormatSample(MemSample sample)
        {
            return "[PresetLeak] " + sample.Label
                + " unity=" + Mb(sample.UnityAlloc)
                + " mono=" + Mb(sample.MonoUsed)
                + " gfx=" + Mb(sample.Gfx)
                + " reserved=" + Mb(sample.Reserved)
                + " spawned=" + sample.Spawned;
        }

        static string Mb(long bytes) => (bytes / (1024f * 1024f)).ToString("0.0") + "MB";

        static string SignedMb(long bytes)
        {
            string text = Mb(Math.Abs(bytes));
            if (bytes > 0)
                return "+" + text;
            if (bytes < 0)
                return "-" + text;
            return text;
        }

        static void Report(string line)
        {
            Debug.Log(line);
            TestContext.WriteLine(line);
        }

        struct MemSample
        {
            public string Label;
            public long UnityAlloc;
            public long MonoUsed;
            public long Gfx;
            public long Reserved;
            public int Spawned;
        }

        static IEnumerator ReloadLaunchScene()
        {
            AsyncOperation op = SceneManager.LoadSceneAsync(SceneAssets.Launch, LoadSceneMode.Single);
            Assert.IsNotNull(op, "LoadSceneAsync(Launch) returned null — is Launch in EditorBuildSettings?");
            while (op != null && !op.isDone)
                yield return null;
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
                        + $"islandScene={IsIslandSceneLoaded()} "
                        + $"worldNear={(GameEntry.World != null && GameEntry.World.IsNearReady)} "
                        + $"cover={BootLoadingService.IsVisible} "
                        + $"preset={(GameEntry.Presets != null ? GameEntry.Presets.CurrentId : 0)} "
                        + $"busy={IslandMainForm.IsPresetTransitionBusy}");
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
