# Post Processing 场景

路径：`Assets/CustomSRP/Scenes/PostProcessing.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreatePostProcessingScene.cs`  
布局：`Assets/CustomSRP/Editor/PostProcessingSceneData/*.json`  
Post FX：`Assets/CustomSRP/Settings/PostFXSettings.asset`（挂在 Pipeline Asset）

本场只验 **Post FX / Bloom**；勿串 Batcher / CSM / GI。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景布局 | 已建；菜单可重建 |
| `PostFXSettings` / `PostFXStack` | **已接入** |
| 中间帧缓冲 `_CameraColorAttachment` + `_CameraDepthAttachment` | **已接入**（Post FX / copyColor / copyDepth 时） |
| Bloom 金字塔 / prefilter threshold / intensity / bicubic | **已接入** |
| Scene 视图 Image Effects 开关 | **已接入**（`PostFXStack.Editor`） |

## 调用链

`Asset.postFXSettings` → `CameraRenderer` → `PostFXStack.Setup` →（活跃则）中间 RT 画几何 → `PostFXStack.Render` → `DoBloom` → `DrawProcedural`（`Hidden/CustomSRP/Post FX Stack`）。

Frame Debugger：看 **Post FX** / **Bloom** 采样与金字塔 RT。

## 布局摘要

- Plane + Metallic/Smoothness 网格 + Pancaking Cubes + Varied Objects + MeshBall（Play）
- 4 Directional + 6 Spot + 6 Point
- Clip / Transparent：`Cast Shadows = Two Sided`
- 相机俯视约 `(0, 12, 1)`

## 怎么打开 / 调参

1. 菜单 **CustomSRP → Create & Assign Pipeline Asset**（会确保 Post FX Settings 已挂）。
2. 菜单 **CustomSRP → Create Post Processing Scene**（若场景缺失）。
3. 选中 `Settings/PostFXSettings`：调 `maxIterations` / `threshold` / `thresholdKnee` / `intensity` / `bicubicUpsampling`。
4. Scene 视图 Effects → **Image Effects** 可关后处理。

## 验收

- intensity=0 或未挂 settings → 等价 Copy / 跳过 stack，无 glow。
- threshold 升高 → 仅亮部贡献 Bloom。
- bicubic 开/关 → 高对比边缘块状感差异。
- Preview / ReflectionProbe 相机不跑 Post FX（`cameraType` 过滤）。
