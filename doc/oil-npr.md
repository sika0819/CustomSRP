# Oil NPR（油画风格）

路径：[`Assets/CustomSRP/Shaders/OilNPR.shader`](../Assets/CustomSRP/Shaders/OilNPR.shader)  
验证场景：[`Empty.unity`](../Assets/CustomSRP/Scenes/Empty.unity)（菜单 **CustomSRP → Create Empty Scene**）  
地形：[`TerrainOilNPR.shader`](../Assets/CustomSRP/Shaders/TerrainOilNPR.shader) → [`MooreaTerrain.unity`](../Assets/CustomSRP/Scenes/MooreaTerrain.unity)

逐物体 NPR：无全屏后处理、无 Renderer Feature。主 Pass `LightMode=CustomLit`；描边 Pass `Name=Outline` / `LightMode=OilOutline`（Geometry 先画 OilOutline 再 CustomLit）。叶卡 `_Clipping>0` 或 `_Outline=0` 时片元硬丢弃，不依赖 keyword 变体。地形版无 Outline。

## 材质参数

| 参数 | 作用 |
|------|------|
| `_BaseMap` / `_BaseColor` | 底色；建议用 `OilAlbedo` 大色块贴图 + `_BaseColor` 染色 |
| `_CanvasMap` | 打包：RG 切线法线、B 厚度；缩放宜 **&lt;1**（织纹更大、更稀） |
| `_CanvasStrength` | 画布法线（宜 0.08–0.12，过大会发脏） |
| `_PaintThickness` | `(1-N·V) × B` 边缘亮部（宜偏低） |
| `_ShadeSteps` / `_ShadeLift` | 量化阶数；最暗档抬升（防死黑） |
| `_ShadowTint` / `_ShadowWarm` | 本影冷/暖两层颜料（笔刷在二者间切换） |
| `_ShadowLift` | 本影颜料强度（宜 ≥0.9，防死黑） |
| `_ShadowWobble` | 笔刷扰动阴影边缘（softstep，非硬切） |
| `_ShadowBrushScale` | 阴影内笔触密度（复用 Outline Brush 贴图） |
| `_SpecularColor` / `_SpecularThreshold` | 弱线性高光 |
| `_AmbientColor` | 纯色环境（宜偏亮暖，~0.5） |
| `_EdgeStrength` / `_EdgeColor` | 内部边（宜弱、偏色） |
| `_OutlineWidth` / `_OutlineColor` | 外扩描边底宽 / 墨色 |
| `_OutlineBrushMap` / `_OutlineBrushScale` | 笔触遮罩（断续墨线） |
| `_OutlineNoise` / `_OutlineNoiseScale` | 线宽起伏（提按） |
| `_OutlineWobble` | 沿切向侧向抖动（书法感） |
| `_OutlineBreak` | 笔触断裂阈值（越高越碎） |
| `_ReceiveShadows` / `_Shadows` | 接收/投射阴影 |

Keyword：`_CLIPPING`、`_RECEIVE_SHADOWS`（`multi_compile_local`）。描边用 Pass 开关 / LOD，不全编译进 Forward。Outline Pass 同样吃 `_CLIPPING`（与 Forward 同 alpha）。

**叶片策略（Validate / `GameMain/Boluo/Fix Foliage Oil Ink`）**：`shu_*` / Billboard / Contour / 灌木等叶卡 → `_BaseMap` 从 `_MainTex` 补齐、开 `_CLIPPING`、`Cull Back`（片元再丢背面）、关 Outline / Internal Edge / ReceiveShadows；Outline Pass 在 `_CLIPPING` 下 **硬 `clip(-1)`**（防 bundle/重导又打开 Pass 时背后外扩碎墨）；阴影用 `_SHADOWS_CLIP`（勿用不透明整卡投影）。关掉 `LOD_Billboard` / `LOD_Contour`，LOD 末档改挂 LOD2/LOD1。实心岩/树干保留描边。

## LOD

| LOD | 内容 |
|-----|------|
| 300（默认） | Forward + Outline + ShadowCaster |
| 150 | Forward + ShadowCaster（无描边） |

也可材质关掉 Outline：`_Outline=0` 或 `SetShaderPassEnabled("Outline", false)`。

## Empty 场景怎么验

