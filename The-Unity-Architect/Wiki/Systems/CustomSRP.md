# 系统 — CustomSRP（油画 NPR 管线）

**路径：** `Assets/CustomSRP/` · **命名空间：** `CustomSRP`（+ `CustomSRP.Editor`）  
**详解：** [`doc/architecture.md`](../../../doc/architecture.md) · skill `custom-srp`

## 职责

基于 Unity SRP Core / Render Graph 的移动端油画 NPR 渲染管线：

```
CustomRenderPipelineAsset → CustomRenderPipeline → CameraRenderer
  裁剪 → 光照/阴影 → 几何（OilNPR / TerrainOilNPR / Outline）→ 后处理
```

## 边界

- GameMain **不能**为了玩法去编译引用具体的 CustomSRP 类型。
- 视觉驱动跨边界走 `Game.IAtmosphereVisualDriver`（实现：`OilAtmosphereDriver`），草地 / 相机 / 扭曲等少量走反射桥。
- `OIL_MOBILE_PERF` 等关键字由 GameEntry 的移动端画质策略设置。

## 关键场景 / 菜单

- 验收场景：`Assets/CustomSRP/Scenes/` 下的 Empty、MooreaTerrain
- Editor 菜单：**CustomSRP → Create & Assign Pipeline Asset**，以及 Create Empty / Moorea Terrain

## 入库备注

- 约 80 个 Runtime + 47 个 Editor 脚本 —— 一等公民里密度仅次于 GameMain Services。
- `CustomRPTest.unity` 带有缺失的 uGUI 脚本 GUID；当遗留测试场景，不是正式入口。
