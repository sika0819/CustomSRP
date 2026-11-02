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
        /// <summary>
        /// 内陆爬高后掐海浪（xz 仍可能贴着内湾）。
        /// 贴岸带内（见 <see cref="OceanHeightGateMinShellMeters"/>）不生效，避免堤岸/悬崖路径忽大忽小。
        /// </summary>
        public const float OceanMaxPlayerY = 18f;
        /// <summary>
        /// 水平离岸线小于该值时忽略 <see cref="OceanMaxPlayerY"/>。
        /// 沿海岸走常高于 18m，硬门控会 SoftStop↔Start。
        /// 约 <see cref="OceanAttenuationMeters"/>×0.6（boluo-main Ocean_RD 可听圈放大后同步）。
        /// </summary>
        public const float OceanHeightGateMinShellMeters = 75f;
        /// <summary>海面高度。岸线发射器贴在这层，不跟胖盒中心的 Y。</summary>
        public const float OceanSurfaceY = 5.7f;
        /// <summary>
        /// 最近岸点 sticky 偏置（米）。离散 8m 网格在 Voronoi 边界会抖，
        /// 对齐 Boluo <c>WanosBoxHelper</c> sticky collider。
        /// </summary>
        public const float OceanShoreStickBiasMeters = 6f;
        /// <summary>
        /// 高于海面这么多才算可走陆地（岸线采样 / 走路禁海共用）。
        /// 海平面附近平坦海底不再当路。
        /// </summary>
        public const float OceanLandClearanceMeters = 0.4f;
        public const float LakeMinPlayerY = 20f;
        public const float LakeMaxPlayerY = 50f;

        // 开播 / 停播 / 卸载：一律对齐 boluo-main <c>WanosAudio.prefab</c>。
        // Enter≈CurveForGain radius；Exit≈SphereCollider；Unload≈unloadwaittime。
        // 河/海/瀑距离量「水面/岸线 shell」，不是固定钉点——贴岸贴瀑即可触发。
        /// <summary>
        /// boluo-main <c>River_Sound_1/2/3</c> CurveForGain radius=40
        ///（Sound_0/Rs 为 25；取多数声道 40）。
        /// </summary>
        public const float RiverAttenuationMeters = 40f;
        /// <summary>boluo-main <c>River_Sound_0</c> SphereCollider：停播球 60。</summary>
        public const float RiverExitMeters = 60f;
        /// <summary>boluo-main <c>River_Sound_0</c> unloadwaittime=0（走路 KeepBank）。</summary>
        public const float RiverUnloadDelaySeconds = 0f;
        /// <summary>
        /// boluo-main <c>Ocean_RD</c> CurveForGain 远端 ≈125。
        /// 整段海岸 shell 内可听，不必走到 Ocean_Audio 钉点。
        /// </summary>
        public const float OceanAttenuationMeters = 125f;
        /// <summary>boluo-main <c>Ocean_RD</c> SphereCollider：停播球 ≈167。</summary>
        public const float OceanExitMeters = 166.97f;
        /// <summary>boluo-main <c>Ocean_RD</c> unloadwaittime=0（走路 KeepBank）。</summary>
        public const float OceanUnloadDelaySeconds = 0f;
        /// <summary>
        /// Boluo <c>NearTriggerMeters</c>：物理已空但人仍贴桥/岸时禁止立刻 Stop/UnLoad。
        /// Forest / 草地出球宽限共用。河/海停播已用 SphereCollider，不再叠进 Exit。
        /// </summary>
        public const float NearSurfaceMeters = 12f;
        /// <summary>近岸海时间宽限用；Exit 已是 SphereCollider，不再加进停播半径。</summary>
        public const float OceanNearSurfaceMeters = NearSurfaceMeters;
        /// <summary>boluo-main <c>lake_0</c> CurveForGain 远端 ≈48m。</summary>
        public const float LakeAttenuationMeters = 48f;
        /// <summary>boluo-main <c>Lake_Audio</c> SphereCollider：停播球 ≈62。</summary>
        public const float LakeExitMeters = 62.21f;
        /// <summary>boluo-main <c>Lake_Audio</c> unloadwaittime=2。</summary>
        public const float LakeUnloadDelaySeconds = 2f;
        /// <summary>boluo-main <c>CampFire_0/1_Audio</c> CurveForGain radius=30。</summary>
        public const float CampfireAttenuationMeters = 30f;
        /// <summary>boluo-main <c>CampFire_0/1_Audio</c> SphereCollider：停播球 50。</summary>
        public const float CampfireExitMeters = 50f;
        /// <summary>
        /// boluo-main <c>WaterFall_Sound_Big_1</c> radius=80（Big_0 为 50；取较大声道，
        /// 观瀑点 / 瀑边壳距都能进区，不必踩到 Big_0 钉点）。
        /// </summary>
        public const float WaterfallBigEnterMeters = 80f;
        /// <summary>boluo-main <c>WaterFall_Audio</c> SphereCollider：停播球 117.8。</summary>
        public const float WaterfallBigExitMeters = 117.8f;
        /// <summary>boluo-main <c>WaterFall_Audio</c> unloadwaittime=2。</summary>
        public const float WaterfallBigUnloadDelaySeconds = 2f;
        /// <summary>
        /// 大瀑在篝火旁的静音圈。产品要火边同听河/瀑，保持 0（Boluo 原值 22）。
        /// </summary>
        public const float WaterfallCampfireMuteMeters = 0f;
        /// <summary>boluo-main <c>WaterFall_Sound_Small_*</c> / <c>WaterFall_S_Audio</c> radius=50。</summary>
        public const float WaterfallSmallEnterMeters = 50f;
        /// <summary>boluo-main 小瀑无更大停播球：Exit=Enter。</summary>
        public const float WaterfallSmallExitMeters = 50f;
        /// <summary>boluo-main <c>WaterFall_S_Audio</c> unloadwaittime=0。</summary>
        public const float WaterfallSmallUnloadDelaySeconds = 0f;
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
        /// <summary>boluo-main <c>CampFire_*_BankLoader</c> unloadwaittime=5。</summary>
        public const float CampfireUnloadDelaySeconds = 5f;
        /// <summary>无显式 Sphere 时的点源进出滞回兜底；河/海/湖/火/瀑已用 prefab 球。</summary>
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
            // 观瀑：boluo-main WaterFall_Sound_Big_0/1 双落点（见 AmbienceQuadChannelLayout）；
            // 可听距用特征∪瀑面壳∪瀑盆河，Enter=80 盖观景点与瀑边。
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
                // Boluo DistanceToWaterfallFeature：水平距离；发射器仍钉在水面最近点（Y 保留）。
                spec.UseXzDistance = true;
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
                    RiverAttenuationMeters,
                    RiverExitMeters);
                // Boluo HorizontalRibbonDistance：忽略桥面高度；3D 会把站桥判成区外。
                spec.UseXzDistance = true;
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
                    CampfireAttenuationMeters,
                    CampfireExitMeters);
                // 水平距离：下坡时 3D 会先出可听圈，桥面/坡上仍应算在火边。
                spec.UseXzDistance = true;
                return true;
            }

            return false;
        }

        public static void AppendNativeRivers(System.Action<AmbienceEmitterSpec> add)
        {
            if (add == null)
                return;
            // 北段河床网格原点（本工程 InlandWater）；boluo-main 无对应 River_Sound 钉点。
            add(Native("River_01", AmbienceEmitterKind.River, new Vector3(238.85437f, 101.538124f, 637.07263f), false));
            // boluo-main River_Audio 跟点中心（主河道）。
            add(Native("River_02", AmbienceEmitterKind.River, new Vector3(424.6952f, 68.49596f, 455.1803f), false));
            // boluo-main River_3。
            add(Native("River_03", AmbienceEmitterKind.River, new Vector3(336.0276f, 84.09204f, 418.66925f), false));
            // Boluo WaterFall_Audio 原点；Big_0/Big_1 相对偏移在 AmbienceQuadChannelLayout。
            add(Native(
                "WaterFall_Audio",
                AmbienceEmitterKind.Waterfall,
                AmbienceQuadChannelLayout.WaterfallBigRoot,
                true));
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
                    RiverAttenuationMeters,
                    RiverExitMeters);
            // 河/瀑：水平距离（桥面/观景点高度不得挤出可听圈）。Point 默认已是 true。
            spec.UseXzDistance = true;
            spec.RiverMeshName = name;
            return spec;
        }

        public static bool IsWaterfallPrefab(string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
                return false;
            if (prefab.Equals("River_04", System.StringComparison.Ordinal))
                return true;
            // Mist 是湖边视觉雾，不是瀑音特征：当成瀑布会把最近点拽到湖侧，
            // 再被湖↔瀑互斥掐掉 → 路过瀑布「没声」。
            if (prefab.IndexOf("WaterfallMist", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
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
            int sticky = -1;
            return TryNearestOceanShore(worldPos, ref sticky, out shorePoint, out shellDist);
        }

        /// <summary>
        /// 带 sticky 的最近岸点：抑制 8m 网格 Voronoi 抖；邻点线段投影让沿海岸距离连续。
        /// <paramref name="stickyIndex"/> 传入上次命中下标（无则 -1），成功后写回。
        /// </summary>
        public static bool TryNearestOceanShore(
            Vector3 worldPos, ref int stickyIndex, out Vector3 shorePoint, out float shellDist)
        {
            shorePoint = worldPos;
            shellDist = float.PositiveInfinity;
            if (_liveShoreCount <= 0)
            {
                stickyIndex = -1;
                return false;
            }

            float bestSq = float.PositiveInfinity;
            int best = -1;
            for (int i = 0; i < _liveShoreCount; i++)
            {
                float sq = HorizontalSq(worldPos, _liveShore[i]);
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                best = i;
            }

            if (best < 0)
            {
                stickyIndex = -1;
                return false;
            }

            // Sticky：旧点只要还在偏置内，就别跳到隔壁采样点（发射器沿岸来回甩）。
            if ((uint)stickyIndex < (uint)_liveShoreCount)
            {
                float stickySq = HorizontalSq(worldPos, _liveShore[stickyIndex]);
                float bias = OceanShoreStickBiasMeters;
                if (stickySq <= bestSq + bias * bias)
                {
                    best = stickyIndex;
                    bestSq = stickySq;
                }
            }

            stickyIndex = best;
            Vector3 bestPt = _liveShore[best];
            bestPt.y = OceanSurfaceY;

            // 邻点线段投影：沿连续岸边走时 shell 不再在格子中心之间阶跃。
            const float neighborMax = 12f;
            float neighborMaxSq = neighborMax * neighborMax;
            float bestDist = Mathf.Sqrt(bestSq);
            Vector3 bestShore = bestPt;
            for (int i = 0; i < _liveShoreCount; i++)
            {
                if (i == best)
                    continue;
                if (HorizontalSq(bestPt, _liveShore[i]) > neighborMaxSq)
                    continue;
                Vector3 other = _liveShore[i];
                other.y = OceanSurfaceY;
                if (!TryProjectToSegmentXZ(worldPos, bestPt, other, out Vector3 proj, out float d))
                    continue;
                if (d >= bestDist)
                    continue;
                bestDist = d;
                bestShore = proj;
            }

            shorePoint = bestShore;
            shorePoint.y = OceanSurfaceY;
            shellDist = bestDist;
            return true;
        }

        static float HorizontalSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        static bool TryProjectToSegmentXZ(
            Vector3 listener, Vector3 a, Vector3 b, out Vector3 proj, out float dist)
        {
            float abx = b.x - a.x;
            float abz = b.z - a.z;
            float abLenSq = abx * abx + abz * abz;
            float t = 0f;
            if (abLenSq > 1e-6f)
            {
                float apx = listener.x - a.x;
                float apz = listener.z - a.z;
                t = Mathf.Clamp01((apx * abx + apz * abz) / abLenSq);
            }

            proj = new Vector3(a.x + abx * t, OceanSurfaceY, a.z + abz * t);
            float dx = listener.x - proj.x;
            float dz = listener.z - proj.z;
            dist = Mathf.Sqrt(dx * dx + dz * dz);
            return true;
        }

        /// <summary>贴岸带内不因绝对高度掐海浪；更远才用 <see cref="OceanMaxPlayerY"/>。</summary>
        public static bool IsOceanMutedByPlayerHeight(Vector3 listener, float shellDistMeters)
        {
            if (listener.y <= OceanMaxPlayerY)
                return false;
            return shellDistMeters > OceanHeightGateMinShellMeters;
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
                ExitRadius = OceanExitMeters,
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
                ExitRadius = LakeExitMeters,
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
