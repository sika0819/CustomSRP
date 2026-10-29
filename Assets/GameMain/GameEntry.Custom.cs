using UnityEngine;
using YooAsset;

namespace Game
{
    public partial class GameEntry
    {
        /// <summary>本轮进岛的句柄袋。卸场景成功后放开并丢掉，下一轮重新建。</summary>
        public static IslandLeaveHandles LeaveHandles { get; private set; }

        public static void OpenLeaveHandles()
        {
            if (LeaveHandles != null)
                return;
            LeaveHandles = new IslandLeaveHandles();
        }

        public static void CloseLeaveHandles()
        {
            IslandLeaveHandles bag = LeaveHandles;
            LeaveHandles = null;
            bag?.ReleaseAll();
        }

        public static void TrackLeaveHandle(AssetHandle handle)
        {
            if (handle == null)
                return;
            if (LeaveHandles != null)
            {
                LeaveHandles.Track(handle);
                return;
            }

            // 这一轮已经丢掉。立刻放开，避免句柄挂到下一次登录。
            Debug.LogWarning("[IslandLeaveHandles] track after visit closed — release now");
            if (handle.IsValid)
                handle.Release();
        }

        public static void ReleaseLeaveHandle(AssetHandle handle)
        {
            if (handle == null)
                return;
            if (LeaveHandles != null)
            {
                LeaveHandles.ReleaseNow(handle);
                return;
            }

            if (handle.IsValid)
                handle.Release();
        }

        public static IslandSceneComponent IslandScene { get; private set; }
        public static WeatherService Weather { get; internal set; }
        public static PresetService Presets { get; internal set; }
        public static AtmospherePropService AtmosphereProps { get; internal set; }
        /// <summary>Playlist facade over <see cref="Sound"/> Music group (replaces AudioSource MusicService).</summary>
        public static MusicPlaylist Music { get; private set; }
        public static IslandWorldService World { get; internal set; }
        public static IslandGrassService Grass { get; internal set; }
        public static EnvTreeBillboardService Billboards { get; internal set; }
        public static EnvMapItemExtras MapItemExtras { get; internal set; }
        /// <summary>地形实例创建之后才有。贴地只读这份，不扫场景。</summary>
        public static EnvGroundFit GroundFit { get; private set; }
        /// <summary>天空盒 OnEnable 登记。天气推送读这份，不扫场景。</summary>
        public static IAtmosphereVisualDriver Atmosphere => AtmosphereVisualRegistry.Driver;

        /// <summary>
        /// 框架组件 Awake 时已注册。预制体上还没有时，挂到 Base 同一个物体上。
        /// 必须在其他物体 OnEnable 之前完成，所以从 GameEntry.Awake 调用。
        /// </summary>
        public static void EnsureIslandSceneComponent()
        {
            IslandScene = UnityGameFramework.Runtime.GameEntry.GetComponent<IslandSceneComponent>();
            if (IslandScene != null)
                return;

            UnityGameFramework.Runtime.BaseComponent host =
                UnityGameFramework.Runtime.GameEntry.GetComponent<UnityGameFramework.Runtime.BaseComponent>();
            if (host == null)
                return;

            IslandScene = host.gameObject.AddComponent<IslandSceneComponent>();
        }

        static void InitCustomComponents()
        {
            EnsureIslandSceneComponent();
            // 天气 / 预设 / 世界 / 草地跟着 Island 场景创建，不在 Launch 上常驻。
            Music = MusicPlaylist.Instance;
            Music.ApplySavedVolumes();
            MusicCatalog.LoadAsync();
            DevicePresetShotHelper.Ensure();
            EnsureEntityGroups();
        }

        /// <summary>
        /// 岛屿场景已成为 Active Scene 之后调用。新建的服务落在这张场景里，卸载时一起销毁。
        /// </summary>
        public static void BindIslandServices()
        {
            Weather = WeatherService.Ensure();
            Presets = PresetService.Ensure();
            AtmosphereProps = AtmospherePropService.Ensure();
            World = IslandWorldService.Ensure();
            Grass = IslandGrassService.Ensure();
            Billboards = EnvTreeBillboardService.Ensure();
            MapItemExtras = EnvMapItemExtras.Ensure();
            IslandAudioSession.Ensure();
        }

        /// <summary>
        /// 地形预制体实例化之后挂上贴地。组件跟着地形一起销毁。
        /// </summary>
        public static void BindGroundFit(GameObject terrainRoot)
        {
            if (terrainRoot == null)
                return;

            EnvGroundFit fit = terrainRoot.GetComponent<EnvGroundFit>();
            if (fit == null)
                fit = terrainRoot.AddComponent<EnvGroundFit>();
            fit.Bind(terrainRoot);
            GroundFit = fit;
        }

        public static void UnbindGroundFit()
        {
            EnvGroundFit fit = GroundFit;
            GroundFit = null;
            if (fit != null)
                fit.Clear();
        }

        /// <summary>Procedure 进岛前确保 World 服务已挂好（热重载后可能为空）。</summary>
        public static void InitWorldService()
        {
            World = IslandWorldService.Ensure();
            Grass = IslandGrassService.Ensure();
        }

        /// <summary>
        /// 岛屿场景已经卸载。服务对象已销毁，这里只丢掉静态引用。
        /// </summary>
        public static void DetachIslandServices()
        {
            Weather = null;
            Presets = null;
            AtmosphereProps = null;
            World = null;
            Grass = null;
            Billboards = null;
            MapItemExtras = null;
            UnbindGroundFit();
            IslandScene?.Clear();
        }

        static void ClearCustomComponents()
        {
            Grass?.Release();
            Grass = null;
            World?.Release();
            World = null;
            Weather = null;
            Presets = null;
            AtmosphereProps = null;
            Billboards = null;
            MapItemExtras = null;
            UnbindGroundFit();
            IslandScene?.Clear();
            IslandScene = null;
            Music = null;
            CloseLeaveHandles();
        }

        public static void EnsureEntityGroups()
        {
            if (Entity == null)
                return;

            EnsureEntityGroup("Environment");
            EnsureEntityGroup("FX");
        }

        static void EnsureEntityGroup(string name)
        {
            if (!Entity.HasEntityGroup(name))
                Entity.AddEntityGroup(name, 60f, 16, 60f, 0);
        }
    }
}
