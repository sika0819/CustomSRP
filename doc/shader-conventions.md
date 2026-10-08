# Shader 规范

路径：`Assets/CustomSRP/Shaders/`；Lit 公共库：`Assets/CustomSRP/ShaderLibrary/`。

## Include 顺序（硬约束）

`UnityInput.hlsl` 必须：

1. 声明 `UnityPerDraw` CBUFFER 与全局矩阵变量
2. `#define UNITY_MATRIX_M` / `I_M` / `V` / `I_V` / `VP` / `P`
3. `#define UNITY_PREV_MATRIX_M` / `I_M`（仅为兼容 Core `SpaceTransforms`；本管线无运动矢量 / TAA）
4. **之后**再 include `UnityInstancing.hlsl` 与 `SpaceTransforms.hlsl`

- Unlit Pass：先 include `UnityInput.hlsl`（见 `UnlitPass.hlsl`）
- Lit Pass：先 include `ShaderLibrary/Common.hlsl`（再拉 `UnityInput` + Core `CommonMaterial`），再 `Surface` / `Light` / `BRDF` / `Lighting`（见 `LitPass.hlsl`）

## Unlit Pass

- Shader 名：`CustomSRP/Unlit`
- `LightMode`：`SRPDefaultUnlit`
- 属性：`_BaseMap`、`_BaseColor`、`_Cutoff`、`_Clipping`、混合与 `_ZWrite`
- 材质属性放在 `UNITY_INSTANCING_BUFFER(UnityPerMaterial)` 中
- `#pragma shader_feature _CLIPPING` + `#pragma multi_compile_instancing`
- Surface：Opaque / Clip（`AlphaTest`）/ Transparent（由 `CustomShaderGUI` Presets 与 `TestSceneUtility.ApplySurface` 统一设置）

## Lit Pass

- Shader 名：`CustomSRP/Lit`
- `LightMode`：`CustomLit`
- 额外属性：`_Metallic`、`_Smoothness`、`_PremulAlpha`（`_PREMULTIPLY_ALPHA`）
- 灯光常量由 `Lighting.cs` 注入（`_DirectionalLightCount` / Colors / Directions；两端上限 4）
- 预乘透明：仅 `brdf.diffuse *= alpha`，specular 不乘 alpha

## Oil NPR Pass

- Shader 名：`CustomSRP/OilNPR`（详见 [oil-npr.md](oil-npr.md)）
- Forward：`LightMode = CustomLit`；仅主方向光 + 硬阴影；材质属性在 `CBUFFER UnityPerMaterial`
- Outline：`Name = Outline`，`LightMode = SRPDefaultUnlit`，`Cull Front`
- LOD 300 含描边；LOD 150 无描边

## Terrain Lit Pass

- Shader 名：`CustomSRP/TerrainLit`（详见 [terrain.md](terrain.md)）
- Forward：`LightMode = CustomLit` → 现有 Opaque GeometryPass（**不**另开 Terrain Pass）
- ShadowCaster；splat：`_Control` + `_Splat0–3`（Terrain `materialTemplate` 注入）
- Dependencies：`Hidden/CustomSRP/TerrainLitAdd` / `Basemap` / `BasemapGen`
- 队列：`Geometry-100`（1900）

## 材质与 GUI

- Custom Editor：`CustomSRP.Editor.CustomShaderGUI`（Presets：Opaque / Clip / Fade / Transparent；Transparent 仅当存在 `_PremulAlpha`）
- 测试场景材质由菜单生成；自建材质同样只用 `CustomSRP/*`
- Terrain 材质用 `CustomSRP/TerrainLit`，挂 `Terrain.materialTemplate`
