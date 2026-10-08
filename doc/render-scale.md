# Render Scale 场景

路径：`Assets/CustomSRP/Scenes/RenderScale.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateRenderScaleScene.cs`  
菜单：**CustomSRP → Create Render Scale Scene**  
布局数据：`Assets/CustomSRP/Editor/RenderScaleSceneData/*.json`（自 ParticlesSceneData）  
Post FX：`Assets/CustomSRP/Settings/PostFXSettingsRenderScale.asset`（2 次 Additive Bloom）

本场只验 **Render Scale / 每相机 Override / LDR Final Rescale / Distortion 的 `_CameraBufferSize`**；勿串 Batcher / CSM / GI。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景 + 菜单可重建 | **已接入**（Particles 布局 + 左右分屏） |
| `CameraBufferSettings.renderScale` / `bicubicRescaling` | **已接入** |
| 每相机 `RenderScaleMode` Inherit / Multiply / Override | **已接入** |
| 中间缓冲按 `bufferSize`；`_CameraBufferSize` | **已接入** |
| Bloom `ignoreRenderScale` + `bufferSize` | **已接入** |
| Post FX `Final Rescale`（LDR 后再缩放） | **已接入** |
| Bicubic UpOnly / UpAndDown | **已接入** |

## 布局摘要

| 相机 | Viewport | Render Scale | 要点 |
|------|----------|--------------|------|
| Main Camera | 左半 `(0,0,0.5,1)` | Inherit（跟 Asset） | Post FX override → RenderScale 专用 Bloom |
| Camera RenderScale Override | 右半 `(0.5,0,0.5,1)` | Override **0.5** | 同机位；像素更粗，Distortion UV 仍对齐 |

几何 / 灯 / Flipbook Distortion 粒子与 Particles 场一致，便于对照 screen UV。

## 怎么打开 / 调参

1. 菜单 **CustomSRP → Create Render Scale Scene**（或打开已有场景）。
2. Game 视图：左 Inherit、右 0.5；Game 窗口放大可看像素块。
3. 调 Pipeline Asset → Camera Buffer → **Render Scale**（0.1–2）与 **Bicubic Rescaling**。
4. 调右相机 `CustomRenderPipelineCamera` → Override / Multiply。
5. Distortion：确认 Flipbook 粒子折射不因 scale≠1 错位。
6. 关 Post FX 时缩小 scale 仍会走中间缓冲 + `DrawFinal` 拉伸。

## 相关

- 跨项目约定：Skill [references/render-scale.md](../.cursor/skills/custom-srp/references/render-scale.md)
- [Particles](particles.md) · [Post Processing](post-processing.md) · [Color Grading](color-grading.md)
