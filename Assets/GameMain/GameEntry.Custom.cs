namespace Game
{
    public partial class GameEntry
    {
        public static WeatherService Weather { get; private set; }
        public static PresetService Presets { get; private set; }
        public static AtmospherePropService AtmosphereProps { get; private set; }
        public static MusicService Music { get; private set; }
        public static IslandWorldService World { get; private set; }
        public static IslandGrassService Grass { get; private set; }

        static void InitCustomComponents()
        {
            // Prefer scene / previous-session instances; Ensure() creates DontDestroyOnLoad hosts.
            Weather = WeatherService.Ensure();
            Presets = PresetService.Ensure();
            AtmosphereProps = AtmospherePropService.Ensure();
            Music = MusicService.Ensure();
            World = IslandWorldService.Ensure();
            Grass = IslandGrassService.Ensure();
            DevicePresetShotHelper.Ensure();
            EnsureEntityGroups();
        }

        /// <summary>Procedure 进岛前确保 World 服务已挂好（热重载后可能为空）。</summary>
        public static void InitWorldService()
        {
            World = IslandWorldService.Ensure();
            Grass = IslandGrassService.Ensure();
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
            Music = null;
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
