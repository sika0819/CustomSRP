using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;
using Debug = UnityEngine.Debug;

namespace Game.Editor
{
    /// <summary>
    /// MCP / 菜单 / Temp flag：打 Yoo+APK 或仅 APK，并 adb 装到真机。
    /// 
    /// 注意：推荐使用 Window/Game/Build Tool 统一打包窗口。
    /// 本类保留以兼容 CI/CD 流程。
    /// 
    /// Flag：Temp/mapitem_deploy_device.flag（全量 Yoo+APK）；
    /// Temp/mapitem_apk_install.flag（仅 APK，代码热修用）。
    /// </summary>
    public static class MapItemDeviceDeploy
    {
        public const string FlagRelPath = "Temp/mapitem_deploy_device.flag";
        public const string ApkOnlyFlagRelPath = "Temp/mapitem_apk_install.flag";
        public const string ForceExitPlayFlagRelPath = "Temp/force_exit_play.flag";
        const string PackageName = "com.customsrp.oilempty";
        static bool _deployQueued;
        static bool _waitingExitPlayMode;
        static double _waitPlayStarted;

        [InitializeOnLoadMethod]
        static void WatchFlag()
        {
            EditorApplication.delayCall += TryConsumeFlag;
            EditorApplication.update -= PollFlag;
            EditorApplication.update += PollFlag;
        }

        static double _nextPollTime;

        static void PollFlag()
        {
            if (EditorApplication.timeSinceStartup < _nextPollTime)
                return;
            _nextPollTime = EditorApplication.timeSinceStartup + 1.5;
            TryConsumeFlag();
        }

        static void TryConsumeFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string forceExit = Path.Combine(root, ForceExitPlayFlagRelPath);
            if (File.Exists(forceExit))
            {
                try { File.Delete(forceExit); } catch { /* ignore */ }
                _waitingExitPlayMode = false;
                _waitPlayStarted = 0;
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    Log.Warning("[MapItemDeploy] Force exit Play Mode");
                    EditorApplication.isPlaying = false;
                }
            }

