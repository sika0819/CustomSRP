# Point and Spot Lights 场景

路径：`Assets/CustomSRP/Scenes/PointAndSpotLights.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreatePointAndSpotLightsScene.cs`  
布局数据：`Assets/CustomSRP/Editor/PointAndSpotLightsSceneData/`

跨项目约定：Skill [`references/point-and-spot-lights.md`](../.cursor/skills/custom-srp/references/point-and-spot-lights.md)

Other 实时阴影验收见 [Point and Spot Shadows](point-and-spot-shadows.md)。

## 场景是干什么的

验收 **Point / Spot 直接光**（衰减、角衰减、多灯叠光）。本场多数灯 **不投阴影**，避免与阴影场混验。

| 期望 | 能力 |
|------|------|
| 多盏 Spot/Point 照亮金属网格 | Other Lights 上传 + HLSL 循环 |
| 弱 Directional 补底 | 与 Other 分套 max |
| MeshBall（Play） | Instanced Lit |

## 本仓接线

| 项 | 状态 |
|----|------|
| Other 直接光 | **已接** |
| Other 实时阴影 | 见 [PointAndSpotShadows](point-and-spot-shadows.md)（本场不验） |
| Lights Per Object / Forward+ | 以源码 `ForwardPlus` / 相关 Pass 为准 |

## 怎么打开

1. **CustomSRP → Create & Assign Pipeline Asset**（已挂可跳过）  
2. **CustomSRP → Create Point and Spot Lights Scene**

## 相关

- [Point and Spot Shadows](point-and-spot-shadows.md)  
- [Directional Lights](directional-lights.md)  
- Skill `references/point-and-spot-lights.md`
