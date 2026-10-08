# Unity Custom SRP 版本升级（跨项目）

自建 SRP 在 Unity 6.x → 6.5+ 的**可移植迁移约定**。字段 / 类名以**当前工程命名**为准；是否已升级一律 grep，**勿断言**某仓进度。

**用法**：用户说「升到 6.x / 7.x」「ImportBackbuffer 报错」「拆阴影 Pass」「去 LPPV」→ 按下方版本块逐项对照源码。项目本地验收清单可放在工程 `doc/`，**不**写入本 reference。

---

## 总览（按顺序升级）

| 版本 | 主题 | 破坏性？ |
|------|------|----------|
| **6.0** | Native Render Passes / EntityId / ResetGraph | API 更名 + Native RP |
| **6.1** | cameraTarget 导入 / Color LUT Debug | Present 目标统一 |
| **6.2** | 3D Color LUT Compute | 后处理 LUT 路径 |
| **7.0** | `RenderTargetInfo` / 去 LPPV / 去 list culling | **编译失败**若缺 Info |
| **7.1** | Directional / Other 阴影类拆分 | 重构（外观不变） |
| **7.2** | 独立 Shadow Pass | 重构（外观不变） |

建议：先 6.0→6.2（渲染/后处理），再 7.0（编译必需），再 7.1→7.2（阴影结构）。

---

## 6.0 — Native Render Passes / EntityId

### 目标符号（grep）

```bash
rg -n "GetEntityId|generateDebugData|ResetGraphAndLogException|nativeRenderPassesEnabled|ImportBackbuffer"
rg -n "InitNoBake|FormerlySerializedAs.*renderingLayerMask"
```

### 约定

| 项 | 做法 |
|----|------|
| `RenderGraphParameters.executionId` | `camera.GetEntityId()`（勿用已弃用 instance id 路径） |
| `generateDebugData` | 非 Preview 且非 `isProcessingRenderRequest` 时为 true |
| 灯光 GI delegate | `LightDataGI.InitNoBake(light.GetEntityId())` |
| Native RP | 保持 SRP Core 默认开启；勿无强制 `nativeRenderPassesEnabled = false` 掩盖问题 |
| 异常 | Pipeline `Render` 失败时 `ResetGraphAndLogException`（或等价） |
| Camera settings | `renderingLayerMask`；改名用 `FormerlySerializedAs` |
| Present | Final / PostFX 经 `ImportBackbuffer` 导入目标（细节见 6.1 / 7.0） |

### 验收原则

- Render Graph Viewer 能看到 pass；Final/PostFX 不被误剔
- 无 `executionName` / 旧灯光 ID API 警告

---

## 6.1 — Camera Target + Color LUT Debug

### 目标符号

```bash
rg -n "cameraTarget|ImportBackbuffer|targetTexture|Show Color LUT|ColorLUT"
```

### 约定

| 项 | 做法 |
|----|------|
| 每相机目标句柄 | 在 Setup（或等价）导入：有 `camera.targetTexture` 用该 RT，否则 `BuiltinRenderTextureType.CameraTarget` |
| Present / Gizmos / PostFX | 一律写到该 `cameraTarget` 句柄，勿混用未导入的 CameraTarget |
| LUT 调试 | Rendering Debugger（或项目 Debugger）「Show Color LUT」；无 Post FX 时勿强画 LUT（resolution=0） |

### 验收原则

- 写 RT 的相机只进该纹理；选中相机不污染编辑器其它区域
- 有 Post FX 时调试条可见 LUT

---

## 6.2 — 3D Color LUT（Compute）

### 目标符号

```bash
rg -n "ColorLUT|ApplyLut3D|AddComputePass|colorLUTComputeShader|Tex3D"
rg -n "ApplyLut2D"   # 升级后应消失或仅兼容残留
```

### 约定

