# 架构

本管线是 Unity 6 最小 Custom SRP：职责分层，无 URP/HDRP 依赖。

```
CustomRenderPipelineAsset  →  CreatePipeline()（含 ShadowSettings + PostFXSettings）
        ↓
CustomRenderPipeline       →  遍历 Camera，设 Batcher / Linear lights
        ↓
CameraRenderer             →  主循环见 [渲染循环](render-loop.md)
        ↓
    Lighting + Shadows     →  Directional / Other globals + 阴影 atlas
        ↓
    PostFXStack            →  可选中间帧缓冲 + Bloom
```

## 关键文件

| 路径 | 职责 |
|------|------|
| `Assets/CustomSRP/Runtime/CustomRenderPipelineAsset.cs` | `RenderPipelineAsset<CustomRenderPipeline>`；批处理开关；`ShadowSettings`；`PostFXSettings` |
| `Assets/CustomSRP/Runtime/CustomRenderPipeline.cs` | `RenderPipeline`；每相机调用 `CameraRenderer`；`lightsUseLinearIntensity` |
| `Assets/CustomSRP/Runtime/CameraRenderer.cs` | 主循环（顺序见 [render-loop](render-loop.md)） |
| `Assets/CustomSRP/Runtime/CameraRenderer.Editor.cs` | Scene 视图 UI；Legacy Shader → Error 材质；Gizmos 分 Pre/Post FX |
| `Assets/CustomSRP/Runtime/Lighting.cs` | 可见 Directional（最多 4）+ Other → globals；调 Shadows |
| `Assets/CustomSRP/Runtime/Shadows.cs` | 级联阴影 atlas / Reserve / keywords |
| `Assets/CustomSRP/Runtime/ShadowSettings.cs` | 阴影配置（挂 Asset） |
| `Assets/CustomSRP/Runtime/PostFXSettings.cs` | Bloom 配置 + stack shader |
| `Assets/CustomSRP/Runtime/PostFXStack.cs` | 中间源 → Bloom 金字塔 → 相机 target |
| `Assets/CustomSRP/Shaders/PostFXStack.shader` | `Hidden/CustomSRP/Post FX Stack` |
| `Assets/CustomSRP/Shaders/Unlit.shader` | `CustomSRP/Unlit`，`LightMode = SRPDefaultUnlit` + ShadowCaster |
| `Assets/CustomSRP/Shaders/Lit.shader` | `CustomSRP/Lit`，`LightMode = CustomLit` + ShadowCaster |
| `Assets/CustomSRP/Shaders/TerrainLit.shader` | `CustomSRP/TerrainLit`，Terrain splat + `CustomLit`（对照） |
| `Assets/CustomSRP/Shaders/TerrainOilNPR.shader` | `CustomSRP/TerrainOilNPR`，Terrain splat + Oil NPR |
| `Assets/CustomSRP/Shaders/OilNPR.shader` | `CustomSRP/OilNPR`，油画 NPR + Outline + ShadowCaster |
| `Assets/CustomSRP/ShaderLibrary/` | Surface / Light / BRDF / Lighting / Shadows HLSL |
| `Assets/CustomSRP/Shaders/UnityInput.hlsl` | `UnityPerDraw` + 矩阵宏（须在 SpaceTransforms 之前）；`_ProjectionParams` |
| `Assets/CustomSRP/Editor/CustomSrpBootstrap.cs` | 菜单挂管线 + 确保 Post FX Settings |
| `Assets/CustomSRP/Editor/CreateEmptyScene.cs` | 菜单生成 `Empty`（无后处理 / LDR / 关 copy） |
| `Assets/CustomSRP/Editor/CreateTestScene.cs` | 菜单生成 `CustomRPTest` |
| `Assets/CustomSRP/Editor/CreateDrawCallsScene.cs` | 菜单生成 `DrawCalls` |
| `Assets/CustomSRP/Editor/CreateDirectionalLightsScene.cs` | 菜单生成 `DirectionalLights` |
| `Assets/CustomSRP/Editor/CreateDirectionalShadowsScene.cs` | 菜单生成 `DirectionalShadows` |
| `Assets/CustomSRP/Editor/CreateMonurikiTerrainScene.cs` | 菜单生成 `MonurikiTerrain`（Terrain + TerrainOilNPR） |

## 命名空间

- 运行时：`CustomSRP`
- Editor：`CustomSRP.Editor`

## 当前边界

| 已有 | 未做 / 待补 |
|------|-------------|
| Unlit Opaque / Transparent / Clip + ShadowCaster | Renderer Feature 风格插件化 |
| Lit（`CustomLit`）+ Directional + Other Lights + BRDF | Lit Meta Pass |
| TerrainOilNPR / TerrainLit（`CustomLit`）splat + ShadowCaster | Terrain normal / holes / 完整 AddPass |
| Directional CSM / PCF；Other 实时阴影 | `LIGHTMAP_ON` multi_compile |
| Post FX：Bloom / Color Grading+LUT / Tone Mapping / FXAA | shadow mask 运行时采样 |
| HDR 中间缓冲、Render Scale、Color/Depth copy | LOD Cross-Fade dither |
| 多相机 viewport / Rendering Layer | |
| Skybox、Editor Error Shader / Scene Overlay | |
| SRP Batcher / Instancing / Dynamic Batching | |
| Examples：MPB、`MeshBall` | |
