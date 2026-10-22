# 真机雨视觉：Editor 有 / 真机无

模块文档索引见 [00-overview.md](00-overview.md)。已知坑总表见 [10-bug-index.md](10-bug-index.md)。

> 2026-10-21 核实。视觉实现在 `Assets/CustomSRP/Runtime/Rain/`，强度由 Game 侧天气经 `OilAtmosphereDriver` 写入。

## 1. 症状

| 现象 | 是否 bug |
|------|----------|
| 真机天气已是「雨」档位 ≥ 1，仍看不到雨丝 | **是**（见 §3） |
| 真机完全没有云 | **否**——`IslandMobileTune.TuneCloudsForMobile` 故意 `Disable OilCloudLayer` |
| 只有雨丝、没有水面涟漪 / splash / 打湿 | **否**——`OilRainSettings` 上这些开关可按性能关掉；当前约定只留雨丝 |
| 切预设虹膜过程中短暂无雨 | **否**——`PresetWeatherHold` 强制 `intensity = 0`，`Release` 后恢复 |
| 默认进岛（Cloud）无雨 | **否**——只有 `IslandWeatherKind.Rain` 才写非零强度 |

## 2. 视觉链路（谁画画）

```
WeatherService.SetWeather(Rain, level)
  → IAtmosphereVisualDriver.SetWeather
  → OilAtmosphereDriver.SetRainIntensity
       intensity = L1/L2/L3 → 0.4 / 0.7 / 1.0（Hold 时强制 0）
       写入 OilRainSettings.Current
  → OilRainStreakQuad.LateUpdate 读 Current.intensity
       → 相机子节点 ~OilRainCurtain（OilRainCurtain.mat / Shader）
```

当前**唯一**可见雨路径：`useSingleQuadStreaks` + `OilRainStreakQuad`。

| 开关（`OilRainSettings.asset`） | 作用 | 约定 |
|---|---|---|
| `useSingleQuadStreaks` | 相机雨丝帘 | **开** |
| `streakMaterial` | 预烘焙帘材质（必填） | `Materials/OilRainCurtain.mat` |
| `rippleFlipbookEnabled` | 水面涟漪法线 | 可关 |
| `splashFlipbookEnabled` | 落点 splash | 可关 |
| `wetStrength` | NPR 打湿 | `0` = 关 |
| `postRippleEnabled` | 全屏屏空涟漪 Pass | **关**（要 Copy Depth，移动端不可靠） |

## 3. 已核实根因与修复

### 3.1 Runtime `Shader.Find` 真机失败（与 Preset Iris 同类）

- **根因**：帘子材质在运行时 `new Material(Shader.Find("CustomSRP/OilRainStreakQuad"))`。场景无材质引用时，Player 可能 strip shader；Find 返回 null → 组件 `enabled = false`（日志：`Shader ... not found`）。
- **Always Included 不够**：`GraphicsSettings.m_AlwaysIncludedShaders` 已收 `OilRainStreakQuad`，仍可能漏进某次包；Boluo 参考工程用的是 **序列化 `RainMaterial`**。
- **修复**：
  - 新增 `Assets/CustomSRP/Materials/OilRainCurtain.mat`
  - `OilRainSettings.streakMaterial` 引用该材质
  - `OilRainStreakQuad` **优先**用 `streakMaterial` 实例化，Find 仅回退
  - Always Included 保留作双保险

### 3.1b SubShader `RenderPipeline` 标签被整包剥离 → 雨丝洋红/粉色

- **症状**：委屈泛滥等雨预设，雨帘整片粉/洋红（`Hidden/InternalErrorShader`），不是「没雨」。
- **根因**：`OilRainStreakQuad` / `OilRainSplash` 写了 `"RenderPipeline" = "CustomSRP"`，但 `CustomRenderPipelineAsset` **没有**对应 pipeline tag（空）。Player 构建日志：`All SubShaders were stripped because their RenderPipeline tags do not match...`。
- **修复**：删掉该 Tag（与 `Unlit` / `OilNPR` / `NoiseFire` 一致）。**禁止**再给 CustomSRP 雨/后处理 Shader 加 `RenderPipeline=CustomSRP`。

