using System.Collections.Generic;
using Game;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGameFramework.Runtime;

namespace CustomSRP
{
    /// <summary>
    /// 绿地草：只读 Bake Sidecar。玩家附近一窗驻留 GPU，镜头动了 CS 视锥剔，没动复用 indirect。
    /// </summary>
    public sealed class OilTerrainGrassBinder : MonoBehaviour
    {
        const float CellSize = 16f;
        const float WindowRebuildMeters = 48f;
        const float WindowPackPaddingMeters = 90f;
        const float FarGrassFullDensityMeters = 32f;
        const int FarBladeSegments = 2;
        const int ArgsStride = sizeof(uint) * 5;

        static readonly int WindDirId = Shader.PropertyToID("_OilGrassWindDir");
        static readonly int WindStrengthId = Shader.PropertyToID("_OilGrassWindStrength");
        static readonly int MaxDrawId = Shader.PropertyToID("_OilGrassMaxDraw");
        [SerializeField] float minPlantWorldY = 6.2f;
        [SerializeField] int maxPerLeaf = 48000;
        [SerializeField] int maxTotalVisible = 48000;
        [SerializeField] float maxDrawDistance = 180f;
        [SerializeField] ComputeShader cullCompute;
        [SerializeField] Material materialOverride;
        [SerializeField] Texture atlasTexture;
        [SerializeField] Texture flowerAtlasTexture;
        [SerializeField] Texture wheatTexture;
        [SerializeField] Texture lavenderTexture;
        [SerializeField] Texture[] variantTextures;
        [SerializeField] TextAsset bakeAsset;
        [SerializeField] Vector3 windDirection = new Vector3(1f, 0f, 0.35f);
        [SerializeField] float windStrength = 0.55f;

        Texture[] _variantTex;

        readonly Plane[] _frustumPlanes = new Plane[6];
        readonly List<int> _visibleCellIds = new List<int>(256);
        readonly uint[] _argsCache = new uint[5];
        readonly List<uint> _cpuVisibleScratch = new List<uint>(4096);
        MaterialPropertyBlock _grassMpb;

        int[] _cellOffsets = System.Array.Empty<int>();
        int[] _cellCounts = System.Array.Empty<int>();
        Vector3[] _cellCenters = System.Array.Empty<Vector3>();
        Vector3[] _cellExtents = System.Array.Empty<Vector3>();
        int[][] _cellOffsetsV;
        int[][] _cellCountsV;
        int[] _packedCellIds = System.Array.Empty<int>();
        int _packedCellCount;
        float[] _visibleCellDistSq = System.Array.Empty<float>();
        int[] _visibleSortOrder = System.Array.Empty<int>();
        int _cellCountX;
        int _cellCountZ;
        float _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
        int _packedCount;

        static readonly ProfilerMarker MarkerLateUpdate = new ProfilerMarker("OilTerrainGrass.LateUpdate");
        static readonly ProfilerMarker MarkerCullAndDraw = new ProfilerMarker("OilTerrainGrass.CullAndDraw");

        ComputeBuffer[] _allPosV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _visibleIdV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _visibleIdFarV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _argsV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _argsFarV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        int[] _packedV = new int[OilTerrainGrassBladeMesh.TopologyCount];
        Mesh[] _variantMeshes;
        Mesh[] _farVariantMeshes;
        Material _material;
        Material[] _variantMats;
        int _bladeSegments = OilTerrainGrassBladeMesh.DefaultSegments;
        int _kernel = -1;
        int _kernelAdd = -1;
        bool _useAdd;
        bool _gpuReady;
        bool _loggedDraw;
        bool _bakeLoaded;
        OilTerrainGrassBake.Data _bakeData;
        ComputeBuffer _keepOutGrid;
        bool _keepOutEnabled;
        bool _keepOutLoadTried;
        int _keepOutRes;
        float _keepOutOriginX;
        float _keepOutOriginZ;
        float _keepOutCell;
        bool _windowDirty = true;
        bool _hasWindow;
        Vector3 _windowCenter;
        int[] _cellOrder = System.Array.Empty<int>();
        float[] _cellDistSq = System.Array.Empty<float>();
        List<Vector4>[] _windowPackLists;
        Vector4[][] _windowPackUpload;
        int[] _pendingPackedV = new int[OilTerrainGrassBladeMesh.TopologyCount];
        int[][] _packOffsetsV;
        int[][] _packCountsV;
        int[] _packCellIds = System.Array.Empty<int>();
        bool _ownsBladeMesh;
        bool _ownsMaterial;
        Camera _bindDrawCamera;
        bool _hasLastGrassSubmit;
        bool _hasGrassView;
        Vector3 _lastGrassCamPos;
        Quaternion _lastGrassCamRot;
        bool _windGlobalsValid;
        Vector3 _lastWindDir;
        float _lastWindStrength;
        float _lastMaxDrawGlobal;

        public static OilTerrainGrassBinder Active { get; private set; }
        public static bool DebugDrawEnabled { get; set; } = true;

        public int LastVisibleCount { get; private set; }
        public bool HasBake => _bakeLoaded;
        public float MaxDrawDistance => maxDrawDistance;

        public static OilTerrainGrassBinder Ensure()
        {
            if (Active != null)
                return Active;
            var existing = FindAnyObjectByType<OilTerrainGrassBinder>();
            if (existing != null)
                return existing;
            var go = new GameObject("[OilTerrainGrass]");
            // DontDestroyOnLoad is play-mode only; editor menu must not call it.
            if (Application.isPlaying)
                DontDestroyOnLoad(go);
            return go.AddComponent<OilTerrainGrassBinder>();
        }

        void OnEnable() => Active = this;

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        void Start()
        {
            if (!_bakeLoaded && bakeAsset != null)
                TryLoadBake(bakeAsset.bytes);
        }

        public void ApplyBudget(int maxVisible, float drawDistance, int bladeSegments = 4)
        {
            maxTotalVisible = Mathf.Max(1, maxVisible);
            maxPerLeaf = Mathf.Max(maxTotalVisible, maxPerLeaf);
            maxDrawDistance = Mathf.Max(1f, drawDistance);
            int segs = Mathf.Clamp(bladeSegments, 1, 8);
            if (segs != _bladeSegments)
            {
                _bladeSegments = segs;
                RebuildTopologyMeshes();
            }

            _windowDirty = true;
        }

