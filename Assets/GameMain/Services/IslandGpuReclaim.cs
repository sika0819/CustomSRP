using System;

namespace Game
{
    /// <summary>
    /// 退登后主相机只清屏加加载 UI，不再分配整屏颜色、深度和阴影。
    /// 岛上的 GPU 对象由 <see cref="ReleaseSession"/> 的订阅者在场景还在时释放。
    /// </summary>
    public static class IslandGpuReclaim
    {
        public static bool CoverOnly { get; private set; }

        /// <summary>Assembly-CSharp 订阅。Game 程序集看不到管线类型。</summary>
        public static event Action ReleaseSession;

        /// <summary>回收协程结束后清 Render Graph 纹理池。</summary>
        public static event Action FlushPool;

        /// <summary>Island 场景已经卸掉。Launch 上残留的物体在这里拆。</summary>
        public static event Action SceneUnloaded;

        public static void Begin()
        {
            CoverOnly = true;
            EnvItemStreamerLite.ReleaseBillboardsImmediate();
            EnvNativeInstancing.ReleaseForLeave();
            FireController.ReleaseRuntimeMaterial();
            ReleaseSession?.Invoke();
            // 立刻清 RenderGraph 池。等 UnloadUnused 再 Flush 时，CoverOnly 的 EndFrame
            // 会把上一帧整屏/阴影纹理一直捂在池里，Adreno kgsl 还不回去。
            FlushGpuPool();
        }

        public static void End() => CoverOnly = false;

        public static void NotifySceneUnloaded()
        {
            SceneUnloaded?.Invoke();
            FlushGpuPool();
        }

        public static void FlushGpuPool() => FlushPool?.Invoke();
    }
}