### 3.2 YooAsset 复制出第二份 `OilRainSettings`

- **根因**：管线 `CustomRenderPipelineAsset.rainSettings` 与 `IslandTerrain` prefab 依赖各加载一份 SO。天气只写 `OilRainSettings.Current`（管线那份）；雨丝若读 prefab 序列化 `_settings`，强度与天气脱节（常见：Current 已有雨强，帘子读到的仍是 0）。
- **修复**：`OilRainStreakQuad` 强度与 settings 解析一律优先 `OilRainSettings.Current`。

### 3.3 强度档位过淡

- **根因**：曾用 `intensity = level / 3f`（L1 ≈ 0.33），叠加移动端 `ResolveQualityScale` 降密度后几乎看不见。
- **修复**：L1/L2/L3 → `0.4 / 0.7 / 1.0`。

### 3.4 相机解析

- **补充**：真机加载期 `Camera.main` 可能滞后；帘子优先挂 `IslandJoystickCamera.Current` 的 Camera。

## 4. 排障顺序（真机没下雨）

1. 天气是否 **Rain** 且 level ≥ 1？（听雨声 ≠ 有雨丝；音频走另一条桥）
2. 是否卡在预设虹膜（`PresetWeatherHold.IsHeld`）？
3. `OilRainSettings.streakMaterial` 是否赋值？包内是否含 `OilRainCurtain.mat`？
4. 设备日志有无 `[OilRainStreakQuad] ... failed` / `Shader ... not found`？
5. Hierarchy 相机下有无 `~OilRainCurtain`？Renderer 是否 enabled？
6. **重打 YooAsset + 重装 APK/IPA**（`streakMaterial` 必须作为 `OilRainSettings` 依赖进包；EditorSimulate ≠ Offline 包体）

## 5. 改动时禁止

- ❌ 为「增强雨感」默认打开 `postRippleEnabled`（移动端 Copy Depth 关了等于白开）。
- ❌ 在 `IslandMobileTune` 里静默关掉雨组件却不更新本文档（云可以关，雨当前没有对等关闭）。
- ❌ 只改 Launch 场景、不改 `AssetRaw/Island/IslandTerrain.prefab`、不重打 Yoo 包。
- ❌ 新增 Runtime 雨/后处理 Shader 却不挂材质引用、也不进 Always Included（会再踩 Preset Iris / 雨丝同类坑）。
- ❌ 给 CustomSRP Shader 写 `"RenderPipeline" = "CustomSRP"`（管线无 tag → 整包 strip → 洋红）。

## 6. 关键文件

| 文件 | 角色 |
|---|---|
| `CustomSRP/Runtime/Rain/OilRainStreakQuad.cs` | 雨丝帘 |
| `CustomSRP/Runtime/Rain/OilRainSettings.cs` + `.asset` | 开关与 `streakMaterial` |
| `CustomSRP/Materials/OilRainCurtain.mat` | 预烘焙帘材质 |
| `CustomSRP/Runtime/OilAtmosphereDriver.cs` | 天气 → intensity |
| `GameMain/Services/WeatherService.cs` | 天气状态 |
| `GameMain/Services/PresetWeatherHold.cs` | 虹膜期间抑雨 |
| `GameMain/Services/IslandMobileTune.cs` | **只关云**，无关雨 |
| `ProjectSettings/GraphicsSettings.asset` | Always Included shaders |
| `AssetRaw/Island/IslandTerrain.prefab` → `OilRain` | 运行时雨根节点 |

## 7. 与「真机没云」的区分

| | 云 | 雨丝 |
|---|---|---|
| 真机无画面 | **设计**：`TuneCloudsForMobile` Disable | **缺陷或配置**：见 §3 |
| 恢复方式 | 改/去掉 MobileTune（产品决策） | 查材质 / Current / 重打包 |
