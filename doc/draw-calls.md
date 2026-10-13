# DrawCalls 场景

路径：`Assets/CustomSRP/Scenes/DrawCalls.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateDrawCallsScene.cs`  
示例组件：`Assets/CustomSRP/Examples/PerObjectMaterialProperties.cs`、`MeshBall.cs`

下列数量、位置、默认材质均为当前生成脚本的近似值，准确值以 [`CreateDrawCallsScene.cs`](../Assets/CustomSRP/Editor/CreateDrawCallsScene.cs) 为准。实例数等以 [`MeshBall.cs`](../Assets/CustomSRP/Examples/MeshBall.cs) 为准。

## 场景是干什么的

**批处理 / Instancing / 透明 / Alpha Clip 验证场景**：在 Custom SRP 下观察 Draw Call 合并方式，以及 MaterialPropertyBlock、GPU Instancing、Cutout 对批处理的影响。

它**不是**开篇「管线能不能画」的验收场（那是 [CustomRPTest](custom-rp-test.md)）。本场景假设管线已挂好、Unlit 已能画，专门用来调 Asset 上的批处理开关、看 Frame Debugger。

| 你期望看到 / 验证 | 对应能力 |
|-------------------|----------|
| 中心区大量红/绿/黄/蓝球 | 共享材质 → **SRP Batcher** 友好 |
| 约 1/3 球颜色各异 | `PerObjectMaterialProperties`（MPB）→ **打断 Batcher**；若材质 Enable GPU Instancing、Shader 支持、且 Asset `Use GPU Instancing` 允许，则**可能**走 Instancing |
| 左侧一排黄半透球 | Unlit Transparent + UVAlpha |
| 右侧 Cutout 球洞洞不同 | Alpha Clip + 逐物体 `_Cutoff` |
| Play 后远处一团实例球 | `MeshBall`：`Graphics.DrawMeshInstanced`（当前代码实例数为 1023，以 `MeshBall.cs` 为准） |

## 怎么打开 / 重建

1. 先挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**（Graphics 已挂可跳过）。
2. 打开已有场景：Project 里双击 `Assets/CustomSRP/Scenes/DrawCalls.unity`。
3. 若场景或材质缺失：菜单 **CustomSRP → Create Draw Calls Scene**  
   - 会重建本场景专用材质（含 `UnlitInstanced` / `UnlitClip` 等）与 `UVAlpha.png`  
   - 加入 Build Settings（**不**抢第一位，第一位留给 CustomRPTest）  
   - 自动打开该场景  

进入 **Play Mode**（否则看不到 `MeshBall`），再用 **Window → Analysis → Frame Debugger** 对照批处理。

## 场景里有什么

### 相机与灯光

| 对象 | 要点 |
|------|------|
| Main Camera | 位置约 `(0, 4, -18)`，俯视整片球体 |
| Directional Light | 默认方向光（Unlit 不读光照） |

无副相机、无 Overlay UI（那些在 CustomRPTest）。

### Hierarchy 分区

| 父物体 | 内容 | 材质 / 组件 |
|--------|------|-------------|
| `Spheres` | ~76 个球，螺旋排布 | 红/绿/黄/蓝共享 Unlit；每 3 个球中约 1 个使用 `UnlitInstanced` + `PerObjectMaterialProperties` |
| `TransparentSpheres` | 8 个球，约 `x=-6…` | `UnlitYellowTransparent`（Transparent + UVAlpha） |
| `ClipSpheres` | 12 个球，约 `x=6…` | `UnlitClip` + `PerObjectMaterialProperties`（随机 cutoff） |
| `MeshBall` | 空物体，约 `(0,0,12)` | `MeshBall` 组件；材质默认 `UnlitClip`（材质须 Enable GPU Instancing） |

### 材质一览

均在 `Assets/CustomSRP/Materials/`，Shader 均为 **`CustomSRP/Unlit`**。

| 材质 | Surface | 队列 / 渲染阶段 | Enable GPU Instancing | 用途 |
|------|---------|-----------------|----------------------|------|
| `UnlitRed` / `Green` / `Yellow` / `Blue` | Opaque | Geometry 2000 | 关 | 共享材质球体 → Batcher |
| `UnlitInstanced` | Opaque | Geometry 2000 | **开** | 带 MPB 的球体（常规 Renderer） |
| `UnlitYellowTransparent` | Transparent | Transparent 3000 | 关 | 透明簇 |
| `UnlitClip` | Clip | AlphaTest 2450 | **开** | Cutout 球 + MeshBall 默认材质 |

### 关键示例组件

**`PerObjectMaterialProperties`**

- 用 `MaterialPropertyBlock` 写 `_BaseColor` / `_Cutoff`
- **会打断 SRP Batcher**（同一材质也合不进同一 Batcher 组）
- 若材质 Enable GPU Instancing、Shader 支持 Instancing，且管线 Asset `Use GPU Instancing` 允许，则**可能**走 GPU Instancing（不是「用了 MPB 就自动 Instancing」）
- 改 Inspector 上的颜色 / Cutoff 会立刻反映到该球

**`MeshBall`**

