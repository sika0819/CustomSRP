# 环境（MapItem 流式加载 / 石头碰撞 / 篝火 / 原生合批）

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

`Environment/Runtime/` 负责**把岛上的美术摆放跑起来**：

| 文件 | 行数 | 作用 |
|---|---|---|
| [Environment/Runtime/EnvMapItemSpawner.cs](../Environment/Runtime/EnvMapItemSpawner.cs) | 330 | MapItem 装配总入口（读数据 → 分类 → 流式/全量 → extras） |
| [Environment/Runtime/EnvItemStreamerLite.cs](../Environment/Runtime/EnvItemStreamerLite.cs) | 583 | 距离流式生成 / 回收 |
| [Environment/Runtime/EnvStreamingConfig.cs](../Environment/Runtime/EnvStreamingConfig.cs) | 469 | **所有半径与分类策略的唯一真源** |
| [Environment/Runtime/EnvRockColliders.cs](../Environment/Runtime/EnvRockColliders.cs) | ~110 | 石头缺碰撞时补 MeshCollider；预制体已有则保留 |
| [Environment/Runtime/EnvNativeInstancing.cs](../Environment/Runtime/EnvNativeInstancing.cs) | 430 | Android `Graphics.RenderMeshInstanced` 合批 |
| [Environment/Runtime/EnvBridgePillarFit.cs](../Environment/Runtime/EnvBridgePillarFit.cs) | 235 | 桥墩底部贴合地形 |
| [Environment/Runtime/EnvMapItemExtras.cs](../Environment/Runtime/EnvMapItemExtras.cs) | 232 | `FX_Day` / `FX_Night` / 篝火 extras |
| [Environment/Runtime/FireController.cs](../Environment/Runtime/FireController.cs) | 514 | 篝火焰卡片 + 点光源 |
| [Environment/Runtime/CampfireWeatherGate.cs](../Environment/Runtime/CampfireWeatherGate.cs) | 57 | 全局篝火点火门控 |

## 关键 API

### EnvMapItemSpawner

```csharp
static readonly string[] DefaultCategories =
{
    "BanksChecker","Common","Guanmu","Hua","JianZhu","Lvshu","Other","Qiao",
    "ShiTou","Songshu","Yeshu","ZaWu","Zacao","Zhibei","texiao","FX_Day","FX_Night"
};

IReadOnlyList<MapItemData> AllMapItems { get; }
int MapItemCount { get; } int SpawnedCount { get; } int ExtrasSpawnedCount { get; }
bool IsReady { get; }

IEnumerator Run(Transform focus, Action<float,string> reportProgress);
void Release();
```

`Run` 的顺序：
```
读 AllMapItem.bytes（YooAsset → Editor AssetDatabase → StreamingAssets/AllMapItem.bytes）
→ 流式模式：EnvItemStreamerLite.Initialize + PrefillAroundFocus
  全量模式：FullSpawnBudgeted
→ EnvMapItemExtras.Spawn（篝火 / FX）
```

### EnvItemStreamerLite

```csharp
void Initialize(List<MapItemData> data, Dictionary<string,Transform> categoryParents,
                Transform mapItemRoot, Transform focus);
IEnumerator PrefillAroundFocus(float progressMin, float progressMax, Action<float,string> report);
int ItemCount { get; } int SpawnedCount { get; }
void Release();
```

- 每帧最多评估 256 个 item（游标轮转），生成预算 `SpawnPerFrameBudget`、回收 `DespawnPerFrameBudget`。
- 生成后处理链：`EnvGroundFit.TrySnapMapItemY` → 关闭远处投影 → `EnvRockColliders.Ensure`（石头）
  → `EnvBridgePillarFit.Ensure`（桥）→ 原生合批。
- 大石（Cliff / Cave / Ramp / Spike / Large）碰撞在 prefab 上，引用 `Models/Collision/*_Collision`。不再按距离开关 MeshCollider。
- 投影迟滞：`ShadowCastMaxDist=26` 关、`ShadowCastRestoreDist=22` 开。

### EnvStreamingConfig（可调参数总表）

