using UnityEngine;

namespace Game
{
    public enum AmbienceEmitterKind
    {
        Ocean,
        River,
        Forest,
        Campfire,
        Waterfall,
        Lake,
        Grassland,
        Plateau,
    }

    /// <summary>One 3D ambience source with enter/exit hysteresis radii.</summary>
    public sealed class AmbienceEmitterSpec
    {
        public string Id;
        public AmbienceEmitterKind Kind;
        public string Bank;
        public string PlayEvent;
        public string StopEvent;
        public Vector3 Position;
        public float EnterRadius;
        public float ExitRadius;
        public bool UseXzDistance = true;
        public bool FollowForestCentroid;
        public int RiverCatalogIndex = -1;
        public string RiverMeshName;
    }

    /// <summary>Static shore/forest/lake anchors + MapItem river/campfire/waterfall.</summary>
    public static class AmbienceZoneCatalog
    {
        public const float OceanMaxPlayerY = 18f;
        public const float LakeMinPlayerY = 20f;
        public const float LakeMaxPlayerY = 50f;

        // 可听 / 进出半径：对齐「3D区域声景」听感常量（催加载体积本工程合并进 Enter）。
        /// <summary>河开播半径（贴着河岸约几米必进区；原 20 偏紧）。</summary>
        public const float RiverAttenuationMeters = 30f;
        public const float OceanAttenuationMeters = 60f;
        /// <summary>近岸海已播后再加约 12m 宽限（河不加）。</summary>
        public const float OceanNearSurfaceMeters = 12f;
        public const float LakeAttenuationMeters = 20f;
        public const float CampfireAttenuationMeters = 30f;
        /// <summary>大瀑开播半径。</summary>
        public const float WaterfallBigEnterMeters = 42f;
        /// <summary>大瀑停播半径。</summary>
        public const float WaterfallBigExitMeters = 50f;
        /// <summary>大瀑在篝火旁的静音圈。</summary>
        public const float WaterfallCampfireMuteMeters = 22f;
        /// <summary>小瀑开播半径。</summary>
        public const float WaterfallSmallEnterMeters = 50f;
        /// <summary>小瀑停播半径。</summary>
        public const float WaterfallSmallExitMeters = 72f;
        /// <summary>Boluo Forest_Audio SphereCollider：3D 半径 150 才是进出区，曲线 160 只衰减。</summary>
        public const float ForestEnterMeters = 150f;
        /// <summary>Boluo <c>NearTriggerMeters</c>：刚出球仍贴表面时继续播。</summary>
        public const float ForestNearSurfaceMeters = 12f;
        /// <summary>Boluo <c>MinTriggerExitGraceSeconds</c>：离开球体至少再等这么久才 Stop。</summary>
        public const float ForestExitGraceSeconds = 2f;
        public const float ForestExitMeters = ForestEnterMeters;
        public const float ForestUnloadDelaySeconds = 3f;
        /// <summary>草地主区触发球（增益曲线远端仍 150）。</summary>
        public const float GrasslandEnterMeters = 180f;
        /// <summary>增益曲线远端 / 空间化。</summary>
        public const float GrasslandAttenuationMeters = 150f;
        public const float GrasslandUnloadDelaySeconds = 0f;
        /// <summary>草地卫星触发（胶囊约 75；增益远端 60）。</summary>
        public const float GrasslandSecondaryEnterMeters = 75f;
        public const float GrasslandSecondaryAttenuationMeters = 60f;
        public const float GrasslandSecondaryUnloadDelaySeconds = 1f;
        /// <summary>高原触发球约 350；增益曲线远端约 316。</summary>
        public const float PlateauEnterMeters = 350f;
        public const float PlateauAttenuationMeters = 316f;
        public const float PlateauUnloadDelaySeconds = 2f;
        public const float CampfireUnloadDelaySeconds = 5f;
        /// <summary>点源（河/岸）进出区滞回；瀑用显式 Enter/Exit；biome 用固定 Enter/Exit。</summary>
        public const float AttenuationExitScale = 1.1f;

