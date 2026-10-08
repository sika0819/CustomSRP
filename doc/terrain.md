# Monuriki Terrain

场景：`Assets/CustomSRP/Scenes/MonurikiTerrain.unity`  
菜单：`CustomSRP → Create Monuriki Terrain Scene (1:1)`  
高度图：`Assets/Terrain/Monuriki/`

## 验什么

Unity **Terrain** 走本管线 **Opaque GeometryPass**：`CustomSRP/TerrainOilNPR` 的 `LightMode = CustomLit`（与 Lit 相同过滤），**不**另开 Terrain Pass。油画：splat 上 control-UV Kuwahara + Canvas + 量化光照（无 Outline）。

## 接线

| 项 | 说明 |
|----|------|
| Shader | `CustomSRP/TerrainOilNPR` + `ShadowCaster`（无 Outline） |
| Dependencies | 复用 `Hidden/CustomSRP/TerrainLitAdd` / `Basemap` / `BasemapGen` |
| 材质 | `TerrainOilNPRMonuriki.mat` → `Terrain.materialTemplate` |
| 层 | Sand / Grass / Rock（程序化细节 splat + 高度/坡度 alphamap） |
| Alphamap | 1024；岸线 softstep + 噪声；陡坡/山脊偏 Rock；缓坡高地留 Grass 口袋 |
| 相机 / 光 | LDR、关 Post/copy/FXAA；平行光 **Hard** 阴影 |
| Basemap Distance | 4500。默认机位到岛对岸约 2 km；低于此值远处切 Basemap |
| 远处 Basemap | 与近处同一套油画阴影（冷/暖本影 + 笔刷边缘）。无 Kuwahara |
| 尺寸 | JSON `recommended_terrain_size_m`（约 1.3 km × 178 m） |

## 油画参数（地形）

| 参数 | 建议 |
|------|------|
| `_KuwaharaRadius` | Control UV ~0.004–0.006（全岛尺度下约数米色块） |
| `_CanvasMap` tiling | ~18（织纹密铺） |
| `_CanvasStrength` | 0.06–0.1 |
| Keyword | `_KUWAHARA_ON` / `_CANVAS_ON` / `_INTERNAL_EDGE_ON` |

移动端可关 `_KUWAHARA_ON`（每像素多次 splat 混合）。

材质必须是 `CustomSRP/TerrainOilNPR`，并挂上 `OilBrush`。停在 `TerrainLit` 时，接收阴影是普通 PBR 压暗，没有笔触本影。

## 限制（MVP）

- 仅前 4 层 splat；Add Pass 为空 stub
- 无 terrain normal map / holes；Canvas 用法线推 TBN
- 优先使用手绘/生成的油画无缝 `SplatSand/Grass/Rock.png`（重建**不覆盖**已有文件；缺失才写程序化 fallback）
- 旧 `CustomSRP/TerrainLit` 仍保留作对照，场景默认改用 Oil

## 相关

- [渲染循环](render-loop.md) — Opaque `CustomLit`
- [Shader 规范](shader-conventions.md) — TerrainLit
- [材质清单](materials.md)
