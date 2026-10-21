using UnityEngine;

namespace Game
{
    /// <summary>
    /// Lite 环境流送半径与分类策略。
    /// 树距离对齐 Boluo <c>EnvStreamingConfig</c> 标准版（Non4K / FillBound）常量。
    /// </summary>
    public static class EnvStreamingConfig
    {
        public const string OverridePrefsKey = "GameEnvStreamingOverride";

        /// <summary>默认开流式。PlayerPrefs 0=全量铺；1=强制流式；未设用默认。</summary>
        public static bool ResolveStreamingEnabled()
        {
            if (PlayerPrefs.HasKey(OverridePrefsKey))
                return PlayerPrefs.GetInt(OverridePrefsKey, 1) != 0;
            return true;
        }

        // ---------- Boluo Base 环（编辑器 / 未套移动档时） ----------

        public static float Ring0EnterRadius = 90f;
        public static float Ring0ExitRadius = 110f;
        public static float Ring1EnterRadius = 170f;
        public static float Ring1ExitRadius = 200f;
        public static float HardUnloadFactor = 1.5f;

        public static float TreeRing0EnterRadius = 70f;
        public static float TreeRing0ExitRadius = 90f;
        public static float TreeRing1EnterRadius = 180f;
        public static float TreeRing1ExitRadius = 220f;

        /// <summary>
        /// 松/绿出 TreeRing1 后仍保留降级实例的最远距离（Boluo TreeBillboardKeepRadius）。
        /// 4K/编辑器默认 380；标准版真机 180。
        /// </summary>
        public static float TreeBillboardKeepRadius = 380f;

        /// <summary>远树隔株起点（Boluo DefaultFillBoundFarTreeSilhouetteMinDistance）。</summary>
        public static float FillBoundFarTreeSilhouetteMinDistance = 120f;

        /// <summary>远树隔株：每 N 棵留 1（Boluo Non4KFarTreeSilhouetteStride=6）。</summary>
        public static int FillBoundFarTreeSilhouetteStride = 6;

        // Boluo FillBoundGuanmu*
        public static float GuanmuHideDistance = 90f;
        public static float GuanmuShowDistance = 75f;
        public static float SmallRockHideDistance = 95f;
        public static float SmallRockShowDistance = 80f;
        public static float SmallVegHideDistance = 40f;
        public static float SmallVegShowDistance = 32f;
        public static float ClutterHideDistance = 55f;
        public static float ClutterShowDistance = 45f;
        public static float BuildingLodHideDistance = 130f;
        public static float BuildingLodShowDistance = 110f;

        /// <summary>
        /// 海滩轮廓大石 Rock_Ramp_02 / _03。
        /// Ramp_02 list≈281：XZ≈海滩181 / 假日躺平194；
        /// Ramp_03 list≈295：XZ≈海滩152 / 假日躺平181。
        /// 须大于上述距离；勿被 ApplyMobileTierDefaults 收到 Ring1（真机 130）。
        /// </summary>
        public static float RockRampLandmarkShowDistance = 220f;
        public static float RockRampLandmarkHideDistance = 280f;

        /// <summary>
        /// Android Native DrawMeshInstanced。编辑器默认关；真机移动档在
        /// <see cref="ApplyMobileTierDefaults"/> 里打开。
        /// OilNPRShaderStripper（CustomSRP.Editor）保留 INSTANCING_ON 变体，剥掉会让
        /// 合批实例化在真机上整片不可见。
        /// </summary>
        public static bool NativeInstancingEnabled = false;

        /// <summary>须 ≥ 树/灌木 Show 半径，否则合批关掉 MR 后超出距离的实例永久看不见。</summary>
        public static float NativeInstancingMaxDist = 180f;
        public static float FinalizePerFrameBudgetMs = 3f;
        public static int SpawnPerFrameBudget = 24;
        public static int DespawnPerFrameBudget = 16;
        /// <summary>
        /// 近距启用 MeshCollider。须明显大于走路半秒路程，否则高速接近大石时
        /// 碰撞体尚未打开就会穿进 mesh，再打开也难把 CharacterController 顶出来。
        /// </summary>
        public static float MeshColliderEnableRadius = 90f;

        /// <summary>远距关闭 MeshCollider（与 Enable 留滞后）。</summary>
        public static float MeshColliderDisableRadius = 120f;

