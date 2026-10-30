using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Game;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
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
        const int ShrinkKindPos = 0;
        const int ShrinkKindUpload = 1;
        const int ShrinkKindVisible = 2;
        const int ShrinkKindVisibleFar = 3;
        const int ShrinkKindCount = 4;
        const long GrassShrinkBudgetMs = 1;

        static readonly int WindDirId = Shader.PropertyToID("_OilGrassWindDir");
        static readonly int WindStrengthId = Shader.PropertyToID("_OilGrassWindStrength");
        static readonly int MaxDrawId = Shader.PropertyToID("_OilGrassMaxDraw");
        static readonly int VpMatrixId = Shader.PropertyToID("_VPMatrix");
        static readonly int CamPosId = Shader.PropertyToID("_CamPos");
        static readonly int MinPlantYId = Shader.PropertyToID("_MinPlantY");
        static readonly int VariantKindId = Shader.PropertyToID("_VariantKind");
        static readonly int MinDrawDistanceId = Shader.PropertyToID("_MinDrawDistance");
        static readonly int MaxDrawDistanceId = Shader.PropertyToID("_MaxDrawDistance");
        static readonly int StartOffsetId = Shader.PropertyToID("_StartOffset");
        static readonly int InstanceCountId = Shader.PropertyToID("_InstanceCount");
        static readonly int AllInstancesPosId = Shader.PropertyToID("_AllInstancesPosWSBuffer");
        static readonly int VisibleIdsAddId = Shader.PropertyToID("_VisibleIdsAdd");
        static readonly int VisibleArgsId = Shader.PropertyToID("_VisibleArgs");
        static readonly int VisibleInstancesOnlyPosId = Shader.PropertyToID("_VisibleInstancesOnlyPosWSIDBuffer");
        static readonly int VisibleInstanceOnlyTransformId = Shader.PropertyToID("_VisibleInstanceOnlyTransformIDBuffer");
        static readonly int KeepOutGridId = Shader.PropertyToID("_KeepOutGrid");
        static readonly int KeepOutEnabledId = Shader.PropertyToID("_KeepOutEnabled");
        static readonly int KeepOutResId = Shader.PropertyToID("_KeepOutRes");
        static readonly int KeepOutOriginXId = Shader.PropertyToID("_KeepOutOriginX");
        static readonly int KeepOutOriginZId = Shader.PropertyToID("_KeepOutOriginZ");
        static readonly int KeepOutCellId = Shader.PropertyToID("_KeepOutCell");
        static readonly int GrassTextureId = Shader.PropertyToID("_GrassTexture");
        static readonly int GrassAtlasRectsId = Shader.PropertyToID("_GrassAtlasRects");
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

        // ComputeShader 是 Resources / 工程资源，退登不会卸。SetBuffer 会握住大 buffer，
        // 不先换成 4 字节占位就 Release，位置缓冲会留在显存里。
        static ComputeBuffer _structuredStub;
        static ComputeBuffer _rawStub;
        static ComputeBuffer _appendStub;

        ComputeBuffer[] _allPosV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _visibleIdV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _visibleIdFarV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _argsV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        ComputeBuffer[] _argsFarV = new ComputeBuffer[OilTerrainGrassBladeMesh.TopologyCount];
        readonly int[] _proceduralCountV = new int[OilTerrainGrassBladeMesh.TopologyCount];
        readonly int[] _proceduralCountFarV = new int[OilTerrainGrassBladeMesh.TopologyCount];
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
        OilTerrainGrassKeepOutBake.Data _keepOutData;
        bool _keepOutEnabled;
        bool _keepOutLoadTried;
        bool _valhallChecked;
        bool _forceCpuCullValhall;
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
        readonly Stopwatch _grassWork = new Stopwatch();
        bool _windowRebuiltThisFrame;
        bool _shrinkQueued;
        int _shrinkCursor;
        int[] _pendingPackedV = new int[OilTerrainGrassBladeMesh.TopologyCount];
        int[][] _packOffsetsV;
        int[][] _packCountsV;
        int[] _packCellIds = System.Array.Empty<int>();
        bool _ownsBladeMesh;
        bool _ownsMaterial;
        bool[] _variantMeshOwned;
        bool[] _farMeshOwned;
        bool _gpuReleased;
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
            {
                MoveToIslandScene(Active.gameObject);
                return Active;
            }
#if UNITY_EDITOR
            OilTerrainGrassBinder existing = FindAnyObjectByType<OilTerrainGrassBinder>();
            if (existing != null)
            {
                MoveToIslandScene(existing.gameObject);
                return existing;
            }
