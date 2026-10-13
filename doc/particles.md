# Particles 场景

路径：`Assets/CustomSRP/Scenes/Particles.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateParticlesScene.cs`  
菜单：**CustomSRP → Create Particles Scene**  
布局数据：`Assets/CustomSRP/Editor/ParticlesSceneData/*.json`  

本场只验 **Unlit 粒子 + Color/Depth Texture**；勿串 Batcher / CSM / GI。

## 当前状态

| 项 | 状态 |
|----|------|
| 场景 + 菜单可重建 | **已接入** |
| 粒子贴图（Single / Flipbook + Distortion） | **已接入**（`Textures/Particle*.png`） |
| `CustomSRP/Particles/Unlit` | **已接入** |
| `_VERTEX_COLORS` / `_FLIPBOOK_BLENDING` | **已接入** |
| `Fragment.hlsl` + 透视/正交 depth | **已接入** |
| `_NEAR_FADE` / `_SOFT_PARTICLES` / `_DISTORTION` | **已接入** |
| `CameraBufferSettings` copyColor / copyDepth | **已接入** |

## 布局摘要

| 物体 | 要点 |
|------|------|
| Main Camera | 与 Multiple Cameras 同机位；`copyColor` + `copyDepth` |
| Varied Objects / Pincushion | Opaque / Clip / Transparent + PerObjectMaterialProperties |
| Directional ×2 Soft + Spot Hard + Point Soft（黄灯泡 intensity 20） | 背景照明 |
| Particles Single | 默认 **Inactive**；Disc + Distortion strength 0.01 |
| Particles Flipbook | **Active**；4×4 sheet、UV2+AnimBlend、flip 50%、Distortion 0.07 |

## 材质

| 材质 | 关键词 |
|------|--------|
| `UnlitParticlesSingle` | Vertex Colors、Near Fade、Soft Particles、Distortion |
| `UnlitParticlesFlipbook` | 同上 + Flipbook Blending |

## 验收

1. 菜单重建场景；Asset 已挂 Camera Renderer shader，Copy Color/Depth 开。  
2. Play：仅 Flipbook 喷发；与几何相交处软边；近相机淡出；背景折射扭曲。  
3. 需要对照 Single 时，在 Hierarchy 启用 `Particles Single`、关闭 Flipbook。  
4. Frame Debugger：opaque 后 color/depth copy；透明粒子采样 `_CameraDepthTexture` / `_CameraColorTexture`。
