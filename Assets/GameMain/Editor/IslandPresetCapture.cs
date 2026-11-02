#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;
using Debug = UnityEngine.Debug;

namespace Game.Editor
{
    /// <summary>
    /// Play 里依次切岛屿预设 1–10，主相机无 UI 截图；也可录 Loading 横/竖屏视频。
    /// 通过关掉 UIDocument.enabled（非仅 opacity），避免 CustomSRP DrawUIOverlay 把 HUD/FPS 画进 RT。
    /// 编辑器截图期间油画开到最大（强度 1、笔触半径 1、满混合、16-tap），截完恢复；renderScale 1。
    /// 菜单：GameMain/Boluo/截图/…；也可丢 Temp/capture_preset_4k、Temp/capture_preset_cover、
    /// Temp/capture_loading_landscape、Temp/capture_loading_portrait（横/竖屏分开录）。
    /// </summary>
    [InitializeOnLoad]
    public static class IslandPresetCapture
    {
        const string RequestFile4K = "Temp/capture_preset_4k";
        const string RequestFileCover = "Temp/capture_preset_cover";
        const string RequestFileLoadingLandscape = "Temp/capture_loading_landscape";
        const string RequestFileLoadingPortrait = "Temp/capture_loading_portrait";
        const string SessionKey = "CustomSRP.PresetCapture.Pending";
        const string SessionPhase = "CustomSRP.PresetCapture.Phase";
        const string SessionMode = "CustomSRP.PresetCapture.Mode";

        const int Mode4K = 0;
        const int ModeCover = 1;
        const int ModeLoadingLandscape = 2;
        const int ModeLoadingPortrait = 3;

        const int Width4K = 3840;
        const int Height4K = 2160;
        /// <summary>对齐 UI/Textures/PresetItem/zhanweitu*.png。</summary>
        const int CoverSize = 264;
        /// <summary>占位图圆角半径（264 上约 46px）。</summary>
        const float CoverCornerRadius = 46f;
        /// <summary>先以接近 OilFilter 参考短边渲，再缩小到 CoverSize。</summary>
        const int CoverRenderSize = 1056;

        /// <summary>对齐 Assets/GameMain/UI/Video/loading.mp4。</summary>
        const int VideoPortraitW = 1080;
        const int VideoPortraitH = 2338;
        /// <summary>
        /// 创维闺蜜机标准版面板 1920×1080（16:9，SKS30 / L701）。
        /// 横 FOV 锁 90° 时竖向视野与预设 4K 截图相同，篝火不会被超宽画幅压到画面底部。
        /// </summary>
        const int VideoLandscapeW = 1920;
        const int VideoLandscapeH = 1080;
        const int VideoFps = 30;
        const float VideoDurationSec = 6.7f;
        const int VideoJpgQuality = 92;
        const int VideoBitrateK = 4800;
        /// <summary>委屈泛滥（篝火夜雨），Loading 参考机位。</summary>
        const int LoadingVideoSchemeId = 4;

        const float LandscapeHFovDeg = 90f;
        /// <summary>竖屏作者竖向视野，对齐 Launch 相机与 <see cref="IslandJoystickCamera"/>。</summary>
        const float PortraitAuthoredVFovDeg = 60f;
        /// <summary>窄于 1080:1920 时按这个横纵比撑满横向视野。</summary>
        const float PortraitRefAspect = 1080f / 1920f;
        const int FirstScheme = 1;
        const int LastScheme = 10;
        const float SettleSeconds = 3.0f;
        const float WorldReadyTimeout = 120f;
        const float ExtrasSettledTimeout = 90f;

        static readonly string[] FallbackNames =
        {
            "清晨起床", "独自旅行", "午后放松", "委屈泛滥", "专注工作",
            "放空发呆", "灵感唤醒", "假日躺平", "学习冲刺", "准备入睡",
        };

        static bool _busy;
        static bool _cancel;
        static double _phaseStart;

        static string OutDir4K =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../.tmp/preset_4k"));

        static string OutDirCover =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../.tmp/preset_cover"));

        static string OutDirLoadingVideo =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../.tmp/loading_video"));

