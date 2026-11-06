# 项目语境 — 菠萝 / CustomSRP

**状态：** 在运产品代码（不是从零脚手架）  
**引擎：** Unity 6（`6000.x`）  
**平台：** 移动端优先（Android / iOS），Editor 可在 macOS 联调

## 这是什么

**莫雷阿岛**体验向产品：第一人称在岛上走动，油画风格画面，带天气 / 昼夜预设、空间环境音、BGM 歌单、篝火与环境道具，以及原生宿主壳（登录、遮罩层、健康 / 计时桥）。

视觉是 **油画 NPR**（量化明暗、笔触本影、墨线描边），用自建 **Custom Scriptable Render Pipeline** 实现 —— **不是 URP**。

## 产品词汇（种子术语表）

| 术语 | 在本仓库里的含义 |
|------|------------------|
| Launch | 构建场景 0；Procedure 链从这里开始 |
| 进岛 / Island | `ProcedureIsland`：游客登录后，世界 + 相机 + HUD 起来 |
| Preset（预设） | 命名氛围落点（时段 + 天气 + 出生点），经 `PresetService` |
| Atmosphere（氛围） | 天空 / 雾 / 光照，经 `IAtmosphereVisualDriver` → `OilAtmosphereDriver` |
| Bank | Wanos 音频包（下载 / 加载 / 引用计数） |
| Ambience zone（环境音区） | 触发或靠近后 Post / Stop 环境事件的区域 |
| Uncover | 加载遮罩结束；音频设备打开，环境音才可以开始 |
| GameFormLogic | UI Toolkit 表单基类，挂在 GF `UIFormLogic` 下 |
| EntityId | Unity 6 对象身份（`GetEntityId()`），不是 InstanceID |

## 给 Agent 的软约束

- 实现细节以 `Assets/GameMain/Docs/` 和 `.cursor/skills/*` 为准。
- Feature Pipeline 的 Grill / GDD 只用于 **全新大系统**；已定 ADR 不要无故推翻。
- 原生桥、Bank 下载、静音恢复策略是高回归区 —— 改之前先读 `05-audio.md` / `09-boluoapi-native.md` / `13-bank-download.md`。
