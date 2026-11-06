#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Game;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 按地形上物体实际占地烤草地禁种遮罩。
    /// 树/灌木只禁根系圆盘；帐篷只禁柱脚；有 Collider 的建筑用碰撞体 XZ；
    /// 山体跳过近水平裙边三角，只禁岩面后填洞；其余用 LOD0 mesh XZ。花草/特效不挡。
    /// 篝火在 MapItemExtras，按占地圆盘另盖一圈。
    /// </summary>
    public static class OilTerrainGrassKeepOutBakeTool
    {
        const int Res = OilTerrainGrassKeepOutBake.DefaultRes;
        const string KeepOutFlagRelPath = "Temp/bake_grass_keepout.flag";

        /// <summary>树干底部切片高度（米）与高度比例取较大。</summary>
        const float TreeRootSliceMeters = 0.45f;
        const float TreeRootSliceFrac = 0.08f;
        const float TreeRootMinRadius = 0.22f;
        const float TreeRootMaxRadius = 1.15f;
        const float TreeRootPadMeters = 0.12f;

        // 灌木只清贴地根系；森林灌木丛下仍应看得见草，半径收紧避免成片秃斑。
        const float ShrubRootSliceMeters = 0.28f;
        const float ShrubRootSliceFrac = 0.12f;
        const float ShrubRootMinRadius = 0.12f;
        const float ShrubRootMaxRadius = 0.45f;
        const float ShrubRootPadMeters = 0.05f;

        /// <summary>
        /// 山体 mesh 常带大面积贴地裙边；|法线.y| 高于此值的三角不投影，避免山脚草地被误禁。
        /// </summary>
        const float MountainMaxUpDot = 0.72f;
        const int MountainHoleFillPasses = 16;

        const float TentPoleBaseMeters = 0.4f;
        const float TentPoleClusterCell = 0.35f;
        const float TentPoleRadius = 0.22f;
        const int TentPoleMinVerts = 3;

        enum StampMode
        {
            FullMesh,
            TreeRoot,
            ShrubRoot,
            TentPoles,
            ColliderElseMesh,
            MountainRock,
        }

        [InitializeOnLoadMethod]
        static void WatchKeepOutFlag()
        {
            EditorApplication.update -= PollKeepOutFlag;
            EditorApplication.update += PollKeepOutFlag;
        }

        static void PollKeepOutFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            if (EditorApplication.isPlaying)
                return;

            string flag = Path.GetFullPath(Path.Combine(Application.dataPath, "..", KeepOutFlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }
            Log.Info("[OilTerrainGrass] Flag detected — baking keep-out");
            BakeMenu();
        }

        static readonly HashSet<string> SkipParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Zacao", "Hua", "Zhibei", "texiao", "FX",
        };

        static readonly HashSet<string> TreeParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Songshu", "Lvshu", "Yeshu",
        };

        static readonly HashSet<string> ShrubParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Guanmu",
        };

        struct Lod0Mesh
        {
            public Vector3[] Verts;
            public int[] Tris;
        }

        struct PrefabKeepOut
        {
            public StampMode Mode;
            public Lod0Mesh[] Meshes;
            public ColliderFootprint[] Colliders;
        }

        struct ColliderFootprint
        {
            public bool IsBox;
            /// <summary>Prefab-local 8 个角点（BoxCollider）。</summary>
            public Vector3[] BoxCornersLocal;
            public Lod0Mesh Mesh;
        }

        [MenuItem("CustomSRP/Grass/Bake Keep-Out")]
        public static void BakeMenu() => TryBake(out _);

        [MenuItem("CustomSRP/Grass/Bake Keep-Out", true)]
        static bool Validate() => !EditorApplication.isPlaying;

        public static bool TryBake(out OilTerrainGrassKeepOutBake.Data data)
        {
            data = null;
            if (!TryResolveTerrainBounds(out float originX, out float originZ, out float worldSize))
            {
                Log.Error(
                    "[OilTerrainGrass] KeepOut bake needs Terrain or " +
                    "Assets/Terrain/Island/IslandTerrainData.asset");
                return false;
            }

            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.AllMapItemAssetPath);
            if (text == null)
            {
                Log.Error("[OilTerrainGrass] missing " + SceneAssets.AllMapItemAssetPath);
                return false;
            }

            var items = new List<MapItemData>(2560);
            if (!MapItemData.ReadBinaryTo(text.bytes, items) || items.Count == 0)
            {
                Log.Error("[OilTerrainGrass] AllMapItem empty");
                return false;
            }
            float cell = worldSize / Res;
            var grid = new byte[Res * Res];
            var cache = new Dictionary<string, PrefabKeepOut>(64);
            var worldScratch = Array.Empty<Vector3>();
            int used = 0, skipped = 0, noMesh = 0, missingPrefab = 0;
            int rootN = 0, tentN = 0, colliderN = 0, meshN = 0;

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if ((i & 63) == 0)
                    {
                        EditorUtility.DisplayProgressBar(
                            "Bake Grass Keep-Out",
                            $"{i}/{items.Count}",
                            items.Count > 0 ? (float)i / items.Count : 1f);
                    }

                    MapItemData item = items[i];
                    if (!ShouldKeepOut(item))
                    {
                        skipped++;
                        continue;
                    }

                    string path = SceneAssets.DaoYu1EditorPath(item.Parent, item.PrefabName);
                    if (string.IsNullOrEmpty(path))
                    {
                        skipped++;
                        continue;
                    }

                    if (!cache.TryGetValue(path, out PrefabKeepOut packed))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab == null)
                        {
                            missingPrefab++;
                            cache[path] = default;
                            continue;
                        }

                        packed = BuildPrefabKeepOut(prefab, item.Parent, item.PrefabName);
                        cache[path] = packed;
                    }

                    var trs = Matrix4x4.TRS(item.Pos, Quaternion.Euler(item.Rotation), item.Scale);
                    if (!StampItem(grid, Res, originX, originZ, cell, packed, trs, item.Pos, ref worldScratch,
                            out StampMode usedMode))
                    {
                        noMesh++;
                        continue;
                    }

                    used++;
                    switch (usedMode)
                    {
                        case StampMode.TreeRoot:
                        case StampMode.ShrubRoot:
                            rootN++;
                            break;
                        case StampMode.TentPoles:
                            tentN++;
                            break;
                        case StampMode.ColliderElseMesh:
                            colliderN++;
                            break;
                        default:
                            meshN++;
                            break;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            int huodui = StampHuodui(grid, Res, originX, originZ, cell);

            data = new OilTerrainGrassKeepOutBake.Data
            {
                OriginX = originX,
                OriginZ = originZ,
                WorldSize = worldSize,
                Res = Res,
                Mask = grid,
            };
            // 不再整图 Dilate：禁种严格跟物体实际占地，避免绿地上大片空秃。
            int rivers = StampRiverBanks(data.Mask, Res, originX, originZ, cell);

            byte[] bytes = OilTerrainGrassKeepOutBake.Write(data);
            if (bytes == null || bytes.Length == 0)
            {
                Log.Error("[OilTerrainGrass] KeepOut write failed");
                data = null;
                return false;
            }

            WriteBytes(OilTerrainGrassKeepOutBake.EditorPath, bytes);
            WriteBytes(OilTerrainGrassKeepOutBake.ResourcesPath, bytes);

            int marked = 0;
            for (int i = 0; i < data.Mask.Length; i++)
            {
                if (data.Mask[i] != 0)
                    marked++;
            }

            Log.Info(
                $"[OilTerrainGrass] KeepOut wrote {OilTerrainGrassKeepOutBake.EditorPath} " +
                $"items={used} skip={skipped} noMesh={noMesh} missing={missingPrefab} " +
                $"root={rootN} tent={tentN} collider={colliderN} mesh={meshN} " +
                $"huodui={huodui} rivers={rivers} cells={marked}/{grid.Length} cell={cell:F2}");
            return true;
        }

        static PrefabKeepOut BuildPrefabKeepOut(GameObject prefab, string parent, string prefabName)
        {
            StampMode mode = ResolveStampMode(parent, prefabName);
            Lod0Mesh[] meshes = ExtractLod0(prefab);
            ColliderFootprint[] colliders = null;
            if (mode == StampMode.ColliderElseMesh)
            {
                colliders = ExtractColliders(prefab);
                if (colliders == null || colliders.Length == 0)
                    mode = StampMode.FullMesh;
            }

            return new PrefabKeepOut
            {
                Mode = mode,
                Meshes = meshes,
                Colliders = colliders,
            };
        }

        static StampMode ResolveStampMode(string parent, string prefabName)
        {
            parent = parent ?? string.Empty;
            prefabName = prefabName ?? string.Empty;
            if (TreeParents.Contains(parent))
                return StampMode.TreeRoot;
            if (ShrubParents.Contains(parent))
                return StampMode.ShrubRoot;
            if (prefabName.IndexOf("Mountain", StringComparison.OrdinalIgnoreCase) >= 0)
                return StampMode.MountainRock;
            if (parent.Equals("JianZhu", StringComparison.OrdinalIgnoreCase)
                && prefabName.IndexOf("Tent", StringComparison.OrdinalIgnoreCase) >= 0)
                return StampMode.TentPoles;
            if (parent.Equals("JianZhu", StringComparison.OrdinalIgnoreCase))
                return StampMode.ColliderElseMesh;
            return StampMode.FullMesh;
        }

        static bool StampItem(
            byte[] grid, int res, float originX, float originZ, float cell,
            PrefabKeepOut packed, Matrix4x4 trs, Vector3 itemPos, ref Vector3[] worldScratch,
            out StampMode usedMode)
        {
            usedMode = packed.Mode;
            switch (packed.Mode)
            {
                case StampMode.TreeRoot:
                    return StampRootDisc(
                        grid, res, originX, originZ, cell, packed.Meshes, trs, itemPos,
                        TreeRootSliceMeters, TreeRootSliceFrac,
                        TreeRootMinRadius, TreeRootMaxRadius, TreeRootPadMeters);
                case StampMode.ShrubRoot:
                    return StampRootDisc(
                        grid, res, originX, originZ, cell, packed.Meshes, trs, itemPos,
                        ShrubRootSliceMeters, ShrubRootSliceFrac,
                        ShrubRootMinRadius, ShrubRootMaxRadius, ShrubRootPadMeters);
                case StampMode.TentPoles:
                    return StampTentPoles(grid, res, originX, originZ, cell, packed.Meshes, trs);
                case StampMode.MountainRock:
                    return StampMountainRock(
                        grid, res, originX, originZ, cell, packed.Meshes, trs, ref worldScratch);
                case StampMode.ColliderElseMesh:
                    if (packed.Colliders != null && packed.Colliders.Length > 0)
                    {
                        StampColliders(grid, res, originX, originZ, cell, packed.Colliders, trs, ref worldScratch);
                        usedMode = StampMode.ColliderElseMesh;
                        return true;
                    }
                    usedMode = StampMode.FullMesh;
                    return StampFullMesh(grid, res, originX, originZ, cell, packed.Meshes, trs, ref worldScratch);
                default:
                    usedMode = StampMode.FullMesh;
                    return StampFullMesh(grid, res, originX, originZ, cell, packed.Meshes, trs, ref worldScratch);
            }
        }

        static bool StampFullMesh(
            byte[] grid, int res, float originX, float originZ, float cell,
            Lod0Mesh[] meshes, Matrix4x4 trs, ref Vector3[] worldScratch)
        {
            if (meshes == null || meshes.Length == 0)
                return false;
            for (int m = 0; m < meshes.Length; m++)
                RasterizeMesh(grid, res, originX, originZ, cell, meshes[m], trs, ref worldScratch);
            return true;
        }

        /// <summary>
        /// 山体：丢掉近水平裙边/台面三角，只投影岩面，再填内部空洞，避免山脚绿地被整坨 AABB 式盖住。
        /// </summary>
        static bool StampMountainRock(
            byte[] grid, int res, float originX, float originZ, float cell,
            Lod0Mesh[] meshes, Matrix4x4 trs, ref Vector3[] worldScratch)
        {
            if (meshes == null || meshes.Length == 0)
                return false;

            var local = new byte[grid.Length];
            int steepTris = 0;
            for (int m = 0; m < meshes.Length; m++)
                steepTris += RasterizeMeshSteep(
                    local, res, originX, originZ, cell, meshes[m], trs, ref worldScratch,
                    MountainMaxUpDot);

            if (steepTris <= 0)
            {
                // 极端扁 mesh 退回全投影，仍保证山体禁种。
                for (int m = 0; m < meshes.Length; m++)
                    RasterizeMesh(grid, res, originX, originZ, cell, meshes[m], trs, ref worldScratch);
                return true;
            }

            FillInteriorHoles(local, res, MountainHoleFillPasses);
            for (int i = 0; i < grid.Length; i++)
            {
                if (local[i] != 0)
                    grid[i] = 1;
            }

            return true;
        }

        static bool StampRootDisc(
            byte[] grid, int res, float originX, float originZ, float cell,
            Lod0Mesh[] meshes, Matrix4x4 trs, Vector3 itemPos,
            float sliceMeters, float sliceFrac, float minR, float maxR, float pad)
        {
            if (meshes == null || meshes.Length == 0)
                return false;

            float yMin = float.PositiveInfinity;
            float yMax = float.NegativeInfinity;
            for (int m = 0; m < meshes.Length; m++)
            {
                Vector3[] verts = meshes[m].Verts;
                if (verts == null)
                    continue;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = trs.MultiplyPoint3x4(verts[i]);
                    if (w.y < yMin) yMin = w.y;
                    if (w.y > yMax) yMax = w.y;
                }
            }

            if (!float.IsFinite(yMin) || !float.IsFinite(yMax))
                return false;

            float cut = yMin + Mathf.Max(sliceMeters, sliceFrac * Mathf.Max(0.01f, yMax - yMin));
            float cx = itemPos.x;
            float cz = itemPos.z;
            float r2 = 0f;
            int hit = 0;
            for (int m = 0; m < meshes.Length; m++)
            {
                Vector3[] verts = meshes[m].Verts;
                if (verts == null)
                    continue;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = trs.MultiplyPoint3x4(verts[i]);
                    if (w.y > cut)
                        continue;
                    hit++;
                    float dx = w.x - cx;
                    float dz = w.z - cz;
                    float d2 = dx * dx + dz * dz;
                    if (d2 > r2)
                        r2 = d2;
                }
            }

            float radius = hit > 0 ? Mathf.Sqrt(r2) : minR;
            radius = Mathf.Clamp(radius, minR, maxR) + pad;
            StampDisc(grid, res, originX, originZ, cell, cx, cz, radius);
            return true;
        }

        static bool StampTentPoles(
            byte[] grid, int res, float originX, float originZ, float cell,
            Lod0Mesh[] meshes, Matrix4x4 trs)
        {
            if (meshes == null || meshes.Length == 0)
                return false;

            float yMin = float.PositiveInfinity;
            for (int m = 0; m < meshes.Length; m++)
            {
                Vector3[] verts = meshes[m].Verts;
                if (verts == null)
                    continue;
                for (int i = 0; i < verts.Length; i++)
                {
                    float y = trs.MultiplyPoint3x4(verts[i]).y;
                    if (y < yMin) yMin = y;
                }
            }

            if (!float.IsFinite(yMin))
                return false;

            float cut = yMin + TentPoleBaseMeters;
            float cellSize = TentPoleClusterCell;
            var buckets = new Dictionary<long, Vector3>(64);
            var counts = new Dictionary<long, int>(64);

            for (int m = 0; m < meshes.Length; m++)
            {
                Vector3[] verts = meshes[m].Verts;
                if (verts == null)
                    continue;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = trs.MultiplyPoint3x4(verts[i]);
                    if (w.y > cut)
                        continue;
                    int bx = Mathf.FloorToInt(w.x / cellSize);
                    int bz = Mathf.FloorToInt(w.z / cellSize);
                    long key = ((long)bx << 32) ^ (uint)bz;
                    if (!buckets.TryGetValue(key, out Vector3 sum))
                    {
                        buckets[key] = new Vector3(w.x, 0f, w.z);
                        counts[key] = 1;
                    }
                    else
                    {
                        buckets[key] = sum + new Vector3(w.x, 0f, w.z);
                        counts[key] = counts[key] + 1;
                    }
                }
            }

            if (buckets.Count == 0)
            {
                // 贴地顶点太少时退回实例原点小圆，避免帐篷完全不禁种。
                Vector3 o = trs.MultiplyPoint3x4(Vector3.zero);
                StampDisc(grid, res, originX, originZ, cell, o.x, o.z, TentPoleRadius);
                return true;
            }

            int stamped = 0;
            foreach (var kv in buckets)
            {
                int n = counts[kv.Key];
                if (n < TentPoleMinVerts)
                    continue;
                Vector3 sum = kv.Value;
                float px = sum.x / n;
                float pz = sum.z / n;
                StampDisc(grid, res, originX, originZ, cell, px, pz, TentPoleRadius);
                stamped++;
            }

            if (stamped == 0)
            {
                Vector3 o = trs.MultiplyPoint3x4(Vector3.zero);
                StampDisc(grid, res, originX, originZ, cell, o.x, o.z, TentPoleRadius);
            }

            return true;
        }

        static ColliderFootprint[] ExtractColliders(GameObject prefab)
        {
            var cols = prefab.GetComponentsInChildren<Collider>(true);
            if (cols == null || cols.Length == 0)
                return Array.Empty<ColliderFootprint>();

            Matrix4x4 rootInv = prefab.transform.worldToLocalMatrix;
            var list = new List<ColliderFootprint>(cols.Length);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || !c.enabled || c.isTrigger)
                    continue;

                if (c is BoxCollider box)
                {
                    Vector3 center = box.center;
                    Vector3 half = box.size * 0.5f;
                    var corners = new Vector3[8];
                    Matrix4x4 toRoot = rootInv * box.transform.localToWorldMatrix;
                    int k = 0;
                    for (int ix = -1; ix <= 1; ix += 2)
                    for (int iy = -1; iy <= 1; iy += 2)
                    for (int iz = -1; iz <= 1; iz += 2)
                    {
                        corners[k++] = toRoot.MultiplyPoint3x4(center + Vector3.Scale(half, new Vector3(ix, iy, iz)));
                    }

                    list.Add(new ColliderFootprint
                    {
                        IsBox = true,
                        BoxCornersLocal = corners,
                    });
                    continue;
                }

                if (c is MeshCollider meshCol && meshCol.sharedMesh != null)
                {
                    Matrix4x4 toRoot = rootInv * meshCol.transform.localToWorldMatrix;
                    if (TryReadMesh(meshCol.sharedMesh, toRoot, out Lod0Mesh packed)
                        && packed.Tris != null && packed.Tris.Length >= 3)
                    {
                        list.Add(new ColliderFootprint
                        {
                            IsBox = false,
                            Mesh = packed,
                        });
                    }
                }
            }

            return list.Count > 0 ? list.ToArray() : Array.Empty<ColliderFootprint>();
        }

        static void StampColliders(
            byte[] grid, int res, float originX, float originZ, float cell,
            ColliderFootprint[] colliders, Matrix4x4 trs, ref Vector3[] worldScratch)
        {
            for (int i = 0; i < colliders.Length; i++)
            {
                ColliderFootprint fp = colliders[i];
                if (fp.IsBox)
                    StampBoxXz(grid, res, originX, originZ, cell, fp.BoxCornersLocal, trs);
                else if (fp.Mesh.Verts != null)
                    RasterizeMesh(grid, res, originX, originZ, cell, fp.Mesh, trs, ref worldScratch);
            }
        }

        static void StampBoxXz(
            byte[] grid, int res, float originX, float originZ, float cell,
            Vector3[] cornersLocal, Matrix4x4 trs)
        {
            if (cornersLocal == null || cornersLocal.Length == 0)
                return;
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            for (int i = 0; i < cornersLocal.Length; i++)
            {
                Vector3 w = trs.MultiplyPoint3x4(cornersLocal[i]);
                if (w.x < minX) minX = w.x;
                if (w.x > maxX) maxX = w.x;
                if (w.z < minZ) minZ = w.z;
                if (w.z > maxZ) maxZ = w.z;
            }

            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - originX) / cell), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((maxX - originX) / cell), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - originZ) / cell), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((maxZ - originZ) / cell), 0, res - 1);
            for (int z = z0; z <= z1; z++)
            {
                int row = z * res;
                for (int x = x0; x <= x1; x++)
                    grid[row + x] = 1;
            }
        }

        /// <summary>篝火模型外再留的空地。柴堆是细网格，只光栅三角形会在缝里和边上留草。</summary>
        const float HuoduiKeepOutPadMeters = 1.5f;

        static int StampHuodui(byte[] grid, int res, float originX, float originZ, float cell)
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.MapItemExtrasAssetPath);
            if (text == null)
            {
                Log.Warning("[OilTerrainGrass] MapItemExtras missing — Huodui keep-out skipped");
                return 0;
            }

            var items = new List<MapItemData>(64);
            if (!MapItemData.ReadBinaryTo(text.bytes, items))
            {
                Log.Warning("[OilTerrainGrass] MapItemExtras parse failed — Huodui keep-out skipped");
                return 0;
            }

            string path = SceneAssets.DaoYu1EditorPath("Other", "Huodui");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Lod0Mesh[] meshes = prefab != null ? ExtractLod0(prefab) : Array.Empty<Lod0Mesh>();
            if (meshes == null || meshes.Length == 0)
                Log.Warning("[OilTerrainGrass] Huodui mesh missing — stamping fallback disc");

            var worldScratch = Array.Empty<Vector3>();
            int stamped = 0;
            for (int i = 0; i < items.Count; i++)
            {
                MapItemData item = items[i];
                if (item == null || !item.IsActive || item.PrefabName != "Huodui")
                    continue;

                var trs = Matrix4x4.TRS(item.Pos, Quaternion.Euler(item.Rotation), item.Scale);
                float radius = HuoduiKeepOutPadMeters;
                if (meshes != null && meshes.Length > 0)
                {
                    for (int m = 0; m < meshes.Length; m++)
                        RasterizeMesh(grid, res, originX, originZ, cell, meshes[m], trs, ref worldScratch);
                    radius += FootprintRadius(meshes, trs, item.Pos);
                }
                else
                {
                    float s = Mathf.Max(Mathf.Abs(item.Scale.x), Mathf.Abs(item.Scale.z));
                    radius += 1.35f * Mathf.Max(s, 0.01f);
                }

                StampDisc(grid, res, originX, originZ, cell, item.Pos.x, item.Pos.z, radius);
                stamped++;
            }

            Log.Info($"[OilTerrainGrass] Huodui keep-out n={stamped} pad={HuoduiKeepOutPadMeters:F1}m");
            return stamped;
        }

        static float FootprintRadius(Lod0Mesh[] meshes, Matrix4x4 trs, Vector3 origin)
        {
            float r2 = 0f;
            for (int m = 0; m < meshes.Length; m++)
            {
                Vector3[] verts = meshes[m].Verts;
                if (verts == null)
                    continue;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = trs.MultiplyPoint3x4(verts[i]);
                    float dx = w.x - origin.x;
                    float dz = w.z - origin.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 > r2)
                        r2 = d2;
                }
            }

            return Mathf.Sqrt(r2);
        }

        static void StampDisc(
            byte[] grid, int res, float originX, float originZ, float cell,
            float cx, float cz, float radius)
        {
            if (radius <= 0f)
                return;
            float r2 = radius * radius;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((cx - radius - originX) / cell), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((cx + radius - originX) / cell), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((cz - radius - originZ) / cell), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((cz + radius - originZ) / cell), 0, res - 1);
            for (int z = z0; z <= z1; z++)
            {
                float pz = originZ + (z + 0.5f) * cell;
                float dz = pz - cz;
                int row = z * res;
                for (int x = x0; x <= x1; x++)
                {
                    float px = originX + (x + 0.5f) * cell;
                    float dx = px - cx;
                    if (dx * dx + dz * dz <= r2)
                        grid[row + x] = 1;
                }
            }
        }

        /// <summary>
        /// 河面 mesh 外再留一窄圈浅水岸。禁止用 Renderer AABB 整块填——弯河包围盒会把中间绿地一并禁种。
        /// </summary>
        const float RiverBankKeepOutMeters = 2f;
        /// <summary>河面光栅常有内部空洞（窄三角/立面投影），闭运算填洞后再外扩岸带。</summary>
        const int RiverHoleFillPasses = 12;

        static int StampRiverBanks(byte[] grid, int res, float originX, float originZ, float cell)
        {
            var placements = new List<(string path, Vector3 position, Quaternion rotation, Vector3 scale)>(4);
            if (Game.Editor.IslandInlandWaterBuilder.CopyRiverPlacements(placements) == 0)
            {
                Log.Warning("[OilTerrainGrass] no river placements for keep-out");
                return 0;
            }

            var water = new byte[grid.Length];
            var worldScratch = Array.Empty<Vector3>();
            int stamped = 0;
            int meshParts = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                var placement = placements[i];
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(placement.path);
                if (prefab == null)
                {
                    Log.Warning("[OilTerrainGrass] river prefab missing " + placement.path);
                    continue;
                }

                // River_02/03/04 是带坐标的 PrefabInstance；不能再对 root-local mesh 乘一次摆放矩阵，
                // 否则禁种会偏到错误位置。按运行时位姿实例化后直接取世界坐标三角。
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (instance == null)
                {
                    Log.Warning("[OilTerrainGrass] river instantiate failed " + placement.path);
                    continue;
                }

                try
                {
                    // InstantiatePrefab 已带 PrefabInstance 的 scale（如 River_03=2.45）。
                    // 仅当 placement 显式给了非 1 的 scale 时覆盖；切勿用 Vector3.one 冲掉作者缩放。
                    instance.transform.SetPositionAndRotation(placement.position, placement.rotation);
                    if (placement.scale != Vector3.one)
                        instance.transform.localScale = placement.scale;
                    Lod0Mesh[] meshes = ExtractWorldMeshes(instance);
                    var identity = Matrix4x4.identity;
                    if (meshes == null || meshes.Length == 0)
                    {
                        Log.Warning("[OilTerrainGrass] river mesh missing " + placement.path);
                        continue;
                    }

                    for (int m = 0; m < meshes.Length; m++)
                        RasterizeMesh(water, res, originX, originZ, cell, meshes[m], identity, ref worldScratch);
                    meshParts += meshes.Length;

                    stamped++;
                    string msg =
                        $"[OilTerrainGrass] river stamped {System.IO.Path.GetFileNameWithoutExtension(placement.path)} " +
                        $"parts={meshes.Length} scale={instance.transform.localScale}";
                    Log.Info(msg);
                    Debug.Log(msg);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }

            if (stamped == 0)
                return 0;

            // 先填河面内部空洞，再外扩岸带，避免 AABB/先膨胀把陆地封进禁种。
            int filled = FillInteriorHoles(water, res, RiverHoleFillPasses);
            int radius = Mathf.Max(1, Mathf.CeilToInt(RiverBankKeepOutMeters / Mathf.Max(cell, 1e-4f)));
            OilTerrainGrassKeepOutBake.DilateBuffer(water, res, radius);
            int waterCells = 0;
            for (int i = 0; i < grid.Length; i++)
            {
                if (water[i] != 0)
                {
                    grid[i] = 1;
                    waterCells++;
                }
            }

            Log.Info(
                $"[OilTerrainGrass] river keep-out rivers={stamped}/{placements.Count} parts={meshParts} " +
                $"bank={RiverBankKeepOutMeters:F1}m radius={radius} holeFill={filled} cells={waterCells}");
            return stamped;
        }

        /// <summary>实例已在世界位姿：收集 Renderer / MeshCollider 的世界坐标三角。</summary>
        static Lod0Mesh[] ExtractWorldMeshes(GameObject root)
        {
            var list = new List<Lod0Mesh>(8);

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || IsBillboard(r))
                    continue;
                Mesh mesh = GetMesh(r);
                if (mesh == null || mesh.vertexCount < 3)
                    continue;
                if (!TryReadMesh(mesh, r.transform.localToWorldMatrix, out Lod0Mesh packed))
                    continue;
                if (packed.Tris != null && packed.Tris.Length >= 3)
                    list.Add(packed);
            }

            // PrefabInstance 河床常挂 MeshCollider；Renderer 读不到时用碰撞 mesh 兜底。
            if (list.Count == 0)
            {
                var cols = root.GetComponentsInChildren<MeshCollider>(true);
                for (int i = 0; i < cols.Length; i++)
                {
                    MeshCollider mc = cols[i];
                    if (mc == null || mc.sharedMesh == null || mc.sharedMesh.vertexCount < 3)
                        continue;
                    if (!TryReadMesh(mc.sharedMesh, mc.transform.localToWorldMatrix, out Lod0Mesh packed))
                        continue;
                    if (packed.Tris != null && packed.Tris.Length >= 3)
                        list.Add(packed);
                }
            }

            return list.Count > 0 ? list.ToArray() : Array.Empty<Lod0Mesh>();
        }

        /// <summary>
        /// 多数邻居已禁种的空格填上，清掉河面光栅内部空洞（不外扩整岛轮廓）。
        /// </summary>
        static int FillInteriorHoles(byte[] grid, int res, int passes)
        {
            if (grid == null || res <= 2 || passes <= 0 || grid.Length != res * res)
                return 0;

            var scratch = new byte[grid.Length];
            int filled = 0;
            for (int p = 0; p < passes; p++)
            {
                Array.Copy(grid, scratch, grid.Length);
                int passFilled = 0;
                for (int z = 1; z < res - 1; z++)
                {
                    int row = z * res;
                    for (int x = 1; x < res - 1; x++)
                    {
                        int idx = row + x;
                        if (grid[idx] != 0)
                            continue;
                        int n = 0;
                        if (grid[idx - 1] != 0) n++;
                        if (grid[idx + 1] != 0) n++;
                        if (grid[idx - res] != 0) n++;
                        if (grid[idx + res] != 0) n++;
                        if (grid[idx - res - 1] != 0) n++;
                        if (grid[idx - res + 1] != 0) n++;
                        if (grid[idx + res - 1] != 0) n++;
                        if (grid[idx + res + 1] != 0) n++;
                        if (n >= 5)
                        {
                            scratch[idx] = 1;
                            passFilled++;
                        }
                    }
                }

                Array.Copy(scratch, grid, grid.Length);
                filled += passFilled;
                if (passFilled == 0)
                    break;
            }

            return filled;
        }

        static bool TryResolveTerrainBounds(out float originX, out float originZ, out float worldSize)
        {
            originX = 0f;
            originZ = 0f;
            worldSize = 1000f;

            Terrain terrain = Terrain.activeTerrain
                ?? UnityEngine.Object.FindAnyObjectByType<Terrain>();
            if (terrain != null && terrain.terrainData != null)
            {
                Vector3 o = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                originX = o.x;
                originZ = o.z;
                worldSize = Mathf.Max(size.x, size.z);
                return true;
            }

            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(
                "Assets/Terrain/Island/IslandTerrainData.asset");
            if (data == null)
                return false;
            worldSize = Mathf.Max(data.size.x, data.size.z);
            return true;
        }

        static bool ShouldKeepOut(MapItemData data)
        {
            if (data == null || !data.IsActive)
                return false;
            string parent = data.Parent ?? string.Empty;
            if (SkipParents.Contains(parent))
                return false;
            string name = data.PrefabName ?? string.Empty;
            if (name.StartsWith("FX_", StringComparison.OrdinalIgnoreCase))
                return false;
            if (name.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf("Flower", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf("Huacao", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf("Wheat", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return true;
        }

        static void WriteBytes(string assetPath, byte[] bytes)
        {
            string abs = Path.GetFullPath(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs) ?? ".");
            File.WriteAllBytes(abs, bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        static Lod0Mesh[] ExtractLod0(GameObject prefab)
        {
            var renderers = CollectLod0Renderers(prefab);
            if (renderers.Count == 0)
                return Array.Empty<Lod0Mesh>();

            var list = new List<Lod0Mesh>(renderers.Count);
            Matrix4x4 rootInv = prefab.transform.worldToLocalMatrix;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer r = renderers[i];
                if (r == null || IsBillboard(r))
                    continue;
                Mesh mesh = GetMesh(r);
                if (mesh == null || mesh.vertexCount < 3)
                    continue;
                if (!TryReadMesh(mesh, rootInv * r.transform.localToWorldMatrix, out Lod0Mesh packed))
                    continue;
                if (packed.Tris != null && packed.Tris.Length >= 3)
                    list.Add(packed);
            }

            return list.Count > 0 ? list.ToArray() : Array.Empty<Lod0Mesh>();
        }

        static List<Renderer> CollectLod0Renderers(GameObject root)
        {
            var list = new List<Renderer>(8);
            var lod = root.GetComponent<LODGroup>();
            if (lod != null)
            {
                LOD[] lods = lod.GetLODs();
                if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                {
                    for (int i = 0; i < lods[0].renderers.Length; i++)
                    {
                        if (lods[0].renderers[i] != null)
                            list.Add(lods[0].renderers[i]);
                    }
                }
            }

            if (list.Count > 0)
                return list;

            Transform named = root.transform.Find($"{root.name}_LOD0");
            if (named != null)
            {
                var rs = named.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] != null)
                        list.Add(rs[i]);
                }
            }

            if (list.Count > 0)
                return list;

            var all = root.GetComponentsInChildren<Renderer>(true);
            Renderer best = null;
            int bestTris = -1;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || IsBillboard(r))
                    continue;
                Mesh mesh = GetMesh(r);
                if (mesh == null)
                    continue;
                int tris = Mathf.Max(mesh.vertexCount / 3, 0);
                if (tris > bestTris)
                {
                    bestTris = tris;
                    best = r;
                }
            }

            if (best != null)
                list.Add(best);
            return list;
        }

        static Mesh GetMesh(Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                return mf.sharedMesh;
            return r is SkinnedMeshRenderer smr ? smr.sharedMesh : null;
        }

        static bool IsBillboard(Renderer r)
        {
            string n = r.gameObject.name;
            if (n.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (n.IndexOf("Billboard", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            var mr = r as MeshRenderer;
            Shader sh = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
            if (sh == null)
                return false;
            string sn = sh.name;
            return sn.IndexOf("Billboard", StringComparison.OrdinalIgnoreCase) >= 0
                   || sn.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool TryReadMesh(Mesh mesh, Matrix4x4 toRoot, out Lod0Mesh packed)
        {
            packed = default;
            Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(mesh);
            try
            {
                if (data.Length == 0)
                    return false;
                Mesh.MeshData md = data[0];
                int vcount = md.vertexCount;
                if (vcount < 3)
                    return false;

                var local = new NativeArray<Vector3>(vcount, Allocator.Temp);
                try
                {
                    md.GetVertices(local);
                    var verts = new Vector3[vcount];
                    for (int i = 0; i < vcount; i++)
                        verts[i] = toRoot.MultiplyPoint3x4(local[i]);

                    int subN = md.subMeshCount;
                    if (subN <= 0)
                        return false;
                    int indexCount = 0;
                    for (int s = 0; s < subN; s++)
                        indexCount += md.GetSubMesh(s).indexCount;
                    if (indexCount < 3)
                        return false;

                    var tris = new int[indexCount];
                    int offset = 0;
                    var idx = new NativeArray<int>(indexCount, Allocator.Temp);
                    try
                    {
                        for (int s = 0; s < subN; s++)
                        {
                            int n = md.GetSubMesh(s).indexCount;
                            if (n <= 0)
                                continue;
                            md.GetIndices(idx.GetSubArray(offset, n), s);
                            offset += n;
                        }

                        idx.CopyTo(tris);
                    }
                    finally
                    {
                        if (idx.IsCreated)
                            idx.Dispose();
                    }

                    packed = new Lod0Mesh { Verts = verts, Tris = tris };
                    return true;
                }
                finally
                {
                    if (local.IsCreated)
                        local.Dispose();
                }
            }
            finally
            {
                data.Dispose();
            }
        }

        static void RasterizeMesh(
            byte[] grid, int res, float originX, float originZ, float cell,
            Lod0Mesh mesh, Matrix4x4 trs, ref Vector3[] world)
        {
            Vector3[] verts = mesh.Verts;
            int[] tris = mesh.Tris;
            if (verts == null || tris == null)
                return;
            if (world == null || world.Length < verts.Length)
                world = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                world[i] = trs.MultiplyPoint3x4(verts[i]);

            int triN = tris.Length / 3;
            for (int t = 0; t < triN; t++)
            {
                int i0 = tris[t * 3];
                int i1 = tris[t * 3 + 1];
                int i2 = tris[t * 3 + 2];
                if ((uint)i0 >= (uint)verts.Length
                    || (uint)i1 >= (uint)verts.Length
                    || (uint)i2 >= (uint)verts.Length)
                    continue;
                RasterizeTriangle(
                    grid, res, originX, originZ, cell,
                    world[i0].x, world[i0].z,
                    world[i1].x, world[i1].z,
                    world[i2].x, world[i2].z);
            }
        }

        /// <summary>只光栅 |法线.y|/|n| ≤ maxUpDot 的三角；返回实际投影三角数。</summary>
        static int RasterizeMeshSteep(
            byte[] grid, int res, float originX, float originZ, float cell,
            Lod0Mesh mesh, Matrix4x4 trs, ref Vector3[] world, float maxUpDot)
        {
            Vector3[] verts = mesh.Verts;
            int[] tris = mesh.Tris;
            if (verts == null || tris == null)
                return 0;
            if (world == null || world.Length < verts.Length)
                world = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                world[i] = trs.MultiplyPoint3x4(verts[i]);

            float maxUp = Mathf.Clamp01(maxUpDot);
            int used = 0;
            int triN = tris.Length / 3;
            for (int t = 0; t < triN; t++)
            {
                int i0 = tris[t * 3];
                int i1 = tris[t * 3 + 1];
                int i2 = tris[t * 3 + 2];
                if ((uint)i0 >= (uint)verts.Length
                    || (uint)i1 >= (uint)verts.Length
                    || (uint)i2 >= (uint)verts.Length)
                    continue;

                Vector3 a = world[i0];
                Vector3 b = world[i1];
                Vector3 c = world[i2];
                Vector3 n = Vector3.Cross(b - a, c - a);
                float n2 = n.sqrMagnitude;
                if (n2 < 1e-12f)
                    continue;
                float up = Mathf.Abs(n.y) / Mathf.Sqrt(n2);
                if (up > maxUp)
                    continue;

                RasterizeTriangle(
                    grid, res, originX, originZ, cell,
                    a.x, a.z, b.x, b.z, c.x, c.z);
                used++;
            }

            return used;
        }

        static void RasterizeTriangle(
            byte[] grid, int res, float originX, float originZ, float cell,
            float ax, float az, float bx, float bz, float cx, float cz)
        {
            float minX = Mathf.Min(ax, Mathf.Min(bx, cx));
            float maxX = Mathf.Max(ax, Mathf.Max(bx, cx));
            float minZ = Mathf.Min(az, Mathf.Min(bz, cz));
            float maxZ = Mathf.Max(az, Mathf.Max(bz, cz));
            float islandMaxX = originX + res * cell;
            float islandMaxZ = originZ + res * cell;
            if (maxX < originX || minX > islandMaxX || maxZ < originZ || minZ > islandMaxZ)
                return;

            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - originX) / cell), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((maxX - originX) / cell), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - originZ) / cell), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((maxZ - originZ) / cell), 0, res - 1);

            for (int z = z0; z <= z1; z++)
            {
                float zMin = originZ + z * cell;
                float zMax = zMin + cell;
                int row = z * res;
                for (int x = x0; x <= x1; x++)
                {
                    int idx = row + x;
                    if (grid[idx] != 0)
                        continue;
                    float xMin = originX + x * cell;
                    float xMax = xMin + cell;
                    if (TriangleAabbOverlap(ax, az, bx, bz, cx, cz, xMin, zMin, xMax, zMax))
                        grid[idx] = 1;
                }
            }
        }

        static bool TriangleAabbOverlap(
            float ax, float az, float bx, float bz, float cx, float cz,
            float xMin, float zMin, float xMax, float zMax)
        {
            if (InAabb(ax, az, xMin, zMin, xMax, zMax)
                || InAabb(bx, bz, xMin, zMin, xMax, zMax)
                || InAabb(cx, cz, xMin, zMin, xMax, zMax))
                return true;
            if (PointInTri(xMin, zMin, ax, az, bx, bz, cx, cz)
                || PointInTri(xMax, zMin, ax, az, bx, bz, cx, cz)
                || PointInTri(xMin, zMax, ax, az, bx, bz, cx, cz)
                || PointInTri(xMax, zMax, ax, az, bx, bz, cx, cz))
                return true;
            return SegSeg(ax, az, bx, bz, xMin, zMin, xMax, zMin)
                   || SegSeg(ax, az, bx, bz, xMax, zMin, xMax, zMax)
                   || SegSeg(ax, az, bx, bz, xMax, zMax, xMin, zMax)
                   || SegSeg(ax, az, bx, bz, xMin, zMax, xMin, zMin)
                   || SegSeg(bx, bz, cx, cz, xMin, zMin, xMax, zMin)
                   || SegSeg(bx, bz, cx, cz, xMax, zMin, xMax, zMax)
                   || SegSeg(bx, bz, cx, cz, xMax, zMax, xMin, zMax)
                   || SegSeg(bx, bz, cx, cz, xMin, zMax, xMin, zMin)
                   || SegSeg(cx, cz, ax, az, xMin, zMin, xMax, zMin)
                   || SegSeg(cx, cz, ax, az, xMax, zMin, xMax, zMax)
                   || SegSeg(cx, cz, ax, az, xMax, zMax, xMin, zMax)
                   || SegSeg(cx, cz, ax, az, xMin, zMax, xMin, zMin);
        }

        static bool InAabb(float x, float z, float xMin, float zMin, float xMax, float zMax) =>
            x >= xMin && x <= xMax && z >= zMin && z <= zMax;

        static bool PointInTri(
            float px, float pz, float ax, float az, float bx, float bz, float cx, float cz)
        {
            float d1 = Sign(px, pz, ax, az, bx, bz);
            float d2 = Sign(px, pz, bx, bz, cx, cz);
            float d3 = Sign(px, pz, cx, cz, ax, az);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        static float Sign(float px, float pz, float ax, float az, float bx, float bz) =>
            (px - bx) * (az - bz) - (ax - bx) * (pz - bz);

        static bool SegSeg(
            float a1x, float a1z, float a2x, float a2z,
            float b1x, float b1z, float b2x, float b2z)
        {
            float d1 = Sign(b1x, b1z, a1x, a1z, a2x, a2z);
            float d2 = Sign(b2x, b2z, a1x, a1z, a2x, a2z);
            float d3 = Sign(a1x, a1z, b1x, b1z, b2x, b2z);
            float d4 = Sign(a2x, a2z, b1x, b1z, b2x, b2z);
            return d1 * d2 < 0f && d3 * d4 < 0f;
        }
    }
}
#endif
