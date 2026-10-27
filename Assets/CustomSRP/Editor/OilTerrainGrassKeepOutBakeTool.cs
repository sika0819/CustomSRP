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
    /// 按 AllMapItem LOD0 mesh XZ 轮廓烤草地禁种遮罩。花草/特效不挡。
    /// </summary>
    public static class OilTerrainGrassKeepOutBakeTool
    {
        const int Res = OilTerrainGrassKeepOutBake.DefaultRes;

        static readonly HashSet<string> SkipParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Zacao", "Hua", "Zhibei", "texiao", "FX",
        };

        struct Lod0Mesh
        {
            public Vector3[] Verts;
            public int[] Tris;
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
            var cache = new Dictionary<string, Lod0Mesh[]>(64);
            var worldScratch = Array.Empty<Vector3>();
            int used = 0, skipped = 0, noMesh = 0, missingPrefab = 0;

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

                    if (!cache.TryGetValue(path, out Lod0Mesh[] meshes))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab == null)
                        {
                            missingPrefab++;
                            cache[path] = Array.Empty<Lod0Mesh>();
                            continue;
                        }

                        meshes = ExtractLod0(prefab);
                        cache[path] = meshes;
                    }

                    if (meshes == null || meshes.Length == 0)
                    {
                        noMesh++;
                        continue;
                    }

                    var trs = Matrix4x4.TRS(item.Pos, Quaternion.Euler(item.Rotation), item.Scale);
                    for (int m = 0; m < meshes.Length; m++)
                        RasterizeMesh(grid, Res, originX, originZ, cell, meshes[m], trs, ref worldScratch);
                    used++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            data = new OilTerrainGrassKeepOutBake.Data
            {
                OriginX = originX,
                OriginZ = originZ,
                WorldSize = worldSize,
                Res = Res,
                Mask = grid,
            };
            OilTerrainGrassKeepOutBake.Dilate(data, 1);
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
                $"rivers={rivers} cells={marked}/{grid.Length} cell={cell:F2}");
            return true;
        }

        /// <summary>河面外再留一圈岸，避免草种进浅水和泥岸。</summary>
        const float RiverBankKeepOutMeters = 4f;

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
            for (int i = 0; i < placements.Count; i++)
            {
                var placement = placements[i];
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(placement.path);
                if (prefab == null)
                {
                    Log.Warning("[OilTerrainGrass] river prefab missing " + placement.path);
                    continue;
                }

                Lod0Mesh[] meshes = ExtractLod0(prefab);
                if (meshes == null || meshes.Length == 0)
                {
                    Log.Warning("[OilTerrainGrass] river mesh missing " + placement.path);
                    continue;
                }

                var trs = Matrix4x4.TRS(placement.position, placement.rotation, placement.scale);
                for (int m = 0; m < meshes.Length; m++)
                    RasterizeMesh(water, res, originX, originZ, cell, meshes[m], trs, ref worldScratch);
                stamped++;
            }

            if (stamped == 0)
                return 0;

            int radius = Mathf.Max(1, Mathf.CeilToInt(RiverBankKeepOutMeters / Mathf.Max(cell, 1e-4f)));
            OilTerrainGrassKeepOutBake.DilateBuffer(water, res, radius);
            for (int i = 0; i < grid.Length; i++)
            {
                if (water[i] != 0)
                    grid[i] = 1;
            }

            Log.Info(
                $"[OilTerrainGrass] river keep-out meshes={stamped} bank={RiverBankKeepOutMeters:F1}m " +
                $"radius={radius} cells");
            return stamped;
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
