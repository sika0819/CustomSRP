#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    /// <summary>
    /// Play 里依次切岛屿预设 1–10，主相机渲 3840×2160，不含 UI。
    /// 进 Play 后自动 <see cref="EditorLoginGate.RequestGuestProceed"/> 走游客登录，不弹窗等人点。
    /// 通过关掉 UIDocument.enabled（非仅 opacity），避免 CustomSRP DrawUIOverlay 把 HUD/FPS 画进 RT。
    /// 菜单：GameMain/Boluo/截图/各预设 4K 无 UI；也可丢 Temp/capture_preset_4k。
    /// </summary>
    [InitializeOnLoad]
    public static class IslandPresetCapture
    {
        const string RequestFile = "Temp/capture_preset_4k";
        const string SessionKey = "CustomSRP.Preset4K.Pending";
        const string SessionPhase = "CustomSRP.Preset4K.Phase";
        const int Width = 3840;
        const int Height = 2160;
        const float LandscapeHFovDeg = 90f;
        const int FirstScheme = 1;
        const int LastScheme = 10;
        /// <summary>MapItem CatchUp 结束后再等一小会儿，让天气/雾/草绑定稳住。</summary>
        const float SettleSeconds = 0.75f;
        const float WorldReadyTimeout = 120f;
        const float CatchUpTimeout = 90f;

        static readonly string[] FallbackNames =
        {
            "清晨起床", "独自旅行", "午后放松", "委屈泛滥", "专注工作",
            "放空发呆", "灵感唤醒", "假日躺平", "学习冲刺", "准备入睡",
        };

        static bool _busy;
        static bool _cancel;
        static double _phaseStart;

        static string OutDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../.tmp/preset_4k"));

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

        static void Poll()
        {
            if (!_busy && File.Exists(RequestFile))
            {
                try { File.Delete(RequestFile); }
                catch { return; }
                Begin();
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
                _ = RunAsync();
            }
        }

        [MenuItem("GameMain/Boluo/截图/各预设 4K 无 UI")]
        public static void RunFromMenu() => Begin();

        [MenuItem("GameMain/Boluo/截图/打开 4K 输出目录")]
        public static void RevealOutDir()
        {
            Directory.CreateDirectory(OutDir);
            EditorUtility.RevealInFinder(OutDir);
        }

        static void Begin()
        {
            if (_busy)
            {
                Debug.LogWarning("[Preset4K] 正在截图，等这轮结束");
                return;
            }

            Directory.CreateDirectory(OutDir);
            _cancel = false;
            SessionState.SetBool(SessionKey, true);
            SessionState.SetInt(SessionPhase, EditorApplication.isPlaying ? 2 : 1);
            _phaseStart = EditorApplication.timeSinceStartup;
            // 尽早闩上游客放行；GuestLogin.OnEnter 会 Reset，WaitWorldReady 里会反复补。
            EditorLoginGate.RequestGuestProceed();
            Log.Info($"[Preset4K] 开始 → {OutDir}  {(EditorApplication.isPlaying ? "已在 Play" : "将进入 Play")}（游客登录）");
        }

        static async Task RunAsync()
        {
            if (_busy)
                return;
            _busy = true;
            string firstPath = null;
            int ok = 0;
            try
            {
                if (!await WaitWorldReady())
                {
                    Debug.LogError("[Preset4K] 世界未就绪（IslandWorldService.IsLoaded）");
                    return;
                }

                await DelaySeconds(1.5f);

                // Custom SRP 在 cam.Render 里会 DrawUIOverlay；整轮截图期间关掉 UIDocument。
                var hiddenUi = HideUi();
                try
                {
                    for (int schemeId = FirstScheme; schemeId <= LastScheme; schemeId++)
                    {
                        if (_cancel || !EditorApplication.isPlaying)
                        {
                            Debug.LogWarning("[Preset4K] 已取消");
                            break;
                        }

                        string name = GetSchemeName(schemeId);
                        float progress = (schemeId - FirstScheme) / (float)(LastScheme - FirstScheme + 1);
                        if (EditorUtility.DisplayCancelableProgressBar(
                                "预设 4K 截图", $"{schemeId:D2} {name}", progress))
                        {
                            _cancel = true;
                            break;
                        }

                        if (!ApplyPreset(schemeId))
                        {
                            Debug.LogError($"[Preset4K] 切预设失败 scheme={schemeId}");
                            continue;
                        }

                        // 等一帧让相机落点写入 focus，再铺齐附近 MapItem，最后短 settle。
                        await DelaySeconds(0.05f);
                        if (!await WaitMapItemsCaughtUp())
                            Debug.LogWarning($"[Preset4K] MapItem CatchUp 超时/跳过 scheme={schemeId}");
                        await DelaySeconds(SettleSeconds);
                        if (_cancel || !EditorApplication.isPlaying)
                            break;

                        // 切预设可能又刷出 HUD；再藏一次（对齐 DevicePresetShotHelper）。
                        MergeHideUi(hiddenUi, HideUi());

                        string file = $"{schemeId:D2}_{Sanitize(name)}.png";
                        string path = Path.Combine(OutDir, file);
                        if (!Capture4K(path))
                        {
                            Debug.LogError($"[Preset4K] 截图失败 {file}");
                            continue;
                        }

                        firstPath ??= path;
                        ok++;
                        Log.Info($"[Preset4K] {file}");
                    }
                }
                finally
                {
                    RestoreUi(hiddenUi);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                SessionState.SetBool(SessionKey, false);
                SessionState.SetInt(SessionPhase, 0);
                _busy = false;
            }

            Log.Info($"[Preset4K] 完成 {ok}/{LastScheme - FirstScheme + 1} → {OutDir}");
            if (!string.IsNullOrEmpty(firstPath) && File.Exists(firstPath))
                EditorUtility.RevealInFinder(firstPath);
        }

        static async Task<bool> WaitWorldReady()
        {
            double start = EditorApplication.timeSinceStartup;
            bool closedLoginUi = false;
            while (EditorApplication.timeSinceStartup - start < WorldReadyTimeout)
            {
                if (_cancel || !EditorApplication.isPlaying)
                    return false;

                // GuestLogin.OnEnter 会 ResetSessionFlags；轮询补 Request，免等人点「游客登录进岛」。
                if (!EditorLoginGate.HasFormalSession)
                    EditorLoginGate.RequestGuestProceed();

                if (!closedLoginUi && EditorLoginGate.GuestProceedRequested)
                {
                    CloseEditorLoginWindow();
                    closedLoginUi = true;
                }

                if (IsWorldReady() && Camera.main != null)
                    return true;
                await Task.Delay(250);
            }

            return IsWorldReady() && Camera.main != null;
        }

        static void CloseEditorLoginWindow()
        {
            var wins = Resources.FindObjectsOfTypeAll<BoluoPhoneLoginWindow>();
            for (int i = 0; i < wins.Length; i++)
            {
                if (wins[i] != null)
                    wins[i].Close();
            }
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

        /// <summary>
        /// 在 Play 主线程上跑 <see cref="EnvMapItemSpawner.CatchUpAroundFocus"/>，
        /// 直到附近物体铺齐或超时。全量路径 / 未就绪时直接成功返回。
        /// </summary>
        static async Task<bool> WaitMapItemsCaughtUp()
        {
            var world = IslandWorldService.Instance
                ?? UnityEngine.Object.FindAnyObjectByType<IslandWorldService>();
            EnvMapItemSpawner spawner = world != null ? world.MapItemSpawner : null;
            if (spawner == null || !spawner.IsReady)
                return true;

            if (world == null)
                return true;

            bool done = false;
            bool started = false;
            try
            {
                world.StartCoroutine(CatchUpRoutine(spawner, () => done = true));
                started = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Preset4K] CatchUp start failed: {e.Message}");
                return false;
            }

            if (!started)
                return false;

            double start = EditorApplication.timeSinceStartup;
            while (!done)
            {
                if (_cancel || !EditorApplication.isPlaying)
                    return false;
                if (EditorApplication.timeSinceStartup - start > CatchUpTimeout)
                {
                    Debug.LogWarning($"[Preset4K] CatchUp timeout {CatchUpTimeout}s");
                    return false;
                }

                await Task.Delay(50);
            }

            Log.Info($"[Preset4K] map items caught up spawned={spawner.SpawnedCount}");
            return true;
        }

        static System.Collections.IEnumerator CatchUpRoutine(EnvMapItemSpawner spawner, Action onDone)
        {
            try
            {
                yield return spawner.CatchUpAroundFocus();
            }
            finally
            {
                onDone?.Invoke();
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
                    Debug.LogError("[Preset4K] PresetService 找不到");
                    return false;
                }
            }

            if (presets.GetById(schemeId) == null)
            {
                Debug.LogError($"[Preset4K] 预设 id={schemeId} 不存在");
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
                Debug.LogWarning("[Preset4K] OilTerrainGrassBinder.DrawForCamera 找不到，草可能空");
                return;
            }

            draw.Invoke(active, new object[] { cam });
        }

        static bool Capture4K(string path)
        {
            var cam = Camera.main;
            if (cam == null)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? OutDir);
            // UI 由 RunAsync 整轮 HideUi 管；这里只渲主相机（仍会走 DrawUIOverlay，故须 UIDocument 已关）。
            float prevAspect = cam.aspect;
            float prevFov = cam.fieldOfView;
            var prevTarget = cam.targetTexture;
            var disabledCams = DisableOverlayCameras(cam);

            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                ApplyLandscapeFov(cam);
                rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1,
                    name = "_IslandPreset4K",
                };
                rt.Create();
                cam.targetTexture = rt;
                SubmitGpuDraws(cam);
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                tex.Apply(false, false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return File.Exists(path) && new FileInfo(path).Length > 1024;
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
            }
        }

        static void ApplyLandscapeFov(Camera cam)
        {
            float aspect = Width / (float)Height;
            cam.aspect = aspect;
            float hRad = LandscapeHFovDeg * Mathf.Deg2Rad;
            float vRad = 2f * Mathf.Atan(Mathf.Tan(hRad * 0.5f) / aspect);
            cam.fieldOfView = vRad * Mathf.Rad2Deg;
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