| 项 | 做法 |
|----|------|
| 生成 | Compute Shader + `AddComputePass`（或等价）写 **Tex3D** UAV |
| 应用 | 片元 `ApplyLut3D`（或项目等价）；去掉每帧 2D strip LUT 生成路径 |
| 资产 | Post FX Settings（或等价）引用 compute；空引用时软失败并日志，勿硬崩 |
| Debug | LUT 条带按 depth slice 展开（无 slice 间错误插值） |

### 验收原则

- Tone Mapping / grading 观感与升级前一致
- Frame Debugger 常看不到 3D LUT 内容（预期）；用项目 LUT debugger 验

---

## 7.0 — RenderTargetInfo / 去 LPPV（Unity 6.5+）

**Unity 6.5+**：无 `RenderTargetInfo` 的 `ImportBackbuffer` 重载已废弃/编译失败。

### 目标符号

```bash
rg -n "RenderTargetInfo|ImportBackbuffer|rendererListCulling|AllowPassCulling"
rg -n "LightProbeProxyVolume|OcclusionProbeProxyVolume|unity_ProbeVolume|SampleLightProbeOcclusion"
rg -n "cascadeBlend|FilterMode|filterQuality|softCascadeBlend"
```

### 约定

| 项 | 做法 |
|----|------|
| `ImportBackbuffer(rt, info)` | 必填 `RenderTargetInfo`：`width/height = camera.pixelWidth/Height`，`volumeDepth = 1`，`msaaSamples = 1`，framebuffer 常用 `GraphicsFormat.R8G8B8A8_UNorm`，RT 用 `targetTexture.graphicsFormat` |
| `rendererListCulling` | **不要**再设为 true（性能回退；空 list 不再靠此剔 pass） |
| Skybox / Gizmos | 可去掉仅为「空 list」而设的 `AllowPassCulling(false)`；其它必须执行的 pass 仍可禁 cull |
| LPPV | 移除 `PerObjectData.LightProbeProxyVolume` / `OcclusionProbeProxyVolume`；MeshBall / GI / UnityPerDraw 中 `unity_ProbeVolume*`；后续用 APV（未接则勿假装） |
| ShadowSettings | 删除废弃 cascade blend 枚举、分灯种旧 `FilterMode`；统一 `filterQuality` + 布尔 `softCascadeBlend`（名以工程为准） |

### 验收原则

- 可编译、可出图；花屏可重启 Editor
- 无 LPPV / rendererListCulling 警告
- Skybox / Gizmos 仍可见

---

## 7.1 — 拆分 Directional / Other Shadows（外观不变）

为独立 Pass 铺路的**纯重构**。

### 目标符号

```bash
rg -n "class DirectionalShadows|class OtherShadows|struct Handles"
rg -n "ConvertToAtlasMatrix|SetTileViewport|ReserveShadows|GetHandles"
rg -n "ShadowResources"   # 升级后应被 Handles 替代
```

### 目标结构

```
Shadows（编排）
├── DirectionalShadows  — atlas / cascades / matrices / SoftCascadeBlend
└── OtherShadows        — atlas / OtherShadowData / Spot+Point tiles
```

| 项 | 做法 |
|----|------|
| `Handles` | 各专类内嵌只读 struct：`atlas` + buffer(s)；`Use(IBaseRenderGraphBuilder)` 声明读依赖 |
| 共用 | `ConvertToAtlasMatrix` / `SetTileViewport` 留在 `Shadows`（static） |
| Reserve | 专类 `ReserveShadows(...)`；shadow mask channel 可写入 data.w；`UsesShadowMask` 或等价 |
| Lighting | 仍可 Reserve；`GetHandles` + `BuildRendererLists` 可暂留 Shadows / Lighting（7.2 再拆） |
| Geometry | `shadowHandles.Use(builder)`，勿散落逐个 UseTexture/UseBuffer |

### 验收原则

- CSM / Spot / Point 影与重构前一致
- 无影灯：default shadow texture + 空 buffer，不报错

---

## 7.2 — 独立 Shadow Passes（外观不变）