        /// <summary>
        /// 静态 biome 锚点。Forest / Grassland / Plateau 对齐 Boluo <c>WanosAudio.prefab</c>；
        /// 海岸（Ocean）与河流仍用原工程点位，不随 Boluo Audio 节点改。
        /// </summary>
        public static readonly AmbienceEmitterSpec[] StaticEmitters =
        {
            Ocean("ocean_shore_1", 610.0767f, 5.7f, 706.1781f),
            Ocean("ocean_shore_8", 603.174f, 5.7f, 650.0313f),
            ForestBed("Forest_Audio", 561.7f, 46.78f, 517.84f),
            Lake("Lake_Audio", 461.1f, 32.77f, 461.12f),
            // 湖在岛 preset 7 那一侧（出生点 504.67 / 463.85），补一个锚点，
            // 玩家走到湖边才听得到湖面声。
            Lake("Lake_Audio_Preset7", 504.6723f, 38.82f, 463.8487f),
            Grassland("GrassLand_Audio", 419.46387f, 41.16188f, 362.95004f, GrasslandEnterMeters),
            Grassland("GrassLand_S_Audio", 503.9f, 17.36f, 667.1f, GrasslandSecondaryEnterMeters),
            Plateau("Plateau_Audio", 171.5f, 50.5f, 583.7f),
        };

        public static bool TryCreateFromMapItem(MapItemData item, int catalogIndex, out AmbienceEmitterSpec spec)
        {
            spec = null;
            if (item == null || !item.IsActive)
                return false;

            string prefab = item.PrefabName ?? "";
            string parent = item.Parent ?? "";

            if (IsWaterfallPrefab(prefab))
            {
                bool small = prefab.IndexOf("Small", System.StringComparison.OrdinalIgnoreCase) >= 0;
                spec = Point(
                    $"waterfall_{catalogIndex}_{prefab}",
                    AmbienceEmitterKind.Waterfall,
                    small ? "Waterfall_Small_Quad" : "Waterfall_Big_Quad",
                    small ? "Play_Waterfall_Small_LR" : "Play_Waterfall_Big_LR",
                    small ? "Stop_Waterfall_Small" : "Stop_Waterfall_Big",
                    item.Pos,
                    small ? WaterfallSmallEnterMeters : WaterfallBigEnterMeters,
                    small ? WaterfallSmallExitMeters : WaterfallBigExitMeters);
                spec.UseXzDistance = false;
                spec.RiverCatalogIndex = catalogIndex;
                spec.RiverMeshName = prefab;
                return true;
            }

            if (prefab.StartsWith("River_", System.StringComparison.Ordinal))
            {
                // Boluo 原工程用 River_Quad（L/Ls/R/Rs 四轨），不是一次性的 River_ADD。
                spec = Point(
                    $"river_{catalogIndex}_{prefab}",
                    AmbienceEmitterKind.River,
                    "River_Quad",
                    "Play_River_L",
                    "Stop_River",
                    item.Pos,
                    RiverAttenuationMeters);
                spec.UseXzDistance = false;
                spec.RiverCatalogIndex = catalogIndex;
                spec.RiverMeshName = prefab;
                return true;
            }

            if (prefab == "Huodui" || parent.StartsWith("Huodui"))
            {
                spec = Point(
                    $"campfire_{catalogIndex}",
                    AmbienceEmitterKind.Campfire,
                    "Campfire",
                    "Play_Campfire_Start_Loop",
                    "Stop_Campfire_End",
                    item.Pos,
                    CampfireAttenuationMeters);
                spec.UseXzDistance = false;
                return true;
            }

            return false;
        }

        public static void AppendNativeRivers(System.Action<AmbienceEmitterSpec> add)
        {
            if (add == null)
                return;
            add(Native("River_01", AmbienceEmitterKind.River, new Vector3(238.85437f, 101.538124f, 637.07263f), false));
            add(Native("River_02", AmbienceEmitterKind.River, new Vector3(435.38004f, 22.099998f, 424.85004f), false));
            add(Native("River_03", AmbienceEmitterKind.River, new Vector3(352.2f, 64.2f, 435.9f), false));
            // 瀑音锚在 WaterfallSplash（听感位置），River_04 mesh 原点离飞溅点约 70m，
            // 用 42m Enter 会「贴着瀑布特效也没声」。
            add(Native("WaterfallSplash", AmbienceEmitterKind.Waterfall, new Vector3(423.81f, 66.34f, 457.4f), true));
        }

        static AmbienceEmitterSpec Native(string name, AmbienceEmitterKind kind, Vector3 pos, bool waterfall)
        {
            AmbienceEmitterSpec spec = waterfall
                ? Point(
                    $"waterfall_native_{name}",
                    kind,
                    "Waterfall_Big_Quad",
                    "Play_Waterfall_Big_LR",
                    "Stop_Waterfall_Big",
                    pos,
                    WaterfallBigEnterMeters,
                    WaterfallBigExitMeters)
                : Point(
                    $"river_native_{name}",
                    kind,
                    "River_Quad",
                    "Play_River_L",
                    "Stop_River",
                    pos,
                    RiverAttenuationMeters);
            spec.UseXzDistance = false;
            spec.RiverMeshName = name;
            return spec;
        }

