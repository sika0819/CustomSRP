# CustomRPTest 场景

路径：`Assets/CustomSRP/Scenes/CustomRPTest.unity`  
生成脚本：`Assets/CustomSRP/Editor/CreateTestScene.cs`

## 场景是干什么的

**开篇管线验证场景**：确认 Custom SRP 已挂载，并且 Opaque / Transparent / Skybox / 多相机 / Overlay UI / 不支持 Shader 这几条路径都能跑通。

它**不是**性能测试场（那是 `DrawCalls`），也**不**演示 Lit、阴影或后处理。目标只有一件事：一眼看出管线是否按预期工作。

| 你期望看到 | 对应能力 |
|------------|----------|
| 绿/黄实心 Cube | Unlit 不透明绘制正常 |
| 白半透 Sphere | Unlit 透明混合 + UVAlpha 贴图 |
| 红 Cube / 蓝 Sphere 呈品红 | Editor 下 Legacy Standard → Error Shader（故意对照） |
| 右上角小视口 | Secondary Camera（Depth 清除、viewport rect） |
| 左上角「CustomSRP」按钮 | Screen Space Overlay UI 在 Game / Scene 可见 |

## 怎么打开 / 重建

1. 先挂管线：菜单 **CustomSRP → Create & Assign Pipeline Asset**（Graphics 已挂可跳过）。
2. 打开已有场景：Project 里双击 `Assets/CustomSRP/Scenes/CustomRPTest.unity`。
3. 若场景或材质缺失：菜单 **CustomSRP → Create Test Scene**  
   - 会重建材质、`UVAlpha.png`、场景本身  
   - 写入 Build Settings（并尽量放到第一位）  
   - 自动打开该场景  
4. 场景文件不存在时，Editor 启动也会尝试自动创建一次（`CreateTestScene.AutoCreateIfMissing`）。

进入 **Play Mode**，或打开 **Window → Analysis → Frame Debugger** 抓一帧即可验收。

## 场景里有什么

### 相机与灯光

| 对象 | 要点 |
|------|------|
| Main Camera | Tag `MainCamera`，Clear = Skybox，`depth = -1`，位置约 `(0, 2.2, -8)` |
| Secondary Camera | Clear = Depth，`depth = 0`，右上角 `rect ≈ (0.65, 0.65, 0.33, 0.33)` |
| Directional Light | 默认方向光（当前 Unlit 不读光照，仅作场景常规配置） |
| Cube_SecondaryMarker | 黄 Unlit Cube，方便辨认副相机附近物体 |

### 物体与材质对照

材质在 `Assets/CustomSRP/Materials/`，由菜单一并生成。

| 物体（命名前缀） | 材质 | Shader | 期望观感 |
|------------------|------|--------|----------|
| `Cube_Green_*` | `UnlitGreen` | `CustomSRP/Unlit` Opaque | 绿色实心 |
| `Cube_Yellow_*` | `UnlitYellow` | `CustomSRP/Unlit` Opaque | 黄色实心 |
| `Sphere_White_*` | `UnlitWhiteTransparent` | `CustomSRP/Unlit` Transparent + `UVAlpha` | 白色半透、边缘有 alpha |
| `Cube_Red_*` | `UnsupportedOpaque` | Built-in **Standard** | Editor 下**品红** Error |
| `Sphere_Blue_*` | `UnsupportedTransparent` | Built-in **Standard** 透明 | Editor 下**品红** Error |

红/蓝不是 bug：本管线不实现 Built-in Standard；Editor 里会用 `Hidden/InternalErrorShader` 画出来，用来对照「支持 vs 不支持」。

### Overlay UI

- `Canvas`：`Screen Space Overlay`
- 左上角 Button，文案 `CustomSRP`
- `EventSystem` 一并生成

若 Scene 视图看不到 UI，检查 `CameraRenderer.Editor.cs` 是否调用了 `EmitWorldGeometryForSceneView`（见 [排查](troubleshooting.md)）。

## 建议怎么用

### 第一次验收（约 1 分钟）

1. 确认 Project Settings → Graphics 的 Default Render Pipeline 是 Custom Asset。  
2. 打开本场景，Game 视图应同时有：绿/黄 Opaque、白 Transparent、红/蓝品红、右上角副视口、左上角按钮。  
3. Frame Debugger 中顺序大致为：**Opaque → Skybox → Transparent**；Error 绘制单独一段（仅 Editor）。

### 改管线时的对照基准

改 `CameraRenderer` 绘制顺序或 Shader Tag 后，先回本场景看：

- 绿/黄是否仍在透明球「后面」被正确遮挡/混合  
- 白球是否仍走 Transparent Pass  
- 红/蓝是否仍走 Unsupported（Editor）  
- 副相机与 Overlay 是否还在

### 自建物体时注意

- 新材质只用 **`CustomSRP/Unlit`**（或日后实现的 `CustomLit`）。  
- 不要把红/蓝改成「修好」成正常颜色——它们是 Error 对照样。  
- 需要批处理 / Instancing / Alpha Clip 演示时，改用 **DrawCalls** 场景，不要往本场景堆球体。

## 相关文档

- [快速开始](getting-started.md) — 挂管线与两个测试场景入口  
- [DrawCalls 场景](draw-calls.md) — 批处理 / Instancing / Clip  
- [渲染循环](render-loop.md) — Opaque / Skybox / Transparent 固定顺序  
- [排查](troubleshooting.md) — 品红、UI、矩阵宏等常见问题  
- 根目录 [README](../README.md) — CustomRPTest / DrawCalls 一句话对照  
