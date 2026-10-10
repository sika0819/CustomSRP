using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CustomSRP
{
    /// <summary>
    /// Boluo WaterGrid pattern: one rest-pose tile that follows the camera.
    /// Gerstner swell is in the vertex shader (world XZ, so sliding the tile does not
    /// swim the waves). Paint ripples stay in the fragment, so the grid only has to
    /// hold a ~2 km swell — about 32 segments across the island, not a 10 m mesh.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class OilOceanSurface : MonoBehaviour
    {
        [Header("Grid (Boluo WaterGrid)")]
        [Tooltip("Tile length in meters. Covers the island; the tile recenters on the camera.")]
        public float gridScale = 2500f;
        [Tooltip("Meters between vertices. Swell is ~2 km, so ~500 m is enough. Ripples are shaded, not meshed.")]
        public float vertexDistance = 500f;
        [Tooltip("Keep the tile centered on the camera and snap to the vertex grid.")]
        public bool followCamera = true;

        [Header("Shore (TerrainData)")]
        [Tooltip("Coast mask uses this terrain's heightmap, not moorea_heightmap.png.")]
        public Terrain terrain;

        [Header("Swell (vertex shader)")]
        public float waveHeight = 6.5f;
        public float boundsPadding = 4f;

        const string MeshName = "OilOceanGrid";

        /// <summary>
        /// 32 segments across the tile. Finer than this does not show up: the swell
        /// wavelength is about 2 km and the fragment shader paints the short waves.
        /// </summary>
        public const int MaxSubdivisions = 32;

        static readonly int ShoreMapId = Shader.PropertyToID("_ShoreHeightMap");
        static readonly int ShoreOriginId = Shader.PropertyToID("_ShoreOriginSize");
        static readonly int ShoreScaleId = Shader.PropertyToID("_ShoreHeightScale");
        static readonly int ShoreWaterId = Shader.PropertyToID("_ShoreWaterLevel");
        static readonly int WaveHeightId = Shader.PropertyToID("_OilOceanWaveHeight");

        MeshFilter meshFilter;
        Texture2D shoreHeight;
        MaterialPropertyBlock shoreBlock;
        Mesh mesh;
        NativeArray<OilOceanVertex> meshVertices;
        NativeArray<int> meshIndices;
        int vertexCount;
        int indexCount;
        static int enableDepth;

        void OnEnable()
        {
            if (enableDepth > 0)
            {
                return;
            }

            enableDepth++;
            try
            {
                meshFilter = GetComponent<MeshFilter>();
                if (!Application.isPlaying)
                {
                    FollowCamera();
                }

                if (NeedsRebuild())
                {
                    Rebuild();
                }
                else
                {
                    ApplyTerrainShore();
                }

                ApplyWaveHeight();
            }
            finally
            {
                enableDepth--;
            }
        }

        void OnDisable()
        {
            if (Application.isPlaying)
            {
                DisposeMeshCpu();
            }
        }

        void OnDestroy()
        {
            DisposeMeshCpu();
            DestroyShoreHeight();
            if (mesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(mesh);
            }
            else
            {
                DestroyImmediate(mesh);
            }

            mesh = null;
        }

        void LateUpdate()
        {
            if (Application.isPlaying)
            {
                FollowCamera();
            }

            ApplyWaveHeight();
            SyncShoreWaterLevel();
        }

        // The heightmap is built once. The waterline has to follow the ocean
        // height, or the column stays 0 and both foam and refraction miss the shore.
        void SyncShoreWaterLevel()
        {
            if (terrain == null)
            {
                terrain = FindAnyObjectByType<Terrain>();
            }

            if (terrain == null)
            {
                return;
            }

            var renderer = GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                return;
            }

            shoreBlock ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(shoreBlock);
            if (shoreBlock.GetTexture(ShoreMapId) == null)
            {
                ApplyTerrainShore();
                return;
            }

            float water = transform.position.y - terrain.transform.position.y;
            if (Mathf.Approximately(shoreBlock.GetFloat(ShoreWaterId), water))
            {
                return;
            }

            shoreBlock.SetFloat(ShoreWaterId, water);
            renderer.SetPropertyBlock(shoreBlock);
        }

        void ApplyWaveHeight()
        {
            Shader.SetGlobalFloat(WaveHeightId, Mathf.Max(0f, waveHeight));
        }

        [ContextMenu("Rebuild Ocean Mesh")]
        public void Rebuild()
        {
            DisposeMeshCpu();
            BuildPlane();
            EnsureMesh();
            UploadRest();
            ApplyWaveHeight();

            var renderer = GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            ApplyTerrainShore();
            Debug.Log(
                $"[CustomSRP] Oil ocean grid: {vertexCount} verts, {indexCount / 3} tris " +
                $"({gridScale:0} m tile, {CellStep():0} m spacing).");
        }

        /// <summary>
        /// Coast mask reads TerrainData heights (origin at min XZ, 0–1 of size.y).
        /// Not moorea_heightmap.png, which is north-up and loses the waterline when compressed.
        /// </summary>
        void ApplyTerrainShore()
        {
            if (terrain == null)
            {
                terrain = FindAnyObjectByType<Terrain>();
            }

            TerrainData data = terrain != null ? terrain.terrainData : null;
            if (data == null)
            {
                Debug.LogWarning("[CustomSRP] Oil ocean shore: no TerrainData.");
                return;
            }

            int res = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, res, res);
            DestroyShoreHeight();
            shoreHeight = new Texture2D(res, res, TextureFormat.R16, false, true)
            {
                name = "MooreaTerrainShore",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };

            var pixels = new ushort[res * res];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float h = math.saturate(heights[z, x]);
                    pixels[z * res + x] = (ushort)math.round(h * 65535f);
                }
            }

            shoreHeight.SetPixelData(pixels, 0);
            shoreHeight.Apply(false, true);

            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            float water = transform.position.y - origin.y;

            var renderer = GetComponent<MeshRenderer>();
            shoreBlock ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(shoreBlock);
            shoreBlock.SetTexture(ShoreMapId, shoreHeight);
            shoreBlock.SetVector(ShoreOriginId, new Vector4(origin.x, origin.z, size.x, size.z));
            shoreBlock.SetFloat(ShoreScaleId, size.y);
            shoreBlock.SetFloat(ShoreWaterId, water);
            renderer.SetPropertyBlock(shoreBlock);
        }

        void DestroyShoreHeight()
        {
            if (shoreHeight == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(shoreHeight);
            }
            else
            {
                DestroyImmediate(shoreHeight);
            }

            shoreHeight = null;
        }

        int Subdivisions()
        {
            float spacing = Mathf.Max(0.5f, vertexDistance);
            float extent = Mathf.Max(spacing, gridScale);
            int n = Mathf.Max(1, Mathf.FloorToInt(extent / spacing));
            return Mathf.Min(n, MaxSubdivisions);
        }

        float CellStep()
        {
            return gridScale / Subdivisions();
        }

        bool NeedsRebuild()
        {
            if (meshFilter == null)
            {
                return true;
            }

            Mesh shared = meshFilter.sharedMesh;
            if (shared != null && shared.name == MeshName && shared.vertexCount > 0)
            {
                mesh = shared;
            }

            int expected = Subdivisions() + 1;
            expected *= expected;
            if (mesh == null || mesh.vertexCount != expected)
            {
                return true;
            }

            return !meshVertices.IsCreated || vertexCount != expected;
        }

        void BuildPlane()
        {
            int subdivisions = Subdivisions();
            int xCount = subdivisions + 1;
            int zCount = subdivisions + 1;
            vertexCount = xCount * zCount;
            indexCount = subdivisions * subdivisions * 6;
            float step = gridScale / subdivisions;
            float half = gridScale * 0.5f;

            meshVertices = new NativeArray<OilOceanVertex>(vertexCount, Allocator.Persistent);
            meshIndices = new NativeArray<int>(indexCount, Allocator.Persistent);

            int v = 0;
            for (int z = 0; z < zCount; z++)
            {
                for (int x = 0; x < xCount; x++)
                {
                    float px = x * step - half;
                    float pz = z * step - half;
                    float seed = Hash21(px, pz);
                    meshVertices[v] = new OilOceanVertex
                    {
                        position = new float3(px, 0f, pz),
                        normal = new float3(0f, 1f, 0f),
                        tangent = new float4(-1f, 0f, 0f, -1f),
                        uv = new float2(px, pz),
                        shorePigment = new float2(900f, seed)
                    };
                    v++;
                }
            }

            int t = 0;
            for (int z = 0; z < subdivisions; z++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int i0 = z * xCount + x;
                    int i1 = i0 + xCount;
                    meshIndices[t++] = i0;
                    meshIndices[t++] = i1;
                    meshIndices[t++] = i0 + 1;
                    meshIndices[t++] = i1;
                    meshIndices[t++] = i1 + 1;
                    meshIndices[t++] = i0 + 1;
                }
            }
        }

        void FollowCamera()
        {
            if (!followCamera)
            {
                return;
            }

            Transform target = FollowTarget();
            if (target == null)
            {
                return;
            }

            float step = CellStep();
            Vector3 p = target.position;
            p.x = Snap(p.x, step);
            p.z = Snap(p.z, step);
            p.y = transform.position.y;
            if (transform.position != p)
            {
                transform.position = p;
            }
        }

        Transform FollowTarget()
        {
            if (Application.isPlaying)
            {
                Camera cam = Camera.main;
                return cam != null ? cam.transform : null;
            }

#if UNITY_EDITOR
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null && view.camera != null)
            {
                return view.camera.transform;
            }