        public static bool IsWaterfallPrefab(string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
                return false;
            if (prefab.Equals("River_04", System.StringComparison.Ordinal))
                return true;
            return prefab.IndexOf("Waterfall", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsForestTree(MapItemData item)
        {
            string parent = item?.Parent;
            return parent == "Lvshu" || parent == "Yeshu" || parent == "Songshu" || parent == "Guanmu";
        }

        static AmbienceEmitterSpec Ocean(string id, float x, float y, float z)
        {
            return new AmbienceEmitterSpec
            {
                Id = id,
                Kind = AmbienceEmitterKind.Ocean,
                Bank = "Ocean_Calm_Quad",
                PlayEvent = "Play_Ocean_Calm_L",
                StopEvent = "Stop_Boluodao_Ocean",
                Position = new Vector3(x, y, z),
                EnterRadius = OceanAttenuationMeters,
                ExitRadius = OceanAttenuationMeters + OceanNearSurfaceMeters,
                UseXzDistance = true,
            };
        }

        static AmbienceEmitterSpec ForestBed(string id, float x, float y, float z)
        {
            return new AmbienceEmitterSpec
            {
                Id = id,
                Kind = AmbienceEmitterKind.Forest,
                Bank = "Boluodao_Forest",
                PlayEvent = "Play_Boluodao_Forest",
                StopEvent = "Stop_Boluodao_Forest",
                Position = new Vector3(x, y, z),
                EnterRadius = ForestEnterMeters,
                ExitRadius = ForestExitMeters,
                UseXzDistance = false,
            };
        }

        static AmbienceEmitterSpec Lake(string id, float x, float y, float z)
        {
            return new AmbienceEmitterSpec
            {
                Id = id,
                Kind = AmbienceEmitterKind.Lake,
                Bank = "Lake_Quad",
                PlayEvent = "Play_Lake_LR",
                StopEvent = "Stop_Lake",
                Position = new Vector3(x, y, z),
                EnterRadius = LakeAttenuationMeters,
                ExitRadius = LakeAttenuationMeters * AttenuationExitScale,
                UseXzDistance = true,
            };
        }

        static AmbienceEmitterSpec Grassland(string id, float x, float y, float z, float radiusMeters)
        {
            return new AmbienceEmitterSpec
            {
                Id = id,
                Kind = AmbienceEmitterKind.Grassland,
                Bank = "Boluodao_Grassland",
                PlayEvent = "Play_Boluodao_Grassland",
                StopEvent = "Stop_Boluodao_Grassland",
                Position = new Vector3(x, y, z),
                EnterRadius = radiusMeters,
                ExitRadius = radiusMeters,
                UseXzDistance = true,
            };
        }

        static AmbienceEmitterSpec Plateau(string id, float x, float y, float z)
        {
            return new AmbienceEmitterSpec
            {
                Id = id,
                Kind = AmbienceEmitterKind.Plateau,
                Bank = "Boluodao_Plateau",
                PlayEvent = "Play_Boluodao_Plateau",
                StopEvent = "Stop_Boluodao_Plateau",
                Position = new Vector3(x, y, z),
                EnterRadius = PlateauEnterMeters,
                ExitRadius = PlateauEnterMeters,
                UseXzDistance = true,
            };
        }

        static AmbienceEmitterSpec Point(
            string id,
            AmbienceEmitterKind kind,
            string bank,
            string play,
            string stop,
            Vector3 pos,
            float attenuationMeters)
        {
            return Point(id, kind, bank, play, stop, pos, attenuationMeters,
                attenuationMeters * AttenuationExitScale);
        }

        static AmbienceEmitterSpec Point(
            string id,
            AmbienceEmitterKind kind,
            string bank,
            string play,
            string stop,
            Vector3 pos,
            float enterMeters,
            float exitMeters)
        {
            return new AmbienceEmitterSpec
            {
                Id = id,
                Kind = kind,
                Bank = bank,
                PlayEvent = play,
                StopEvent = stop,
                Position = pos,
                EnterRadius = enterMeters,
                ExitRadius = exitMeters,
                UseXzDistance = true,
            };
        }
    }
}
