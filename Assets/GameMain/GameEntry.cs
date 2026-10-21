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
            AudioInteractionBootstrap.ResetSession();
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
            // Medium (2): lower GI/LOD cost than Ultra; keep CRP + hard shadows.
            // Clarity comes from DPI×renderScale; FPS from quality + streaming + far clip.
            int medium = Mathf.Clamp(2, 0, QualitySettings.names.Length - 1);
            QualitySettings.SetQualityLevel(medium, true);
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            // Never Screen.SetResolution: 1280×720 locks 16:9, then FullScreenWindow
            // stretches onto taller phones (20:9) and flattens UI + game view.
            // Pixel budget = native aspect × DPI scale × CRP render scale.
            QualitySettings.resolutionScalingFixedDPIFactor = 0.7f;
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

            EnsureGroup("Loading", 0);
            EnsureGroup("Main", 1);
            EnsureGroup("Panel", 2);
            EnsureGroup("Dialog", 3);
        }

        static void EnsureGroup(string name, int depth)
        {
            if (!UI.HasUIGroup(name))
                UI.AddUIGroup(name, depth);
        }
    }
}