1. 挂管线后执行 **CustomSRP → Create Empty Scene**（或依赖 `.force-rebuild-Empty`）。
2. 相机：LDR、关 Color/Depth copy、override Post FX 为空、关 FXAA。
3. 平行光：**Hard** 阴影；地面 + Sphere / Cube / Capsule 使用 `Oil*` 材质。
4. Play：色块 Albedo、4 档明暗、硬影、外轮廓、折痕内边。
5. Frame Debugger：无 Bloom/FXAA/Copy；OilNPR 应走 **SRP Batcher**。

## Asset / 相机建议（本管线）

- Pipeline Asset：`useSRPBatcher = true`（本仓默认已开）。
- 验证相机：关 HDR、关 Post、关 copy（Empty 已配）。
- 不需要 Renderer Feature。
- 附加光：Shader 不循环 Other Lights；场景勿依赖点/聚光做主造型光。

## 性能注意

- 片元尽量 `half`；世界位置 / 矩阵仍 `float`。
- Outline 多一趟不透明 Draw；小物体 / LOD 150 / 关 Outline Pass。
- Canvas 单贴图打包，少一次采样绑定。
- 移动端构建：[`OilNPRShaderStripper`](../Assets/CustomSRP/Editor/OilNPRShaderStripper.cs) 剥离 `INSTANCING_ON`（优先 Batcher）。
- 勿开 Soft Shadow keyword 档（本 Shader 不 pragma medium/high PCF）。

## 相关材质

| 材质 | 用途 |
|------|------|
| `OilOchre` / `OilTeal` / `OilClay` | 主物体，Outline 开 |
| `OilGround` | 地面，Outline 关 |
| 贴图 `OilAlbedo.png` | 物体笔触底图 |
| 贴图 `OilGround.png` | 地面油画笔触（大地色/橄榄绿） |
| 贴图 `OilCanvas.png` | **线性**打包：RG 法线、B 厚度（预览紫灰正常） |
| 贴图 `OilCanvas_Weave.png` | 织纹参考（sRGB） |
| `LitMooreaOcean` / `OilOceanNPR` | 岛周海面。`OilOcean.png` 只提供笔触遮罩，水色是 `_DeepColor` / `_MidColor` 海蓝；近岸用浅蓝，浪花跟亮笔。倒影读四张静态立方体（黎明/白天/黄昏/夜晚），按 `_OilSkyPeriod` 混合，`_ReflectStrength` 浅浅盖在笔触上 |
| `OilRiverNPR` / `Stylized River*.mat` | 岛内湖与河。对齐 Boluo `FlowUvMode=MeshUV`：流向走 `_MeshFlowScale` 网格 UV（勿用世界 XZ）；`OilRiverPaint.png` 莫奈横向笔触、`OilRiverGlint.png` 阳光碎笔；岸缘 = Depth 交线 + `BankUv`（无 FoamMap）；`_OIL_NO_VIOLET_UMBRA`。倒影与海面共用四张时段贴图 |
| `OilWaterfallNPR` / `Stylized Waterfall.mat` | 瀑布竖帘。对齐 Boluo `_FallSheet`：mesh UV；`OilWaterfallPaint.png` 竖向油画帘、`OilWaterfallMist.png` 底雾（无 FoamMap）；`MeshFlowScale≈(50.9,-3.91)`；Cull Off |
| `OilSkybox` / `OilSkyboxNPR` | Boluo 架构（方向笔触 + 日/月/星贴图）；`OilSkyboxTime.timeOfDay` 为 0–24 小时（06 黎明 / 12 白天 / 18 黄昏 / 00 夜晚），换算为 `_Period` 后在 Shader 内连续插值；编辑器拖时间会同步天空、环境光与平行光 |
| Pipeline | 全局 `copyDepth=1`、`renderScale=1`（笔触不被 0.75 缩放糊掉）。`copyColor` 保持关闭。海/河倒影走反射探针：菜单 **CustomSRP → Bake Water Reflection** 运行时全岛 **Realtime ReflectionProbe（32）**：只在 `OilSkyboxTime.animate` 或 `WeatherService` 时段过渡时每帧刷新，平时冻结；`cullingMask` 剔除 Water 层。编辑器菜单 **CustomSRP → Bake Water Reflection** 仍可烘焙 4 时段静态贴图作备份。Empty 相机仍关 copyDepth |
