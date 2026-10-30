using System.Collections.Generic;
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
        /// <summary>
        /// 观景点等固定世界锚：River/Waterfall 不走 <c>WaterSurfaceProbe</c>，
        /// 距离按 <see cref="Position"/>；湖↔瀑互斥也不压制这类床位。
        /// </summary>
        public bool FixedWorldAnchor;
        public int RiverCatalogIndex = -1;
        public string RiverMeshName;
    }

    /// <summary>Static shore/forest/lake anchors + MapItem river/campfire/waterfall.</summary>
    public static class AmbienceZoneCatalog
    {
        public const float OceanMaxPlayerY = 18f;
        /// <summary>海面高度。岸线发射器贴在这层，不跟胖盒中心的 Y。</summary>
        public const float OceanSurfaceY = 5.7f;
        /// <summary>
        /// 高于海面这么多才算可走陆地（岸线采样 / 走路禁海共用）。
        /// 海平面附近平坦海底不再当路。
        /// </summary>
        public const float OceanLandClearanceMeters = 0.4f;
        public const float LakeMinPlayerY = 20f;
        public const float LakeMaxPlayerY = 50f;

        // 可听 / 进出半径：对齐「3D区域声景」听感常量（催加载体积本工程合并进 Enter）。
        /// <summary>河开播半径（贴着河岸约几米必进区；原 20 偏紧）。</summary>
        public const float RiverAttenuationMeters = 30f;
        public const float OceanAttenuationMeters = 60f;
        /// <summary>
        /// Boluo <c>NearTriggerMeters</c>：物理已空但人仍贴桥/岸时禁止立刻 Stop/UnLoad。
        /// 近岸海 / Forest / 草地出球宽限共用。
        /// </summary>
        public const float NearSurfaceMeters = 12f;
        /// <summary>近岸海已播后再加约 12m 宽限（河不加 near 进停播半径；宽限时间仍用 ExitGrace）。</summary>
        public const float OceanNearSurfaceMeters = NearSurfaceMeters;
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
        public const float ForestNearSurfaceMeters = NearSurfaceMeters;
        /// <summary>
        /// Boluo <c>MinTriggerExitGraceSeconds</c>：离开可听区至少再等这么久才 Stop。
        /// 须盖住桥面 CharacterController 0.5–1.5s 的 Exit→Enter 抖动。
        /// </summary>
        public const float ExitGraceSeconds = 2f;
        public const float ForestExitGraceSeconds = ExitGraceSeconds;
        public const float ForestExitMeters = ForestEnterMeters;
        public const float ForestUnloadDelaySeconds = 3f;
        /// <summary>草地主区触发球（增益曲线远端仍 150）。</summary>
        public const float GrasslandEnterMeters = 180f;
        /// <summary>增益曲线远端 / 空间化。</summary>
        public const float GrasslandAttenuationMeters = 150f;
        /// <summary>禁止 0：共享 Boluodao_Grassland 一侧 Exit 立刻 UnLoad 会掐另一侧。</summary>
        public const float GrasslandUnloadDelaySeconds = 2f;
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
        /// 河流仍用原工程点位。海岸可听带是地形外海水线（见 <see cref="RebuildLiveShoreline"/>）。
        /// </summary>
        public static readonly AmbienceEmitterSpec[] StaticEmitters =
        {
            Ocean("ocean_shore", 610.0767f, OceanSurfaceY, 706.1781f),
            ForestBed("Forest_Audio", 561.7f, 46.78f, 517.84f),
            Lake("Lake_Audio", 461.1f, 32.77f, 461.12f),
            // 湖在岛 preset 7 那一侧（出生点 504.67 / 463.85），补一个锚点，
            // 玩家走到湖边才听得到湖面声。
            Lake("Lake_Audio_Preset7", 504.6723f, 38.82f, 463.8487f),
            // 灵感唤醒湖边观瀑点：落点离 WaterfallSplash ~86m，探针瀑进不了 42m 圈。
            // 固定锚在观景点朝瀑方向约 20m，开播用 catalog 距离（见 FixedWorldAnchor）。
            ScenicWaterfallOverlook(),
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

        /// <summary>地形外海水线上的采样点。0 表示地形还没采样，海岸距离不可用。</summary>
        public static int LiveShoreCount => _liveShoreCount;

        public static Vector3 LiveShorePoint(int index) => _liveShore[index];

        public static void ClearLiveShoreline()
        {
            _liveShoreCount = 0;
        }

        /// <summary>
        /// 用地形高度把与地图外海连通的水线采样下来。
        /// 内陆封闭水面不进这圈，避免湖面被当成海。
        /// </summary>
        public static int RebuildLiveShoreline(Terrain terrain)
        {
            _liveShoreCount = 0;
            if (terrain == null || terrain.terrainData == null)
                return 0;

            Vector3 size = terrain.terrainData.size;
            Vector3 origin = terrain.transform.position;
            const float step = 8f;
            int nx = Mathf.Clamp(Mathf.CeilToInt(size.x / step), 2, 256);
            int nz = Mathf.Clamp(Mathf.CeilToInt(size.z / step), 2, 256);
            float cellX = size.x / nx;
            float cellZ = size.z / nz;
            int cellCount = nx * nz;
            var land = new bool[cellCount];
            float landY = OceanSurfaceY + OceanLandClearanceMeters;
            for (int ix = 0; ix < nx; ix++)
            {
                float x = origin.x + (ix + 0.5f) * cellX;
                for (int iz = 0; iz < nz; iz++)
                {
                    float z = origin.z + (iz + 0.5f) * cellZ;
                    float h = terrain.SampleHeight(new Vector3(x, 0f, z));
                    land[ix * nz + iz] = h > landY;
                }
            }

            var sea = new bool[cellCount];
            var queue = new int[cellCount];
            int qt = 0;
            for (int ix = 0; ix < nx; ix++)
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    if (ix != 0 && iz != 0 && ix != nx - 1 && iz != nz - 1)
                        continue;
                    int id = ix * nz + iz;
                    if (land[id] || sea[id])
                        continue;
                    sea[id] = true;
                    queue[qt++] = id;
                }
            }

            int qh = 0;
            while (qh < qt)
            {
                int id = queue[qh++];
                int ix = id / nz;
                int iz = id - ix * nz;
                EnqueueSea(ix - 1, iz, nx, nz, land, sea, queue, ref qt);
                EnqueueSea(ix + 1, iz, nx, nz, land, sea, queue, ref qt);
                EnqueueSea(ix, iz - 1, nx, nz, land, sea, queue, ref qt);
                EnqueueSea(ix, iz + 1, nx, nz, land, sea, queue, ref qt);
            }

            var pts = new List<Vector3>(512);
            for (int ix = 0; ix < nx; ix++)
            {
                for (int iz = 0; iz < nz; iz++)
                {
                    int id = ix * nz + iz;
                    if (!sea[id])
                        continue;
                    bool edge = (ix > 0 && land[(ix - 1) * nz + iz])
                        || (ix + 1 < nx && land[(ix + 1) * nz + iz])
                        || (iz > 0 && land[ix * nz + iz - 1])
                        || (iz + 1 < nz && land[ix * nz + iz + 1]);
                    if (!edge)
                        continue;
                    pts.Add(new Vector3(
                        origin.x + (ix + 0.5f) * cellX,
                        OceanSurfaceY,
                        origin.z + (iz + 0.5f) * cellZ));
                }
            }

            _liveShore = pts.ToArray();
            _liveShoreCount = _liveShore.Length;
            return _liveShoreCount;
        }

        static Vector3[] _liveShore = System.Array.Empty<Vector3>();
        static int _liveShoreCount;

        static void EnqueueSea(
            int ix, int iz, int nx, int nz, bool[] land, bool[] sea, int[] queue, ref int qt)
        {
            if ((uint)ix >= (uint)nx || (uint)iz >= (uint)nz)
                return;
            int id = ix * nz + iz;
            if (land[id] || sea[id])
                return;
            sea[id] = true;
            queue[qt++] = id;
        }

        /// <summary>
        /// 听者到最近外海水线的水平距离，以及水线上的落点（Y 贴海面）。
        /// 地形还没采样时返回 false，海浪先不开。
        /// </summary>
        public static bool TryNearestOceanShore(Vector3 worldPos, out Vector3 shorePoint, out float shellDist)
        {
            shorePoint = worldPos;
            shellDist = float.PositiveInfinity;
            if (_liveShoreCount <= 0)
                return false;

            float bestSq = float.PositiveInfinity;
            int best = -1;
            for (int i = 0; i < _liveShoreCount; i++)
            {
                Vector3 p = _liveShore[i];
                float dx = worldPos.x - p.x;
                float dz = worldPos.z - p.z;
                float sq = dx * dx + dz * dz;
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                best = i;
            }

            if (best < 0)
                return false;
            shellDist = Mathf.Sqrt(bestSq);
            shorePoint = _liveShore[best];
            shorePoint.y = OceanSurfaceY;
            return true;
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

        /// <summary>
        /// Preset7 湖边观瀑：锚在观景点（504.67）朝 <c>WaterfallSplash</c> 约 20m，
        /// 站在灵感唤醒落点即可进大瀑 Enter；与探针瀑床位共用 bank，由最近者独占 Post。
        /// </summary>
        static AmbienceEmitterSpec ScenicWaterfallOverlook()
        {
            // overlook (504.67, 38.82, 463.85) → splash (423.81, 66.34, 457.4), |d|≈85.7m
            var pos = new Vector3(485.79f, 45.25f, 462.34f);
            var spec = Point(
                "waterfall_scenic_Preset7Overlook",
                AmbienceEmitterKind.Waterfall,
                "Waterfall_Big_Quad",
                "Play_Waterfall_Big_LR",
                "Stop_Waterfall_Big",
                pos,
                WaterfallBigEnterMeters,
                WaterfallBigExitMeters);
            spec.UseXzDistance = true;
            spec.FixedWorldAnchor = true;
            return spec;
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
