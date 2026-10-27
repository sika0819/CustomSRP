using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Gmj：揭罩前在加载罩下扫几个 yaw，把右/左扇区的 Opaque / ShadowCaster / 草 / 镜子
    /// 变体先编进 PSO，避免「刚启动向右转」首帧 CreateGPUProgram 尖峰。
    /// 不改 renderScale / 流送半径 / LOD。
    /// </summary>
    public static class IslandStartupWarmup
    {
        // 出生朝向 → 右前 → 右侧 → 左前 → 左侧 → 归位。右侧优先匹配体感卡点。
        static readonly float[] YawOffsetsDeg = { 0f, 40f, 85f, -40f, -85f, 0f };

        /// <summary>
        /// 每个朝向等若干帧，让 LateUpdate 合批 + CRP + mirror 真正跑完。
        /// </summary>
        const int FramesPerPeek = 2;

        public static async UniTask RunAsync(int uncoverGeneration)
        {
            if (DeviceGraphicsPolicy.Current != DeviceGraphicsClass.Gmj)
            {
                Log.Info("[Hitch] IslandStartupWarmup skip class=" + DeviceGraphicsPolicy.Current);
                return;
            }

            IslandJoystickCamera cam = IslandJoystickCamera.Current;
            if (cam == null)
            {
                Log.Warning("[IslandStartupWarmup] skip — no IslandJoystickCamera");
                Log.Info("[Hitch] IslandStartupWarmup skip no-camera");
                return;
            }

            float wallStart = Time.realtimeSinceStartup;
            Log.Info("[Hitch] IslandStartupWarmup begin");
            float baseYaw = cam.YawDegrees;
            IslandPlayerPosSave.PushSaveSuppress();
            BootLoadingService.ReportProgress(
                BootLoadingService.ItemsPrepareMax, "正在预热画面...");
            try
            {
                for (int i = 0; i < YawOffsetsDeg.Length; i++)
                {
                    if (!UncoverGenerationAlive(uncoverGeneration))
                        return;

                    cam.PeekYaw(baseYaw + YawOffsetsDeg[i]);
                    // Force mirror redraw for this frustum (Gmj turn throttle would skip).
                    IslandLocomotionState.ForceMirrorRedraw = true;

                    for (int f = 0; f < FramesPerPeek; f++)
                    {
                        if (!UncoverGenerationAlive(uncoverGeneration))
                            return;
                        await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
                    }
                }
            }
            finally
            {
                cam.PeekYaw(baseYaw);
                IslandPlayerPosSave.PopSaveSuppress();
                Log.Info(
                    "[Hitch] IslandStartupWarmup end wallMs="
                    + ((Time.realtimeSinceStartup - wallStart) * 1000f).ToString("F0"));
            }

            Log.Info(
                "[IslandStartupWarmup] done peeks=" + YawOffsetsDeg.Length
                + " baseYaw=" + baseYaw.ToString("F1"));
        }

        static bool UncoverGenerationAlive(int gen) => gen == ProcedureIsland.UncoverGeneration;
    }
}
