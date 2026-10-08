# 快速开始

## 前置

- Unity Hub，编辑器 **6000.6.4f1**（见 `ProjectSettings/ProjectVersion.txt`）
- 颜色空间建议 **Linear**（`Edit → Project Settings → Player`）

## 打开与挂载

1. Unity Hub → Open → 选择本仓库根目录
2. 菜单 **CustomSRP → Create & Assign Pipeline Asset**
   - 生成/复用 `Assets/CustomSRP/Settings/CustomRenderPipelineAsset.asset`
   - 写入 `GraphicsSettings.defaultRenderPipeline` 与 `QualitySettings.renderPipeline`
3. 菜单 **CustomSRP → Create Test Scene**
   - 生成材质、贴图与场景 `Assets/CustomSRP/Scenes/CustomRPTest.unity`
   - 用途与物体对照见 [CustomRPTest 场景](custom-rp-test.md)
4. 菜单 **CustomSRP → Create Draw Calls Scene**
   - 生成 `Assets/CustomSRP/Scenes/DrawCalls.unity`（批处理 / Instancing / Clip）
   - 用途与实验步骤见 [DrawCalls 场景](draw-calls.md)
5. 其余 17 个验证场景：见根目录 [`README.md`](../README.md) 场景表，或 [`doc/README.md`](README.md)
6. 打开对应场景，进入 Play / 用 Frame Debugger 查看

若 Graphics 已挂管线，步骤 2 可跳过；Asset 缺失时 Editor 启动也会尝试自动挂载。

## 自建材质

- Shader 选 **`CustomSRP/Unlit`**
- 不要用 Built-in Standard / URP Lit：本管线不会按预期绘制它们（Editor 下会走 Error 品红）

## Asset 开关

在 `CustomRenderPipelineAsset` Inspector 上：

| 字段 | 默认 | 作用 |
|------|------|------|
| Use Dynamic Batching | 开 | 传给 `DrawingSettings.enableDynamicBatching` |
| Use GPU Instancing | 开 | 传给 `DrawingSettings.enableInstancing` |
| Use SRP Batcher | 开 | 构造 Pipeline 时设 `GraphicsSettings.useScriptableRenderPipelineBatching` |

GameObject 路径上 SRP Batcher 与 GPU Instancing 通常不同时生效（Unity 优先 Batcher）。