        public void SetComputeShader(ComputeShader cs)
        {
            cullCompute = cs;
            _kernel = -1;
            _kernelAdd = -1;
            _gpuReady = false;
        }

        public void SetMaterialOverride(Material material)
        {
            materialOverride = material;
            DestroyVariantMaterials();
            if (_ownsMaterial && _material != null)
                Destroy(_material);
            _material = null;
            _ownsMaterial = false;
            _gpuReady = false;
            _windowDirty = true;
        }

        public void SetAtlasTexture(Texture atlas)
        {
            atlasTexture = atlas;
            RebuildVariantTextures();
            ApplyAtlasToMaterials();
        }

        public void SetFlowerAtlas(Texture atlas)
        {
            flowerAtlasTexture = atlas;
            RebuildVariantTextures();
            ApplyAtlasToMaterials();
        }

        public void SetWheatTexture(Texture wheat)
        {
            wheatTexture = wheat;
            RebuildVariantTextures();
            ApplyAtlasToMaterials();
        }

        public void SetLavenderTexture(Texture lavender)
        {
            lavenderTexture = lavender;
            RebuildVariantTextures();
            ApplyAtlasToMaterials();
        }

        /// <summary>按株型设置单张纹理（长度应对齐 VariantCount）。</summary>
        public void SetVariantTextures(Texture[] textures)
        {
            variantTextures = textures;
            RebuildVariantTextures();
            ApplyAtlasToMaterials();
        }

        void RebuildVariantTextures()
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            _variantTex = new Texture[vc];
            for (int v = 0; v < vc; v++)
            {
                Texture assigned = null;
                if (variantTextures != null
                    && (uint)v < (uint)variantTextures.Length
                    && variantTextures[v] != null)
                    assigned = variantTextures[v];
                else if (OilTerrainGrassBladeMesh.IsLavender(v))
                    assigned = lavenderTexture != null ? lavenderTexture : flowerAtlasTexture;
                else if (OilTerrainGrassBladeMesh.UsesAtlasColor(v))
                    assigned = flowerAtlasTexture;
                else if (OilTerrainGrassAtlas.UsesGrassAtlas(v))
                    assigned = atlasTexture != null ? atlasTexture : wheatTexture;
                else
                    assigned = atlasTexture;

                _variantTex[v] = assigned != null ? assigned : atlasTexture;
            }
        }

        public void SetBakeAsset(TextAsset asset)
        {
            bakeAsset = asset;
            if (asset != null)
                TryLoadBake(asset.bytes);
        }

        public bool TryLoadBake(byte[] bytes)
        {
            if (!OilTerrainGrassBake.TryRead(bytes, out var data) || data.Positions.Length == 0)
                return false;

            _bakeData = data;
            _bakeLoaded = true;
            ApplyBakeData(data);
            Log.Info(
                $"[OilTerrainGrass] bake n={data.Positions.Length} cells={data.Cells.Length} " +
                $"grid={data.CellCountX}x{data.CellCountZ}");
            return true;
        }

        void ApplyBakeData(OilTerrainGrassBake.Data data)
        {
            int cellN = data.CellCountX * data.CellCountZ;
            _cellCountX = data.CellCountX;
            _cellCountZ = data.CellCountZ;
            _cellOffsets = new int[cellN];
            _cellCounts = new int[cellN];
            _cellCenters = new Vector3[cellN];
            _cellExtents = new Vector3[cellN];

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < cellN; i++)
            {
                var cell = data.Cells[i];
                _cellOffsets[i] = cell.Offset;
                _cellCounts[i] = cell.Count;
                _cellCenters[i] = cell.Center;
                _cellExtents[i] = cell.Extent;
                Vector3 lo = cell.Center - cell.Extent;
                Vector3 hi = cell.Center + cell.Extent;
                if (lo.x < minX) minX = lo.x;
                if (lo.y < minY) minY = lo.y;
                if (lo.z < minZ) minZ = lo.z;
                if (hi.x > maxX) maxX = hi.x;
                if (hi.y > maxY) maxY = hi.y;
                if (hi.z > maxZ) maxZ = hi.z;
            }

            if (cellN <= 0)
            {
                minX = minY = minZ = 0f;
                maxX = maxY = maxZ = 0f;
            }

