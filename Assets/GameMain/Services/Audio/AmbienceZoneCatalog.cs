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

        // 可听 / 3D radius：对齐 Boluo WanosAudio.prefab WanosObject.radius + SpatialAmbienceRange。
        public const float RiverAttenuationMeters = 20f;
        public const float OceanAttenuationMeters = 60f;
        public const float LakeAttenuationMeters = 20f;
        public const float CampfireAttenuationMeters = 30f;
        public const float WaterfallBigAttenuationMeters = 50f;
        public const float WaterfallSmallAttenuationMeters = 72f;
        /// <summary>Boluo Forest_Audio SphereCollider：3D 半径 150 才是进出区，曲线 160 只衰减。</summary>
        public const float ForestEnterMeters = 150f;
        /// <summary>Boluo <c>NearTriggerMeters</c>：刚出球仍贴表面时继续播。</summary>
        public const float ForestNearSurfaceMeters = 12f;
        /// <summary>Boluo <c>MinTriggerExitGraceSeconds</c>：离开球体至少再等这么久才 Stop。</summary>
        public const float ForestExitGraceSeconds = 2f;
        public const float ForestExitMeters = ForestEnterMeters;
        /// <summary>Boluo GrassLand_Audio WanosObject.radius。</summary>
        public const float GrasslandAttenuationMeters = 150f;
        /// <summary>Boluo GrassLand_S_Audio WanosObject.radius（北岸小草地）。</summary>
        public const float GrasslandSecondaryAttenuationMeters = 60f;
        /// <summary>Boluo Plateau_Audio WanosObject.radius。</summary>
        public const float PlateauAttenuationMeters = 316f;
        /// <summary>点源（河/岸/瀑）进出区滞回；biome 用固定 Enter/Exit，不用此系数。</summary>
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
            Grassland("GrassLand_Audio", 419.46387f, 41.16188f, 362.95004f, GrasslandAttenuationMeters),
            Grassland("GrassLand_S_Audio", 503.9f, 17.36f, 667.1f, GrasslandSecondaryAttenuationMeters),
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
                    small ? WaterfallSmallAttenuationMeters : WaterfallBigAttenuationMeters);
                spec.UseXzDistance = false;
                spec.RiverCatalogIndex = catalogIndex;
                spec.RiverMeshName = prefab;
                return true;
            }

            if (prefab.StartsWith("River_", System.StringComparison.Ordinal))
            {
                spec = Point(
                    $"river_{catalogIndex}_{prefab}",
                    AmbienceEmitterKind.River,
                    "River_ADD",
                    "Play_River_ADD",
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
            add(Native("River_04", AmbienceEmitterKind.Waterfall, new Vector3(352.2f, 64.2f, 435.9f), true));
        }

        static AmbienceEmitterSpec Native(string name, AmbienceEmitterKind kind, Vector3 pos, bool waterfall)
        {
            var spec = Point(
                waterfall ? $"waterfall_native_{name}" : $"river_native_{name}",
                kind,
                waterfall ? "Waterfall_Big_Quad" : "River_ADD",
                waterfall ? "Play_Waterfall_Big_LR" : "Play_River_ADD",
                waterfall ? "Stop_Waterfall_Big" : "Stop_River",
                pos,
                waterfall ? WaterfallBigAttenuationMeters : RiverAttenuationMeters);
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
                ExitRadius = OceanAttenuationMeters * AttenuationExitScale,
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
                EnterRadius = PlateauAttenuationMeters,
                ExitRadius = PlateauAttenuationMeters * AttenuationExitScale,
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
            return new AmbienceEmitterSpec
            {
                Id = id,
                Kind = kind,
                Bank = bank,
                PlayEvent = play,
                StopEvent = stop,
                Position = pos,
                EnterRadius = attenuationMeters,
                ExitRadius = attenuationMeters * AttenuationExitScale,
                UseXzDistance = true,
            };
        }
    }
}
