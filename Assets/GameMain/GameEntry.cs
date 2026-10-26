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
            AudioInteractionBootstrap.ResetSession();
            AudioAppLifecycle.ResetSession();
            AudioAppLifecycle.EnsureExists();
            // Before SoundComponent.Start: swap helper type names to Wanos implementations.
            WanosSoundSetup.ConfigureHelpersBeforeSoundStart();
            DeviceGraphicsPolicy.DetectAndApplyBoot();
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
