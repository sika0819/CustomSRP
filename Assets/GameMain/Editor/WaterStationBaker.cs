// WaterStationBaker.cs
//
// Editor-only utility that bakes static water emitter data from prefab meshes
// into a `BakedWaterStationDatabase` ScriptableObject. Run once after geometry
// changes (menu GameMain/Boluo/Bake Water Stations) or by writing
// `Temp/bake_water_stations.flag` and reopening the editor.
//
// What it produces:
// - For River_01..04 prefabs: `SegmentChain` of 16-32 evenly spaced world points
//   along the river's main axis (read from Mesh.vertices; importer must have
//   Read/Write enabled — the baker toggles it on, bakes, and restores).
// - For IslandOcean.prefab: a `CoarseGrid` over the ocean's top-down AABB with
//   per-cell distance to the ocean surface (only cells with `DistToWater <= 50m`
//   are kept; the rest are inferred as `> 50m`).
// - For Lake_preset_7 / native_river fallbacks: single-point `SegmentChain`.
// - For the four `AppendNativeRivers` fallback points: also added as
//   `SegmentChain` so the runtime never falls back to the PhysX path.
//
// Output: Assets/GameMain/Services/Audio/Baked/BakedWaterStationDatabase.asset

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    public static class WaterStationBaker
    {
        // Output path matches the existing AssetRaw/Configs/ location used by
        // YooAsset (AllMapItem.bytes, Island_GrassBake.bytes, MapItemExtras.bytes).
        // The runtime loads it via `SceneAssets.BakedWaterStationsLocation` which
        // the YooAsset collector maps to AddressByFileName "BakedWaterStations".
        public const string OutputAssetPath =
            "Assets/AssetRaw/Configs/BakedWaterStations.asset";
        public const string FlagRelPath = "Temp/bake_water_stations.flag";

        // Sampling knobs
        const int RiverTargetChainPoints = 24;        // 16-32 recommended
        const int WaterfallTargetChainPoints = 16;
        const float OceanCellSize = 8f;               // meters
        const float OceanCellDistCutoff = 50f;        // cells farther than this are dropped
        const float OceanDistSampleStep = 2f;         // vertical sample step on ocean mesh

        // River prefab positions (must match IslandInlandWaterBuilder.Placements).
        // We sample prefab mesh.vertices in local space then transform by this pose.
        struct Placement
        {
            public string PrefabPath;
            public string Id;
            public AmbienceEmitterKind Kind;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public float EnterRadius;
            public float ExitRadius;
            public int TargetChainPoints;
        }

        static readonly Placement[] RiverPlacements =
        {
            new Placement
            {
                PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/Common/River_01.prefab",
                Id = "river_native_River_01",
                Kind = AmbienceEmitterKind.River,
                Position = new Vector3(238.85437f, 101.538124f, 637.07263f),
                Rotation = Quaternion.identity,
                Scale = Vector3.one,
                EnterRadius = 80f,
                ExitRadius = 80f * 1.1f,
                TargetChainPoints = RiverTargetChainPoints,
            },
            new Placement
            {
                PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/Common/River_02.prefab",
                Id = "river_native_River_02",
                Kind = AmbienceEmitterKind.River,
                Position = new Vector3(435.38004f, 22.099998f, 424.85004f),
                Rotation = Quaternion.identity,
                Scale = Vector3.one,
                EnterRadius = 80f,
                ExitRadius = 80f * 1.1f,
                TargetChainPoints = RiverTargetChainPoints,
            },
            new Placement
            {
                PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/Common/River_03.prefab",
                Id = "river_native_River_03",
                Kind = AmbienceEmitterKind.River,
                Position = new Vector3(352.2f, 64.2f, 435.9f),
                Rotation = Quaternion.identity,
                Scale = Vector3.one,
                EnterRadius = 80f,
                ExitRadius = 80f * 1.1f,
                TargetChainPoints = RiverTargetChainPoints,
            },
            new Placement
            {
                // 听感锚点对齐 WaterfallSplash（与 AmbienceZoneCatalog.AppendNativeRivers 一致）
                PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/texiao/WaterfallSplash.prefab",
                Id = "waterfall_native_WaterfallSplash",
                Kind = AmbienceEmitterKind.Waterfall,
                Position = new Vector3(423.81f, 66.34f, 457.4f),
                Rotation = Quaternion.Euler(0f, 80.507f, 0f),
                Scale = new Vector3(1.5176096f, 1.5176096f, 1.5176096f),
                EnterRadius = 80f,
                ExitRadius = 80f * 1.1f,
                TargetChainPoints = WaterfallTargetChainPoints,
            },
        };

        // Static Ocean / Lake anchors from AmbienceZoneCatalog.
        // Ocean bakes from a real mesh; Lake has no mesh in the project, so we
        // use a small synthesized grid around the catalog anchor point.
        const string OceanPrefabPath = "Assets/AssetRaw/Ocean/IslandOcean.prefab";

        static readonly (string id, Vector3 position, float enterRadius, float exitRadius) OceanAnchor =
            ("ocean_shore_1", new Vector3(610.0767f, 5.7f, 706.1781f), 200f, 200f * 1.1f);

        static readonly (string id, Vector3 position, float enterRadius, float exitRadius) LakeAnchor =
            ("lake_preset_7", new Vector3(504.6723f, 31.6f, 463.8487f), 200f, 200f * 1.1f);

        [InitializeOnLoadMethod]
        static void WatchFlag()
        {
            EditorApplication.delayCall += TryConsumeFlag;
        }

        static void TryConsumeFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryConsumeFlag;
                return;
            }

            string flag = Path.GetFullPath(Path.Combine(Application.dataPath, "..", FlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }
            Log.Info("[WaterStationBaker] Flag detected — baking water stations");
            Build();
        }

        [MenuItem("GameMain/Boluo/Bake Water Stations")]
        public static void Build()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutputAssetPath));

                var stations = new List<BakedWaterStation>();

                // 1. Bake river / waterfall placements as SegmentChain.
                for (int i = 0; i < RiverPlacements.Length; i++)
                {
                    var p = RiverPlacements[i];
                    var station = BakeSegmentChain(p);
                    if (station != null)
                    {
                        stations.Add(station);
                        Log.Info($"[WaterStationBaker] baked {p.Id} chainPoints={station.ChainPoints?.Length ?? 0}");
                    }
                    else
                    {
                        // Fall back to a single-point chain at the catalog pose so the
                        // runtime still has a usable record for this station.
                        var fallback = new BakedWaterStation
                        {
                            Id = p.Id,
                            Kind = p.Kind,
                            PrefabHint = Path.GetFileNameWithoutExtension(p.PrefabPath),
                            EnterRadius = p.EnterRadius,
                            ExitRadius = p.ExitRadius,
                            UseXzDistance = false,
                            Shape = WaterShape.SegmentChain,
                            ChainPoints = new[] { p.Position },
                            BoundsMin = p.Position - new Vector3(1f, 1f, 1f),
                            BoundsMax = p.Position + new Vector3(1f, 1f, 1f),
                        };
                        stations.Add(fallback);
                        Log.Warning($"[WaterStationBaker] fallback single-point for {p.Id}");
                    }
                }

                // 2. Bake ocean as CoarseGrid from IslandOcean.prefab mesh.
                var ocean = BakeOceanCoarseGrid();
                if (ocean != null)
                {
                    stations.Add(ocean);
                    Log.Info($"[WaterStationBaker] baked ocean grid cells={ocean.Cells?.Length ?? 0} size={ocean.GridSizeX}x{ocean.GridSizeZ}");
                }
                else
                {
                    // Ocean mesh 读不到时：用海岸 catalog 锚点合成网格（勿误用 LakeAnchor）。
                    var fallback = BakeFallbackGrid(
                        OceanAnchor.id, OceanAnchor.position,
                        OceanAnchor.enterRadius, OceanAnchor.exitRadius,
                        AmbienceEmitterKind.Ocean, 16f);
                    if (fallback != null) stations.Add(fallback);
                    Log.Warning("[WaterStationBaker] ocean mesh bake failed — used shore-anchor fallback grid");
                }

                // 3. Lake has no prefab mesh — synthesize a small CoarseGrid around
                //    the catalog anchor. If the area is empty, runtime treats it as
                //    "always within radius if xz close to anchor".
                var lake = BakeFallbackGrid(LakeAnchor.id, LakeAnchor.position, LakeAnchor.enterRadius, LakeAnchor.exitRadius, AmbienceEmitterKind.Lake, 8f);
                if (lake != null)
                {
                    stations.Add(lake);
                    Log.Info($"[WaterStationBaker] baked lake fallback grid cells={lake.Cells?.Length ?? 0}");
                }

                // 4. Write the database asset (overwrite in place).
                var db = AssetDatabase.LoadAssetAtPath<BakedWaterStationDatabase>(OutputAssetPath);
                bool created = false;
                if (db == null)
                {
                    db = ScriptableObject.CreateInstance<BakedWaterStationDatabase>();
                    AssetDatabase.CreateAsset(db, OutputAssetPath);
                    created = true;
                }
                db.Version = BakedWaterStationDatabase.CurrentVersion;
                db.Stations = stations.ToArray();
                EditorUtility.SetDirty(db);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Log.Info($"[WaterStationBaker] wrote {OutputAssetPath} stations={db.Stations.Length} ({(created ? "new" : "updated")})");
            }
            catch (Exception e)
            {
                Log.Error($"[WaterStationBaker] failed: {e}");
            }
        }

        // ---- Segment chain baker (rivers / waterfalls) ----

        static BakedWaterStation BakeSegmentChain(Placement p)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.PrefabPath);
            if (prefab == null)
            {
                Log.Warning($"[WaterStationBaker] missing prefab: {p.PrefabPath}");
                return null;
            }

            MeshFilter mf = prefab.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                Log.Warning($"[WaterStationBaker] no MeshFilter on {p.PrefabPath}");
                return null;
            }

            Vector3[] localVerts = ReadMeshVertices(mf.sharedMesh, p.PrefabPath);
            if (localVerts == null || localVerts.Length == 0)
                return null;

            // Transform to world space.
            var matrix = Matrix4x4.TRS(p.Position, p.Rotation, p.Scale);
            var worldVerts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                worldVerts[i] = matrix.MultiplyPoint3x4(localVerts[i]);

            // Determine the river's main axis: spread of X vs Z. Pick the longer
            // axis as the "downstream" direction.
            Bounds b = ComputeBounds(worldVerts);
            float dx = b.max.x - b.min.x;
            float dz = b.max.z - b.min.z;
            bool mainAxisIsX = dx >= dz;

            // Project onto the main axis and sort.
            Array.Sort(worldVerts, (a, c) =>
                mainAxisIsX
                    ? a.x.CompareTo(c.x)
                    : a.z.CompareTo(c.z));

            // Resample evenly along the sorted chain to N points.
            Vector3[] sampled = ResampleAlongChain(worldVerts, p.TargetChainPoints);
            if (sampled.Length < 2)
            {
                sampled = new[] { sampled.Length == 1 ? sampled[0] : matrix.MultiplyPoint3x4(Vector3.zero) };
            }

            Bounds sampledBounds = ComputeBounds(sampled);
            // Pad bounds by 5m so a listener slightly outside the polyline still hits
            // the AABB coarse filter.
            sampledBounds.Expand(5f);

            return new BakedWaterStation
            {
                Id = p.Id,
                Kind = p.Kind,
                PrefabHint = Path.GetFileNameWithoutExtension(p.PrefabPath),
                EnterRadius = p.EnterRadius,
                ExitRadius = p.ExitRadius,
                UseXzDistance = false,
                Shape = WaterShape.SegmentChain,
                ChainPoints = sampled,
                BoundsMin = sampledBounds.min,
                BoundsMax = sampledBounds.max,
            };
        }

        static Vector3[] ResampleAlongChain(Vector3[] sorted, int targetCount)
        {
            if (sorted.Length == 0) return Array.Empty<Vector3>();
            if (sorted.Length == 1) return new[] { sorted[0] };
            if (targetCount < 2) targetCount = 2;

            // Compute cumulative arc length.
            float[] cum = new float[sorted.Length];
            cum[0] = 0f;
            for (int i = 1; i < sorted.Length; i++)
                cum[i] = cum[i - 1] + Vector3.Distance(sorted[i - 1], sorted[i]);
            float total = cum[cum.Length - 1];
            if (total <= 1e-4f)
                return new[] { sorted[0] };

            var result = new Vector3[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                float t = (float)i / (targetCount - 1) * total;
                // Binary search the segment containing arc length t.
                int seg = 1;
                while (seg < cum.Length - 1 && cum[seg] < t) seg++;
                float segStart = cum[seg - 1];
                float segEnd = cum[seg];
                float segLen = segEnd - segStart;
                float u = segLen > 1e-4f ? (t - segStart) / segLen : 0f;
                result[i] = Vector3.Lerp(sorted[seg - 1], sorted[seg], u);
            }
            return result;
        }

        // ---- Ocean CoarseGrid baker ----

        static BakedWaterStation BakeOceanCoarseGrid()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OceanPrefabPath);
            if (prefab == null)
            {
                Log.Warning($"[WaterStationBaker] ocean prefab missing: {OceanPrefabPath}");
                return null;
            }

            // The ocean is a large flat mesh — sample a representative MeshFilter.
            // Use the first MeshFilter with non-empty vertices.
            MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            Mesh picked = null;
            Vector3[] worldVerts = null;
            foreach (var mf in filters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                Vector3[] lv = ReadMeshVertices(mf.sharedMesh, OceanPrefabPath);
                if (lv == null || lv.Length == 0) continue;
                var m = Matrix4x4.TRS(mf.transform.position, mf.transform.rotation, mf.transform.lossyScale);
                worldVerts = new Vector3[lv.Length];
                for (int i = 0; i < lv.Length; i++) worldVerts[i] = m.MultiplyPoint3x4(lv[i]);
                picked = mf.sharedMesh;
                break;
            }

            if (picked == null || worldVerts == null)
                return null;

            Bounds wb = ComputeBounds(worldVerts);
            // Quantize bounds to CellSize.
            Vector3 origin = new Vector3(
                Mathf.Floor(wb.min.x / OceanCellSize) * OceanCellSize,
                wb.min.y,
                Mathf.Floor(wb.min.z / OceanCellSize) * OceanCellSize);
            Vector3 size = wb.max - origin;
            int gx = Mathf.Max(1, Mathf.CeilToInt(size.x / OceanCellSize));
            int gz = Mathf.Max(1, Mathf.CeilToInt(size.z / OceanCellSize));

            // For each cell, find min distance to any ocean vertex (in xz).
            // With dense ocean vertices this is a reasonable proxy for the surface.
            var cells = new List<BakedGridCell>(gx * gz);
            for (int cz = 0; cz < gz; cz++)
            {
                for (int cx = 0; cx < gx; cx++)
                {
                    Vector3 cellCenter = new Vector3(
                        origin.x + (cx + 0.5f) * OceanCellSize,
                        origin.y,
                        origin.z + (cz + 0.5f) * OceanCellSize);
                    float minDist = XZDistanceToPoints(cellCenter, worldVerts);
                    if (minDist <= OceanCellDistCutoff)
                    {
                        cells.Add(new BakedGridCell
                        {
                            Center = cellCenter,
                            DistToWater = minDist,
                        });
                    }
                }
            }

            return new BakedWaterStation
            {
                Id = OceanAnchor.id,
                Kind = AmbienceEmitterKind.Ocean,
                PrefabHint = "IslandOcean",
                EnterRadius = OceanAnchor.enterRadius,
                ExitRadius = OceanAnchor.exitRadius,
                UseXzDistance = true,
                Shape = WaterShape.CoarseGrid,
                GridOrigin = origin,
                GridSizeX = gx,
                GridSizeZ = gz,
                CellSize = OceanCellSize,
                Cells = cells.ToArray(),
                BoundsMin = origin,
                BoundsMax = origin + new Vector3(gx * OceanCellSize, 0f, gz * OceanCellSize),
            };
        }

        // Fallback: synthesize a tiny grid around an anchor with DistToWater=0
        // for the center cell, growing linearly outward. Used for Lake and as
        // last-resort safety when mesh baking fails.
        static BakedWaterStation BakeFallbackGrid(
            string id, Vector3 anchor, float enterRadius, float exitRadius,
            AmbienceEmitterKind kind, float halfSize)
        {
            int gx = 3, gz = 3;
            float cs = halfSize; // cell size = half-size so 3x3 covers ±halfSize
            Vector3 origin = anchor - new Vector3(cs, 0f, cs);
            var cells = new BakedGridCell[gx * gz];
            int idx = 0;
            for (int cz = 0; cz < gz; cz++)
            {
                for (int cx = 0; cx < gx; cx++)
                {
                    Vector3 c = origin + new Vector3((cx + 0.5f) * cs, 0f, (cz + 0.5f) * cs);
                    float dx = c.x - anchor.x;
                    float dz = c.z - anchor.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    cells[idx++] = new BakedGridCell { Center = c, DistToWater = d };
                }
            }
            return new BakedWaterStation
            {
                Id = id,
                Kind = kind,
                PrefabHint = "fallback",
                EnterRadius = enterRadius,
                ExitRadius = exitRadius,
                UseXzDistance = true,
                Shape = WaterShape.CoarseGrid,
                GridOrigin = origin,
                GridSizeX = gx,
                GridSizeZ = gz,
                CellSize = cs,
                Cells = cells,
                BoundsMin = origin,
                BoundsMax = origin + new Vector3(gx * cs, 0f, gz * cs),
            };
        }

        // ---- Mesh import toggling ----

        // Read mesh.vertices. If the mesh isn't readable, temporarily flip the
        // importer setting, reimport, read, then restore. Caller is responsible
        // for being in a valid editor context (Build is the only entry point).
        static Vector3[] ReadMeshVertices(Mesh mesh, string ownerPath)
        {
            if (mesh == null) return null;
            if (mesh.isReadable) return mesh.vertices;

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(assetPath))
            {
                Log.Warning($"[WaterStationBaker] mesh not readable and no asset path: {ownerPath}");
                return null;
            }

            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                Log.Warning($"[WaterStationBaker] mesh not readable (no ModelImporter): {assetPath}");
                return null;
            }

            try
            {
                Log.Info($"[WaterStationBaker] temporarily enabling Read/Write on {assetPath}");
                importer.isReadable = true;
                importer.SaveAndReimport();
                var freshMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                return freshMesh != null ? freshMesh.vertices : null;
            }
            finally
            {
                try
                {
                    importer.isReadable = false;
                    importer.SaveAndReimport();
                }
                catch { /* ignore — leave the mesh readable if restore fails */ }
            }
        }

        // ---- helpers ----

        static Bounds ComputeBounds(Vector3[] points)
        {
            if (points == null || points.Length == 0) return new Bounds();
            var b = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++) b.Encapsulate(points[i]);
            return b;
        }

        static float XZDistanceToPoints(Vector3 p, Vector3[] points)
        {
            float best = float.MaxValue;
            for (int i = 0; i < points.Length; i++)
            {
                float dx = points[i].x - p.x;
                float dz = points[i].z - p.z;
                float d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }
    }
}
#endif
