# 移动端性能优化

改 RT / Shader / 材质批处理或移动端构建前，用本清单审查。不绑定具体仓库路径或接线进度——按代码现状给建议。

管线架构 / 渲染循环 / 接线口径见主 Skill；Draw Calls 合批细节见 [draw-calls.md](draw-calls.md)。

## 核心原则

### 1. 启用 SRP Batcher

最核心的 CPU 优化：减少 SetPass Call。确保 Shader 兼容 SRP Batcher。

**Agent：**

- 材质属性放 `CBUFFER_START(UnityPerMaterial)`（GPU Instancing 时用 `UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)`），勿散落 `float4` 打断 Batcher。二者都是 `UnityPerMaterial` 缓冲区约定，勿随意混用命名。
- 默认勿给共享材质加 `MaterialPropertyBlock`（打断合批）。
- GameObject 路径上 Unity **优先 SRP Batcher**；通常勿指望与 GPU Instancing 同时生效。

### 2. Store Actions

不需要读回时用 `DontCare` / `Discard`（或 Render Graph 的 `Auto`）；仅在后续要读回时用 `Store`。

**Agent：**

- 新建/修改 `SetRenderTarget`：后续 Pass **不采样**该 RT → `DontCare` / `Discard`（Render Graph 可用 `Auto`）；仅在后续要读回时用 `Store`。
- 阴影 atlas、需跨 Pass 的中间 RT 才保留 `Store`。
- **若**存在须被 Final 采样的 Color LUT / 中间色缓冲，则该目标需 Store；LUT 临时 RT 用完应释放（见 [color-grading.md](color-grading.md)）。
- 审查时在 Frame Debugger / Render Graph Viewer 看 Load/Store，避免无意义 Store 占带宽。

### 3. 控制 Shader 变体

卡通 / 多档效果易产生海量变体。用 Shader Stripping，只保留移动端需要的关键字。

**Agent：**

- 按材质切换用 `#pragma shader_feature`；全局/质量档位才用 `#pragma multi_compile`。
- 若运行时用脚本动态启用 keyword，需确认该变体未被构建剥离；否则改用 `multi_compile` 或保留变体。
- 移动端：剥离调试、高阶 PCF、软级联、多余抗锯齿等；构建侧启用 Graphics → Shader Stripping。
- 新增 keyword 前评估组合爆炸；优先 feature 而非全量 multi_compile。

### 4. 善用 GPU Instancing

大量重复网格（植被、建筑等）：Instancing 合并 Draw Call。

**Agent：**

- 材质 Enable GPU Instancing + `#pragma multi_compile_instancing`；或 `Graphics.DrawMeshInstanced` / `RenderMeshInstanced`。
- 与 Batcher 冲突时：共享材质多样物体优先 Batcher；同 mesh 大量实例优先 Instancing（必要时关 Batcher 做对照）。
- 动态逐物体属性若必须用 MPB，Batcher 失效，改走 Instancing 路径。

### 5. 精简渲染通道

默认关闭 Depth Texture、Opaque Texture，除非效果明确要采样。

**Agent：**

- 默认 **不**拷贝 Depth / Opaque；软粒子、折射、屏幕特效需要时才开（Particles → [particles.md](particles.md)）。
- 新增全屏/后处理前先问：能否不采样场景色/深度。

### 6. 简化计算

减少高频 `pow` / `sin` 等；光照限制主方向光，避免 fragment 无界多光源循环。

**Agent：**

- 用查找表、近似、预计算替代复杂分支。
- 移动端目标：**1** 盏主平行光（或项目明确的低上限）。
- 改灯光上限时同步 C# 上传数组与 HLSL `CBUFFER` / `#define`。

### 7. 纹理优化

Mipmap + 平台压缩（如 ASTC）+ 必要时图集。

**Agent：**

- 导入：`mipmapEnabled = true`（UI/特殊贴图除外）；移动端优先 ASTC（按平台 Override）。
- 多小贴图合并 Atlas，减少 SetPass / 材质切换；注意 UV 与 bleed。
- 法线/遮罩按通道用途选压缩，避免错误 sRGB。

## 审查清单

```
- [ ] Shader 兼容 SRP Batcher（`CBUFFER_START(UnityPerMaterial)` / Instancing 时 `UNITY_INSTANCING_BUFFER_START`；无多余 MPB）
- [ ] 中间 RT Store Action 为 DontCare/Discard（或 RG Auto）；仅必需处 Store
- [ ] keyword：shader_feature 优先；运行时动态 keyword 确认变体未被剥离；已剥离移动端不需要的变体
- [ ] 大量重复物：Instancing 或 DrawMeshInstanced
- [ ] Depth Texture / Opaque Texture 默认关
- [ ] 着色：少 pow/sin；移动端主光数量受限
- [ ] 纹理：Mipmap + ASTC（或平台压缩）+ 图集评估
```

## 范围边界

- **管**：性能原则与改代码时的检查项。
- **不管**：场景怎么建、功能是否已接线（以当前工程源码为准，见主 Skill §6）；项目路径/菜单一律现场查，不写进本文件。
- **不宣称**：目标工程已实现 Opaque Texture、自定义 Variant Stripper、或已把灯光收紧为 1——审查时按代码现状说话，按本清单给建议。