        static string AssetVideoDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "GameMain/UI/Video"));

        static IslandPresetCapture()
        {
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                _cancel = true;
            if (!SessionState.GetBool(SessionKey, false))
                return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                SessionState.SetInt(SessionPhase, 2);
                _phaseStart = EditorApplication.timeSinceStartup;
            }
        }

        static bool IsLoadingVideoMode(int mode) =>
            mode == ModeLoadingLandscape || mode == ModeLoadingPortrait;

        static void Poll()
        {
            if (!_busy)
            {
                if (File.Exists(RequestFileLoadingLandscape))
                {
                    try { File.Delete(RequestFileLoadingLandscape); }
                    catch { return; }
                    Begin(ModeLoadingLandscape);
                }
                else if (File.Exists(RequestFileLoadingPortrait))
                {
                    try { File.Delete(RequestFileLoadingPortrait); }
                    catch { return; }
                    Begin(ModeLoadingPortrait);
                }
                else if (File.Exists(RequestFileCover))
                {
                    try { File.Delete(RequestFileCover); }
                    catch { return; }
                    Begin(ModeCover);
                }
                else if (File.Exists(RequestFile4K))
                {
                    try { File.Delete(RequestFile4K); }
                    catch { return; }
                    Begin(Mode4K);
                }
            }

            if (!SessionState.GetBool(SessionKey, false) || _busy)
                return;

            int phase = SessionState.GetInt(SessionPhase, 0);
            if (phase == 1 && !EditorApplication.isPlaying)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return;
                EditorApplication.isPlaying = true;
                return;
            }

            if (phase == 2 && EditorApplication.isPlaying)
            {
                if (EditorApplication.timeSinceStartup - _phaseStart < 1.0)
                    return;
                int mode = SessionState.GetInt(SessionMode, Mode4K);
                if (IsLoadingVideoMode(mode))
                    _ = RunLoadingVideoAsync(mode);
                else
                    _ = RunAsync();
            }
        }

        [MenuItem("GameMain/Boluo/截图/各预设 4K 无 UI")]
        public static void Run4KFromMenu() => Begin(Mode4K);

        [MenuItem("GameMain/Boluo/截图/各预设占位图封面（264 圆角）")]
        public static void RunCoverFromMenu() => Begin(ModeCover);

        [MenuItem("GameMain/Boluo/截图/委屈泛滥 Loading 横屏视频（无 UI）")]
        public static void RunLoadingLandscapeFromMenu() => Begin(ModeLoadingLandscape);

        [MenuItem("GameMain/Boluo/截图/委屈泛滥 Loading 竖屏视频（无 UI）")]
        public static void RunLoadingPortraitFromMenu() => Begin(ModeLoadingPortrait);

        [MenuItem("GameMain/Boluo/截图/打开 4K 输出目录")]
        public static void RevealOutDir4K()
        {
            Directory.CreateDirectory(OutDir4K);
            EditorUtility.RevealInFinder(OutDir4K);
        }

        [MenuItem("GameMain/Boluo/截图/打开封面输出目录")]
        public static void RevealOutDirCover()
        {
            Directory.CreateDirectory(OutDirCover);
            EditorUtility.RevealInFinder(OutDirCover);
        }

        [MenuItem("GameMain/Boluo/截图/打开 Loading 视频输出目录")]
        public static void RevealOutDirLoadingVideo()
        {
            Directory.CreateDirectory(OutDirLoadingVideo);
            EditorUtility.RevealInFinder(OutDirLoadingVideo);
        }

        static void Begin(int mode)
        {
            if (_busy)
            {
                Debug.LogWarning("[PresetCapture] 正在截图/录制，等这轮结束");
                return;
            }

            string outDir = mode == ModeCover
                ? OutDirCover
                : IsLoadingVideoMode(mode)
                    ? OutDirLoadingVideo
                    : OutDir4K;
            Directory.CreateDirectory(outDir);
            _cancel = false;
            SessionState.SetBool(SessionKey, true);
            SessionState.SetInt(SessionMode, mode);
            SessionState.SetInt(SessionPhase, EditorApplication.isPlaying ? 2 : 1);
            _phaseStart = EditorApplication.timeSinceStartup;
            string label = mode == ModeCover
                ? "占位图封面 264 圆角"
                : mode == ModeLoadingLandscape
                    ? "委屈泛滥 Loading 横屏视频"
                    : mode == ModeLoadingPortrait
                        ? "委屈泛滥 Loading 竖屏视频"
                        : "4K";
            Log.Info($"[PresetCapture] 开始 {label} → {outDir}  {(EditorApplication.isPlaying ? "已在 Play" : "将进入 Play")}");
        }

        static async Task RunLoadingVideoAsync(int mode)
        {
            if (_busy)
                return;
            _busy = true;
            bool landscape = mode == ModeLoadingLandscape;
            string outDir = OutDirLoadingVideo;
            string revealPath = null;
            string orientLabel = landscape ? "横屏" : "竖屏";
            try
            {
                if (!await WaitWorldReady())
                {
                    Debug.LogError("[PresetCapture] 世界未就绪（IslandWorldService.IsLoaded）");
                    return;
                }

                await DelaySeconds(1.5f);
                if (_cancel || !EditorApplication.isPlaying)
                    return;

                BeginOilCaptureBoost();
                var envBoost = BeginEnvCaptureBoost();
                float prevRenderScale = ForceRenderScale(1f);
                var hiddenUi = HideUi();
                try
                {
                    string name = GetSchemeName(LoadingVideoSchemeId);
                    EditorUtility.DisplayCancelableProgressBar(
                        $"Loading {orientLabel}",
                        $"切预设 {LoadingVideoSchemeId:D2} {name}",
                        0.05f);

                    if (!ApplyPreset(LoadingVideoSchemeId))
                    {
                        Debug.LogError($"[PresetCapture] 切预设失败 scheme={LoadingVideoSchemeId}");
                        return;
                    }

                    await DelaySeconds(SettleSeconds);
                    if (_cancel || !EditorApplication.isPlaying)
                        return;

                    MergeHideUi(hiddenUi, HideUi());
                    ForceRenderScale(1f);
                    EnvItemStreamerLite.ForceCatchUpForCapture();

                    if (!await WaitExtrasSettled())
                        Debug.LogWarning("[PresetCapture] ExtrasSettled 超时，继续录制");

                    EnvItemStreamerLite.ForceCatchUpForCapture();
                    await DelaySeconds(1.2f);
                    if (_cancel || !EditorApplication.isPlaying)
                        return;

                    MergeHideUi(hiddenUi, HideUi());
                    ForceRenderScale(1f);

                    int width = landscape ? VideoLandscapeW : VideoPortraitW;
                    int height = landscape ? VideoLandscapeH : VideoPortraitH;
                    string framesDir = Path.Combine(
                        outDir, "frames", landscape ? "landscape" : "portrait");
                    string mp4Name = landscape ? "loading_landscape.mp4" : "loading.mp4";
                    string pngName = landscape ? "loading_landscape.png" : "loading.png";
                    string mp4Path = Path.Combine(outDir, mp4Name);
                    string pngPath = Path.Combine(outDir, pngName);
                    Directory.CreateDirectory(outDir);

                    EditorUtility.DisplayCancelableProgressBar(
                        $"Loading {orientLabel}", $"录{orientLabel}…", 0.2f);
                    if (!await RecordClip(framesDir, width, height, orientLabel, hiddenUi))
                        return;

                    EditorUtility.DisplayCancelableProgressBar(
                        $"Loading {orientLabel}", "ffmpeg 编码…", 0.85f);
                    if (!EncodeMp4(framesDir, mp4Path))
                        return;

                    ExtractStill(mp4Path, pngPath);
                    InstallLoadingVideos(outDir, mp4Name, pngName);
                    InstallBootStill(pngPath, landscape);
                    revealPath = Path.Combine(AssetVideoDir, mp4Name);
                    Log.Info(
                        $"[PresetCapture] Loading {orientLabel} 完成 → {mp4Path} 并已写入 {AssetVideoDir}");
                }
                finally
                {
                    RestoreUi(hiddenUi);
                    RestoreRenderScale(prevRenderScale);
                    EndEnvCaptureBoost(envBoost);
                    EndOilCaptureBoost();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                SessionState.SetBool(SessionKey, false);
                SessionState.SetInt(SessionPhase, 0);
                _busy = false;
            }

            if (!string.IsNullOrEmpty(revealPath) && File.Exists(revealPath))
                EditorUtility.RevealInFinder(revealPath);
        }

        /// <summary>
        /// Play 里跑协程：等真实帧进 RT，禁止再调 cam.Render()。
        /// 反复手动 Render 会嵌套打坏 Custom SRP Render Graph，常见表现就是录两三帧后 Game 一直黑。
        /// </summary>
        sealed class VideoCaptureHost : MonoBehaviour
        {
            public bool Done;
            public bool Ok;
        }

        static async Task<bool> RecordClip(
            string framesDir, int width, int height, string label, List<HiddenUi> hiddenUi)
        {
            if (Directory.Exists(framesDir))
                Directory.Delete(framesDir, true);
            Directory.CreateDirectory(framesDir);

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[PresetCapture] Camera.main 为空，无法录制");
                return false;
            }

            var hostGo = new GameObject("_IslandPresetVideoCaptureHost")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var host = hostGo.AddComponent<VideoCaptureHost>();
            host.StartCoroutine(RecordClipCoroutine(
                host, framesDir, width, height, label, hiddenUi, cam));

            while (!host.Done)
            {
                if (_cancel || !EditorApplication.isPlaying)
                {
                    Debug.LogWarning($"[PresetCapture] {label} 等待协程时取消/退出 Play");
                    if (host != null)
                        host.StopAllCoroutines();
                    break;
                }

                await Task.Delay(50);
            }

            bool ok = host != null && host.Ok;
            if (hostGo != null)
                UnityEngine.Object.DestroyImmediate(hostGo);
            return ok;
        }

        static IEnumerator RecordClipCoroutine(
            VideoCaptureHost host,
            string framesDir,
            int width,
            int height,
            string label,
            List<HiddenUi> hiddenUi,
            Camera cam)
        {
            int frameCount = Mathf.Max(1, Mathf.RoundToInt(VideoDurationSec * VideoFps));
            float frameDt = 1f / VideoFps;
            float prevAspect = cam.aspect;
            float prevFov = cam.fieldOfView;
            var prevTarget = cam.targetTexture;
            var disabledCams = DisableOverlayCameras(cam);

            RenderTexture rt = null;
            Texture2D tex = null;
            bool ok = false;
            try
            {
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1,
                    name = "_IslandPresetVideoCapture",
                };
                rt.Create();
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);

                EnvItemStreamerLite.ForceCatchUpForCapture();
                MergeHideUi(hiddenUi, HideUi());
                ForceRenderScale(1f);

                for (int i = 0; i < frameCount; i++)
                {
                    if (_cancel || cam == null || !EditorApplication.isPlaying)
                    {
                        Debug.LogWarning($"[PresetCapture] {label} 中断 @frame={i}/{frameCount}");
                        yield break;
                    }

                    if (i % 60 == 0)
                    {
                        float p = 0.2f + 0.6f * (i / (float)frameCount);
                        if (EditorUtility.DisplayCancelableProgressBar(
                                $"Loading {label}", $"{label} {i + 1}/{frameCount}", p))
                        {
                            _cancel = true;
                            yield break;
                        }

                        // 进度条会盖住 Game 视图；立刻清掉，避免「一直黑」。
                        EditorUtility.ClearProgressBar();
                        MergeHideUi(hiddenUi, HideUi());
                        ForceRenderScale(1f);
                    }

                    ApplyVideoFov(cam, width, height);
                    cam.targetTexture = rt;
                    // 等这一帧由播放器正常渲进 RT，不要 cam.Render()。
                    yield return new WaitForEndOfFrame();

                    if (cam == null || !EditorApplication.isPlaying)
                        yield break;

                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    tex.Apply(false, false);
                    RenderTexture.active = null;

                    cam.targetTexture = prevTarget;
                    cam.aspect = prevAspect;
                    cam.fieldOfView = prevFov;

                    string path = Path.Combine(framesDir, $"frame_{i:D4}.jpg");
                    File.WriteAllBytes(path, tex.EncodeToJPG(VideoJpgQuality));

                    // 墙钟对齐约 30fps；中间几帧让 Game 正常画回屏幕。
                    float until = Time.realtimeSinceStartup + frameDt;
                    while (Time.realtimeSinceStartup < until)
                    {
                        if (_cancel || !EditorApplication.isPlaying)
                            yield break;
                        yield return null;
                    }
                }

                Log.Info($"[PresetCapture] {label} 帧已写 {frameCount} → {framesDir}");
                ok = true;
            }
            finally
            {
                if (cam != null)
                {
                    cam.targetTexture = prevTarget;
                    cam.aspect = prevAspect;
                    cam.fieldOfView = prevFov;
                }

                RestoreOverlayCameras(disabledCams);
                RenderTexture.active = null;
                EditorUtility.ClearProgressBar();
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);

                host.Ok = ok;
                host.Done = true;
            }
        }

        static bool EncodeMp4(string framesDir, string outMp4)
        {
            string ffmpeg = ResolveFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Debug.LogError("[PresetCapture] 找不到 ffmpeg（/opt/homebrew/bin/ffmpeg 或 PATH）");
                return false;
            }

            string pattern = Path.Combine(framesDir, "frame_%04d.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(outMp4) ?? ".");
            if (File.Exists(outMp4))
                File.Delete(outMp4);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments =
                    $"-y -framerate {VideoFps} -i \"{pattern}\" " +
                    $"-c:v libx264 -pix_fmt yuv420p -b:v {VideoBitrateK}k " +
                    $"-movflags +faststart \"{outMp4}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Debug.LogError("[PresetCapture] ffmpeg 启动失败");
                    return false;
                }

                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(180000);
                if (proc.ExitCode != 0 || !File.Exists(outMp4) || new FileInfo(outMp4).Length < 1024)
                {
                    Debug.LogError($"[PresetCapture] ffmpeg 失败 exit={proc.ExitCode}\n{stderr}");
                    return false;
                }

                Log.Info($"[PresetCapture] 编码完成 {outMp4} ({new FileInfo(outMp4).Length} bytes)");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PresetCapture] ffmpeg 异常: {e.Message}");
                return false;
            }
        }

        static void ExtractStill(string mp4, string pngPath)
        {
            string ffmpeg = ResolveFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(mp4))
                return;

            try
            {
                if (File.Exists(pngPath))
                    File.Delete(pngPath);
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{mp4}\" -frames:v 1 \"{pngPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(30000);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PresetCapture] 抽静帧失败: {e.Message}");
            }
        }

        static void InstallLoadingVideos(string srcDir, params string[] names)
        {
            Directory.CreateDirectory(AssetVideoDir);
            if (names == null || names.Length == 0)
            {
                names = new[]
                {
                    "loading.mp4", "loading_landscape.mp4",
                    "loading.png", "loading_landscape.png",
                };
            }

            for (int i = 0; i < names.Length; i++)
            {
                string src = Path.Combine(srcDir, names[i]);
                string dst = Path.Combine(AssetVideoDir, names[i]);
                if (!File.Exists(src))
                    continue;
                File.Copy(src, dst, overwrite: true);
                Log.Info($"[PresetCapture] 已安装 {dst}");
            }

            AssetDatabase.Refresh();
        }

        /// <summary>启动底图与对应方向视频第一帧同一张，避免开机封面闪旧篝火帧。</summary>
        static void InstallBootStill(string pngPath, bool landscape)
        {
            if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath))
                return;

            string fileName = landscape
                ? "BootEarlyCover_Landscape.png"
                : "BootEarlyCover_Portrait.png";
            string assetPath = "Assets/GameMain/Boot/" + fileName;
            string dst = Path.GetFullPath(Path.Combine(Application.dataPath, "GameMain/Boot/" + fileName));
            Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? ".");
            File.Copy(pngPath, dst, overwrite: true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            Log.Info($"[PresetCapture] 启动{(landscape ? "横" : "竖")}屏图已换成视频首帧 → {dst}");
        }

        static string ResolveFfmpeg()
        {
            string[] candidates =
            {
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "ffmpeg",
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                string c = candidates[i];
                if (c == "ffmpeg")
                    return c;
                if (File.Exists(c))
                    return c;
            }

            return null;
        }

        static async Task<bool> WaitExtrasSettled()
        {
            double start = EditorApplication.timeSinceStartup;
            while (EditorApplication.timeSinceStartup - start < ExtrasSettledTimeout)
            {
                if (_cancel || !EditorApplication.isPlaying)
                    return false;
                if (IsExtrasSettled())
                    return true;
                await Task.Delay(250);
            }

            return IsExtrasSettled();
        }

        static bool IsExtrasSettled()
        {
            var world = IslandWorldService.Instance
                ?? UnityEngine.Object.FindAnyObjectByType<IslandWorldService>();
            EnvMapItemSpawner spawner = world != null ? world.MapItemSpawner : null;
            if (spawner == null)
                spawner = UnityEngine.Object.FindAnyObjectByType<EnvMapItemSpawner>();
            return spawner != null && spawner.ExtrasSettled;
        }

        static async Task RunAsync()
        {
            if (_busy)
                return;
            _busy = true;
            int mode = SessionState.GetInt(SessionMode, Mode4K);
            bool cover = mode == ModeCover;
            string outDir = cover ? OutDirCover : OutDir4K;
            string progressTitle = cover ? "预设占位图封面" : "预设 4K 截图";
            string firstPath = null;
            int ok = 0;
            try
            {
                if (!await WaitWorldReady())
                {
                    Debug.LogError("[PresetCapture] 世界未就绪（IslandWorldService.IsLoaded）");
                    return;
                }

                await DelaySeconds(1.5f);

                BeginOilCaptureBoost();
                var envBoost = BeginEnvCaptureBoost();
                float prevRenderScale = ForceRenderScale(1f);
                var hiddenUi = HideUi();
                try
                {
                    for (int schemeId = FirstScheme; schemeId <= LastScheme; schemeId++)
                    {
                        if (_cancel || !EditorApplication.isPlaying)
                        {
                            Debug.LogWarning("[PresetCapture] 已取消");
                            break;
                        }

                        string name = GetSchemeName(schemeId);
                        float progress = (schemeId - FirstScheme) / (float)(LastScheme - FirstScheme + 1);
                        if (EditorUtility.DisplayCancelableProgressBar(
                                progressTitle, $"{schemeId:D2} {name}", progress))
                        {
                            _cancel = true;
                            break;
                        }

                        if (!ApplyPreset(schemeId))
                        {
                            Debug.LogError($"[PresetCapture] 切预设失败 scheme={schemeId}");
                            continue;
                        }

                        await DelaySeconds(SettleSeconds);
                        if (_cancel || !EditorApplication.isPlaying)
                            break;

                        // 切预设可能又刷出 HUD；再藏一次（对齐 DevicePresetShotHelper）。
                        MergeHideUi(hiddenUi, HideUi());
                        // 走路/面板可能又压低 renderScale。
                        ForceRenderScale(1f);
                        // 强制满铺完整树模，避免远树片板 / 公告板「一板」。
                        EnvItemStreamerLite.ForceCatchUpForCapture();
                        await DelaySeconds(0.6f);
                        if (_cancel || !EditorApplication.isPlaying)
                            break;

                        string file = $"{schemeId:D2}_{Sanitize(name)}.png";
                        string path = Path.Combine(outDir, file);
                        bool saved = cover ? CaptureCover(path) : Capture4K(path);
                        if (!saved)
                        {
                            Debug.LogError($"[PresetCapture] 截图失败 {file}");
                            continue;
                        }

                        firstPath ??= path;
                        ok++;
                        Log.Info($"[PresetCapture] {file}");
                    }
                }
                finally
                {
                    RestoreUi(hiddenUi);
                    RestoreRenderScale(prevRenderScale);
                    EndEnvCaptureBoost(envBoost);
                    EndOilCaptureBoost();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                SessionState.SetBool(SessionKey, false);
                SessionState.SetInt(SessionPhase, 0);
                _busy = false;
            }

            Log.Info($"[PresetCapture] 完成 {ok}/{LastScheme - FirstScheme + 1} → {outDir}");
            if (!string.IsNullOrEmpty(firstPath) && File.Exists(firstPath))
                EditorUtility.RevealInFinder(firstPath);
        }

        static async Task<bool> WaitWorldReady()
        {
            double start = EditorApplication.timeSinceStartup;
            while (EditorApplication.timeSinceStartup - start < WorldReadyTimeout)
            {
                if (_cancel || !EditorApplication.isPlaying)
                    return false;
                if (IsWorldReady() && Camera.main != null)
                    return true;
                await Task.Delay(250);
            }

            return IsWorldReady() && Camera.main != null;
        }

        static bool IsWorldReady()
        {
            var world = IslandWorldService.Instance;
            if (world != null && world.IsLoaded)
                return true;
            // Fallback if Instance not yet set but another service exists.
            var found = UnityEngine.Object.FindAnyObjectByType<IslandWorldService>();
            return found != null && found.IsLoaded;
        }

        static async Task DelaySeconds(float seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
            {
                if (_cancel || !EditorApplication.isPlaying)
                    return;
                await Task.Delay(100);
            }
        }

        static bool ApplyPreset(int schemeId)
        {
            var presets = GameEntry.Presets;
            if (presets == null)
            {
                presets = UnityEngine.Object.FindAnyObjectByType<PresetService>();
                if (presets == null)
                {
                    Debug.LogError("[PresetCapture] PresetService 找不到");
                    return false;
                }
            }

            if (presets.GetById(schemeId) == null)
            {
                Debug.LogError($"[PresetCapture] 预设 id={schemeId} 不存在");
                return false;
            }

            // 直接 Apply，跳过 veil/标签，画面干净（对齐 DevicePresetShotHelper）。
            presets.Apply(schemeId, teleportPlayer: true);
            return true;
        }

        static string GetSchemeName(int schemeId)
        {
            var presets = GameEntry.Presets
                ?? UnityEngine.Object.FindAnyObjectByType<PresetService>();
            IslandPresetAsset asset = presets?.GetById(schemeId);
            if (asset != null && !string.IsNullOrEmpty(asset.DisplayName))
                return asset.DisplayName;

            if (schemeId >= 1 && schemeId <= FallbackNames.Length)
                return FallbackNames[schemeId - 1];
            return $"预设{schemeId}";
        }

        static Type FindOilFilterType()
        {
            var type = Type.GetType("CustomSRP.OilFilter, Assembly-CSharp");
            if (type != null)
                return type;

            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                type = asms[i].GetType("CustomSRP.OilFilter");
                if (type != null)
                    return type;
            }

            return null;
        }

        static void BeginOilCaptureBoost()
        {
            // CustomSRP.OilFilter 在 Assembly-CSharp，经反射调用。
            var type = FindOilFilterType();
            var begin = type?.GetMethod("BeginEditorCaptureBoost", BindingFlags.Public | BindingFlags.Static);
            if (begin == null)
            {
                Debug.LogWarning("[PresetCapture] OilFilter.BeginEditorCaptureBoost 找不到，油画无法开到最大");
                return;
            }

            begin.Invoke(null, null);
            Log.Info("[PresetCapture] 油画开到最大：16-tap + strength=1 + radius=1，截完恢复");
        }

        static void EndOilCaptureBoost()
        {
            var type = FindOilFilterType();
            var end = type?.GetMethod("EndEditorCaptureBoost", BindingFlags.Public | BindingFlags.Static);
            end?.Invoke(null, null);
        }

        /// <summary>返回强制前的 renderScale（读不到则 -1）。</summary>
        static float ForceRenderScale(float scale)
        {
            var cam = Camera.main;
            if (cam == null)
                return -1f;

            float prev = ReadRenderScale(cam.gameObject);
            IslandCameraSrpSetup.SetRenderScale(cam.gameObject, scale);
            return prev;
        }

        static void RestoreRenderScale(float prev)
        {
            if (prev < 0f)
                return;
            var cam = Camera.main;
            if (cam == null)
                return;
            IslandCameraSrpSetup.SetRenderScale(cam.gameObject, prev);
        }

        static float ReadRenderScale(GameObject go)
        {
            if (go == null)
                return -1f;
            try
            {
                var crpType = Type.GetType(
                    "CustomSRP.CustomRenderPipelineCamera, Assembly-CSharp");
                if (crpType == null)
                    return -1f;
                var crp = go.GetComponent(crpType);
                if (crp == null)
                    return -1f;
                var settingsProp = crpType.GetProperty(
                    "Settings", BindingFlags.Public | BindingFlags.Instance);
                object settings = settingsProp?.GetValue(crp);
                if (settings == null)
                    return -1f;
                var field = settings.GetType().GetField(
                    "renderScale", BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                    return -1f;
                return (float)field.GetValue(settings);
            }
            catch
            {
                return -1f;
            }
        }

        static void SubmitGpuDraws(Camera cam)
        {
            // CustomSRP.OilTerrainGrassBinder 在 Assembly-CSharp，经反射调用。
            var binderType = Type.GetType("CustomSRP.OilTerrainGrassBinder, Assembly-CSharp");
            var activeProp = binderType?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
            object active = activeProp?.GetValue(null);
            if (active == null)
                return;

            var draw = binderType.GetMethod(
                "DrawForCamera",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Camera) },
                null);
            if (draw == null)
            {
                Debug.LogWarning("[PresetCapture] OilTerrainGrassBinder.DrawForCamera 找不到，草可能空");
                return;
            }

            draw.Invoke(active, new object[] { cam });
        }

        static bool Capture4K(string path)
        {
            return CaptureToFile(path, Width4K, Height4K, squareCover: false, rounded: false);
        }

        static bool CaptureCover(string path)
        {
            // 高分辨率方图 → 缩到 264 → 圆角 alpha（对齐 zhanweitu）。
            return CaptureToFile(
                path, CoverRenderSize, CoverRenderSize, squareCover: true, rounded: true);
        }

        static bool CaptureToFile(
            string path, int width, int height, bool squareCover, bool rounded)
        {
            var cam = Camera.main;
            if (cam == null)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            float prevAspect = cam.aspect;
            float prevFov = cam.fieldOfView;
            var prevTarget = cam.targetTexture;
            var disabledCams = DisableOverlayCameras(cam);

            RenderTexture rt = null;
            Texture2D tex = null;
            Texture2D outTex = null;
            try
            {
                if (squareCover)
                    ApplySquareCoverFov(cam);
                else
                    ApplyLandscapeFov(cam, width, height);

                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1,
                    name = "_IslandPresetCapture",
                };
                rt.Create();
                cam.targetTexture = rt;
                SubmitGpuDraws(cam);
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                tex.Apply(false, false);

                if (rounded)
                {
                    outTex = DownscaleRounded(tex, CoverSize, CoverCornerRadius);
                    File.WriteAllBytes(path, outTex.EncodeToPNG());
                }
                else
                {
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                }

                return File.Exists(path) && new FileInfo(path).Length > 256;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                cam.aspect = prevAspect;
                cam.fieldOfView = prevFov;
                RestoreOverlayCameras(disabledCams);
                RenderTexture.active = null;
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);
                if (outTex != null)
                    UnityEngine.Object.DestroyImmediate(outTex);
            }
        }

        static void ApplyLandscapeFov(Camera cam, int width, int height)
        {
            float aspect = width / (float)height;
            cam.aspect = aspect;
            float hRad = LandscapeHFovDeg * Mathf.Deg2Rad;
            float vRad = 2f * Mathf.Atan(Mathf.Tan(hRad * 0.5f) / aspect);
            cam.fieldOfView = vRad * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Loading 视频：横屏锁 90° 横 FOV（竖向视野与预设 4K 相同）；
        /// 竖屏锁作者 60° 竖 FOV，窄于 1080:1920 时按参考横纵比撑满横向视野。
        /// 对齐 <see cref="IslandJoystickCamera"/> 的运行时规则。
        /// </summary>
        static void ApplyVideoFov(Camera cam, int width, int height)
        {
            float aspect = width / (float)Mathf.Max(1, height);
            if (aspect > 1f)
            {
                ApplyLandscapeFov(cam, width, height);
                return;
            }

            cam.aspect = aspect;
            cam.fieldOfView = PortraitAuthoredVFovDeg;
            if (aspect >= PortraitRefAspect)
                return;

            float tanHalfHorizontal =
                Mathf.Tan(PortraitAuthoredVFovDeg * Mathf.Deg2Rad * 0.5f) * PortraitRefAspect;
            cam.fieldOfView = 2f * Mathf.Atan(tanHalfHorizontal / aspect) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 方图：沿用 16:9 / 90° 横 FOV 算出的竖 FOV，做中心裁切，避免方画幅拉宽视野。
        /// </summary>
        static void ApplySquareCoverFov(Camera cam)
        {
            float landscapeAspect = Width4K / (float)Height4K;
            float hRad = LandscapeHFovDeg * Mathf.Deg2Rad;
            float vRad = 2f * Mathf.Atan(Mathf.Tan(hRad * 0.5f) / landscapeAspect);
            cam.aspect = 1f;
            cam.fieldOfView = vRad * Mathf.Rad2Deg;
        }

        struct EnvCaptureBoost
        {
            public float PrevFullMesh;
            public float PrevKeep;
            public float PrevLodBias;
            public int PrevThinStride;
            public bool Active;
        }

        /// <summary>
        /// 截图期间：拉远完整树模、关远树隔株、提高 lodBias，避免片板 / 少树。
        /// </summary>
        static EnvCaptureBoost BeginEnvCaptureBoost()
        {
            var boost = new EnvCaptureBoost
            {
                Active = true,
                PrevFullMesh = EnvStreamingConfig.TreeFullMeshMaxDist,
                PrevKeep = EnvStreamingConfig.TreeBillboardKeepRadius,
                PrevLodBias = QualitySettings.lodBias,
                PrevThinStride = EnvStreamingConfig.FillBoundFarTreeSilhouetteStride,
            };
            EnvStreamingConfig.TreeFullMeshMaxDist = Mathf.Max(boost.PrevFullMesh, 420f);
            EnvStreamingConfig.TreeBillboardKeepRadius = Mathf.Max(boost.PrevKeep, 520f);
            EnvStreamingConfig.FillBoundFarTreeSilhouetteStride = 1;
            QualitySettings.lodBias = Mathf.Max(boost.PrevLodBias, 2f);
            QualitySettings.maximumLODLevel = 0;
            Log.Info(
                $"[PresetCapture] env boost fullMesh={EnvStreamingConfig.TreeFullMeshMaxDist:F0} " +
                $"keep={EnvStreamingConfig.TreeBillboardKeepRadius:F0} " +
                $"thinStride=1 lodBias={QualitySettings.lodBias:F2}");
            return boost;
        }

        static void EndEnvCaptureBoost(EnvCaptureBoost boost)
        {
            if (!boost.Active)
                return;
            EnvStreamingConfig.TreeFullMeshMaxDist = boost.PrevFullMesh;
            EnvStreamingConfig.TreeBillboardKeepRadius = boost.PrevKeep;
            EnvStreamingConfig.FillBoundFarTreeSilhouetteStride = boost.PrevThinStride;
            QualitySettings.lodBias = boost.PrevLodBias;
        }

        /// <summary>双线性缩小到 size²，并写入圆角矩形 alpha。</summary>
        static Texture2D DownscaleRounded(Texture2D src, int size, float cornerRadius)
        {
            var dst = new Texture2D(size, size, TextureFormat.RGBA32, false);
            // 输出固定 CoverSize；圆角按占位图像素半径，不随渲染分辨率缩放。
            float r = cornerRadius;

            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    Color c = src.GetPixelBilinear(u, v);
                    float a = RoundedRectAlpha(x, y, size, size, r);
                    byte ab = (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255);
                    pixels[y * size + x] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
                        ab);
                }
            }

            dst.SetPixels32(pixels);
            dst.Apply(false, false);
            return dst;
        }

        static float RoundedRectAlpha(int x, int y, int w, int h, float radius)
        {
            float r = Mathf.Max(0.5f, radius);
            float fx = x + 0.5f;
            float fy = y + 0.5f;
            float cx = fx < r ? r : (fx > w - r ? w - r : fx);
            float cy = fy < r ? r : (fy > h - r ? h - r : fy);
            // 在直边区域内 cx/cy 贴到像素本身 → dist=0 → 不透明。
            float dist = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
            // ~1px AA，对齐占位图软边。
            return Mathf.Clamp01(r + 0.55f - dist);
        }

        /// <summary>关非主相机（含可能的 UI/调试相机），Custom SRP 无 URP cameraStack。</summary>
        static List<(Camera cam, bool enabled)> DisableOverlayCameras(Camera main)
        {
            var list = new List<(Camera, bool)>(4);
            var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude);
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null || c == main || !c.enabled)
                    continue;
                // 保留场景深度/特效相机若挂在主相机子级且 depth 更低？一律关 overlay 更干净。
                list.Add((c, true));
                c.enabled = false;
            }

            return list;
        }

        static void RestoreOverlayCameras(List<(Camera cam, bool enabled)> list)
        {
            if (list == null)
                return;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].cam != null && list[i].enabled)
                    list[i].cam.enabled = true;
            }
        }

        struct HiddenUi
        {
            public UIDocument Doc;
            public bool DocEnabled;
            public Canvas Canvas;
            public bool CanvasEnabled;
            public GameObject Go;
            public bool Active;
            public DebuggerComponent Debugger;
            public bool DebuggerWasActive;
        }

        /// <summary>
        /// 必须关 UIDocument.enabled：仅改 opacity 要等下一帧 Panel 更新，
        /// 而 CustomSRP CameraRenderer 在 cam.Render 里同步 DrawUIOverlay，同帧仍会画上 HUD/FPS。
        /// </summary>
        static List<HiddenUi> HideUi()
        {
            var list = new List<HiddenUi>(32);

            var debugger = UnityEngine.Object.FindAnyObjectByType<DebuggerComponent>();
            if (debugger != null && debugger.ActiveWindow)
            {
                list.Add(new HiddenUi
                {
                    Debugger = debugger,
                    DebuggerWasActive = true,
                });
                debugger.ActiveWindow = false;
            }

            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude);
            for (int i = 0; i < docs.Length; i++)
            {
                var doc = docs[i];
                if (doc == null || !doc.enabled)
                    continue;
                list.Add(new HiddenUi { Doc = doc, DocEnabled = true });
                doc.enabled = false;
            }

            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude);
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null || !c.enabled)
                    continue;
                if (c.renderMode == RenderMode.WorldSpace)
                    continue;
                list.Add(new HiddenUi { Canvas = c, CanvasEnabled = true });
                c.enabled = false;
            }

            HideNamed(list, "SRDebugger");
            HideNamed(list, "SR_Debugger");
            HideNamed(list, "LoadUpdateUI");
            HideNamed(list, "SplashUI");
            HideNamed(list, "LoadTipsUI");
            return list;
        }

        static void MergeHideUi(List<HiddenUi> into, List<HiddenUi> extra)
        {
            if (into == null || extra == null || extra.Count == 0)
                return;
            into.AddRange(extra);
        }

        static void HideNamed(List<HiddenUi> list, string name)
        {
            var go = GameObject.Find(name);
            if (go == null || !go.activeSelf)
                return;
            list.Add(new HiddenUi { Go = go, Active = true });
            go.SetActive(false);
        }

        static void RestoreUi(List<HiddenUi> list)
        {
            if (list == null)
                return;
            // 倒序：后藏的先恢复，避免 Debugger 的 UIDocument 状态被盖掉。
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var h = list[i];
                if (h.Doc != null && h.DocEnabled)
                    h.Doc.enabled = true;
                if (h.Canvas != null && h.CanvasEnabled)
                    h.Canvas.enabled = true;
                if (h.Go != null && h.Active)
                    h.Go.SetActive(true);
                if (h.Debugger != null)
                    h.Debugger.ActiveWindow = h.DebuggerWasActive;
            }
        }

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "preset";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }
    }
}
#endif