### 目标符号

```bash
rg -n "ShadowsPass|DirectionalShadowsPass|OtherShadowsPass"
rg -n "LightingPass\.Handles|LightResources|CullShadowCasters"
rg -n "UsesShadowMask"
```

### 目标结构与顺序

```
CameraRenderer
  Shadows.Setup(settings)
  LightingPass.Record(..., shadows)     # 仅 Reserve + 上传灯光
  ShadowsPass.Record(...)               # 内录三个阴影相关 pass
    → DirectionalShadowsPass
    → OtherShadowsPass
    → CullShadowCasters（若有影灯）
    → ShadowsPass（关键词 / 距离淡出 / atlas 尺寸 globals）
  Geometry... Use(LightResources)
```

| 项 | 做法 |
|----|------|
| `Shadows` 归属 | **CameraRenderer**（或等价）持有实例；**不要**在 LightingPass 内 `new Shadows()` |
| `LightingPass` | 只负责灯光 buffers；`Handles`（directional/other/tiles）；SetupLights 调 `shadows.directionalShadows.ReserveShadows` / `otherShadows.ReserveShadows`；**禁止**在 Lighting 里 Render 阴影 |
| `ShadowsPass.Handles` | 包装 Directional + Other 的 Handles；供 `LightResources` |
| `LightResources` | `lightHandles` + `shadowHandles` + `Use(builder)` |
| Culling | `ShadowCastersCullingInfos` 在 ShadowsPass.Record 创建，传入两专类 `BuildRendererLists`，有灯时 `CullShadowCasters` |
| `Shadows.Render` | 仅全局：filter / shadowmask keywords、distance fade、`_ShadowAtlasSize`（名以工程为准） |
| `UsesShadowMask` | 专类自管；Shadows.Render 读 `directional \|\| other` |

### 验收原则

- 画面与 7.1 一致
- Render Graph Viewer：Directional Shadows / Other Shadows / Shadows 三个 pass + Lighting
- Lighting 与阴影解耦：改灯光上传不碰 atlas 绘制

---

## Agent 升级硬约束

1. **先 grep 再改**：确认当前处于哪一档（缺 `RenderTargetInfo` → 先 7.0；仍是单体 `Shadows` 含全部绘制 → 可走 7.1→7.2）。
2. **外观不变优先**：7.1 / 7.2 是结构重构；验收以阴影画面为准，勿顺便改 bias / cascade 默认。
3. **跨项目**：不写死路径/菜单/「本仓已接」；符号表是搜索起点。
4. **LPPV**：删除即可；勿在未接 APV 时声称探针体积已替换。
5. **ImportBackbuffer**：6.5+ 必须带 `RenderTargetInfo`；width/height 用 **camera 像素尺寸**（与教程及现行 API 一致）。
6. **Pass 顺序**：Reserve（Lighting）→ 建 list + 画 atlas（专 Pass）→ CullShadowCasters → 全局阴影状态 → 几何采样。阴影 atlas 仍须在相机 color Setup/Present 逻辑可采样之前完成写入。
7. 项目本地进度 / 验收勾选 → 工程 `doc/`；本文件只留可复制约定。

## 排障速查

| 现象 | 查 |
|------|-----|
| `ImportBackbuffer` 编译错 / obsolete | 缺 `RenderTargetInfo` → §7.0 |
| RT 相机画到错误目标 | cameraTarget 未统一 → §6.1 |
| LUT 观感变 / banding | 2D→3D 路径、compute 空引用 → §6.2 |
| LPPV / ProbeVolume 警告 | §7.0 清理 |
| 拆类后无影 | Handles 未 Use；Reserve 在 Build 之前；Cull 未调 → §7.1/7.2 |
| Lighting 里仍画阴影 | 未完成 7.2；应只在 *ShadowsPass 画 atlas |
| Skybox/Gizmos 消失 | 误删必要 `AllowPassCulling(false)`；或 list 未创建 → §7.0 |
