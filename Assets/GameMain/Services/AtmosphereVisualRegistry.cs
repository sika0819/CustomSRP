using UnityEngine;

namespace Game
{
    /// <summary>
    /// 转发到 <see cref="IslandSceneComponent"/>。旧调用点继续用这个类型，名单只有一份。
    /// </summary>
    public static class AtmosphereVisualRegistry
    {
        static IslandSceneComponent Scene => IslandSceneComponent.Active;

        public static Component Skybox => Scene != null ? Scene.Skybox : null;

        public static IAtmosphereVisualDriver Driver => Scene != null ? Scene.Driver : null;

        public static void RegisterSkybox(Component skybox) => Scene?.RegisterSkybox(skybox);

        public static void UnregisterSkybox(Component skybox) => Scene?.UnregisterSkybox(skybox);

        public static void RegisterDriver(IAtmosphereVisualDriver driver) => Scene?.RegisterDriver(driver);

        public static void UnregisterDriver(IAtmosphereVisualDriver driver) =>
            Scene?.UnregisterDriver(driver);

        public static void Clear() => Scene?.ClearAtmosphere();
    }
}