            _minX = minX;
            _minY = minY;
            _minZ = minZ;
            _maxX = maxX;
            _maxY = maxY;
            _maxZ = maxZ;
            _packedCount = 0;
            _hasWindow = false;
            _windowDirty = true;
            EnsureGpu();
            RebuildBakeWindow(ResolveGrassFocus());
        }

        public void DrawForCamera(Camera cam)
        {
            if (cam == null || !DebugDrawEnabled || !_bakeLoaded)
                return;
            EnsureGpu();
            if (!_gpuReady || _packedCount <= 0)
                return;
            _bindDrawCamera = cam;
            try
            {
                CullAndDraw(cam);
            }
            finally
            {
                _bindDrawCamera = null;
            }
        }

        void LateUpdate()
        {
            using (MarkerLateUpdate.Auto())
            {
                if (!DebugDrawEnabled || !_bakeLoaded)
                {
                    LastVisibleCount = 0;
                    return;
                }

                var cam = Camera.main;
                if (cam == null)
                {
                    LastVisibleCount = 0;
                    return;
                }

                MaybeRebuildBakeWindow(ResolveGrassFocus());
                LastVisibleCount = 0;
                EnsureGpu();
                PushWindGlobals();
                if (!_gpuReady || _packedCount <= 0)
                    return;

                var camTf = cam.transform;
                bool settled = _hasGrassView
                    && (camTf.position - _lastGrassCamPos).sqrMagnitude <= 0.04f
                    && Quaternion.Angle(camTf.rotation, _lastGrassCamRot) <= 0.75f;
                // DrawMeshInstancedIndirect only scopes the submit when its camera
                // argument is set; leaving it null makes the planar mirror probe
                // re-draw the whole grass window into the reflection RT. Bind the
                // main camera for both the fresh and the replay path.
                _bindDrawCamera = cam;
                try
                {
                    if (_hasLastGrassSubmit && settled)
                        ReplayLastGrassDraw();
                    else
                        CullAndDraw(cam);
                }
                finally
                {
                    _bindDrawCamera = null;
                }
            }
        }

        void OnDestroy()
        {
            ReleaseBuffers();
            if (_ownsBladeMesh)
            {
                DestroyOwnedMeshes(_variantMeshes);
                DestroyOwnedMeshes(_farVariantMeshes);
            }

            _variantMeshes = null;
            _farVariantMeshes = null;
            DestroyVariantMaterials();
            if (_ownsMaterial && _material != null)
                Destroy(_material);
            _material = null;
        }

        Vector3 ResolveGrassFocus()
        {
            var cam = Camera.main;
            if (cam != null)
                return cam.transform.position;
            return new Vector3((_minX + _maxX) * 0.5f, 0f, (_minZ + _maxZ) * 0.5f);
        }

        void PushWindGlobals()
        {
            Vector3 dir = windDirection.sqrMagnitude > 1e-6f ? windDirection.normalized : Vector3.right;
            // Get effective wind strength from weather system. When weather is not Wind, grass should not sway.
            float effectiveWind = GetWeatherWindStrength();
            if (_windGlobalsValid
                && (dir - _lastWindDir).sqrMagnitude < 1e-8f
                && Mathf.Abs(effectiveWind - _lastWindStrength) < 1e-5f
                && Mathf.Abs(maxDrawDistance - _lastMaxDrawGlobal) < 1e-4f)
                return;

            Shader.SetGlobalVector(WindDirId, dir);
            Shader.SetGlobalFloat(WindStrengthId, effectiveWind);
            Shader.SetGlobalFloat(MaxDrawId, maxDrawDistance);
            _lastWindDir = dir;
            _lastWindStrength = effectiveWind;
            _lastMaxDrawGlobal = maxDrawDistance;
            _windGlobalsValid = true;
        }

        /// <summary>
        /// Get wind strength based on current weather. When weather is not Wind type, returns 0
        /// so grass doesn't sway without wind.
        /// </summary>
        float GetWeatherWindStrength()
        {
            var weather = WeatherService.Ensure();
            // VisualWind01 already eases across a weather change (0 when not wind).
            return weather.VisualWind01 * windStrength;
        }

        static float HorizontalDistXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        int BakeCellCount(int i)
        {
            if (_bakeLoaded && _bakeData?.Cells != null && (uint)i < (uint)_bakeData.Cells.Length)
                return _bakeData.Cells[i].Count;
            return 0;
        }

        float WindowPackRadiusMeters() => Mathf.Max(maxDrawDistance, 8f) + WindowPackPaddingMeters;

        void MaybeRebuildBakeWindow(Vector3 focus)
        {
            bool need = _windowDirty || !_hasWindow || _packedCount <= 0;
            if (!need && _hasWindow)
                need = HorizontalDistXZ(focus, _windowCenter) >= WindowRebuildMeters;
            if (need)
                RebuildBakeWindow(focus);
        }

        void RebuildBakeWindow(Vector3 focus)
        {
            int cellN = _cellCountX * _cellCountZ;
            if (cellN <= 0 || !_bakeLoaded || _bakeData?.Cells == null)
            {
                _windowDirty = false;
                _packedCount = 0;
                return;
            }

            if (_cellOrder.Length != cellN)
            {
                _cellOrder = new int[cellN];
                _cellDistSq = new float[cellN];
            }

            float maxDistSq = WindowPackRadiusMeters() * WindowPackRadiusMeters();
            int n = 0;
            for (int i = 0; i < cellN; i++)
            {
                int count = BakeCellCount(i);
                if (count <= 0)
                    continue;
                Vector3 c = _cellCenters[i];
                float dx = c.x - focus.x;
                float dz = c.z - focus.z;
                float dSq = dx * dx + dz * dz;
                if (dSq > maxDistSq)
                    continue;
                _cellOrder[n] = i;
                _cellDistSq[n] = dSq;
                n++;
            }

            if (n > 1)
                System.Array.Sort(_cellDistSq, _cellOrder, 0, n);

            int cap = Mathf.Max(0, maxTotalVisible);
            int take = 0;
            int packed = 0;
            for (int i = 0; i < n; i++)
            {
                int count = BakeCellCount(_cellOrder[i]);
                int stride = GrassPackStride(_cellDistSq[i]);
                int add = (count + stride - 1) / stride;
                if (packed > 0 && packed + add > cap)
                    break;
                packed += add;
                take = i + 1;
                if (packed >= cap)
                    break;
            }

            EnsureWindowPackLists();
            EnsurePackedCellTables(take);
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            for (int v = 0; v < vc; v++)
                _windowPackLists[v].Clear();

            int remaining = cap;
            for (int i = 0; i < take && remaining > 0; i++)
                PackOneBakeCell(_cellOrder[i], focus, i, ref remaining);

            for (int v = 0; v < vc; v++)
            {
                int count = _windowPackLists[v].Count;
                _pendingPackedV[v] = count;
                if (count <= 0)
                    continue;
                if (_windowPackUpload[v] == null || _windowPackUpload[v].Length < count)
                    _windowPackUpload[v] = new Vector4[Mathf.NextPowerOfTwo(count)];
                _windowPackLists[v].CopyTo(_windowPackUpload[v]);
            }

            CommitPackedWindow(focus, take);
            UploadPackedVariantBuffers();
            _windowDirty = false;
        }

        void EnsureWindowPackLists()
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            if (_windowPackLists != null && _windowPackLists.Length == vc)
                return;
            _windowPackLists = new List<Vector4>[vc];
            _windowPackUpload = new Vector4[vc][];
            for (int v = 0; v < vc; v++)
            {
                _windowPackLists[v] = new List<Vector4>(4096);
                _windowPackUpload[v] = System.Array.Empty<Vector4>();
            }
        }

        void EnsurePackedCellTables(int take)
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            if (take <= 0)
                return;
            if (_packOffsetsV == null || _packOffsetsV.Length != vc)
            {
                _packOffsetsV = new int[vc][];
                _packCountsV = new int[vc][];
            }

            if (_packCellIds.Length < take)
                _packCellIds = new int[take];
            for (int v = 0; v < vc; v++)
            {
                if (_packOffsetsV[v] == null || _packOffsetsV[v].Length < take)
                {
                    int cap = Mathf.Max(take, 16);
                    _packOffsetsV[v] = new int[cap];
                    _packCountsV[v] = new int[cap];
                }
            }
        }

        void PackOneBakeCell(int cell, Vector3 focus, int packedIdx, ref int remaining)
        {
            if (remaining <= 0)
                return;
            if (_packCellIds.Length > packedIdx)
                _packCellIds[packedIdx] = cell;
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            for (int v = 0; v < vc; v++)
                _packOffsetsV[v][packedIdx] = _windowPackLists[v].Count;

            int count = BakeCellCount(cell);
            int stride = GrassPackStride(HorizontalDistSqXZ(focus, cell));
            for (int j = 0; j < count && remaining > 0; j += stride)
            {
                if (!TryReadWindowPlant(cell, j, out Vector3 p, out int variant, out byte dry))
                    continue;
                if ((uint)variant >= (uint)OilTerrainGrassBladeMesh.VariantCount)
                    variant = (int)OilTerrainGrassBladeMesh.Variant.Patch;
                int topo = OilTerrainGrassPacked.TopologyOf(variant);
                _windowPackLists[topo].Add(OilTerrainGrassPacked.Pack(p, variant, dry));
                remaining--;
            }

            for (int v = 0; v < vc; v++)
                _packCountsV[v][packedIdx] = _windowPackLists[v].Count - _packOffsetsV[v][packedIdx];
        }

        float HorizontalDistSqXZ(Vector3 focus, int cell)
        {
            if ((uint)cell >= (uint)_cellCenters.Length)
                return 0f;
            float dx = _cellCenters[cell].x - focus.x;
            float dz = _cellCenters[cell].z - focus.z;
            return dx * dx + dz * dz;
        }

        static int GrassPackStride(float distSq)
        {
            float full = FarGrassFullDensityMeters * FarGrassFullDensityMeters;
            return distSq > full ? 2 : 1;
        }

        bool TryReadWindowPlant(int cell, int j, out Vector3 p, out int variant, out byte dry)
        {
            p = default;
            variant = 0;
            dry = 0;
            if (!_bakeLoaded || _bakeData?.Cells == null || _bakeData.Positions == null)
                return false;
            if ((uint)cell >= (uint)_bakeData.Cells.Length)
                return false;
            var c = _bakeData.Cells[cell];
            int idx = c.Offset + j;
            if ((uint)idx >= (uint)_bakeData.Positions.Length)
                return false;
            p = _bakeData.Positions[idx];
            variant = _bakeData.Variants != null && idx < _bakeData.Variants.Length
                ? _bakeData.Variants[idx]
                : OilTerrainGrassBladeMesh.PickVariant(p);
            dry = _bakeData.Dryness != null && idx < _bakeData.Dryness.Length
                ? _bakeData.Dryness[idx]
                : (byte)0;
            return true;
        }

        void CommitPackedWindow(Vector3 focus, int take)
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            int packedSum = 0;
            for (int v = 0; v < vc; v++)
            {
                _packedV[v] = _pendingPackedV[v];
                packedSum += _pendingPackedV[v];
            }

            _packedCount = packedSum;
            PublishPackedCellTables(take);
            _windowCenter = focus;
            _hasWindow = true;
            _hasLastGrassSubmit = false;
        }

        void PublishPackedCellTables(int count)
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            if (count <= 0)
            {
                _packedCellCount = 0;
                return;
            }

            if (_cellOffsetsV == null || _cellOffsetsV.Length != vc)
            {
                _cellOffsetsV = new int[vc][];
                _cellCountsV = new int[vc][];
            }

            if (_packedCellIds.Length < count)
                _packedCellIds = new int[count];
            System.Array.Copy(_packCellIds, _packedCellIds, count);
            for (int v = 0; v < vc; v++)
            {
                if (_cellOffsetsV[v] == null || _cellOffsetsV[v].Length < count)
                {
                    int cap = Mathf.Max(count, 16);
                    _cellOffsetsV[v] = new int[cap];
                    _cellCountsV[v] = new int[cap];
                }

                System.Array.Copy(_packOffsetsV[v], _cellOffsetsV[v], count);
                System.Array.Copy(_packCountsV[v], _cellCountsV[v], count);
            }

            _packedCellCount = count;
        }

        void UploadPackedVariantBuffers()
        {
            SyncGrassIndirectMode();
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            for (int v = 0; v < vc; v++)
            {
                int n = Mathf.Max(_pendingPackedV[v], 1);
                EnsureBuffer(ref _allPosV[v], n, sizeof(float) * 4, ComputeBufferType.Structured);
                if (_pendingPackedV[v] > 0
                    && _windowPackUpload[v] != null
                    && _windowPackUpload[v].Length >= _pendingPackedV[v])
                    _allPosV[v].SetData(_windowPackUpload[v], 0, 0, _pendingPackedV[v]);
                EnsureGrassVisible(ref _visibleIdV[v], n);
                if (_variantMats != null && _variantMats[v] != null && _allPosV[v] != null)
                {
                    _variantMats[v].SetBuffer("_AllInstancesPosWSBuffer", _allPosV[v]);
                    _variantMats[v].SetBuffer("_VisibleInstanceOnlyTransformIDBuffer", _visibleIdV[v]);
                }
            }
        }

        void CollectVisiblePackedCells(Camera cam)
        {
            _visibleCellIds.Clear();
            if (cam == null || _packedCellCount <= 0)
                return;

            float far = cam.farClipPlane;
            cam.farClipPlane = maxDrawDistance;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
            cam.farClipPlane = far;

            Vector3 camPos = cam.transform.position;
            float maxDistSq = maxDrawDistance * maxDrawDistance;
            if (_visibleCellDistSq.Length < _packedCellCount)
                _visibleCellDistSq = new float[_packedCellCount];

            for (int i = 0; i < _packedCellCount; i++)
            {
                if (PackedCellPlantCount(i) <= 0)
                    continue;
                int cell = _packedCellIds[i];
                if ((uint)cell >= (uint)_cellCenters.Length)
                    continue;
                Vector3 c = _cellCenters[cell];
                float dx = c.x - camPos.x;
                float dz = c.z - camPos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq > maxDistSq)
                    continue;
                Vector3 ext = _cellExtents[cell];
                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, new Bounds(c, ext * 2f)))
                    continue;
                _visibleCellDistSq[_visibleCellIds.Count] = dSq;
                _visibleCellIds.Add(i);
            }

            int n = _visibleCellIds.Count;
            if (n <= 1)
                return;

            if (_visibleSortOrder.Length < n)
                _visibleSortOrder = new int[n];
            for (int i = 0; i < n; i++)
                _visibleSortOrder[i] = _visibleCellIds[i];
            System.Array.Sort(_visibleCellDistSq, _visibleSortOrder, 0, n);

            int budgetLeft = maxTotalVisible > 0 ? maxTotalVisible : int.MaxValue;
            int keep = 0;
            for (int i = 0; i < n && budgetLeft > 0; i++)
            {
                budgetLeft -= PackedCellPlantCount(_visibleSortOrder[i]);
                keep++;
            }

            _visibleCellIds.Clear();
            for (int i = 0; i < keep; i++)
                _visibleCellIds.Add(_visibleSortOrder[i]);
            _visibleCellIds.Sort();
        }

        int PackedCellPlantCount(int packed)
        {
            if (_cellCountsV == null)
                return 0;
            int sum = 0;
            for (int v = 0; v < OilTerrainGrassBladeMesh.TopologyCount; v++)
            {
                if (_cellCountsV[v] != null && (uint)packed < (uint)_cellCountsV[v].Length)
                    sum += _cellCountsV[v][packed];
            }

            return sum;
        }

        void CullAndDraw(Camera cam)
        {
            using (MarkerCullAndDraw.Auto())
            {
                CollectVisiblePackedCells(cam);
                if (_visibleCellIds.Count == 0)
                {
                    _hasLastGrassSubmit = false;
                    return;
                }

                Matrix4x4 vp = cam.projectionMatrix * cam.worldToCameraMatrix;
                var renderBound = BuildGrassRenderBound();
                bool useGpu = cullCompute != null && ResolveCullKernel() >= 0;
                float nearMax = FarGrassFullDensityMeters;

                if (useGpu)
                {
                    EnsureKeepOutBuffer();
                    // Shared frustum/matrix globals: set ONCE per frame instead of per-variant.
                    cullCompute.SetMatrix("_VPMatrix", vp);
                    cullCompute.SetVector("_CamPos", cam.transform.position);
                    cullCompute.SetFloat("_MinPlantY", minPlantWorldY);
                    BindKeepOutForKernel(ResolveCullKernel());
                }

                const int vc = OilTerrainGrassBladeMesh.TopologyCount;
                int visibleSum = 0;
                for (int v = 0; v < vc; v++)
                {
                    Mesh nearMesh = ResolveVariantMesh(v, far: false);
                    Mesh farMesh = ResolveVariantMesh(v, far: true);
                    if (_packedV[v] <= 0 || nearMesh == null)
                        continue;
                    if (_variantMats == null || _variantMats[v] == null || _allPosV[v] == null)
                        continue;

                    EnsureGrassArgs(ref _argsV[v]);
                    EnsureGrassArgs(ref _argsFarV[v]);
                    EnsureGrassVisible(ref _visibleIdV[v], _packedV[v]);
                    EnsureGrassVisible(ref _visibleIdFarV[v], _packedV[v]);
                    if (_visibleIdV[v] == null || _argsV[v] == null)
                        continue;

                    BindGrassGlobals(_variantMats[v], v);
                    if (useGpu)
                    {
                        int kernel = ResolveCullKernel();
                        BindKeepOutForKernel(kernel);
                        cullCompute.SetBuffer(kernel, "_AllInstancesPosWSBuffer", _allPosV[v]);
                        cullCompute.SetInt("_VariantKind", v);
                        // One dispatch per band over the full packed window; CS does frustum + distance.
                        // Mobile: near band only — far blades are tiny after renderScale.
                        float bandMax = Application.isMobilePlatform
                            ? maxDrawDistance
                            : nearMax;
                        visibleSum += DispatchPackedBand(
                            v, nearMesh, renderBound, _visibleIdV[v], _argsV[v],
                            0f, bandMax);
                        if (!Application.isMobilePlatform
                            && farMesh != null
                            && _visibleIdFarV[v] != null
                            && _argsFarV[v] != null)
                        {
                            visibleSum += DispatchPackedBand(
                                v, farMesh, renderBound, _visibleIdFarV[v], _argsFarV[v],
                                nearMax, maxDrawDistance);
                        }
                    }
                    else
                    {
                        float bandMax = Application.isMobilePlatform
                            ? maxDrawDistance
                            : nearMax;
                        visibleSum += CpuCullAndDrawBand(
                            v, nearMesh, renderBound, cam, vp, far: false, 0f, bandMax);
                        if (!Application.isMobilePlatform && farMesh != null)
                        {
                            visibleSum += CpuCullAndDrawBand(
                                v, farMesh, renderBound, cam, vp, far: true,
                                nearMax, maxDrawDistance);
                        }
                    }
                }

                _hasLastGrassSubmit = visibleSum > 0;
                if (cam != null)
                {
                    _lastGrassCamPos = cam.transform.position;
                    _lastGrassCamRot = cam.transform.rotation;
                    _hasGrassView = true;
                }

                LastVisibleCount += visibleSum;
                if (!_loggedDraw)
                {
                    _loggedDraw = true;
                    Log.Info(
                        $"[OilTerrainGrass] draw packed={_packedCount} visJobs={visibleSum} " +
                        $"cells={_visibleCellIds.Count} gpu={useGpu} dist={maxDrawDistance:F0}");
                }
            }
        }

        int DispatchPackedBand(
            int variant,
            Mesh mesh,
            Bounds renderBound,
            ComputeBuffer visibleIds,
            ComputeBuffer args,
            float minDraw,
            float maxDraw)
        {
            int count = _packedV[variant];
            if (count <= 0 || mesh == null || visibleIds == null || args == null)
                return 0;

            int kernel = ResolveCullKernel();
            BindGrassCullOutputs(kernel, _useAdd, visibleIds, args, mesh);
            cullCompute.SetFloat("_MinDrawDistance", minDraw);
            cullCompute.SetFloat("_MaxDrawDistance", maxDraw);
            cullCompute.SetInt("_StartOffset", 0);
            cullCompute.SetInt("_InstanceCount", count);
            cullCompute.Dispatch(kernel, Mathf.CeilToInt(count / 64f), 1, 1);
            SubmitIndirectDraw(variant, mesh, renderBound, visibleIds, args, _useAdd);
            return count;
        }

        int CpuCullAndDrawBand(
            int variant,
            Mesh mesh,
            Bounds renderBound,
            Camera cam,
            Matrix4x4 vp,
            bool far,
            float minDraw,
            float maxDraw)
        {
            if (_windowPackUpload == null || mesh == null || _windowPackUpload[variant] == null)
                return 0;

            ComputeBuffer visibleIds = far ? _visibleIdFarV[variant] : _visibleIdV[variant];
            ComputeBuffer args = far ? _argsFarV[variant] : _argsV[variant];
            if (visibleIds == null || args == null)
                return 0;

            _cpuVisibleScratch.Clear();
            Vector3 camPos = cam.transform.position;
            int count = _packedV[variant];
            for (int j = 0; j < count; j++)
            {
                Vector4 inst = _windowPackUpload[variant][j];
                Vector3 pos = new Vector3(inst.x, inst.y, inst.z);
                if (pos.y < minPlantWorldY)
                    continue;
                float dx = pos.x - camPos.x;
                float dz = pos.z - camPos.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < minDraw || d > maxDraw)
                    continue;
                Vector4 clip = vp * new Vector4(pos.x, pos.y, pos.z, 1f);
                float w = clip.w;
                float cullR = d < 10f ? 5.5f : 2f;
                if (w < -cullR)
                    continue;
                float aw = Mathf.Max(Mathf.Abs(w), 0.25f);
                if (Mathf.Abs(clip.x) <= aw * 1.15f + cullR
                    && Mathf.Abs(clip.y) <= aw * 2.15f + cullR
                    && clip.z <= aw + cullR)
                    _cpuVisibleScratch.Add((uint)j);
            }

            if (_cpuVisibleScratch.Count == 0)
                return 0;

            if (far)
                EnsureGrassVisible(ref _visibleIdFarV[variant], Mathf.Max(_cpuVisibleScratch.Count, 1));
            else
                EnsureGrassVisible(ref _visibleIdV[variant], Mathf.Max(_cpuVisibleScratch.Count, 1));
            visibleIds = far ? _visibleIdFarV[variant] : _visibleIdV[variant];

            visibleIds.SetData(_cpuVisibleScratch);
            ResetGrassArgs(args, mesh);
            _argsCache[0] = mesh.GetIndexCount(0);
            _argsCache[1] = (uint)_cpuVisibleScratch.Count;
            _argsCache[2] = mesh.GetIndexStart(0);
            _argsCache[3] = mesh.GetBaseVertex(0);
            _argsCache[4] = 0;
            args.SetData(_argsCache);
            DrawIndirect(variant, mesh, renderBound, visibleIds, args);
            return _cpuVisibleScratch.Count;
        }

        void BindGrassCullOutputs(
            int kernel, bool add, ComputeBuffer visibleIds, ComputeBuffer args, Mesh mesh)
        {
            if (add)
            {
                ResetGrassArgs(args, mesh);
                cullCompute.SetBuffer(kernel, "_VisibleIdsAdd", visibleIds);
                cullCompute.SetBuffer(kernel, "_VisibleArgs", args);
            }
            else
            {
                visibleIds.SetCounterValue(0);
                cullCompute.SetBuffer(kernel, "_VisibleInstancesOnlyPosWSIDBuffer", visibleIds);
            }
        }

        void SubmitIndirectDraw(
            int variant, Mesh mesh, Bounds renderBound,
            ComputeBuffer visibleIds, ComputeBuffer args, bool add)
        {
            if (!add)
            {
                _argsCache[0] = mesh.GetIndexCount(0);
                _argsCache[1] = 0;
                _argsCache[2] = mesh.GetIndexStart(0);
                _argsCache[3] = mesh.GetBaseVertex(0);
                _argsCache[4] = 0;
                args.SetData(_argsCache);
                ComputeBuffer.CopyCount(visibleIds, args, sizeof(uint));
            }

            DrawIndirect(variant, mesh, renderBound, visibleIds, args);
        }

        void DrawIndirect(
            int variant, Mesh mesh, Bounds renderBound,
            ComputeBuffer visibleIds, ComputeBuffer args)
        {
            Material mat = _variantMats[variant];
            if (mat == null || mesh == null || args == null || visibleIds == null)
                return;
            if (_grassMpb == null)
                _grassMpb = new MaterialPropertyBlock();
            _grassMpb.Clear();
            _grassMpb.SetBuffer("_AllInstancesPosWSBuffer", _allPosV[variant]);
            _grassMpb.SetBuffer("_VisibleInstanceOnlyTransformIDBuffer", visibleIds);
            Graphics.DrawMeshInstancedIndirect(
                mesh, 0, mat, renderBound, args,
                0, _grassMpb, ShadowCastingMode.Off, false, 0, _bindDrawCamera);
        }

        void ReplayLastGrassDraw()
        {
            var renderBound = BuildGrassRenderBound();
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            for (int v = 0; v < vc; v++)
            {
                if (_packedV[v] <= 0 || _variantMats == null || _variantMats[v] == null)
                    continue;
                Mesh nearMesh = ResolveVariantMesh(v, far: false);
                if (nearMesh != null && _argsV[v] != null && _visibleIdV[v] != null)
                    DrawIndirect(v, nearMesh, renderBound, _visibleIdV[v], _argsV[v]);

                Mesh farMesh = ResolveVariantMesh(v, far: true);
                if (farMesh != null && _argsFarV[v] != null && _visibleIdFarV[v] != null)
                    DrawIndirect(v, farMesh, renderBound, _visibleIdFarV[v], _argsFarV[v]);
            }
        }

        Bounds BuildGrassRenderBound()
        {
            var center = new Vector3(
                (_minX + _maxX) * 0.5f,
                (_minY + _maxY) * 0.5f,
                (_minZ + _maxZ) * 0.5f);
            var size = new Vector3(
                Mathf.Max(_maxX - _minX, CellSize) + 8f,
                Mathf.Max(_maxY - _minY, 16f) + 8f,
                Mathf.Max(_maxZ - _minZ, CellSize) + 8f);
            return new Bounds(center, size);
        }

        Mesh ResolveVariantMesh(int variant, bool far)
        {
            Mesh[] src = far ? _farVariantMeshes : _variantMeshes;
            if (src == null || (uint)variant >= (uint)src.Length)
                return null;
            return src[variant];
        }

        void EnsureGpu()
        {
            if (_gpuReady && _variantMats != null && _variantMeshes != null)
                return;

            if (_variantMeshes == null)
                RebuildTopologyMeshes();

            if (_material == null)
            {
                Shader shader = materialOverride != null
                    ? materialOverride.shader
                    : Shader.Find("CustomSRP/OilTerrainGrass");
                if (shader == null)
                    return;
                if (materialOverride != null)
                {
                    _material = new Material(materialOverride) { name = materialOverride.name + "_Runtime" };
                    _ownsMaterial = true;
                }
                else
                {
                    _material = new Material(shader) { name = "OilTerrainGrass_Runtime" };
                    _ownsMaterial = true;
                }

                ApplyRuntimeLook(_material);
            }

            if (_variantTex == null)
                RebuildVariantTextures();

            if (_variantMats == null)
            {
                const int vc = OilTerrainGrassBladeMesh.TopologyCount;
                _variantMats = new Material[vc];
                for (int v = 0; v < vc; v++)
                {
                    _variantMats[v] = new Material(_material) { name = $"OilTerrainGrass_T{v}" };
                    ApplyTopologyLook(_variantMats[v], v);
                    BindGrassGlobals(_variantMats[v], v);
                }
            }

            if (cullCompute != null)
            {
                if (_kernel < 0)
                    _kernel = cullCompute.FindKernel("CSMain");
                if (_kernelAdd < 0)
                    _kernelAdd = cullCompute.FindKernel("CSMainAdd");
            }

            SyncGrassIndirectMode();
            const int n = OilTerrainGrassBladeMesh.TopologyCount;
            if (_argsFarV == null)
                _argsFarV = new ComputeBuffer[n];
            if (_visibleIdFarV == null)
                _visibleIdFarV = new ComputeBuffer[n];
            for (int v = 0; v < n; v++)
            {
                EnsureGrassArgs(ref _argsV[v]);
                EnsureGrassArgs(ref _argsFarV[v]);
            }

            _gpuReady = true;
        }

        int ResolveCullKernel()
        {
            SyncGrassIndirectMode();
            return _useAdd ? _kernelAdd : _kernel;
        }

        void SyncGrassIndirectMode()
        {
            _useAdd = _kernelAdd >= 0;
        }

        void RebuildTopologyMeshes()
        {
            DestroyOwnedMeshes(_variantMeshes);
            DestroyOwnedMeshes(_farVariantMeshes);
            _variantMeshes = OilTerrainGrassBladeMesh.BuildAll(_bladeSegments);
            _farVariantMeshes = OilTerrainGrassBladeMesh.BuildAll(FarBladeSegments);
            _ownsBladeMesh = true;
        }

        void DestroyOwnedMeshes(Mesh[] meshes)
        {
            if (meshes == null)
                return;
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh m = meshes[i];
                if (m == null)
                    continue;
                // OBJ/FBX 共享 Mesh 不能 Destroy。
                if (OilTerrainGrassBladeMesh.IsImportedMesh(m))
                    continue;
                Destroy(m);
            }
        }

        void DestroyVariantMaterials()
        {
            if (_variantMats == null)
                return;
            for (int i = 0; i < _variantMats.Length; i++)
            {
                if (_variantMats[i] != null)
                    Destroy(_variantMats[i]);
            }

            _variantMats = null;
        }

        static void ApplyRuntimeLook(Material mat)
        {
            if (mat == null)
                return;
            if (mat.HasProperty("_GrassWidth") && mat.GetFloat("_GrassWidth") < 0.08f)
                mat.SetFloat("_GrassWidth", 1f);
            if (mat.HasProperty("_GrassHeight") && mat.GetFloat("_GrassHeight") < 0.08f)
                mat.SetFloat("_GrassHeight", 1f);
            if (mat.HasProperty("_WindStrength") && mat.GetFloat("_WindStrength") < 0.2f)
                mat.SetFloat("_WindStrength", 0.55f);
            if (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") < 0.05f)
                mat.SetFloat("_AlphaClip", 0.34f);
            mat.enableInstancing = true;
        }

        void ApplyTopologyLook(Material mat, int topology)
        {
            ApplyRuntimeLook(mat);
            if (mat.HasProperty("_Billboard"))
                mat.SetFloat(
                    "_Billboard",
                    topology == (int)OilTerrainGrassBladeMesh.Variant.Billboard ? 1f : 0f);
            if (mat.HasProperty("_UseTexColor"))
                mat.SetFloat("_UseTexColor", 1f);
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", OilTerrainGrassBladeMesh.IsLavender(topology) ? 0.28f : 0.34f);

            if (mat.HasProperty("_WindStrength"))
            {
                if (OilTerrainGrassBladeMesh.IsClover(topology))
                    mat.SetFloat("_WindStrength", 0.22f);
                else if (OilTerrainGrassBladeMesh.IsLavender(topology))
                    mat.SetFloat("_WindStrength", 0.38f);
                else if (OilTerrainGrassBladeMesh.UsesAtlasColor(topology))
                    mat.SetFloat("_WindStrength", 0.42f);
            }

            if ((uint)topology < (uint)OilTerrainGrassBladeMesh.HeightScales.Length)
            {
                if (mat.HasProperty("_GrassHeight"))
                    mat.SetFloat("_GrassHeight", OilTerrainGrassBladeMesh.HeightScales[topology]);
                if (mat.HasProperty("_GrassWidth"))
                    mat.SetFloat("_GrassWidth", OilTerrainGrassBladeMesh.WidthScales[topology]);
            }

            mat.SetVectorArray("_GrassAtlasRects", OilTerrainGrassAtlas.RectsForTopology(topology));
            mat.enableInstancing = true;
        }

        Texture TextureForVariant(int topology)
        {
            if (_variantTex != null
                && (uint)topology < (uint)_variantTex.Length
                && _variantTex[topology] != null)
                return _variantTex[topology];
            return atlasTexture;
        }

        void BindGrassGlobals(Material mat, int topology)
        {
            if (mat == null)
                return;
            Texture tex = TextureForVariant(topology);
            if (tex != null && mat.HasProperty("_GrassTexture"))
                mat.SetTexture("_GrassTexture", tex);
            mat.SetVectorArray("_GrassAtlasRects", OilTerrainGrassAtlas.RectsForTopology(topology));
            if (_allPosV != null && (uint)topology < (uint)_allPosV.Length && _allPosV[topology] != null)
                mat.SetBuffer("_AllInstancesPosWSBuffer", _allPosV[topology]);
            if (_visibleIdV != null && (uint)topology < (uint)_visibleIdV.Length && _visibleIdV[topology] != null)
                mat.SetBuffer("_VisibleInstanceOnlyTransformIDBuffer", _visibleIdV[topology]);
        }

        void ApplyAtlasToMaterials()
        {
            if (_variantMats == null)
                return;
            for (int i = 0; i < _variantMats.Length; i++)
                BindGrassGlobals(_variantMats[i], i);
        }

        static void EnsureBuffer(ref ComputeBuffer buffer, int count, int stride, ComputeBufferType type)
        {
            count = Mathf.Max(1, count);
            if (buffer != null && buffer.count >= count && buffer.stride == stride)
                return;
            buffer?.Release();
            buffer = new ComputeBuffer(count, stride, type);
        }

        void EnsureGrassVisible(ref ComputeBuffer buffer, int count)
        {
            count = Mathf.Max(1, count);
            ComputeBufferType type = _useAdd
                ? ComputeBufferType.Structured
                : ComputeBufferType.Append;
            if (buffer != null && buffer.count >= count)
                return;
            buffer?.Release();
            buffer = new ComputeBuffer(count, sizeof(uint), type);
            if (!_useAdd)
                buffer.SetCounterValue(0);
        }

        void EnsureGrassArgs(ref ComputeBuffer args)
        {
            if (args != null)
                return;
            args = new ComputeBuffer(1, ArgsStride, ComputeBufferType.IndirectArguments);
        }

        void ResetGrassArgs(ComputeBuffer args, Mesh mesh)
        {
            if (args == null || mesh == null)
                return;
            _argsCache[0] = mesh.GetIndexCount(0);
            _argsCache[1] = 0;
            _argsCache[2] = mesh.GetIndexStart(0);
            _argsCache[3] = mesh.GetBaseVertex(0);
            _argsCache[4] = 0;
            args.SetData(_argsCache);
        }

        void ReleaseBuffers()
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            for (int v = 0; v < vc; v++)
            {
                if (_allPosV != null)
                {
                    _allPosV[v]?.Release();
                    _allPosV[v] = null;
                }

                if (_visibleIdV != null)
                {
                    _visibleIdV[v]?.Release();
                    _visibleIdV[v] = null;
                }

                if (_visibleIdFarV != null)
                {
                    _visibleIdFarV[v]?.Release();
                    _visibleIdFarV[v] = null;
                }

                if (_argsV != null)
                {
                    _argsV[v]?.Release();
                    _argsV[v] = null;
                }

                if (_argsFarV != null)
                {
                    _argsFarV[v]?.Release();
                    _argsFarV[v] = null;
                }
            }

            _keepOutGrid?.Release();
            _keepOutGrid = null;
            _keepOutEnabled = false;
            _keepOutLoadTried = false;
            _gpuReady = false;
            _windGlobalsValid = false;
        }

        /// <summary>
        /// Metal/Vulkan 要求 kernel 声明的 StructuredBuffer 必须绑定；
        /// KeepOut 关闭时也要挂一个占位 buffer。
        /// </summary>
        void EnsureKeepOutBuffer()
        {
            if (!_keepOutLoadTried)
            {
                _keepOutLoadTried = true;
                TryLoadKeepOutFromResources();
            }

            if (_keepOutGrid != null)
                return;

            _keepOutGrid = new ComputeBuffer(1, sizeof(uint));
            _keepOutGrid.SetData(new uint[] { 0 });
            _keepOutEnabled = false;
            _keepOutRes = 0;
        }

        void BindKeepOutForKernel(int kernel)
        {
            EnsureKeepOutBuffer();
            cullCompute.SetBuffer(kernel, "_KeepOutGrid", _keepOutGrid);
            cullCompute.SetInt("_KeepOutEnabled", _keepOutEnabled ? 1 : 0);
            cullCompute.SetInt("_KeepOutRes", _keepOutRes);
            cullCompute.SetFloat("_KeepOutOriginX", _keepOutOriginX);
            cullCompute.SetFloat("_KeepOutOriginZ", _keepOutOriginZ);
            cullCompute.SetFloat("_KeepOutCell", _keepOutCell);
        }

        public bool TryLoadKeepOut(byte[] bytes)
        {
            if (!OilTerrainGrassKeepOutBake.TryRead(bytes, out var data) || data.Mask == null)
                return false;

            int len = data.Res * data.Res;
            var upload = new uint[len];
            for (int i = 0; i < len; i++)
                upload[i] = data.Mask[i];

            _keepOutGrid?.Release();
            _keepOutGrid = new ComputeBuffer(len, sizeof(uint));
            _keepOutGrid.SetData(upload);
            _keepOutRes = data.Res;
            _keepOutOriginX = data.OriginX;
            _keepOutOriginZ = data.OriginZ;
            _keepOutCell = data.WorldSize / data.Res;
            _keepOutEnabled = _keepOutRes > 0 && _keepOutCell > 1e-4f;
            _keepOutLoadTried = true;
            Log.Info(
                $"[OilTerrainGrass] KeepOut gpu res={_keepOutRes} cell={_keepOutCell:F2} " +
                $"enabled={_keepOutEnabled}");
            return true;
        }

        void TryLoadKeepOutFromResources()
        {
#if UNITY_EDITOR
            var editorAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(
                OilTerrainGrassKeepOutBake.EditorPath);
            if (editorAsset != null && TryLoadKeepOut(editorAsset.bytes))
                return;
#endif
            var res = Resources.Load<TextAsset>("OilTerrainGrass/Island_GrassKeepOut");
            if (res != null)
                TryLoadKeepOut(res.bytes);
        }
    }
}