```csharp
const string OverridePrefsKey = "GameEnvStreamingOverride";
static bool ResolveStreamingEnabled();     // PlayerPrefs "0"=全量 "1"=强制流式，未设=流式

// 通用环
static float Ring0Enter / Ring0Exit / Ring1Enter / Ring1Exit;  static float HardUnloadFactor = 1.5f;
// 树
static float TreeRing0 / TreeRing1;  static float TreeBillboardKeepRadius = 380f;
// 预算
static int SpawnPerFrameBudget = 24;  static int DespawnPerFrameBudget = 16;
static float FinalizePerFrameBudgetMs = 3f;
// 原生合批
static bool NativeInstancingEnabled = false;  static float NativeInstancingMaxDist = 180f;
// 分类策略
static bool IsTreeCategory / IsRockCategory / IsSmallTreeCategory / IsClutterCategory
static bool IsBridgeCategory / IsSmallVegCategory / IsNeverUnloadCategory / IsLargeRockPrefab
static bool IsFarFieldLandmark / IsBuildingForFarCull / IsSmallRockForFarCull
static void ApplyMobileTierDefaults();   // #if !UNITY_EDITOR，移动端收紧半径
```

### 石头碰撞

大石 prefab 自带启用的非 Trigger `MeshCollider`，网格是预烘焙的 `*_Collision`。
`EnvRockColliders.Ensure` 只在实例上没有 MeshCollider、且 `ShouldHaveRockCollider` 为真时才补。

### FireController / CampfireWeatherGate

```csharp
// FireController（挂在篝火实例上）
void Ignite(); void Extinguish(); void SetInstant(bool instant);
void EnsureRuntimeBindings(); void BindFlameMaterial(Material mat);

// CampfireWeatherGate（静态）
static bool SuppressIgnite { get; }        // 默认 true
static bool IsVisuallyIgnited { get; }
static void SetSuppressIgnite(bool suppress);
static void SetVisualIgnited(EntityId id, bool ignited);
static void ClearVisualIgnited();
```

## 日志 tag

`[MapItem]` `[MapItemExtras]` `[MapItemData]` `[EnvItemStreamerLite]` `[FireController]` `[OilCampfire]`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 走远后撞不到大石 | 大石 prefab 缺 MeshCollider，或 `*_Collision` 网格丢失 | 看 ShiTou 里 Cliff/Cave/Ramp/Spike/Large 的 MeshCollider 是否启用并指向 Collision 资源 |
| 帧率随走动起伏 | `EnvNativeInstancing` 每帧全量 `SyncMatrices`；全岛 MeshCollider 常开 | 调 `SpawnPerFrameBudget`；移动端走 `ApplyMobileTierDefaults` |
| 植物生成在坡上悬空 / 陷地 | `EnvGroundFit.TrySnapMapItemY` 只对部分类别生效 | 看 prefab 名与分类策略 |
| 桥墩悬空 | `EnvBridgePillarFit` 按顶点聚类中心采样地形高度，斜坡上会有一端悬空 | 已知限制 |
| 桥墩被拉变形 | `yCut = bounds.min.y + size.y × 0.73` 的启发式只对特定比例的模型成立 | 换模型需重调 |
| 篝火没有火焰（粉红） | 材质 shader 名必须是 `CustomSRP/NoiseFire`；设备上没有烘焙材质 | 日志 `[FireController] No valid CustomSRP/NoiseFire material` |
| 篝火一直不点燃 | `CampfireWeatherGate.SuppressIgnite` 默认 true；大雨会压制 | 看 `AtmospherePropService.Refresh` |
| 原生合批物体不显示 | 仅 Android 生效；`TryExtractLod0` 只支持单网格单材质 prefab | 多材质 prefab 会退回 MeshRenderer |
| 合批物体被整批剔除 | `RenderParams.worldBounds` 曾误用局部 bounds | 已改为批次世界 AABB 并集 |
| 编辑器和真机表现不一致 | `ApplyMobileTierDefaults()` 是 `#if !UNITY_EDITOR`；Editor 不开原生合批 | 预期行为 |
| MapItem 数据加载失败 | 三级回退：YooAsset → AssetDatabase → `StreamingAssets/AllMapItem.bytes`（Android 走 `UnityWebRequest`） | 看 `[MapItem] AllMapItem empty or failed to load` |
