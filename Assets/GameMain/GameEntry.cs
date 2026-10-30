using GameFramework.UI;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Game-side entry (not UnityGameFramework.Runtime.GameEntry static class).
    /// Runs after UIComponent.Start so AddUIGroup has InstanceRoot.
    /// ProcedureComponent also waits EndOfFrame before entrance procedure.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public partial class GameEntry : MonoBehaviour
    {
        void Awake()
        {
            EnsureIslandSceneComponent();
            // 关域重载时静态槽可能还指着上一轮句柄袋。
            CloseLeaveHandles();
            // Survives Disable Domain Reload — RuntimeInitializeOnLoadMethod will not re-fire.
            BootLoadingService.ResetSession();
            YooResourceModule.ResetSession();
            LocomotionInput.ClearAll();
            Game.Native.NativeBridgeFacade.ResetSession();
            Game.BoluoApi.BankUpgradeBootstrap.ResetSession();
            Game.BoluoApi.BoluoApiEnvironment.ClearCached();
            WanosSoundSetup.ResetSession();
            MusicPlaylist.ResetSession();
            UiTipToast.ResetSession();
            IslandAudioSession.ResetSession();
            AudioAppLifecycle.ResetSession();
            AudioAppLifecycle.EnsureExists();
            AtmosphereVisualRegistry.Clear();
            // Before SoundComponent.Start: swap helper type names to Wanos implementations.
            WanosSoundSetup.ConfigureHelpersBeforeSoundStart();
            ApplyDeviceGraphicsPolicy();
            // 真机触摸会再合成一次鼠标事件。第一次点 UI 时两套指针互相取消，
            // 按钮只闪一下，要点第二次。编辑器没有触摸，保留鼠标。
#if !UNITY_EDITOR
            Input.simulateMouseWithTouches = false;
#endif
        }

        void Start()
        {
            InitBuiltinComponents();
#if UNITY_EDITOR
            if (Base != null)
                Base.EditorResourceMode = true;
#endif
            if (Base != null)
                Base.FrameRate = IslandCameraSrpSetup.DefaultFrameRate;
            InitCustomComponents();
            EnsureUiGroups();
#if !UNITY_EDITOR
            Log.Info("[GameEntry] res after Start=" + Screen.width + "x" + Screen.height
                + " dpi=" + Screen.dpi
                + " dpiScale=" + QualitySettings.resolutionScalingFixedDPIFactor);
#endif
        }

        /// <summary>
        /// Orientation already says landscape, but pixels are still portrait.
        /// Android chrome can report that for a frame; do not refit FOV on it.
        /// </summary>
        public static bool IsTransientPortraitReport
        {
            get
            {
                if (Screen.width >= Screen.height)
                    return false;

                ScreenOrientation orientation = Screen.orientation;
                return orientation == ScreenOrientation.LandscapeLeft
                    || orientation == ScreenOrientation.LandscapeRight;
            }
        }

#if !UNITY_EDITOR
        /// <summary>
        /// 与 ProjectSettings.targetPixelDensity 保持一致。改其中一处必须改另一处。
        /// </summary>
        const float BackbufferTargetDpi = 300f;

        const float BackbufferScale = 0.7f;

        static void ApplyBackbufferScale()
        {
            float dpi = Screen.dpi;
            float factor = BackbufferScale;
            if (dpi > 1f)
                factor = BackbufferScale * dpi / BackbufferTargetDpi;
            QualitySettings.resolutionScalingFixedDPIFactor = factor;
        }
#endif

        static void ApplyDeviceGraphicsPolicy()
        {
#if !UNITY_EDITOR
            // Very Low (0): lowest GI / shadow cost; keep CRP + hard shadows (QualitySettings.asset).
            // Clarity comes from DPI×renderScale; FPS from quality + streaming + far clip.
            // lodBias 不跟 Very Low 的 0.3：见下方覆盖，否则树 LOD 片树会贴脸。
            int veryLow = 0;
            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == "Very Low")
                {
                    veryLow = i;
                    break;
                }
            }
            QualitySettings.SetQualityLevel(veryLow, true);
            // 目标帧按机型：闺蜜机标准 45 / 4K 60，手机 30/45/60。见 IslandCameraSrpSetup。
            Application.targetFrameRate = IslandCameraSrpSetup.DefaultFrameRate;
            QualitySettings.vSyncCount = 0;
            // 不调用 Screen.SetResolution：固定 1280×720 会锁 16:9，再铺到 20:9 上把画面和 UI 拉扁。
            // Player：Resolution Scaling = Fixed DPI，Target DPI = 300（ProjectSettings.targetPixelDensity）。
            // scale = min(TargetDPI × factor / Screen.dpi, 1)。factor 按本机 dpi 反算，使背缓冲为原生的 0.7。
            // 3D 再乘 CRP renderScale（站立 0.5，走路/弹窗/沉浸 0.375）。
            ApplyBackbufferScale();
            // Very Low asset lodBias=0.3 会把树 prefab 的 screenRelativeHeight(0.12)
            // 片树切入压到 ~20m；拉回 1 与 LOD 作者距离一致（median ~70–90m @fov60）。
            // GI/阴影等仍用 Very Low；仅恢复 LOD 屏占比标定。
            QualitySettings.lodBias = 1f;
            QualitySettings.maximumLODLevel = 0;
            // 峰值目标 700MB：再丢一级 mip（Very Low 资源已是 1）。近树远树都还在，只是更软。
            // 流式预算不跟机型档的 256/512，否则 12GB 手机会把贴图重新堆回来。
            QualitySettings.globalTextureMipmapLimit = 2;
            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsMemoryBudget = 112f;
            QualitySettings.streamingMipmapsMaxLevelReduction = 4;
            Texture.streamingTextureDiscardUnusedMips = true;
            // Keep oil NPR; cut ocean stamps / terrain micro-spec on mobile.
            // Directional hard shadows still sample (see OilNPRLighting).
            Shader.EnableKeyword("OIL_MOBILE_PERF");
            Log.Info(
                "[GameEntry] Quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()]
                + " RP=" + (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                    ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name
                    : "null")
                + " res=" + Screen.width + "x" + Screen.height
                + " dpi=" + Screen.dpi
                + " dpiScale=" + QualitySettings.resolutionScalingFixedDPIFactor
                + " lodBias=" + QualitySettings.lodBias
                + " mipLimit=" + QualitySettings.globalTextureMipmapLimit
                + " mipStream=" + QualitySettings.streamingMipmapsMemoryBudget
                + " mipDrop=" + QualitySettings.streamingMipmapsMaxLevelReduction
                + " targetFPS=" + Application.targetFrameRate
                + " OIL_MOBILE_PERF");
