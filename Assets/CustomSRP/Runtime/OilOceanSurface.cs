using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGameFramework.Runtime;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CustomSRP
{
    /// <summary>
    /// Boluo DaoYu WaterGrid pattern: one rest-pose tile (scale 2500).
    /// DaoYu scenes pin the grid and leave followTarget unset; optional follow snaps XZ
    /// to the camera like StylizedWater2 WaterGrid. Gerstner swell is world-XZ in the
    /// vertex shader (sliding does not swim waves). Paint ripples stay in the fragment,
    /// so ~32 segments across the tile is enough — not a 10 m Boluo mesh.
    /// Shore foam uses camera depth in OilOceanNPR (no TerrainHeight MPB).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class OilOceanSurface : MonoBehaviour
    {
        [Header("Grid (Boluo WaterGrid)")]
        [Tooltip("Tile length in meters. Boluo DaoYu WaterGrid scale = 2500.")]
        public float gridScale = 2500f;
        [Tooltip("Meters between vertices. Swell is ~2 km, so ~500 m is enough. Ripples are shaded, not meshed.")]
        public float vertexDistance = 500f;
        [Tooltip("Boluo DaoYu leaves this off (fixed anchor). When on, recenters XZ on the camera.")]
        public bool followCamera = false;

        [Header("Swell (vertex shader)")]
        public float waveHeight = 0.2f;
        public float boundsPadding = 4f;

        const string MeshName = "OilOceanGrid";

        /// <summary>
        /// 32 segments across the tile. Finer than this does not show up: the swell
        /// wavelength is about 2 km and the fragment shader paints the short waves.
        /// </summary>
        public const int MaxSubdivisions = 32;

        static readonly int WaveHeightId = Shader.PropertyToID("_OilOceanWaveHeight");

        MeshFilter meshFilter;
        Mesh mesh;
        NativeArray<OilOceanVertex> meshVertices;
        NativeArray<int> meshIndices;
        int vertexCount;
        int indexCount;
        float _lastWaveHeight = float.NaN;
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
        }

        void ApplyWaveHeight()
        {
            float h = Mathf.Max(0f, waveHeight);
            if (!float.IsNaN(_lastWaveHeight) && Mathf.Abs(_lastWaveHeight - h) < 1e-4f)
                return;
            _lastWaveHeight = h;
            Shader.SetGlobalFloat(WaveHeightId, h);
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
            Log.Info(
                $"[CustomSRP] Oil ocean grid: {vertexCount} verts, {indexCount / 3} tris " +
                $"({gridScale:0} m tile, {CellStep():0} m spacing).");
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
