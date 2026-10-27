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
            AudioInteractionBootstrap.ResetSession();
            AudioAppLifecycle.ResetSession();
            AudioAppLifecycle.EnsureExists();
            // Before SoundComponent.Start: swap helper type names to Wanos implementations.
            WanosSoundSetup.ConfigureHelpersBeforeSoundStart();
            ApplyDeviceGraphicsPolicy();
        }

        void Start()
        {
            InitBuiltinComponents();
#if UNITY_EDITOR
            if (Base != null)
                Base.EditorResourceMode = true;
#endif
            if (Base != null)
                Base.FrameRate = 60;
            InitCustomComponents();
            EnsureUiGroups();
#if !UNITY_EDITOR
            Log.Info("[GameEntry] res after Start=" + Screen.width + "x" + Screen.height
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
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            // Never Screen.SetResolution: 1280×720 locks 16:9, then FullScreenWindow
            // stretches onto taller phones (20:9) and flattens UI + game view.
            // Pixel budget = native aspect × DPI scale × CRP render scale.
            QualitySettings.resolutionScalingFixedDPIFactor = 0.7f;
            // Very Low asset lodBias=0.3 会把树 prefab 的 screenRelativeHeight(0.12)
            // 片树切入压到 ~20m；拉回 1 与 LOD 作者距离一致（median ~70–90m @fov60）。
            // GI/阴影等仍用 Very Low；仅恢复 LOD 屏占比标定。
            QualitySettings.lodBias = 1f;
            QualitySettings.maximumLODLevel = 0;
            // Keep oil NPR; cut ocean stamps / terrain micro-spec on mobile.
            // Directional hard shadows still sample (see OilNPRLighting).
            Shader.EnableKeyword("OIL_MOBILE_PERF");
            Log.Info(
                "[GameEntry] Quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()]
                + " RP=" + (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                    ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name
                    : "null")
                + " res=" + Screen.width + "x" + Screen.height
                + " dpiScale=" + QualitySettings.resolutionScalingFixedDPIFactor
                + " lodBias=" + QualitySettings.lodBias
                + " targetFPS=" + Application.targetFrameRate
                + " OIL_MOBILE_PERF");
#endif
        }

        void OnDestroy()
        {
            BootLoadingService.ResetSession();
            AudioInteractionBootstrap.Shutdown();
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