#endif
            Camera fallback = Camera.main;
            return fallback != null ? fallback.transform : null;
        }

        static float Snap(float position, float cell)
        {
            return Mathf.Floor(position / cell) * cell + cell * 0.5f;
        }

        static float Hash21(float x, float z)
        {
            float n = Mathf.Sin(x * 127.1f + z * 311.7f) * 43758.5453f;
            return n - Mathf.Floor(n);
        }

        void EnsureMesh()
        {
            meshFilter = GetComponent<MeshFilter>();
            if (mesh != null && mesh.name == MeshName)
            {
                meshFilter.sharedMesh = mesh;
                return;
            }

            Mesh previous = meshFilter.sharedMesh;
            mesh = OilOceanAdvancedMesh.CreateShell(MeshName);
            meshFilter.sharedMesh = mesh;
            if (previous != null && previous != mesh)
            {
                if (Application.isPlaying)
                {
                    Destroy(previous);
                }
                else
                {
                    DestroyImmediate(previous);
                }
            }
        }

        void DisposeMeshCpu()
        {
            if (meshVertices.IsCreated)
            {
                meshVertices.Dispose();
            }

            if (meshIndices.IsCreated)
            {
                meshIndices.Dispose();
            }

            vertexCount = 0;
            indexCount = 0;
        }

        void UploadRest()
        {
            if (!meshVertices.IsCreated || vertexCount <= 0 || mesh == null)
            {
                return;
            }

            float yExtent = Mathf.Max(waveHeight * 4f + 40f, boundsPadding);
            var bounds = new Bounds(
                new Vector3(0f, waveHeight, 0f),
                new Vector3(gridScale, yExtent, gridScale));
            OilOceanAdvancedMesh.Upload(
                mesh,
                meshVertices,
                meshIndices,
                vertexCount,
                indexCount,
                bounds);
            meshFilter.sharedMesh = mesh;
        }
    }
}
