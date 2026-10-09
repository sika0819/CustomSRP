# Real-Island Terrain (1:1)

## Moorea

场景：`Assets/CustomSRP/Scenes/MooreaTerrain.unity`  
菜单：**CustomSRP → Create Moorea Terrain Scene (1:1)**  
高度图与参考：`Assets/Terrain/Moorea/`  
TerrainData（二进制）：`MooreaTerrainData.asset` — **不是场景**；打开 `MooreaTerrain.unity`

| 数据 | 说明 |
|------|------|
| DEM | NASA SRTM 1″，瓦片 `S18W150`，crop **16500 m × 16500 m**（约 **8 m/px**） |
| 垂直 | 0–**~1185 m**（Mont Tohiea 一带） |
| OSM 参考 | `moorea_osm_reference.png`（红=道路、蓝=水道、青=瀑布） |
| 矢量 | `moorea_osm_features.geojson`（QGIS / 外置对齐） |
| 土地覆盖 | `moorea_landuse.png`（ESA WorldCover 2021，10 m，与高度图同一网格） |
| 类别索引 | `moorea_landuse_index.png`（像元值 = 类别码，0 = 无数据） |
| 重生高度图 | `python3 Assets/Terrain/Moorea/fetch_moorea_heightmap.py` → 菜单 **Apply Moorea Heightmap**（或整场 **Create Moorea Terrain Scene (1:1)**） |
| 岸线 | 浪蚀造型：去细刺/离岛 → 平海底（低于 Ocean ≈0.35 m）→ 扇贝状 soft beach + 冲刷沟；alphamap 湿沙带刷洗痕迹 |
| 重生土地覆盖 | `python3 Assets/Terrain/Moorea/fetch_moorea_landuse.py` |
| 油画 splat | `SplatForest.png` / `SplatBuilt.png`（`make_oil_splats.py`）；沙/草沿用已有贴图 |
| 重刷 alphamap | 菜单 **CustomSRP → Repaint Moorea Land Cover** |

crop 覆盖全岛 + 潟湖边（陆地约 60%），OSM 约 **5700+ 道路**、**700+ 水道**、**25 瀑布**。画 splat 道路时把 `moorea_osm_reference.png` 叠在 `moorea_preview.png` 上对齐。

土地覆盖与高度图同一范围、北在上：绿=林地、黄=草地、红=建成区、品红=耕地、青=红树林/湿地、蓝=水体。图例与像元计数在 `moorea_landuse.json`。潟湖浅滩在高度图上可能仍算陆地，在覆盖图上是水体。

## 验什么

Unity **Terrain** 走本管线 **Opaque GeometryPass**：`CustomSRP/TerrainOilNPR` 的 `LightMode = CustomLit`（与 Lit 相同过滤），**不**另开 Terrain Pass。油画：splat 上 control-UV Kuwahara + Canvas + 量化光照（无 Outline）。

## 接线

| 项 | 说明 |
|----|------|
| Shader | `CustomSRP/TerrainOilNPR` + `ShadowCaster`（无 Outline） |
| Dependencies | 复用 `Hidden/CustomSRP/TerrainLitAdd` / `Basemap` / `BasemapGen` |
| 材质 | `TerrainOilNPRMoorea.mat` → `Terrain.materialTemplate` |
| 层 | Sand / Grass / Forest / Built（前 4 层） |
| 山脊 | 多尺度 TPI（比邻域更高的凸脊）× 坡度，沟谷和贴海像素排除；岩石写入 alphamap 第 5 层，只从草地/林地扣权重。主 Pass 用 control 残差混 `SplatRock` |
| Alphamap | 1024；按 `moorea_landuse_index.png` 双线性写入。林地+红树林→Forest，草地/灌木/耕地/湿地→Grass，建成区→Built，裸地/水体→Sand |
| 相机 / 光 | LDR、关 Post、开 copyDepth、关 FXAA；平行光 **Hard** 阴影 |
| 天空 | `OilSkybox`（`OilSkyboxTime.timeOfDay` 0–24h，同步天空 / 环境光 / 平行光） |
| Heightmap Pixel Error | 2 |
| Heightmap Min LOD Simplification | **2** |
| Draw Instanced | 开（`TerrainInstancing.hlsl` 采 heightmap；无此路径会整块不画） |
| Internal Edge | 关 |
| Basemap Distance | **≥ SizeX×1.25** |
| Heightmap LOD Frustum Cull | **关**（大岛远景机会误剔光整块） |
| 尺寸 | JSON `recommended_terrain_size_m` |

## 油画参数（地形）

| 参数 | 建议 |
|------|------|
| `_KuwaharaRadius` | ~**0.00055**（16.5 km 尺度） |
| `_CanvasMap` tiling | ~18 |
| `_CanvasStrength` | 0.06–0.1 |
| `_PaintMap` | `Textures/Skybox/OilCanvas.png`，世界空间厚涂 |
| `_PaintTile` | ~**1600 m**（远景笔触；splat 的几十米瓷砖在景区相机会被滤平） |
| `_PaintRelief` | **1**（0.5 亮度不变，亮笔偏暖、暗笔偏冷） |

## 贴图无缝验收

Splat / OilCanvas / OilBrush 必须 **Repeat**，且左右、上下边缘像素差 ≈ 0。

## 限制（MVP）

- 沙/草/林/建成占前 4 层；第 5 层是山脊岩石。主 Pass 读 `1 - sum(control)`，Add Pass 不着色
- 无 terrain normal map / holes
- OSM 仅作 **2D 参考**，不会自动写入 alphamap
- 土地覆盖会写入 alphamap（菜单 **Repaint Moorea Land Cover**），并在草/林上叠山脊岩石层
- 旧 `CustomSRP/TerrainLit` 仍保留作对照

## 相关

- [渲染循环](render-loop.md)
- [Shader 规范](shader-conventions.md)
- [材质清单](materials.md)
