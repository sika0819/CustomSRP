# FXAA 场景

路径：`Assets/CustomSRP/Scenes/FXAA.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateFxaaScene.cs`  
菜单：**CustomSRP → Create FXAA Scene**  
Post FX：`Assets/CustomSRP/Settings/PostFXSettingsFxaa.asset`（Neutral Tone Mapping，Bloom 关闭）

本场只验 **FXAA 对锯齿 / 长边 / 亚像素细节的平滑**，以及和 Render Scale 叠用；勿串 Batcher / CSM / GI。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景 + 菜单可重建 | **已接入** |
| 左右分屏 `allowFXAA` 关 / 开 | **已接入** |
| `CameraBufferSettings.FXAA`（阈值 / subpixel / Quality） | **已接入** |
| `keepAlpha` → green vs luma | **已接入** |
| FXAA Pass（luma、邻域混合、沿边搜索） | **已接入** |

## 布局摘要

| 相机 | Viewport | allowFXAA | 要点 |
|------|----------|-----------|------|
| Main Camera | 左半 `(0,0,0.5,1)` | 关 | 锯齿对照 |
| Camera FXAA | 右半 `(0.5,0,0.5,1)` | 开 | 同机位；`keepAlpha` 关（优先 luma） |

两台相机都 Inherit Asset Render Scale，并 override 到本场 Post FX（FXAA 依赖 Post FX Stack）。

| 物体 | 验收观察点 |
|------|----------------|
| Pincushion | 长边阶梯 |
| Circuitry | 小细节被 subpixel blending 糊掉 |
| Edge Cube / Slab / Blade | 不贴像素网格的硬边 |

## 怎么打开 / 调参

1. 菜单 **CustomSRP → Create FXAA Scene**（或打开已有场景）。
2. Game 视图：左锯齿、右平滑。放大 Game 窗口更容易看。
3. Pipeline Asset → Camera Buffer → **FXAA**：`fixedThreshold` 0.0833、`relativeThreshold` 0.166、`subpixelBlending` 0.75、Quality **High**。
4. 和 Render Scale 叠用：改 Asset **Render Scale**（例如 2、1.333、0.5）和 **Bicubic Rescaling**。
5. 需要保留 alpha 时打开相机 `keepAlpha`，FXAA 退回绿色通道当 luma。

## 接线要点

- Asset `fxaa.enabled` ∧ 相机 `allowFXAA` → 才跑 FXAA。
- Color grading 后写中间 LDR → FXAA →（若有）Final Rescale。
- `keepAlpha` 关：`Apply Color Grading With Luma` + `FXAA With Luma`；开：退回 green。

## 相关

- [Render Scale](render-scale.md) · [Color Grading](color-grading.md) · [Post Processing](post-processing.md)
