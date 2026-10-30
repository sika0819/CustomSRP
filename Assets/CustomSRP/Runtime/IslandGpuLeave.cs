using Game;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// 退登时释放挂在 Launch 或静态字段上的 GPU 对象。Game 程序集通过事件调进来。
    /// </summary>
    static class IslandGpuLeave
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Hook()
        {
            IslandGpuReclaim.ReleaseSession -= Release;
            IslandGpuReclaim.ReleaseSession += Release;
            IslandGpuReclaim.SceneUnloaded -= ReleaseCurtain;
            IslandGpuReclaim.SceneUnloaded += ReleaseCurtain;
            IslandGpuReclaim.FlushPool -= FlushAfterLeave;
            IslandGpuReclaim.FlushPool += FlushAfterLeave;
        }

        static void FlushAfterLeave()
        {
            ReleaseCoverVideo();
            CustomRenderPipeline.ReleasePinnedGlobals();
            LightingPass.Cleanup();
            CustomRenderPipeline.FlushGpuPool();
            ReleaseLargeRenderTextures();
            // ComputeBuffer 不是 UnityEngine.Object，无法 FindObjectsOfTypeAll；
            // 靠 ReleaseSession / ReleaseStubs / LightingPass.Cleanup 对口释放。
        }

        static void ReleaseCoverVideo()
        {
            LoadingForm[] forms = Resources.FindObjectsOfTypeAll<LoadingForm>();
            for (int i = 0; i < forms.Length; i++)
            {
                if (forms[i] != null)
                    forms[i].ReleasePlaybackForLeave();
            }
        }

        /// <summary>
        /// 场景已经卸掉。阴影/反射/相机目标不还驱动的话，下一趟进岛再分配会在 Adreno 上 OOM。
        /// Honor 上 kgsl 映射会挂到上千条；只卸大 RT 不够，创建的也卸。
        /// active / Camera.targetTexture 握着的 RT 以前被跳过，Adreno 上会一直占着 kgsl。
        /// </summary>
        static void ReleaseLargeRenderTextures()
        {
            RenderTexture.active = null;
            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera != null && camera.targetTexture != null)
                    camera.targetTexture = null;
            }

            RenderTexture[] all = Resources.FindObjectsOfTypeAll<RenderTexture>();
            int released = 0;
            int kept = 0;
            for (int i = 0; i < all.Length; i++)
            {
                RenderTexture rt = all[i];
                if (rt == null || !rt.IsCreated())
                    continue;
                rt.Release();
                released++;
            }

            for (int i = 0; i < all.Length; i++)
            {
                RenderTexture rt = all[i];
                if (rt != null && rt.IsCreated())
                    kept++;
            }

            Debug.Log(
                "[IslandGpuLeave] released RT " + released + "/" + all.Length
                + " keptCreated=" + kept);
        }

        static void Release()
        {
            CustomRenderPipeline.ReleasePinnedGlobals();
            LightingPass.Cleanup();
            OilPlanarReflection.ReleaseForLeave();
            OilTerrainGrassBinder.Active?.ReleaseGpu();
            if (IslandSceneComponent.Active?.Sea is OilOceanSurface ocean)
                ocean.ReleaseGpuMesh();
            OilRainFlipbookDriver.ReleaseSession();
            ColorLUTPass.ReleasePersistent();
        }

        static void ReleaseCurtain()
        {
            OilRainStreakQuad.ReleaseForLeave();
            OilSkyboxTime.ClearFogCanvasGlobal();
            OilSkyboxTime.ClearCelestialTexturesGlobal();
            OilCloudLayer.ReleaseSharedMeshes();
            OilTerrainGrassBinder.ReleaseStubs();
        }
    }
}
