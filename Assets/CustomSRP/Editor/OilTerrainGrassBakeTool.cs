#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 从 Island Terrain alphamap 草层烘焙种点 sidecar。
    /// 层序对齐 CreateIslandTerrainScene：0 sand / 1 grass / 2 rock。
    /// 可无场景 Terrain：直接读 IslandTerrainData.asset。
    /// </summary>
    public static class OilTerrainGrassBakeTool
    {
        const int GrassLayerIndex = 1;
        const float GrassThreshold = 0.18f;
        const float PlantSpacing = 0.36f;
        const float MaxSlopeDeg = 42f;
        /// <summary>海面约 5.7m；再低一点留给滩涂草。</summary>
        const float MinPlantWorldY = 6.2f;
        const float SeaLevelY = 5.7f;
        const string TerrainDataPath = "Assets/Terrain/Island/IslandTerrainData.asset";
        /// <summary>Yoo / GameFramework 运行时地址（与 AllMapItem 同目录）。</summary>
        const string GameBakePath = "Assets/AssetRaw/Configs/Island_GrassBake.bytes";
        const string StreamingBakePath = "Assets/StreamingAssets/Island_GrassBake.bytes";
        const string LegacyResourcesBakePath =
            "Assets/Resources/OilTerrainGrass/Island_GrassBake.bytes";

        [MenuItem("CustomSRP/Grass/Bake Island Sidecar")]
        public static void Bake()
        {
            if (!TryResolveTerrain(out TerrainData data, out Vector3 origin))
            {
                Log.Error(
                    "[OilTerrainGrass] Bake needs TerrainData at " + TerrainDataPath +
                    " (or an active Terrain in the scene).");
                return;
            }

            OilTerrainGrassKeepOutBake.Data keepOut = null;
            if (!OilTerrainGrassKeepOutBakeTool.TryBake(out keepOut))
                Log.Warning("[OilTerrainGrass] KeepOut bake failed — planting without object mask");

            Vector3 size = data.size;
            int aw = data.alphamapWidth;
            int ah = data.alphamapHeight;
            int layers = data.alphamapLayers;
            if (layers <= GrassLayerIndex)
            {
                Log.Error($"[OilTerrainGrass] alphamapLayers={layers}, need grass at index {GrassLayerIndex}");
                return;
            }

            float[,,] maps = data.GetAlphamaps(0, 0, aw, ah);
            float maxSlopeRad = MaxSlopeDeg * Mathf.Deg2Rad;
            float spacing = Mathf.Max(0.2f, PlantSpacing);
            int stepsX = Mathf.Max(1, Mathf.CeilToInt(size.x / spacing));
            int stepsZ = Mathf.Max(1, Mathf.CeilToInt(size.z / spacing));

            var positions = new List<Vector3>(256000);
            var variants = new List<byte>(256000);
            var dryness = new List<byte>(256000);

            EditorUtility.DisplayProgressBar("OilTerrainGrass Bake", "planting…", 0f);
            try
            {
                for (int iz = 0; iz < stepsZ; iz++)
                {
                    if ((iz & 31) == 0)
                    {
                        EditorUtility.DisplayProgressBar(
                            "OilTerrainGrass Bake",
                            $"z {iz}/{stepsZ} n={positions.Count}",
                            (float)iz / stepsZ);
                    }

                    float nz = (iz + 0.5f) / stepsZ;
                    for (int ix = 0; ix < stepsX; ix++)
                    {
                        float nx = (ix + 0.5f) / stepsX;
                        float jx = (Hash01(ix, iz, 11f) - 0.5f) * spacing * 0.7f;
                        float jz = (Hash01(ix, iz, 29f) - 0.5f) * spacing * 0.7f;
                        float wx = origin.x + nx * size.x + jx;
                        float wz = origin.z + nz * size.z + jz;
                        float u = Mathf.Clamp01((wx - origin.x) / size.x);
                        float v = Mathf.Clamp01((wz - origin.z) / size.z);

                        int ax = Mathf.Clamp(Mathf.FloorToInt(u * (aw - 1)), 0, aw - 1);
                        int az = Mathf.Clamp(Mathf.FloorToInt(v * (ah - 1)), 0, ah - 1);
                        float grass = maps[az, ax, GrassLayerIndex];
                        if (grass < GrassThreshold)
                            continue;

                        float wy = origin.y + data.GetInterpolatedHeight(u, v);
                        if (wy < MinPlantWorldY || wy < SeaLevelY + 0.35f)
                            continue;

                        Vector3 normal = data.GetInterpolatedNormal(u, v);
                        float slope = Mathf.Acos(Mathf.Clamp(normal.y, -1f, 1f));
                        if (slope > maxSlopeRad)
                            continue;

                        if (Hash01(ix, iz, 47f) > Mathf.Lerp(0.35f, 1f, grass))
                            continue;

                        var pos = new Vector3(wx, wy, wz);
                        positions.Add(pos);
                        variants.Add((byte)OilTerrainGrassBladeMesh.PickVariant(pos));
                        dryness.Add((byte)Mathf.Clamp(Mathf.RoundToInt((1f - grass) * 180f), 0, 255));
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            int stripped = StripKeepOut(positions, variants, dryness, keepOut);
            if (stripped > 0)
                Log.Info($"[OilTerrainGrass] KeepOut stripped={stripped} remain={positions.Count}");

            if (positions.Count == 0)
            {
                Log.Error(
                    "[OilTerrainGrass] no plant positions — check alphamap grass layer / thresholds. " +
                    $"threshold={GrassThreshold} minY={MinPlantWorldY}");
                return;
            }

            var bake = PackCells(positions, variants, dryness, origin.x, origin.z);
            byte[] bytes = OilTerrainGrassBake.Write(bake);
            if (bytes == null || bytes.Length == 0)
            {
                Log.Error("[OilTerrainGrass] write failed");
                return;
            }

            WriteBytes(OilTerrainGrassBake.EditorPath, bytes);
            WriteBytes(GameBakePath, bytes);
            WriteBytes(StreamingBakePath, bytes);
            WriteBytes(LegacyResourcesBakePath, bytes);
            SyncGrassResources();
            AssetDatabase.Refresh();

            var hist = new int[OilTerrainGrassBladeMesh.VariantCount];
            for (int i = 0; i < bake.Variants.Length; i++)
            {
                int v = bake.Variants[i];
                if ((uint)v < (uint)hist.Length)
                    hist[v]++;
            }

            Log.Info(
                $"[OilTerrainGrass] wrote {GameBakePath} (+ StreamingAssets / CustomSRP/Grass) " +
                $"n={positions.Count} cells={bake.Cells.Length} bytes={bytes.Length} " +
                $"variants=[{string.Join(",", hist)}]");

            if (Application.isPlaying && OilTerrainGrassBinder.Active != null)
                OilTerrainGrassBinder.Active.TryLoadBake(bytes);
        }

        [MenuItem("CustomSRP/Grass/Bake Island Sidecar", true)]
        static bool ValidateBake() => !EditorApplication.isPlaying;

        [MenuItem("CustomSRP/Grass/Attach Binder In Scene")]
        public static void AttachBinder()
        {
            if (!BakeExists())
            {
                Log.Info("[OilTerrainGrass] bake missing — baking now…");
                Bake();
            }

            if (!Application.isPlaying)
            {
                Log.Info(
                    "[OilTerrainGrass] bake ready. Enter Play → ProcedureIsland；" +
                    "GameEntry.Grass (IslandGrassService) will load the binder.");
                var bytes = AssetDatabase.LoadAssetAtPath<TextAsset>(GameBakePath);
                if (bytes == null)
                    bytes = AssetDatabase.LoadAssetAtPath<TextAsset>(OilTerrainGrassBake.EditorPath);
                if (bytes != null)
                {
                    // Edit-mode preview host (no DontDestroyOnLoad).
                    var binder = OilTerrainGrassBootstrap.EnsureConfigured(bytes.bytes);
                    if (binder != null)
                        Selection.activeGameObject = binder.gameObject;
                }

                return;
            }

            var playBinder = OilTerrainGrassBootstrap.EnsureConfigured(
                AssetDatabase.LoadAssetAtPath<TextAsset>(GameBakePath)?.bytes);
            if (playBinder == null || !playBinder.HasBake)
            {
                Log.Error("[OilTerrainGrass] Attach failed — bake/shader/compute?");
                return;
            }

            Selection.activeGameObject = playBinder.gameObject;
            Log.Info(
                $"[OilTerrainGrass] binder ready HasBake={playBinder.HasBake} " +
                $"maxDraw={playBinder.MaxDrawDistance:F0}");
        }

        public static bool BakeExists() =>
            File.Exists(GameBakePath)
            || File.Exists(OilTerrainGrassBake.EditorPath)
            || File.Exists(StreamingBakePath)
            || AssetDatabase.LoadAssetAtPath<TextAsset>(GameBakePath) != null
            || AssetDatabase.LoadAssetAtPath<TextAsset>(OilTerrainGrassBake.EditorPath) != null;

        static bool TryResolveTerrain(out TerrainData data, out Vector3 origin)
        {
            data = null;
            origin = Vector3.zero;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null)
                terrain = UnityEngine.Object.FindAnyObjectByType<Terrain>();
            if (terrain != null && terrain.terrainData != null)
            {
                data = terrain.terrainData;
                origin = terrain.transform.position;
                return true;
            }

            data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data == null)
                return false;
            origin = Vector3.zero;
            return true;
        }

        static void WriteBytes(string assetPath, byte[] bytes)
        {
            string abs = Path.GetFullPath(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs) ?? ".");
            File.WriteAllBytes(abs, bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        static void SyncGrassResources()
        {
            const string resDir = "Assets/Resources/OilTerrainGrass";
            Directory.CreateDirectory(resDir);
            CopyIfExists("Assets/CustomSRP/Grass/GrassAtlas.png", resDir + "/GrassAtlas.png");
            CopyIfExists("Assets/CustomSRP/Grass/FlowerAtlas.png", resDir + "/FlowerAtlas.png");
            CopyIfExists("Assets/CustomSRP/Grass/Wheat.png", resDir + "/Wheat.png");
            CopyIfExists(
                "Assets/CustomSRP/Grass/OilTerrainGrassCull.compute",
                resDir + "/OilTerrainGrassCull.compute");
            CopyIfExists(
                "Assets/CustomSRP/Grass/OilTerrainGrass.mat",
                resDir + "/OilTerrainGrass.mat");
        }

        static int StripKeepOut(
            List<Vector3> positions,
            List<byte> variants,
            List<byte> dryness,
            OilTerrainGrassKeepOutBake.Data keepOut)
        {
            if (keepOut == null || positions.Count == 0)
                return 0;
            int write = 0;
            int stripped = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                if (OilTerrainGrassKeepOutBake.Contains(keepOut, p.x, p.z))
                {
                    stripped++;
                    continue;
                }

                positions[write] = p;
                variants[write] = variants[i];
                dryness[write] = dryness[i];
                write++;
            }

            if (write < positions.Count)
            {
                positions.RemoveRange(write, positions.Count - write);
                variants.RemoveRange(write, variants.Count - write);
                dryness.RemoveRange(write, dryness.Count - write);
            }

            return stripped;
        }

        static void CopyIfExists(string src, string dst)
        {
            if (!File.Exists(src))
                return;
            File.Copy(src, dst, overwrite: true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
        }

        static OilTerrainGrassBake.Data PackCells(
            List<Vector3> positions,
            List<byte> variants,
            List<byte> dryness,
            float originX,
            float originZ)
        {
            float cell = OilTerrainGrassBake.DefaultCellSize;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            float ox = Mathf.Floor(minX / cell) * cell;
            float oz = Mathf.Floor(minZ / cell) * cell;
            int countX = Mathf.Max(1, Mathf.CeilToInt((maxX - ox) / cell) + 1);
            int countZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - oz) / cell) + 1);
            int cellN = countX * countZ;

            var buckets = new List<int>[cellN];
            for (int i = 0; i < cellN; i++)
                buckets[i] = new List<int>(32);

            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                int cx = Mathf.Clamp(Mathf.FloorToInt((p.x - ox) / cell), 0, countX - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt((p.z - oz) / cell), 0, countZ - 1);
                buckets[cz * countX + cx].Add(i);
            }

            var orderedPos = new Vector3[positions.Count];
            var orderedVar = new byte[positions.Count];
            var orderedDry = new byte[positions.Count];
            var cells = new OilTerrainGrassBake.Cell[cellN];
            int cursor = 0;
            for (int i = 0; i < cellN; i++)
            {
                int cx = i % countX;
                int cz = i / countX;
                var bucket = buckets[i];
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int b = 0; b < bucket.Count; b++)
                {
                    int src = bucket[b];
                    Vector3 p = positions[src];
                    orderedPos[cursor] = p;
                    orderedVar[cursor] = variants[src];
                    orderedDry[cursor] = dryness[src];
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                    cursor++;
                }

                if (bucket.Count == 0)
                {
                    minY = 0f;
                    maxY = 0f;
                }

                float centerX = ox + (cx + 0.5f) * cell;
                float centerZ = oz + (cz + 0.5f) * cell;
                float centerY = (minY + maxY) * 0.5f;
                cells[i] = new OilTerrainGrassBake.Cell
                {
                    Center = new Vector3(centerX, centerY, centerZ),
                    Extent = new Vector3(cell * 0.5f, Mathf.Max(4f, (maxY - minY) * 0.5f + 2f), cell * 0.5f),
                    Count = bucket.Count,
                    Offset = cursor - bucket.Count,
                };
            }

            return new OilTerrainGrassBake.Data
            {
                OriginX = ox,
                OriginZ = oz,
                CellSize = cell,
                CellCountX = countX,
                CellCountZ = countZ,
                Cells = cells,
                Positions = orderedPos,
                Variants = orderedVar,
                Dryness = orderedDry,
            };
        }

        static float Hash01(int x, int z, float salt)
        {
            float v = Mathf.Sin(x * 12.9898f + z * 78.233f + salt * 37.719f) * 43758.5453f;
            return v - Mathf.Floor(v);
        }
    }
}
#endif