        /// <summary>
        /// 非 Cliff/Cave/Ramp/Spike/Large 的石头：世界空间最长边 ≥ 此值才补 MeshCollider。
        /// ≈2.5m 可挡住海滩放大的 Rock_Small_01（scale≈7→~2.6m），放过脚边小石。
        /// </summary>
        public static float RockColliderMinWorldExtent = 2.5f;

        /// <summary>
        /// Prefill 大树最少铺装半径。标准版对齐 TreeBillboardKeepRadius（180），
        /// 配合隔株，避免首装空林又不过密。
        /// </summary>
        public static float PrefillTreeBoostRadius = 180f;

        /// <summary>
        /// 可投射物体超过此距离关闭 ShadowCasting。与 CRP / Quality shadowDistance 对齐。
        /// 必须在流送轮询里走近后再打开，不能只在生成时关一次。
        /// </summary>
        public static float ShadowCastMaxDist = 26f;

        /// <summary>近于此距离重新打开投射。与 <see cref="ShadowCastMaxDist"/> 之间留滞后，避免边界闪烁。</summary>
        public static float ShadowCastRestoreDist = 22f;

        static readonly string[] TreeCategories =
        {
            "Songshu", "Lvshu", "Yeshu", "Guanmu",
        };

        static readonly string[] RockCategories = { "ShiTou" };

        /// <summary>
        /// 地标永不卸。椰树（Yeshu）在 Boluo 也是 NeverUnload（格子级）；
        /// Lite 仍按树环+keep 流送，仅不做隔株（见 <see cref="ShouldThinFarTree"/>）。
        /// </summary>
        static readonly string[] NeverUnloadCategories =
        {
            "JianZhu", "Qiao", "Other", "Common", "texiao", "FX_Day", "FX_Night",
        };

        static readonly string[] SmallVegCategories = { "Hua", "Zacao", "Zhibei" };

        public static bool IsTreeCategory(string category)
        {
            for (int i = 0; i < TreeCategories.Length; i++)
            {
                if (TreeCategories[i] == category)
                    return true;
            }

            return false;
        }

        public static bool IsRockCategory(string category)
        {
            for (int i = 0; i < RockCategories.Length; i++)
            {
                if (RockCategories[i] == category)
                    return true;
            }

            return false;
        }

        public static bool IsSmallTreeCategory(string category) => category == "Guanmu";

        /// <summary>松/绿：出环可 keep + 可隔株。椰树不隔株。</summary>
        public static bool IsFarTreeSilhouetteCategory(string category) =>
            category == "Songshu" || category == "Lvshu";

        public static bool IsClutterCategory(string category) => category == "ZaWu";

        public static bool IsBridgeCategory(string category) => category == "Qiao";

        public static bool IsSmallVegCategory(string category)
        {
            for (int i = 0; i < SmallVegCategories.Length; i++)
            {
                if (SmallVegCategories[i] == category)
                    return true;
            }

            return false;
        }

        public static bool IsNeverUnloadCategory(string category)
        {
            for (int i = 0; i < NeverUnloadCategories.Length; i++)
            {
                if (NeverUnloadCategories[i] == category)
                    return true;
            }

            return false;
        }