- `Update` 里 `Graphics.DrawMeshInstanced`；实例数当前代码为 **1023**（`MaxInstances`，以 `MeshBall.cs` 为准）
- **仅 Play Mode** 可见；Edit Mode 场景里只有空物体
- **不经过** `CameraRenderer` / Asset `Use GPU Instancing`；可见性取决于：材质 Enable GPU Instancing、Shader 支持 Instancing、MPB 颜色数组长度与实例数匹配、实例在相机视锥内且层未剔除
- 默认绑 `UnlitClip`；颜色通过 MPB 的 `SetVectorArray(_BaseColor, …)` 逐实例传入

## Pipeline Asset 怎么配合看

在 `CustomRenderPipelineAsset` Inspector 上切换：

| 字段 | 影响对象 | 建议实验 |
|------|----------|----------|
| Use SRP Batcher | 常规 Renderer | 开：共享材质球应大量合批；关：再观察 Instancing / Dynamic |
| Use GPU Instancing | 常规 Renderer（`DrawRendererList`）；**不影响** `DrawMeshInstanced` / MeshBall | 开：带 MPB 的 `UnlitInstanced` 球才可能走实例化；MeshBall 仍看材质 Enable GPU Instancing + Shader |
| Use Dynamic Batching | 仅常规 Renderer 的小网格合批；**不影响** `DrawMeshInstanced` | 开：小网格、无 Batcher/Instancing 时可能合批（次要对照） |

本管线事实：GameObject 渲染路径上 Unity **优先 SRP Batcher**，通常不要指望与 GPU Instancing「同时」生效。要验证常规路径的 Instancing，需关掉 Batcher，并同时开 Asset `Use GPU Instancing` + 材质 Enable GPU Instancing。MeshBall 走 `DrawMeshInstanced`，与 Asset 该开关无关。

## 预期现象速查

| 条件 | 预期 |
|------|------|
| SRP Batcher 开 | 共享材质球大量合批 |
| SRP Batcher 关 + Asset `Use GPU Instancing` 开 + 材质 Enable GPU Instancing 开 | `UnlitInstanced` 球可能走 Instanced |
| 带 MPB 的球 | 不再进同一 SRP Batcher 组 |
| Edit Mode | MeshBall 不可见，不是丢失 |
| Play + 材质 Enable GPU Instancing 开（与 Asset 开关无关） | MeshBall 远处实例团；另需视锥内、层未剔除 |

## 建议怎么用

### 第一次验收

1. 确认 Graphics 已挂 Custom Pipeline Asset。  
2. 打开本场景 → **Play**。  
3. Game 视图：中心彩色球簇 + 左透明 + 右 Cutout + 远处 `MeshBall` 实例团。  
4. Frame Debugger 排序（队列号）：**Opaque（2000）→ AlphaTest / Cutout（2450）→ Skybox（2500）→ Transparent（3000）**。  
   本管线用 `RenderQueueRange.opaque` 一次提交 0–2500，故 Opaque 与 AlphaTest 同段绘制，但队列号与排序仍分档。注意共享材质段与带 MPB 段的 Draw Call 差异。

### 对照实验（推荐顺序）

1. **Batcher 开**：看 `Spheres` 里同色共享材质是否合批。  
2. **点开带 `PerObjectMaterialProperties` 的球**：MPB 后该球不应再进同一 Batcher 组。  
3. **关 SRP Batcher、开 Asset Use GPU Instancing**，并确认材质 Enable GPU Instancing：再抓一帧，看 `UnlitInstanced` 球；MeshBall 另查材质开关（与 Asset 无关）。  
4. **改 Clip 球的 Cutoff**：洞大小变化，确认 `_CLIPPING` 与逐物体 cutoff 生效。  
5. **改 MeshBall 的 Material**：换 `UnlitInstanced`（Opaque）或保持 `UnlitClip`，对比实例化外观。

### 不要用本场景做的事

- 验证「Standard → 品红 Error」、多相机、Overlay UI → 用 [CustomRPTest](custom-rp-test.md)  
- 在 Edit Mode 判断 MeshBall「丢了」——必须进 Play  
- 给普通共享材质球默认加 MPB——会故意打断 Batcher，冲掉本场景的对照意义  

## 相关代码

| 文件 | 用途 |
|------|------|
| [`CreateDrawCallsScene.cs`](../Assets/CustomSRP/Editor/CreateDrawCallsScene.cs) | 场景 / 材质生成；数量与布局以它为准 |
| [`PerObjectMaterialProperties.cs`](../Assets/CustomSRP/Examples/PerObjectMaterialProperties.cs) | 逐物体 MPB（`_BaseColor` / `_Cutoff`） |
| [`MeshBall.cs`](../Assets/CustomSRP/Examples/MeshBall.cs) | `DrawMeshInstanced`；`MaxInstances` 与 MPB 数组长度以它为准 |

## 相关文档

- [快速开始](getting-started.md) — Asset 批处理开关表  
- [CustomRPTest 场景](custom-rp-test.md) — 开篇管线验收  
- [Shader 规范](shader-conventions.md) — Instancing buffer、`_CLIPPING`、Transparent / Clip  
- [排查](troubleshooting.md) — 常见现象  
- 根目录 [README](../README.md) — 两场景一句话对照  
