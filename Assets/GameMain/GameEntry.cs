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
            // Awake is too early on some Android devices; re-apply after display is ready.
            Screen.SetResolution(1280, 720, FullScreenMode.FullScreenWindow);
            Log.Info("[GameEntry] res after Start=" + Screen.width + "x" + Screen.height);
#endif
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
            // OEM often ignores SetResolution; DPI scale is the real backbuffer lever.
            // 0.70×0.75 + bicubic：略低于旧 0.78×0.58 的 fill，换稳定帧。
            QualitySettings.resolutionScalingFixedDPIFactor = 0.7f;
            Screen.SetResolution(1280, 720, FullScreenMode.FullScreenWindow);
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