        public static bool IsLargeRockPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;
            return prefabName.IndexOf("Cliff", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("Cave", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("Ramp", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("Spike", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("Large", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// MeshCollider 永不按距离关闭：Mountain 地标 + Cliff/Cave/Ramp/Spike/Large。
        /// 大石可视距离远大于旧 collider LOD 半径，关碰撞会直接穿模。
        /// </summary>
        public static bool ShouldKeepMeshColliderAlwaysOn(string objectName)
        {
            return IsFarFieldLandmark(objectName) || IsLargeRockPrefab(objectName);
        }

        /// <summary>
        /// 走路碰撞：结构大石按名；其余按实例世界尺寸（须在 localScale 设好后测）。
        /// 远距剔除仍只用 <see cref="IsLargeRockPrefab"/>，不受此门控影响。
        /// </summary>
        public static bool ShouldHaveRockCollider(string prefabName, GameObject go)
        {
            if (IsLargeRockPrefab(prefabName))
                return true;
            if (go == null)
                return false;
            return EstimateRockWorldExtent(go) >= RockColliderMinWorldExtent;
        }

        /// <summary>用 LODGroup.size 或 Mesh.bounds × lossyScale 估最长边（忽略旋转，够做门槛）。</summary>
        public static float EstimateRockWorldExtent(GameObject go)
        {
            float maxExtent = 0f;
            LODGroup lod = go.GetComponentInChildren<LODGroup>(true);
            if (lod != null && lod.size > 1e-4f)
            {
                Vector3 s = lod.transform.lossyScale;
                float sc = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                maxExtent = lod.size * sc;
            }

            MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;
                Vector3 size = mf.sharedMesh.bounds.size;
                Vector3 s = mf.transform.lossyScale;
                float ex = Mathf.Max(
                    Mathf.Abs(size.x * s.x),
                    Mathf.Abs(size.y * s.y),
                    Mathf.Abs(size.z * s.z));
                if (ex > maxExtent)
                    maxExtent = ex;
            }

            return maxExtent;
        }

        public static bool IsSmallRockForFarCull(string prefabName) => !IsLargeRockPrefab(prefabName);

        public static bool IsBuildingForFarCull(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;
            return prefabName.IndexOf("House", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("building_closed", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("shed_wood", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("Tent", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool ShouldForceSkipNativeInstancing(string category, string prefabName)
        {
            if (IsBridgeCategory(category) || IsRockCategory(category))
                return true;
            if (string.IsNullOrEmpty(prefabName))
                return false;
            return prefabName.IndexOf("Dock", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("Pier", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Planar mirror probe (CameraType.Reflection) inherits source.cullingMask,
        /// so anything in the native instancing batch that <see cref="Graphics.DrawMeshInstanced"/>
        /// queues will show up in the reflection unless we mark it main-camera-only.
        /// Grass (small veg) and clutter shouldn't reflect in water — they're cheap
        /// to skip and look wrong blurred into the surface.
        /// Rocks already skip native (<see cref="ShouldForceSkipNativeInstancing"/>),
        /// so they're handled separately via the reflection culling layer mask.
        /// </summary>
        public static bool ShouldBeMainCameraOnly(string category, string prefabName)
        {
            if (IsSmallVegCategory(category))
                return true;
            if (IsClutterCategory(category))
                return true;
            return false;
        }

        /// <summary>
        /// Match <see cref="EnvItemStreamerLite.NeverCastsShadows"/>: small veg,
        /// clutter, flat ground cards and far-field landmarks skip shadow casting
        /// to keep shadow atlas headroom for trees/buildings.
        /// </summary>
        public static bool CanItemCastShadows(string category, string prefabName)
        {
            if (EnvGroundFit.IsFlatGroundCard(prefabName))
                return false;
            if (IsFarFieldLandmark(prefabName))
                return false;
            if (IsSmallTreeCategory(category))
                return false;
            if (IsSmallVegCategory(category))
                return false;
            if (IsClutterCategory(category))
                return false;
            return true;
        }

        public static bool IsFarFieldLandmark(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;
            return prefabName.IndexOf("Mountain", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>海滩轮廓大石 SM_Env_Rock_Ramp_02 / _03（含超大缩放那块）。</summary>
        public static bool IsRockRampLandmark(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;
            return prefabName.IndexOf("Rock_Ramp_02", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || prefabName.IndexOf("Rock_Ramp_03", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Boluo ShouldKeepFillBoundFarTreeSilhouette：隔株留轮廓。</summary>
        public static bool ShouldKeepFillBoundFarTreeSilhouette(int itemIndex)
        {
            int stride = FillBoundFarTreeSilhouetteStride;
            if (stride <= 1)
                return true;
            return (itemIndex % stride) == 0;
        }

        /// <summary>
        /// 标准版远树隔株：松/绿在 SilhouetteMin 外且非留株 → 应卸（不生成）。
        /// 椰树 / 灌木不隔株。
        /// </summary>
        public static bool ShouldThinFarTree(string category, float distSq, int itemIndex)
        {
            if (!IsFarTreeSilhouetteCategory(category))
                return false;
            float min = FillBoundFarTreeSilhouetteMinDistance;
            if (distSq <= min * min)
                return false;
            return !ShouldKeepFillBoundFarTreeSilhouette(itemIndex);
        }

        /// <summary>Prefill：大树铺到 PrefillTreeBoost（标准版=keep 180）；灌木跟 Show。</summary>
        public static float GetPrefillShowDistance(string category, float runtimeShowDistance)
        {
            if (IsTreeCategory(category) && !IsSmallTreeCategory(category))
                return Mathf.Max(runtimeShowDistance, PrefillTreeBoostRadius);
            if (IsSmallTreeCategory(category))
                return Mathf.Max(runtimeShowDistance, GuanmuShowDistance);
            return runtimeShowDistance;
        }

        public static float GetHideDistance(string category, string prefabName, out float showDistance)
        {
            if (IsFarFieldLandmark(prefabName) || IsNeverUnloadCategory(category))
            {
                showDistance = float.MaxValue;
                return float.MaxValue;
            }

            // 大树（松/绿/椰）：Boluo 出 TreeRing1 仍留到 TreeBillboardKeep（标准版 180）。
            // Lite 无 mesh 降级环，Show/Hide 都映射 keep；120m 外松/绿隔株（stride=6）控 fill。
            if (IsTreeCategory(category) && !IsSmallTreeCategory(category))
            {
                showDistance = TreeBillboardKeepRadius;
                return TreeBillboardKeepRadius;
            }

            if (IsSmallTreeCategory(category))
            {
                showDistance = GuanmuShowDistance;
                return GuanmuHideDistance;
            }

            if (IsRockCategory(category) && IsRockRampLandmark(prefabName))
            {
                showDistance = RockRampLandmarkShowDistance;
                return RockRampLandmarkHideDistance;
            }

            if (IsRockCategory(category) && IsSmallRockForFarCull(prefabName))
            {
                showDistance = SmallRockShowDistance;
                return SmallRockHideDistance;
            }

            if (IsRockCategory(category))
            {
                showDistance = Ring1EnterRadius;
                return Ring1ExitRadius * HardUnloadFactor;
            }

            if (IsSmallVegCategory(category))
            {
                showDistance = SmallVegShowDistance;
                return SmallVegHideDistance;
            }

            if (IsClutterCategory(category))
            {
                showDistance = ClutterShowDistance;
                return ClutterHideDistance;
            }

            if (category == "JianZhu" && IsBuildingForFarCull(prefabName))
            {
                showDistance = BuildingLodShowDistance;
                return BuildingLodHideDistance;
            }

            showDistance = Ring1EnterRadius;
            return Ring1ExitRadius * HardUnloadFactor;
        }

        /// <summary>
        /// 真机套 Boluo 标准版（Non4K FillBound）树环：R0=70/90，R1=117/143，keep=180，隔株 stride=6。
        /// 非树物件仍略收半径保帧。
        /// </summary>
        public static void ApplyMobileTierDefaults()
        {
#if !UNITY_EDITOR
            // 非树环：略收（Lite 无格子驻留，需比 Boluo 通用环紧一点控 fill）。
            Ring0EnterRadius = 60f;
            Ring0ExitRadius = 78f;
            Ring1EnterRadius = 130f;
            Ring1ExitRadius = 160f;

            // B2：树 keep 180→130，远景更稀但少 DrawMeshInstanced / 阴影 caster。
            TreeRing0EnterRadius = 70f;
            TreeRing0ExitRadius = 90f;
            TreeRing1EnterRadius = 130f * 0.65f; // ~85
            TreeRing1ExitRadius = 160f * 0.65f;  // ~104
            TreeBillboardKeepRadius = 130f;
            FillBoundFarTreeSilhouetteMinDistance = 90f;
            FillBoundFarTreeSilhouetteStride = 5;
            PrefillTreeBoostRadius = 130f;

            // Boluo FillBoundGuanmu*（标准版不收灌木）
            GuanmuHideDistance = 90f;
            GuanmuShowDistance = 75f;

            // 小物件仍紧收
            SmallRockHideDistance = 75f;
            SmallRockShowDistance = 62f;
            SmallVegHideDistance = 30f;
            SmallVegShowDistance = 24f;
            ClutterHideDistance = 40f;
            ClutterShowDistance = 32f;
            BuildingLodHideDistance = 110f;
            BuildingLodShowDistance = 95f;
            // 真机略收，但仍须大于 MoveSpeed 数帧路程，避免穿进未开碰撞的大石。
            MeshColliderEnableRadius = 72f;
            MeshColliderDisableRadius = 96f;

            // 格子驻留须盖住树环（Boluo：Ring1 >= TreeRing1）
            Ring1EnterRadius = Mathf.Max(Ring1EnterRadius, TreeRing1EnterRadius);
            Ring1ExitRadius = Mathf.Max(Ring1ExitRadius, TreeRing1ExitRadius);

            NativeInstancingEnabled = true;
            NativeInstancingMaxDist = Mathf.Max(130f, TreeBillboardKeepRadius);
            SpawnPerFrameBudget = 8;
            DespawnPerFrameBudget = 8;
            FinalizePerFrameBudgetMs = 2.5f;
#endif
        }
    }
}
