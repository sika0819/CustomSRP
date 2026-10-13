# HDR 场景

路径：`Assets/CustomSRP/Scenes/Hdr.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateHdrScene.cs`  
布局：`Assets/CustomSRP/Editor/HdrSceneData/*.json`  
Post FX：`Assets/CustomSRP/Settings/PostFXSettings.asset`（挂在 Pipeline Asset）

本场只验 **HDR 中间缓冲 / 散射 Bloom / Tone Mapping**；勿串 Batcher / CSM / GI。  
Color Grading / LUT 见 [color-grading.md](color-grading.md)。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景（Tone Mapping Scene 布局） | 已建；菜单可重建 |
| Asset `Camera Buffer / allowHDR` + 相机 `allowHDR` | **已接入** |
| `DefaultHDR` 中间帧缓冲 | **已接入** |
| Bloom `fadeFireflies` / Scattering | **已接入** |
| Tone Mapping（经 Color Grading LUT） | **已接入**（见 Color Grading） |

## 布局摘要

- Plane + 多档 Emission 球 / 立方体（最强约 **8**）
- 1 盏弱 Directional（intensity **0.05**，Hard 影）+ MeshBall（Play）
- Ambient = 黑（`intensity 0`）
- 相机约 `(0.68, 13.3, -14.6)`，俯视发射体网格

布局数字以 `HdrSceneData/*.json` 为准；勿写死后合计。

## 怎么打开 / 调参

1. 菜单 **CustomSRP → Create & Assign Pipeline Asset**（确保 Post FX Settings 已挂）。
2. 菜单 **CustomSRP → Create HDR Scene**（若场景缺失）。
3. 强制重建：在 `HdrSceneData/` 下放空文件 `.force-rebuild` 后让 Editor 重载，或再跑菜单。
4. 选中 `Settings/PostFXSettings`：调 scatter / fadeFireflies / toneMapping；Asset 关 `Camera Buffer / allowHDR` 对照 LDR。

## 验收

- Asset 关 `Camera Buffer / allowHDR` 或相机关 HDR → 中间缓冲走 LDR Default。
- threshold≈1 → 主要只有 HDR Emission 贡献 Bloom。
- Scattering vs Additive；Tone Mapping 相对亮度差异。
- 移动相机时 fireflies：开 `fadeFireflies` 应明显减弱闪烁。