#endif
            // 进岛时 Island 已是激活场景，对象落在这张场景里，退登跟着卸。
            var go = new GameObject("[OilTerrainGrass]");
            OilTerrainGrassBinder created = go.AddComponent<OilTerrainGrassBinder>();
            MoveToIslandScene(go);
            return created;
        }

        /// <summary>激活场景若不是 Island，new GameObject 会落到 Launch，卸岛清不掉显存。</summary>
        static void MoveToIslandScene(GameObject go)
        {
            if (!Application.isPlaying || go == null)
                return;
            string sceneName = Path.GetFileNameWithoutExtension(SceneAssets.Island);
            Scene island = SceneManager.GetSceneByName(sceneName);
            if (!island.IsValid() || !island.isLoaded || go.scene == island)
                return;
            SceneManager.MoveGameObjectToScene(go, island);
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
            if (_gpuReleased || cam == null || !DebugDrawEnabled || !_bakeLoaded)
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
                _windowRebuiltThisFrame = false;
                _grassWork.Restart();
                try
                {
                    TickGrass();
                }
                finally
                {
                    TryShrinkOneGrassPiece();
                }
            }
        }

        void TickGrass()
        {
            if (_gpuReleased || !DebugDrawEnabled || !_bakeLoaded)
            {
                LastVisibleCount = 0;
                return;
            }

            Camera cam = IslandJoystickCamera.ActiveView;
            if (cam == null)
            {
                LastVisibleCount = 0;
                return;
            }

            MaybeRebuildBakeWindow(cam.transform.position);
            LastVisibleCount = 0;
            EnsureGpu();
            PushWindGlobals();
            if (!_gpuReady || _packedCount <= 0)
                return;

            var camTf = cam.transform;
            // 闺蜜机：停稳判定更严，微抖/刚停住也继续复用，避免树密朝向草重算尖刺。
            bool gmj = Game.BoluoApi.BoluoDeviceInfo.IsSkyworthGmjStandard();
            float settlePosSq = gmj ? 0.01f : 0.04f;
            float settleAngle = gmj ? 0.35f : 0.75f;
            bool settled = _hasGrassView
                && (camTf.position - _lastGrassCamPos).sqrMagnitude <= settlePosSq
                && Quaternion.Angle(camTf.rotation, _lastGrassCamRot) <= settleAngle;
            // 闺蜜机标准版：转头/走动/未完全停稳时整段复用上一份草 submit。
            bool gmjThrottle = !settled
                && _hasLastGrassSubmit
                && gmj;
            // DrawMeshInstancedIndirect only scopes the submit when its camera
            // argument is set; leaving it null makes the planar mirror probe
            // re-draw the whole grass window into the reflection RT. Bind the
            // main camera for both the fresh and the replay path.
            _bindDrawCamera = cam;
            try
            {
                if (_hasLastGrassSubmit && (settled || gmjThrottle))
                    ReplayLastGrassDraw();
                else
                    CullAndDraw(cam);
            }
            finally
            {
                _bindDrawCamera = null;
            }
        }

        void OnDestroy()
        {
            // 卸岛走这里。必须在本帧还 GPU：延迟 Destroy 在场景卸载里会被丢掉。
            ReleaseGpu();
            if (Active == this)
                Active = null;
        }

        /// <summary>
        /// 退登 / 卸岛：解开 ComputeShader 上的大 buffer，立刻销毁运行时 Mesh / Material，再 Release。
        /// 贴图引用一并丢掉，随后的 Resources.UnloadUnusedAssets 才能把图集显存收回。
        /// </summary>
        public void ReleaseGpu()
        {
            if (_gpuReleased)
                return;
            _gpuReleased = true;

            long bytes = SumGpuBytes();
            if (_grassMpb != null)
            {
                _grassMpb.Clear();
                _grassMpb = null;
            }

            UnbindComputeToStub();
            DestroyVariantMaterialsImmediate();
            if (_ownsMaterial && _material != null)
                DestroyImmediate(_material);
            _material = null;
            _ownsMaterial = false;

            DestroyOwnedMeshes(_variantMeshes, _variantMeshOwned, immediate: true);
            DestroyOwnedMeshes(_farVariantMeshes, _farMeshOwned, immediate: true);
            _variantMeshes = null;
            _farVariantMeshes = null;
            _variantMeshOwned = null;
            _farMeshOwned = null;
            _ownsBladeMesh = false;

            ReleaseBuffers();

            atlasTexture = null;
            flowerAtlasTexture = null;
            wheatTexture = null;
            lavenderTexture = null;
            variantTextures = null;
            _variantTex = null;
            materialOverride = null;
            cullCompute = null;
            bakeAsset = null;
            _bakeData = null;
            _bakeLoaded = false;
            _windowPackLists = null;
            _windowPackUpload = null;
            _packedCount = 0;
            _hasLastGrassSubmit = false;
            Log.Info($"[OilTerrainGrass] gpu released buffers={bytes}");
        }

        Vector3 ResolveGrassFocus()
        {
            Camera cam = IslandJoystickCamera.ActiveView;
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
            _windowRebuiltThisFrame = true;
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
            // 重包这一帧只记下过大的块，缩小留到后面有余量的帧。
            _shrinkQueued = AnyGrassPieceOversized();
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
                    _variantMats[v].SetBuffer(AllInstancesPosId, _allPosV[v]);
                    _variantMats[v].SetBuffer(VisibleInstanceOnlyTransformId, _visibleIdV[v]);
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
                bool useGpu = WantsGpuGrassCull();
                float nearMax = FarGrassFullDensityMeters;

                if (!useGpu)
                    EnsureKeepOutBuffer();

                if (useGpu)
                {
                    EnsureKeepOutBuffer();
                    // Shared frustum/matrix globals: set ONCE per frame instead of per-variant.
                    cullCompute.SetMatrix(VpMatrixId, vp);
                    cullCompute.SetVector(CamPosId, cam.transform.position);
                    cullCompute.SetFloat(MinPlantYId, minPlantWorldY);
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

                    if (useGpu || !_forceCpuCullValhall)
                    {
                        EnsureGrassArgs(ref _argsV[v]);
                        EnsureGrassArgs(ref _argsFarV[v]);
                    }

                    EnsureGrassVisible(ref _visibleIdV[v], _packedV[v]);
                    EnsureGrassVisible(ref _visibleIdFarV[v], _packedV[v]);
                    if (_visibleIdV[v] == null)
                        continue;

                    BindGrassGlobals(_variantMats[v], v);
                    if (useGpu)
                    {
                        if (_argsV[v] == null)
                            continue;
                        int kernel = ResolveCullKernel();
                        BindKeepOutForKernel(kernel);
                        cullCompute.SetBuffer(kernel, AllInstancesPosId, _allPosV[v]);
                        cullCompute.SetInt(VariantKindId, v);
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
                    string gpuNote = useGpu
                        ? "gpu=True"
                        : $"gpu=False reason=Mali-Valhall name={SystemInfo.graphicsDeviceName} draw=procedural";
                    Log.Info(
                        $"[OilTerrainGrass] draw packed={_packedCount} visJobs={visibleSum} " +
                        $"cells={_visibleCellIds.Count} {gpuNote} dist={maxDrawDistance:F0}");
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
            cullCompute.SetFloat(MinDrawDistanceId, minDraw);
            cullCompute.SetFloat(MaxDrawDistanceId, maxDraw);
            cullCompute.SetInt(StartOffsetId, 0);
            cullCompute.SetInt(InstanceCountId, count);
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
            if (visibleIds == null || (!_forceCpuCullValhall && args == null))
                return 0;

            _cpuVisibleScratch.Clear();
            Vector3 camPos = cam.transform.position;
            int count = _packedV[variant];
            bool keepOut = _keepOutEnabled && _keepOutData != null;
            for (int j = 0; j < count; j++)
            {
                Vector4 inst = _windowPackUpload[variant][j];
                Vector3 pos = new Vector3(inst.x, inst.y, inst.z);
                if (pos.y < minPlantWorldY)
                    continue;
                float dx = pos.x - camPos.x;
                float dz = pos.z - camPos.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                VariantCullLimits(variant, d, maxDraw, out float maxD, out int stride);
                if (d < minDraw || d > maxD)
                    continue;
                if (stride > 1 && (j % stride) != 0)
                    continue;
                if (keepOut && OilTerrainGrassKeepOutBake.Contains(_keepOutData, pos.x, pos.z))
                    continue;
                Vector4 clip = vp * new Vector4(pos.x, pos.y, pos.z, 1f);
                float w = clip.w;
                float cullR = d < 10f ? 5.5f : 2f;
                if (w < -cullR)
                    continue;
                float aw = Mathf.Max(Mathf.Abs(w), 0.25f);
                if (Mathf.Abs(clip.x) <= aw * 1.15f + cullR
                    && Mathf.Abs(clip.y) <= aw * 2.15f + cullR
                    && clip.z <= aw + cullR
                    && aw <= maxD + cullR)
                    _cpuVisibleScratch.Add((uint)j);
            }

            if (_cpuVisibleScratch.Count == 0)
            {
                StoreProceduralCount(variant, far, 0);
                return 0;
            }

            if (far)
                EnsureGrassVisible(ref _visibleIdFarV[variant], Mathf.Max(_cpuVisibleScratch.Count, 1));
            else
                EnsureGrassVisible(ref _visibleIdV[variant], Mathf.Max(_cpuVisibleScratch.Count, 1));
            visibleIds = far ? _visibleIdFarV[variant] : _visibleIdV[variant];

            visibleIds.SetData(_cpuVisibleScratch);
            int visibleCount = _cpuVisibleScratch.Count;
            StoreProceduralCount(variant, far, visibleCount);
            if (!_forceCpuCullValhall)
            {
                ResetGrassArgs(args, mesh);
                _argsCache[0] = mesh.GetIndexCount(0);
                _argsCache[1] = (uint)visibleCount;
                _argsCache[2] = mesh.GetIndexStart(0);
                _argsCache[3] = mesh.GetBaseVertex(0);
                _argsCache[4] = 0;
                args.SetData(_argsCache);
            }

            DrawIndirect(variant, mesh, renderBound, visibleIds, args, visibleCount);
            return visibleCount;
        }

        void BindGrassCullOutputs(
            int kernel, bool add, ComputeBuffer visibleIds, ComputeBuffer args, Mesh mesh)
        {
            if (add)
            {
                ResetGrassArgs(args, mesh);
                cullCompute.SetBuffer(kernel, VisibleIdsAddId, visibleIds);
                cullCompute.SetBuffer(kernel, VisibleArgsId, args);
            }
            else
            {
                visibleIds.SetCounterValue(0);
                cullCompute.SetBuffer(kernel, VisibleInstancesOnlyPosId, visibleIds);
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

            DrawIndirect(variant, mesh, renderBound, visibleIds, args, 0);
        }

        void StoreProceduralCount(int variant, bool far, int count)
        {
            int[] counts = far ? _proceduralCountFarV : _proceduralCountV;
            if ((uint)variant < (uint)counts.Length)
                counts[variant] = count;
        }

        void DrawIndirect(
            int variant, Mesh mesh, Bounds renderBound,
            ComputeBuffer visibleIds, ComputeBuffer args, int proceduralCount)
        {
            Material mat = _variantMats[variant];
            if (mat == null || mesh == null || visibleIds == null)
                return;
            if (_grassMpb == null)
                _grassMpb = new MaterialPropertyBlock();
            _grassMpb.Clear();
            _grassMpb.SetBuffer(AllInstancesPosId, _allPosV[variant]);
            _grassMpb.SetBuffer(VisibleInstanceOnlyTransformId, visibleIds);
            if (_forceCpuCullValhall)
            {
                if (proceduralCount <= 0)
                    return;
                Graphics.DrawMeshInstancedProcedural(
                    mesh, 0, mat, renderBound, proceduralCount,
                    _grassMpb, ShadowCastingMode.Off, false, 0, _bindDrawCamera);
                return;
            }

            if (args == null)
                return;
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
                if (_forceCpuCullValhall)
                {
                    if (nearMesh != null && _visibleIdV[v] != null && _proceduralCountV[v] > 0)
                        DrawIndirect(v, nearMesh, renderBound, _visibleIdV[v], null, _proceduralCountV[v]);
                    Mesh farMesh = ResolveVariantMesh(v, far: true);
                    if (farMesh != null && _visibleIdFarV[v] != null && _proceduralCountFarV[v] > 0)
                        DrawIndirect(v, farMesh, renderBound, _visibleIdFarV[v], null, _proceduralCountFarV[v]);
                    continue;
                }

                if (nearMesh != null && _argsV[v] != null && _visibleIdV[v] != null)
                    DrawIndirect(v, nearMesh, renderBound, _visibleIdV[v], _argsV[v], 0);
                Mesh farReplay = ResolveVariantMesh(v, far: true);
                if (farReplay != null && _argsFarV[v] != null && _visibleIdFarV[v] != null)
                    DrawIndirect(v, farReplay, renderBound, _visibleIdFarV[v], _argsFarV[v], 0);
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
            if (_gpuReleased)
                return;
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

        bool WantsGpuGrassCull()
        {
            if (cullCompute == null || ResolveCullKernel() < 0)
                return false;
            if (!_valhallChecked)
            {
                string gpu = SystemInfo.graphicsDeviceName;
                if (string.IsNullOrEmpty(gpu))
                    return true;
                _valhallChecked = true;
                // 闺蜜机等非 Valhall 仍走 GPU 剔除 + indirect；Valhall 才改 CPU + procedural。
                _forceCpuCullValhall = IsValhallMaliGpu(gpu);
            }

            return !_forceCpuCullValhall;
        }

        static bool IsValhallMaliGpu(string gpu)
        {
            if (string.IsNullOrEmpty(gpu))
                return false;
            return gpu.IndexOf("Mali-G61", System.StringComparison.Ordinal) >= 0
                || gpu.IndexOf("Mali-G62", System.StringComparison.Ordinal) >= 0
                || gpu.IndexOf("Mali-G71", System.StringComparison.Ordinal) >= 0
                || gpu.IndexOf("Mali-G72", System.StringComparison.Ordinal) >= 0
                || gpu.IndexOf("Immortalis", System.StringComparison.Ordinal) >= 0;
        }

        /// <summary>与 CSMainAdd 同一套株型远裁和远距抽稀，srcId 是该株型 packed 下标。</summary>
        static void VariantCullLimits(int variant, float d, float maxDraw, out float maxD, out int stride)
        {
            stride = 1;
            if (variant >= 7 && variant <= 10)
            {
                maxD = Mathf.Min(maxDraw, 70f);
                if (d >= 48f)
                    stride = 2;
            }
            else if (variant == 11)
            {
                maxD = Mathf.Min(maxDraw, 28f);
                if (d >= 16f)
                    stride = 2;
            }
            else if (variant == 12)
            {
                maxD = Mathf.Min(maxDraw, 95f);
                if (d >= 55f)
                    stride = 3;
                else if (d >= 32f)
                    stride = 2;
            }
            else if (variant == 1 || variant == 3 || variant == 5)
            {
                maxD = Mathf.Min(maxDraw, 70f);
                if (d >= 42f)
                    stride = 16;
                else if (d >= 24f)
                    stride = 8;
            }
            else
            {
                maxD = maxDraw;
                if (d >= 110f)
                    stride = 8;
                else if (d >= 70f)
                    stride = 4;
                else if (d >= 45f)
                    stride = 2;
            }
        }

        void SyncGrassIndirectMode()
        {
            _useAdd = _kernelAdd >= 0;
        }

        void RebuildTopologyMeshes()
        {
            DestroyOwnedMeshes(_variantMeshes, _variantMeshOwned, immediate: false);
            DestroyOwnedMeshes(_farVariantMeshes, _farMeshOwned, immediate: false);
            _variantMeshes = OilTerrainGrassBladeMesh.BuildAll(_bladeSegments);
            _farVariantMeshes = OilTerrainGrassBladeMesh.BuildAll(FarBladeSegments);
            _variantMeshOwned = MarkRuntimeMeshes(_variantMeshes);
            _farMeshOwned = MarkRuntimeMeshes(_farVariantMeshes);
            _ownsBladeMesh = true;
        }

        static bool[] MarkRuntimeMeshes(Mesh[] meshes)
        {
            if (meshes == null)
                return null;
            var owned = new bool[meshes.Length];
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh mesh = meshes[i];
                // 进岛时记一次。退登再问 IsImportedMesh 会 Resources.Load，把网格重新钉在显存里。
                owned[i] = mesh != null && !OilTerrainGrassBladeMesh.IsImportedMesh(mesh);
            }

            return owned;
        }

        void DestroyOwnedMeshes(Mesh[] meshes, bool[] owned, bool immediate)
        {
            if (meshes == null)
                return;
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh mesh = meshes[i];
                if (mesh == null)
                    continue;
                bool isOwned = owned != null && (uint)i < (uint)owned.Length
                    ? owned[i]
                    : !OilTerrainGrassBladeMesh.IsImportedMesh(mesh);
                if (!isOwned)
                    continue;
                if (immediate)
                    DestroyImmediate(mesh);
                else
                    Destroy(mesh);
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

        void DestroyVariantMaterialsImmediate()
        {
            if (_variantMats == null)
                return;
            for (int i = 0; i < _variantMats.Length; i++)
            {
                Material mat = _variantMats[i];
                if (mat == null)
                    continue;
                if (mat.HasProperty("_GrassTexture"))
                    mat.SetTexture("_GrassTexture", null);
                DestroyImmediate(mat);
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
            if (tex != null && mat.HasProperty(GrassTextureId))
                mat.SetTexture(GrassTextureId, tex);
            mat.SetVectorArray(GrassAtlasRectsId, OilTerrainGrassAtlas.RectsForTopology(topology));
            if (_allPosV != null && (uint)topology < (uint)_allPosV.Length && _allPosV[topology] != null)
                mat.SetBuffer(AllInstancesPosId, _allPosV[topology]);
            if (_visibleIdV != null && (uint)topology < (uint)_visibleIdV.Length && _visibleIdV[topology] != null)
                mat.SetBuffer(VisibleInstanceOnlyTransformId, _visibleIdV[topology]);
        }

        void ApplyAtlasToMaterials()
        {
            if (_variantMats == null)
                return;
            for (int i = 0; i < _variantMats.Length; i++)
                BindGrassGlobals(_variantMats[i], i);
        }

        /// <summary>
        /// 计算着色器和材质还握着旧 buffer 时直接 Release，驱动不会把显存还回来。
        /// </summary>
        void ReleaseReplacing(ref ComputeBuffer buffer)
        {
            if (buffer == null)
                return;
            UnbindComputeToStub();
            ComputeBuffer stub = StructuredStub();
            if (_variantMats != null)
            {
                for (int i = 0; i < _variantMats.Length; i++)
                {
                    Material mat = _variantMats[i];
                    if (mat == null)
                        continue;
                    mat.SetBuffer(AllInstancesPosId, stub);
                    mat.SetBuffer(VisibleInstanceOnlyTransformId, stub);
                }
            }

            if (_grassMpb != null)
            {
                _grassMpb.SetBuffer(AllInstancesPosId, stub);
                _grassMpb.SetBuffer(VisibleInstanceOnlyTransformId, stub);
            }

            if (buffer.IsValid())
                buffer.Release();
            buffer = null;
        }

        void TryShrinkOneGrassPiece()
        {
            if (_gpuReleased || !_shrinkQueued || _windowRebuiltThisFrame)
                return;
            if (_grassWork.ElapsedMilliseconds >= GrassShrinkBudgetMs)
                return;
            if (!GrassFrameHasHeadroom())
                return;
            if (!ShrinkNextOversizedPiece())
                _shrinkQueued = false;
        }

        static bool GrassFrameHasHeadroom()
        {
            int fps = Application.targetFrameRate;
            if (fps < 1)
                fps = IslandCameraSrpSetup.DefaultFrameRate;
            return Time.unscaledDeltaTime <= 1.5f / fps;
        }

        bool AnyGrassPieceOversized()
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            for (int v = 0; v < vc; v++)
            {
                for (int kind = 0; kind < ShrinkKindCount; kind++)
                {
                    if (GrassPieceOversized(v, kind))
                        return true;
                }
            }

            return false;
        }

        bool ShrinkNextOversizedPiece()
        {
            const int vc = OilTerrainGrassBladeMesh.TopologyCount;
            int total = vc * ShrinkKindCount;
            for (int step = 0; step < total; step++)
            {
                int index = (_shrinkCursor + step) % total;
                int variant = index / ShrinkKindCount;
                int kind = index % ShrinkKindCount;
                if (!GrassPieceOversized(variant, kind))
                    continue;
                ShrinkGrassPiece(variant, kind);
                _shrinkCursor = (index + 1) % total;
                return true;
            }

            return false;
        }

        bool GrassPieceOversized(int variant, int kind)
        {
            int used = PackedVariantCount(variant);
            switch (kind)
            {
                case ShrinkKindPos:
                    return BufferOversized(_allPosV, variant, used);
                case ShrinkKindUpload:
                    return UploadOversized(variant, used);
                case ShrinkKindVisible:
                    return BufferOversized(_visibleIdV, variant, used);
                case ShrinkKindVisibleFar:
                    return BufferOversized(_visibleIdFarV, variant, used);
                default:
                    return false;
            }
        }

        int PackedVariantCount(int variant)
        {
            if (_packedV == null || (uint)variant >= (uint)_packedV.Length)
                return 0;
            return _packedV[variant];
        }

        static bool BufferOversized(ComputeBuffer[] buffers, int variant, int used)
        {
            if (buffers == null || (uint)variant >= (uint)buffers.Length)
                return false;
            ComputeBuffer buffer = buffers[variant];
            if (buffer == null || !buffer.IsValid())
                return false;
            return CapacityOversized(buffer.count, used);
        }

        bool UploadOversized(int variant, int used)
        {
            if (_windowPackUpload == null || (uint)variant >= (uint)_windowPackUpload.Length)
                return false;
            Vector4[] upload = _windowPackUpload[variant];
            return upload != null && CapacityOversized(upload.Length, used);
        }

        /// <summary>新数量不到容量一半才缩。容量为 1 的占位不再动。</summary>
        static bool CapacityOversized(int capacity, int used)
        {
            if (capacity <= 1)
                return false;
            if (used <= 0)
                return true;
            return (long)used * 2 < capacity;
        }

        void ShrinkGrassPiece(int variant, int kind)
        {
            int used = PackedVariantCount(variant);
            switch (kind)
            {
                case ShrinkKindPos:
                    ShrinkPosBuffer(variant, used);
                    RebindAllVariantBuffers();
                    break;
                case ShrinkKindUpload:
                    ShrinkUpload(variant, used);
                    break;
                case ShrinkKindVisible:
                    ShrinkVisible(ref _visibleIdV[variant], used);
                    _hasLastGrassSubmit = false;
                    RebindAllVariantBuffers();
                    break;
                case ShrinkKindVisibleFar:
                    ShrinkVisible(ref _visibleIdFarV[variant], used);
                    _hasLastGrassSubmit = false;
                    RebindAllVariantBuffers();
                    break;
            }
        }

        void ShrinkPosBuffer(int variant, int used)
        {
            ReleaseReplacing(ref _allPosV[variant]);
            int count = Mathf.Max(used, 1);
            _allPosV[variant] = new ComputeBuffer(count, sizeof(float) * 4, ComputeBufferType.Structured);
            if (used > 0
                && _windowPackUpload != null
                && (uint)variant < (uint)_windowPackUpload.Length
                && _windowPackUpload[variant] != null
                && _windowPackUpload[variant].Length >= used)
                _allPosV[variant].SetData(_windowPackUpload[variant], 0, 0, used);
        }

        void ShrinkUpload(int variant, int used)
        {
            if (_windowPackUpload != null && (uint)variant < (uint)_windowPackUpload.Length)
            {
                Vector4[] current = _windowPackUpload[variant];
                if (current != null && CapacityOversized(current.Length, used))
                {
                    if (used <= 0)
                        _windowPackUpload[variant] = System.Array.Empty<Vector4>();
                    else
                    {
                        var next = new Vector4[Mathf.NextPowerOfTwo(used)];
                        System.Array.Copy(current, next, used);
                        _windowPackUpload[variant] = next;
                    }
                }
            }
        }

        void ShrinkVisible(ref ComputeBuffer buffer, int used)
        {
            int count = Mathf.Max(used, 1);
            ComputeBufferType type = _useAdd
                ? ComputeBufferType.Structured
                : ComputeBufferType.Append;
            ReleaseReplacing(ref buffer);
            buffer = new ComputeBuffer(count, sizeof(uint), type);
            if (!_useAdd)
                buffer.SetCounterValue(0);
        }

        void RebindAllVariantBuffers()
        {
            if (_variantMats == null)
                return;
            for (int i = 0; i < _variantMats.Length; i++)
                BindGrassGlobals(_variantMats[i], i);
        }

        void EnsureBuffer(ref ComputeBuffer buffer, int count, int stride, ComputeBufferType type)
        {
            count = Mathf.Max(1, count);
            if (buffer != null && buffer.count >= count && buffer.stride == stride)
                return;
            ReleaseReplacing(ref buffer);
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
            ReleaseReplacing(ref buffer);
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
            _keepOutData = null;
            _keepOutEnabled = false;
            _keepOutLoadTried = false;
            _gpuReady = false;
            _windGlobalsValid = false;
        }

        long SumGpuBytes()
        {
            long bytes = BufferBytes(_keepOutGrid);
            bytes += SumBufferBytes(_allPosV);
            bytes += SumBufferBytes(_visibleIdV);
            bytes += SumBufferBytes(_visibleIdFarV);
            bytes += SumBufferBytes(_argsV);
            bytes += SumBufferBytes(_argsFarV);
            return bytes;
        }

        static long SumBufferBytes(ComputeBuffer[] buffers)
        {
            if (buffers == null)
                return 0;
            long bytes = 0;
            for (int i = 0; i < buffers.Length; i++)
                bytes += BufferBytes(buffers[i]);
            return bytes;
        }

        static long BufferBytes(ComputeBuffer buffer)
        {
            if (buffer == null || !buffer.IsValid())
                return 0;
            return (long)buffer.count * buffer.stride;
        }

        void UnbindComputeToStub()
        {
            if (cullCompute == null)
                return;
            if (_kernel < 0)
                _kernel = cullCompute.FindKernel("CSMain");
            if (_kernelAdd < 0)
                _kernelAdd = cullCompute.FindKernel("CSMainAdd");
            SyncGrassIndirectMode();

            ComputeBuffer structured = StructuredStub();
            if (_kernel >= 0)
            {
                cullCompute.SetBuffer(_kernel, AllInstancesPosId, structured);
                cullCompute.SetBuffer(_kernel, KeepOutGridId, structured);
                if (!_useAdd)
                    cullCompute.SetBuffer(_kernel, VisibleInstancesOnlyPosId, AppendStub());
            }

            if (_kernelAdd < 0)
                return;
            cullCompute.SetBuffer(_kernelAdd, AllInstancesPosId, structured);
            cullCompute.SetBuffer(_kernelAdd, KeepOutGridId, structured);
            if (_useAdd)
            {
                cullCompute.SetBuffer(_kernelAdd, VisibleIdsAddId, structured);
                cullCompute.SetBuffer(_kernelAdd, VisibleArgsId, ArgsStub());
            }
        }

        /// <summary>退登后拆掉占位 buffer。卸岛时为了解绑会把它们建出来，不放就留在静态字段上。</summary>
        public static void ReleaseStubs()
        {
            ReleaseStub(ref _structuredStub);
            ReleaseStub(ref _rawStub);
            ReleaseStub(ref _appendStub);
        }

        static void ReleaseStub(ref ComputeBuffer buffer)
        {
            if (buffer == null)
                return;
            if (buffer.IsValid())
                buffer.Release();
            buffer = null;
        }

        static ComputeBuffer StructuredStub() =>
            ValidStub(ref _structuredStub, ComputeBufferType.Structured);

        static ComputeBuffer ArgsStub()
        {
            if (_rawStub != null && !_rawStub.IsValid())
                _rawStub = null;
            if (_rawStub == null)
                _rawStub = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
            return _rawStub;
        }

        static ComputeBuffer AppendStub() =>
            ValidStub(ref _appendStub, ComputeBufferType.Append);

        static ComputeBuffer ValidStub(ref ComputeBuffer stub, ComputeBufferType type)
        {
            if (stub != null && !stub.IsValid())
                stub = null;
            if (stub == null)
                stub = new ComputeBuffer(1, sizeof(uint), type);
            return stub;
        }

        /// <summary>
        /// Metal/Vulkan 要求 kernel 声明的 StructuredBuffer 必须绑定；
        /// KeepOut 关闭时也要挂一个占位 buffer。Valhall CPU 路径只读内存 mask。
        /// </summary>
        void EnsureKeepOutBuffer()
        {
            if (!_keepOutLoadTried)
            {
                _keepOutLoadTried = true;
                TryLoadKeepOutFromResources();
            }

            if (_forceCpuCullValhall || _keepOutGrid != null)
                return;

            _keepOutGrid = new ComputeBuffer(1, sizeof(uint));
            _keepOutGrid.SetData(new uint[] { 0 });
            if (_keepOutData == null)
            {
                _keepOutEnabled = false;
                _keepOutRes = 0;
            }
        }

        void BindKeepOutForKernel(int kernel)
        {
            EnsureKeepOutBuffer();
            cullCompute.SetBuffer(kernel, KeepOutGridId, _keepOutGrid);
            cullCompute.SetInt(KeepOutEnabledId, _keepOutEnabled ? 1 : 0);
            cullCompute.SetInt(KeepOutResId, _keepOutRes);
            cullCompute.SetFloat(KeepOutOriginXId, _keepOutOriginX);
            cullCompute.SetFloat(KeepOutOriginZId, _keepOutOriginZ);
            cullCompute.SetFloat(KeepOutCellId, _keepOutCell);
        }

        public bool TryLoadKeepOut(byte[] bytes)
        {
            if (!OilTerrainGrassKeepOutBake.TryRead(bytes, out var data) || data.Mask == null)
                return false;

            _keepOutData = data;
            _keepOutRes = data.Res;
            _keepOutOriginX = data.OriginX;
            _keepOutOriginZ = data.OriginZ;
            _keepOutCell = data.WorldSize / data.Res;
            _keepOutEnabled = _keepOutRes > 0 && _keepOutCell > 1e-4f;
            _keepOutLoadTried = true;
            if (_forceCpuCullValhall)
            {
                Log.Info(
                    $"[OilTerrainGrass] KeepOut cpu res={_keepOutRes} cell={_keepOutCell:F2} " +
                    $"enabled={_keepOutEnabled}");
                return true;
            }

            int len = data.Res * data.Res;
            var upload = new uint[len];
            for (int i = 0; i < len; i++)
                upload[i] = data.Mask[i];

            if (_keepOutGrid != null)
            {
                UnbindComputeToStub();
                if (_keepOutGrid.IsValid())
                    _keepOutGrid.Release();
                _keepOutGrid = null;
            }

            _keepOutGrid = new ComputeBuffer(len, sizeof(uint));
            _keepOutGrid.SetData(upload);
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
