# Multiple Cameras

多相机（分屏 / Overlay / Render Texture）、viewport 与最终 Present、Rendering Layer（几何 vs 灯光）、按相机 Post FX。字段 / 类名以**当前项目命名 / 源码为准**。

接线状态见主 Skill §6（**先识别入口**，再分层 grep；**勿断言**某仓已接或未接）。

通用 Color Grading / LUT / Bloom → [color-grading.md](color-grading.md)。本文只谈**多相机语境**下的 Post FX 差异（viewport、final blend、per-camera override、`Load`）。

## 1. 先识别入口

| 线索 | 可能入口 |
|------|----------|
| Camera Stack / Base + Overlay / Render Type | URP Camera Stack |
| per-camera Settings 组件 + Final Blend / Rendering Layer / override Post FX | 自建 / Custom SRP |
| 多 Camera、不同 `rect` / `targetTexture`，无自定义 Settings | 仅引擎多相机 + 默认 RP 行为 |
| 无 Stack、无 per-camera config、单相机 | **该项目无多相机管线入口（正常）** |

未识别前**不要**假设存在某套固定类名。跨项目第一步是上表，不是 grep 固定符号名。

## 2. 怎么用（通用）

1. 找到项目的多相机 / Stack / per-camera config（若 §1 判定无入口则停止）。
2. Game 视图验分屏或 Overlay；排除 Preview / Reflection（若管线有过滤）。
3. Frame Debugger：按**相机名**看每段 Cull → Draw → Post FX → Present。
4. 改一项可见差异（viewport、final blend、rendering layer、per-camera Post FX）→ 确认生效。

## 3. 通用概念（非单一实现）

- **Present / Final Pass**：把中间色拷到 CameraTarget 时，须在 `SetRenderTarget` **之后** `SetViewport(camera.pixelRect)`；否则分屏 Post FX 会画满整屏。
- **Load vs DontCare**：非全屏 `rect`，或 final dest blend 非 Zero 时，对 CameraTarget 常需 **Load**（Tile GPU 上 DontCare 易脏边）。全屏 + 覆盖式 Zero dest 才宜 DontCare。
- **Final blend**：基准相机常用 `One` / `Zero`；Overlay 常用 `One` / `OneMinusSrcAlpha`（premultiplied）或项目等价配置。
- **Alpha**：写深度的不透明片元最终 alpha 常强制为 1；半透明按表面 alpha。有 Bloom 时最终合成宜保留高分辨率源的 alpha，否则 Overlay 透明失效。
- **Rendering Layer**：相机 mask 常同时进 **FilteringSettings（几何）** 与（可选）灯光过滤。「同场景不同灯」：物体 Everything；灯各占一层；相机 mask 排除对方灯层；灯光过滤须显式开启（如 mask-lights 开关）。
- **默认 Layer**：新建 MeshRenderer 常仅 bit0（Layer 1）。相机 mask=`~Layer1` 会滤掉默认几何 → 整侧无物体。
- **`DrawMeshInstanced` / 无 MeshRenderer**：其 renderingLayer **不跟** 手改的 MeshRenderer mask，常默认 Layer 1；一侧相机排除 Layer 1 时 Instanced 会消失。
- **Per-camera Post FX**：可覆盖全局 Settings；部分实现里 override + null = 关闭 FX（以项目为准）。
- **`GetTemporaryRT`**：在 SRP 中 `width || height <= 0` 被视为 camera-relative 且**禁止**。常见来源：`pixelWidth/Height==0`；Bloom 金字塔在 `downscaleLimit==0` 时除到 0。

概念流（通用名）：

```mermaid
flowchart LR
  Identify["Identify entry"] --> PerCamConfig["PerCameraConfig"]
  PerCamConfig --> CullDraw["Cull and Draw filter layers"]
  CullDraw --> Lights["Lights optional mask"]
  Lights --> PostFX["PostFX if any"]
  PostFX --> Present["Present viewport and blend"]
```

## 4. 排障（通用）

| 现象 | 查 |
|------|-----|
| 分屏 Post FX 后后画的相机盖满全屏 | Final Present 是否 `SetViewport(pixelRect)`；中间 RT 是否按 camera pixel 尺寸 |
| Tile GPU 分屏边缘脏数据 | 非全屏时 CameraTarget 是否 `Load` 而非 `DontCare` |
| Overlay 不透 / 黑底（有 Post FX） | final blend、背景 alpha；管线若强制 Color clear，勿只靠 Depth clear |
| Overlay 无 Post FX 仍不透底层 | 是否 Depth-only clear；final 是否覆盖式 `One`/`Zero` |
| 一侧相机整屏无几何 | 相机 `renderingLayerMask` 是否滤掉默认 Layer 1；物体是否 Everything |
| 「同场景不同灯」失败 | 物体 Everything？灯各一层？相机排除对方灯层？灯光 mask 开关是否打开？ |
| Instanced / 无 MeshRenderer 一侧消失 | Instanced 路径的 renderingLayer；勿只查 MeshRenderer |
| `GetTemporaryRT (width \|\| height <= 0)` | pixel 尺寸为 0；Bloom `downscaleLimit==0`；SRP 禁止 camera-relative 临时 RT |
| 按相机 Post FX 无效 / Overlay 仍吃全局 FX | per-camera override；null Settings 是否表示关 FX |

## 5. 分层 rg

```bash
# 层 1：通用（跨项目先跑；禁止路径/场景名/菜单名）
rg -n "renderingLayerMask|SetViewport|pixelRect|CameraStack|targetTexture|maskLights|FinalBlend|finalBlend"

# 层 2：仅当项目像本管线风格时再查（候选，非跨项目必存在；禁止路径）
rg -n "DrawFinal|CustomRenderPipelineCamera|CameraSettings|maskLights|_FinalSrcBlend|_FinalDstBlend|GetFinalAlpha|ReinterpretAsFloat|DirectionsAndMasks"
```

- 层 2 未命中 ≠ 无多相机（常见于 URP Stack）。
- 层 2 符号为 Custom SRP / **候选**；命中也不等于「必须按某一固定架构实现」。

## 6. 验收 checklist（通用）

- [ ] 已识别入口类型（含「无入口」），未假设固定类名
- [ ] 分屏 / Overlay / RT 用通用概念描述；具体类名以项目为准
- [ ] 分层 rg：先层 1，仅在像 本管线风格时才层 2
- [ ] 一侧无画面时先查 Rendering Layer（几何 + 灯），再查 viewport / Post FX

## 7. 与其它主题边界

- Color Grading / LUT / tone mapping → [color-grading.md](color-grading.md)
- 每相机 Render Scale / bufferSize / Final Rescale → [render-scale.md](render-scale.md)
- 移动端 Store / 中间 RT → [mobile-perf.md](mobile-perf.md)（若 Present 采样中间色，则需 Store）
- DrawMeshInstanced 可见性通则 → [draw-calls.md](draw-calls.md)；本文补 **Rendering Layer** 一侧

