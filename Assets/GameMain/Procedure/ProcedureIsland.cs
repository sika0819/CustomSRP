using System.Collections;
using GameFramework.Procedure;
using UnityEngine;
using UnityGameFramework.Runtime;
using ProcedureOwner = GameFramework.Fsm.IFsm<GameFramework.Procedure.IProcedureManager>;

namespace Game
{
    /// <summary>
    /// 岛屿流程编排：Services → World → Camera → Hud → Uncover。世界加载见 IslandWorldService。
    /// </summary>
    public class ProcedureIsland : ProcedureBase
    {
        enum Phase
        {
            Services,
            World,
            Camera,
            Hud,
            Uncover,
            Done,
        }

        Phase _phase;
        bool _hudOpened;
        bool _worldLoadStarted;
        bool _fadeStarted;
        float _hudWait;
        Coroutine _worldLoadRoutine;

        protected override void OnEnter(ProcedureOwner procedureOwner)
        {
            base.OnEnter(procedureOwner);
            _phase = Phase.Services;
            _hudOpened = false;
            _worldLoadStarted = false;
            _fadeStarted = false;
            _hudWait = 0f;
            if (!BootLoadingService.HasForm)
                BootLoadingService.Show("正在进入世界...", BootLoadingService.VisualPrepareMin);
            else
                BootLoadingService.ReportProgress(BootLoadingService.VisualPrepareMin, "正在进入世界...");
            EnsureSingleAudioListener();
        }

        static void EnsureSingleAudioListener()
        {
            AudioListener[] listeners = Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            Camera main = Camera.main;
            for (int i = 0; i < listeners.Length; i++)
            {
                bool keep = main != null && listeners[i].GetComponent<Camera>() == main;
                if (!keep && main != null)
                    listeners[i].enabled = false;
            }

            if (main != null)
            {
                AudioListener onCam = main.GetComponent<AudioListener>();
                if (onCam == null)
                    onCam = main.gameObject.AddComponent<AudioListener>();
                onCam.enabled = true;
            }
        }

        static IslandJoystickCamera EnsureJoystickCamera()
        {
            Camera main = Camera.main;
            if (main == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                main = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            IslandCameraSrpSetup.ApplyForIsland(main);

            IslandJoystickCamera cam = main.GetComponent<IslandJoystickCamera>();
            if (cam == null)
                cam = main.gameObject.AddComponent<IslandJoystickCamera>();
            cam.ApplySpawnPose();
            Log.Info(
                "[ProcedureIsland] camera pose pos="
                + main.transform.position.ToString("F1")
                + " yaw=" + main.transform.eulerAngles.y.ToString("F1")
                + " far=" + main.farClipPlane.ToString("F0")
                + " terrainActive=" + (Terrain.activeTerrain != null));
            return cam;
        }

        IEnumerator LoadWorld()
        {
            IslandWorldService world = GameEntry.World ?? IslandWorldService.Ensure();
            yield return world.Load((p, tip) => BootLoadingService.ReportProgress(p, tip));
            _worldLoadRoutine = null;
        }

        protected override void OnUpdate(ProcedureOwner procedureOwner, float elapseSeconds, float realElapseSeconds)
        {
            base.OnUpdate(procedureOwner, elapseSeconds, realElapseSeconds);
            if (_phase == Phase.Done)
                return;

            TickPhase();
        }

        protected override void OnLeave(ProcedureOwner procedureOwner, bool isShutdown)
        {
            if (_worldLoadRoutine != null && GameEntry.Base != null)
            {
                GameEntry.Base.StopCoroutine(_worldLoadRoutine);
                _worldLoadRoutine = null;
            }

            if (!isShutdown)
            {
                GameEntry.World?.Release();
                _worldLoadStarted = false;
            }

            base.OnLeave(procedureOwner, isShutdown);
        }

        void TickPhase()
        {
            switch (_phase)
            {
                case Phase.Services:
                    GameEntry.EnsureUiGroups();
                    GameEntry.EnsureEntityGroups();
                    if (GameEntry.World == null)
                        GameEntry.InitWorldService();
                    GameEntry.Weather?.ApplyLocalClockIfNeeded();
                    GameEntry.Music?.ApplySavedVolumes();
                    BootLoadingService.ReportProgress(0.71f, "正在进入世界...");
                    _phase = Phase.World;
                    break;

                case Phase.World:
                    if (!_worldLoadStarted)
                    {
                        if (!YooResourceModule.IsInitialized)
                        {
                            Log.Error("YooAsset not initialized — cannot load island world.");
                            break;
                        }

                        MonoBehaviour host = GameEntry.Base;
                        if (host == null)
                            break;

                        _worldLoadStarted = true;
                        _worldLoadRoutine = host.StartCoroutine(LoadWorld());
                    }

                    if (GameEntry.World == null || !GameEntry.World.IsLoaded)
                        break;

                    GameEntry.Weather?.PushVisuals();
                    _phase = Phase.Camera;
                    break;

                case Phase.Camera:
                    EnsureJoystickCamera();
                    BootLoadingService.ReportProgress(BootLoadingService.CameraPrepareMax, "正在进入岛屿");
                    _phase = Phase.Hud;
                    break;

                case Phase.Hud:
                    if (!_hudOpened && !GameEntry.UI.HasUIForm(UiAssets.IslandMainForm))
                    {
                        _hudOpened = true;
                        GameEntry.Weather?.ApplyLocalClockIfNeeded();
                        GameEntry.Weather?.PushVisuals();
                        Log.Info("[ProcedureIsland] OpenUIForm IslandMainForm");
                        GameEntry.UI.OpenUIForm(UiAssets.IslandMainForm, "Main");
                    }

                    if (!GameEntry.UI.HasUIForm(UiAssets.IslandMainForm))
                    {
                        _hudWait += Time.unscaledDeltaTime;
                        if (_hudWait < 10f)
                            break;
                        Log.Error("[ProcedureIsland] IslandMainForm timeout — uncover without HUD");
                    }

                    BootLoadingService.ReportProgress(BootLoadingService.ItemsPrepareMax, "即将进入...");
                    _phase = Phase.Uncover;
                    break;

                case Phase.Uncover:
                    if (!_fadeStarted)
                    {
                        _fadeStarted = true;
                        Log.Info("[ProcedureIsland] FadeHide loading cover");
                        BootLoadingService.FadeHide(1f);
                    }

                    if (!BootLoadingService.IsVisible && !GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
                        _phase = Phase.Done;
                    break;
            }
        }
    }
}