            // Stuck wait (domain reload / missed EnteredEditMode) — unblock after ~20s.
            if (_waitingExitPlayMode
                && _waitPlayStarted > 0
                && EditorApplication.timeSinceStartup - _waitPlayStarted > 20.0)
            {
                Log.Warning("[MapItemDeploy] Play-exit wait timed out — retry deploy");
                EditorApplication.playModeStateChanged -= OnPlayModeChangedForDeploy;
                _waitingExitPlayMode = false;
                _waitPlayStarted = 0;
                System.Action pending = _pendingAfterPlay;
                _pendingAfterPlay = null;
                if (pending != null && !EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorApplication.delayCall += () => pending();
            }

            if (_deployQueued || _waitingExitPlayMode)
                return;

            string apkOnly = Path.Combine(root, ApkOnlyFlagRelPath);
            if (File.Exists(apkOnly))
            {
                try { File.Delete(apkOnly); } catch { /* ignore */ }
                Log.Info("[MapItemDeploy] APK-only flag — build APK + adb install");
                DeployApkOnly();
                return;
            }

            string flag = Path.Combine(root, FlagRelPath);
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }
            Log.Info("[MapItemDeploy] Flag detected — starting Yoo + Android + adb install");
            Deploy();
        }

        [MenuItem("Game/Build Android Island + Install Device (MapItem)", false, 301)]
        public static void Deploy()
        {
            EnsureEditModeThen(() => RunDeploy(apkOnly: false));
        }

        [MenuItem("Game/Build Android APK + Install Device (code only)", false, 302)]
        public static void DeployApkOnly()
        {
            EnsureEditModeThen(() => RunDeploy(apkOnly: true));
        }

        static void EnsureEditModeThen(System.Action next)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (_waitingExitPlayMode)
                    return;
                _waitingExitPlayMode = true;
                _waitPlayStarted = EditorApplication.timeSinceStartup;
                Log.Warning("[MapItemDeploy] Exit Play Mode first — stopping Play, then rebuild…");
                EditorApplication.playModeStateChanged -= OnPlayModeChangedForDeploy;
                _pendingAfterPlay = next;
                EditorApplication.playModeStateChanged += OnPlayModeChangedForDeploy;
                EditorApplication.isPlaying = false;
                return;
            }

            next();
        }

        static System.Action _pendingAfterPlay;

        static void OnPlayModeChangedForDeploy(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode)
                return;
            EditorApplication.playModeStateChanged -= OnPlayModeChangedForDeploy;
            _waitingExitPlayMode = false;
            System.Action pending = _pendingAfterPlay;
            _pendingAfterPlay = null;
            if (pending != null)
                EditorApplication.delayCall += () => pending();
        }

        static void RunDeploy(bool apkOnly)
        {
            if (_deployQueued)
                return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (apkOnly)
                    DeployApkOnly();
                else
                    Deploy();
                return;
            }

            _deployQueued = true;
            try
            {
                if (!apkOnly)
                {
                    // 0) Rivers/waterfall aggregate prefab + collector
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(SceneAssets.IslandInlandWaterPrefabPath) == null)
                        IslandInlandWaterBuilder.Build();
                    MapItemEditorMenu.EnsureFoliageClipAndShadows();
                    YooAssetCollectorSetup.SetupAll();

                    // 1) Yoo package (Builtin copies into StreamingAssets) — OilNPR 变体变更须重打
                    if (!YooAssetBuildMenu.BuildPackageBuiltin())
                    {
                        Log.Error(
                            "[MapItemDeploy] YooAsset build failed — abort APK (StreamingAssets would stay stale).");
                        return;
                    }

                    if (!VerifyMapItemInStreamingPackage())
                    {
                        Log.Error(
                            "[MapItemDeploy] StreamingAssets package missing AllMapItem / DaoYu1 — abort APK. " +
                            "Check Collector DaoYu1MapItem + Configs contain AllMapItem.bytes.");
                        return;
                    }
                }
                else if (!VerifyMapItemInStreamingPackage())
                {
                    Log.Error(
                        "[MapItemDeploy] APK-only aborted — StreamingAssets still missing MapItem. Run full Deploy.");
                    return;
                }

                // 2) APK
                BuildAndroidIslandRelease.Build();

                string apk = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", BuildAndroidIslandRelease.ApkPath));
                if (!File.Exists(apk))
                {
                    Log.Error("[MapItemDeploy] APK missing: " + apk);
                    return;
                }

                // 3) adb install + launch (Unity 6 uses UnityPlayerGameActivity)
                int code = Run("adb", $"install -r \"{apk}\"");
                if (code != 0)
                {
                    Log.Error("[MapItemDeploy] adb install failed code=" + code);
                    return;
                }

                Run("adb", $"shell am start -n {PackageName}/com.unity3d.player.UnityPlayerGameActivity");
                Log.Info("[MapItemDeploy] Installed and launched on device. Watch FPS / logcat Unity.");
            }
            finally
            {
                _deployQueued = false;
            }
        }

        /// <summary>Ensure rebuilt builtin package actually contains MapItem data + at least one DaoYu1 prefab.</summary>
        static bool VerifyMapItemInStreamingPackage()
        {
            string root = Path.GetFullPath(
                Path.Combine(Application.dataPath, "StreamingAssets/package/DefaultPackage"));
            if (!Directory.Exists(root))
            {
                Log.Error("[MapItemDeploy] Missing " + root);
                return false;
            }

            bool hasAllMapItem = false;
            bool hasMapItemExtras = false;
            bool hasDaoYu = false;
            bool hasInlandWater = false;
            bool hasIslandFx = false;
            foreach (string path in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(path);
                if (text.Contains("AllMapItem"))
                    hasAllMapItem = true;
                if (text.Contains("MapItemExtras"))
                    hasMapItemExtras = true;
                if (text.Contains("Lvshu_") || text.Contains("Songshu_") || text.Contains("DaoYu1")
                    || text.Contains("Lvshu/") || text.Contains("Prefabs/DaoYu1"))
                    hasDaoYu = true;
                if (text.Contains("IslandInlandWater") || text.Contains("Common_River_")
                    || text.Contains("texiao_Waterfall"))
                    hasInlandWater = true;
                if (text.Contains("FX_SunBeam") || text.Contains("FX_Fireflies")
                    || text.Contains("Other_Huodui") || text.Contains("FX_Butterflies"))
                    hasIslandFx = true;
            }

            foreach (string path in Directory.GetFiles(root, "*.report", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(path);
                if (text.Contains("AllMapItem"))
                    hasAllMapItem = true;
                if (text.Contains("MapItemExtras"))
                    hasMapItemExtras = true;
                if (text.Contains("Lvshu_") || text.Contains("Prefabs/DaoYu1") || text.Contains("Lvshu/"))
                    hasDaoYu = true;
                if (text.Contains("IslandInlandWater") || text.Contains("Common_River_")
                    || text.Contains("texiao_Waterfall"))
                    hasInlandWater = true;
                if (text.Contains("FX_SunBeam") || text.Contains("FX_Fireflies")
                    || text.Contains("Other_Huodui") || text.Contains("FX_Butterflies"))
                    hasIslandFx = true;
            }

            // Bundle name fallback when json not present as text asset in StreamingAssets
            foreach (string path in Directory.GetFiles(root, "*.bundle", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path).ToLowerInvariant();
                if (name.Contains("daoyu1"))
                    hasDaoYu = true;
                if (name.Contains("configs") && new FileInfo(path).Length > 50_000)
                    hasAllMapItem = true; // AllMapItem.bytes ~148KB
                if (name.Contains("island") && new FileInfo(path).Length > 1_000)
                    hasInlandWater = true; // IslandInlandWater rides Island group
                if (name.Contains("fx_sun") || name.Contains("fx_fire") || name.Contains("islandfx"))
                    hasIslandFx = true;
            }

            // Manifest hash/bytes may list addresses even when catalog json is compact
            foreach (string path in Directory.GetFiles(root, "DefaultPackage_*.bytes", SearchOption.TopDirectoryOnly))
            {
                byte[] raw = File.ReadAllBytes(path);
                string asText = System.Text.Encoding.UTF8.GetString(raw);
                if (asText.Contains("AllMapItem"))
                    hasAllMapItem = true;
                if (asText.Contains("MapItemExtras"))
                    hasMapItemExtras = true;
                if (asText.Contains("Lvshu_") || asText.Contains("DaoYu1") || asText.Contains("Songshu_"))
                    hasDaoYu = true;
                if (asText.Contains("IslandInlandWater") || asText.Contains("Common_River_")
                    || asText.Contains("texiao_Waterfall"))
                    hasInlandWater = true;
                if (asText.Contains("FX_SunBeam") || asText.Contains("FX_Fireflies")
                    || asText.Contains("Other_Huodui") || asText.Contains("FX_Butterflies"))
                    hasIslandFx = true;
            }

            Log.Info(
                $"[MapItemDeploy] Verify package AllMapItem={hasAllMapItem} MapItemExtras={hasMapItemExtras} " +
                $"DaoYu1={hasDaoYu} InlandWater={hasInlandWater} IslandFX={hasIslandFx} root={root}");
            if (!hasInlandWater)
                Log.Warning(
                    "[MapItemDeploy] IslandInlandWater / River_* not found in package text — " +
                    "runtime may still spawn via Common_River_* piece fallback.");
            if (!hasIslandFx)
                Log.Warning(
                    "[MapItemDeploy] FX_SunBeam / Fireflies / Huodui not found in package text — " +
                    "re-run Game/YooAsset/Setup then full Deploy for MapItem extras.");
            if (!hasMapItemExtras)
                Log.Warning(
                    "[MapItemDeploy] MapItemExtras not found in package — campfire/FX/inland pieces need Configs rebuild.");
            return hasAllMapItem && hasDaoYu;
        }

        static int Run(string file, string args)
        {
            string exe = ResolveExecutable(file);
            if (string.IsNullOrEmpty(exe))
            {
                Log.Error(
                    "[MapItemDeploy] Cannot find '" + file + "' on PATH. " +
                    "Install platform-tools or symlink adb into /opt/homebrew/bin.");
                return -1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process p = Process.Start(psi);
            if (p == null)
                return -1;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (!string.IsNullOrEmpty(stdout))
                Log.Info("[MapItemDeploy] " + file + " out:\n" + stdout);
            if (!string.IsNullOrEmpty(stderr))
                Log.Warning("[MapItemDeploy] " + file + " err:\n" + stderr);
            return p.ExitCode;
        }

        /// <summary>
        /// Unity Editor on macOS often has a minimal PATH (no Homebrew).
        /// Resolve common locations before Process.Start.
        /// </summary>
        static string ResolveExecutable(string file)
        {
            if (string.IsNullOrEmpty(file))
                return null;
            if (Path.IsPathRooted(file) && File.Exists(file))
                return file;

            string pathEnv = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir))
                    continue;
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate))
                    return candidate;
            }

            string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string[] fallbacks =
            {
                "/opt/homebrew/bin/" + file,
                "/usr/local/bin/" + file,
                Path.Combine(home, "Library/Android/sdk/platform-tools", file),
                Path.Combine(home, "Android/Sdk/platform-tools", file),
            };
            for (int i = 0; i < fallbacks.Length; i++)
            {
                if (File.Exists(fallbacks[i]))
                    return fallbacks[i];
            }

            return null;
        }
    }
}