#endif
        }

        void OnDestroy()
        {
            BootLoadingService.ResetSession();
            IslandAudioSession.Instance?.Shutdown();
            // Unload stops DSP then DestoryEngine. Editor also forces this on
            // ExitingPlayMode; second call is a no-op if already torn down.
            WanosAudio.WanosEngine.Unload("GameEntry.OnDestroy");
            ClearCustomComponents();
            ClearBuiltinComponents();
        }

        public static void EnsureUiGroups()
        {
            if (UI == null)
                return;

            // Loading 必须高于 Main/Panel/Dialog：进岛时 IslandMainForm 会先打开，
            // 罩子淡出前仍须盖住 HUD（sortingOrder = depth * 100 + depthInGroup）。
            EnsureGroup("Main", 1);
            EnsureGroup("Panel", 2);
            EnsureGroup("Dialog", 3);
            // Tip is a fixed overlay. Depth 4 sits above sheets (Panel 2 / Dialog 3)
            // and under the loading cover (10). HudOverlayGate does not count it.
            EnsureGroup(UiAssets.TipGroup, 4);
            EnsureGroup("Loading", 10);
        }

        static void EnsureGroup(string name, int depth)
        {
            if (!UI.HasUIGroup(name))
            {
                UI.AddUIGroup(name, depth);
                return;
            }

            IUIGroup group = UI.GetUIGroup(name);
            if (group != null && group.Depth != depth)
                group.Depth = depth;
        }
    }
}
