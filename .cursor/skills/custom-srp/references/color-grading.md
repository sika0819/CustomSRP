# Color Grading

调色（color grading）与 tone mapping（tonemap）、LUT。字段 / Pass / 资产名以**当前项目命名 / 源码为准**。

接线状态见主 Skill §6（**先识别入口**，再分层 grep；**勿断言**某仓已接或未接）。

## 1. 先识别入口

| 线索 | 可能入口 |
|------|----------|
| Volume Color Grading / Color Adjustments / Tonemapping / Lift Gamma Gain | URP / HDRP 后处理 |
| PostProcessLayer / PostProcessVolume / ColorGrading（旧栈） | 内置 Post Processing Stack |
| Settings（曝光/白平衡/LUT）+ Stack / CameraRenderer / Renderer Feature | 自建 / Custom SRP |
| 无 Volume、无后处理包、无 Settings/Stack 调色字段 | **该项目无调色入口（正常，非漏识别）** |

未识别前**不要**假设存在某套固定类名或 LUT 纹理符号。

**跨项目第一步是上表识别，不是 grep 固定符号名。**

## 2. 怎么用（通用）

1. 找到项目的后处理 / Volume / Settings 资产（若 §1 判定无入口则停止，勿强搜层 2 符号）。
2. Game 或 Scene 相机验；排除 Preview / Reflection（若管线有过滤）。
3. Frame Debugger / Render Graph Viewer：看 grading / tonemap 相关 Pass；**若**项目用 LUT，再确认烘焙与最终采样两段。
4. 调一项可见参数（曝光 / 对比 / 饱和）或切换 tonemap 模式 → 确认生效。

## 3. 通用概念（非单一实现）

- **顺序**：color grading（校正+风格）→ **tone mapping（tonemap）**（HDR→显示域）；二者可合并进一次 LUT，也可分 Pass——以项目为准
- **常见工具**（名以项目为准）：Post Exposure、Contrast、Color Filter、Hue/Saturation；White Balance；Split Toning；Channel Mixer；Shadows Midtones Highlights；tone mapping（None / Neutral / ACES / Reinhard 等）
- **常省略**：Color Curves、Lift Gamma Gain（依赖自定义编辑器）——以项目是否提供为准
- **色彩空间**：部分管线在特定色彩空间做对比度后再 tonemap，以项目为准
- **LUT（可选路径）**：用低分辨率 3D（或 2D strip 模拟）烘焙 grading+tonemap，全屏只采样；也有项目逐像素做 grading、不建 LUT。分辨率↑减 banding、↑成本；HDR 输入常经 Log C 扩展可表示范围——**以项目实现为准**
- **banding**：LUT 分辨率不足 + 强 HDR 渐变时更明显；升分辨率或靠画面噪声掩盖；勿与 8-bit 帧缓冲 banding 混淆

grading 通常在 Bloom 等散射效果**之后**、显示前（若项目有 Bloom）。Bloom 细节属后处理链其它主题，不归本文展开。

勿把某一实现的 LUT 形状、重建频率、Log C 开关写成跨项目规范；实现细节查**当前工程文档或源码**（不假定必有 `doc/` 目录）。

## 4. 通用原则与项目适配

标题用「通用原则与项目适配」，不用「必须一律」。

| 通用原则 | 适配说明 |
|----------|----------|
| grading 改已合成色 | 常需中间色缓冲；是否独立 RT / Render Graph handle 以项目为准 |
| LUT 被后续全屏 Pass 采样 | **若**存在该 LUT RT，则需 Store；用完应释放——见 [mobile-perf.md](mobile-perf.md)（条件化） |
| tone mapping 与 grading 同 LUT 或分 Pass | 以项目为准（例如部分 URP LDR 路径可能分开） |
| 配置挂 Asset / Volume / Profile | 以项目入口为准 |

概念数据流（LUT 路径；非唯一）：

```mermaid
flowchart LR
  Identify["Identify entry"] --> Config["Volume or Settings"]
  Config --> SceneColor["Scene or postFX color"]
  SceneColor --> Grade["Grading plus tonemap"]
  Grade --> Present["Display"]
```

若项目烘焙 LUT，可在 Grade 与 Present 之间插入「Bake LUT → Sample LUT」；细图与类名只查当前工程文档/源码，不写死为本 reference 规范。

## 5. 排障（通用）

| 现象 | 查 |
|------|-----|
| 调参无变化 | 入口启用？相机过滤？Volume 层/碰撞？Settings 是否挂上？无入口则属预期 |
| 过曝 / 过灰 | Post Exposure / Contrast / tone mapping 模式 |
| 色偏诡异 | White Balance / Filter / Channel Mixer / Split Toning |
| banding 条带 | LUT 分辨率（若用 LUT）；强 HDR 渐变；是否关掉插值做对比 |
| 无 LUT Pass | 可能未用 LUT、或未接线、或逐像素 grading；先识别入口再 grep |
| URP/HDRP 无效果 | Camera/Renderer 是否开 Post Processing；Volume Mode/Layer/Mask |

## 6. 分层 rg

```bash
# 层 1：通用（跨项目先跑；大小写不确定时可加 -i）
rg -n "ColorGrading|Color Adjustments|ToneMapping|Tonemapping|Tonemap|WhiteBalance|SplitToning|ChannelMixer|ColorLookup|ColorCurves|LiftGammaGain|PostProcessVolume|PostProcessLayer|LUT|Lut|lut"

# 层 2：仅当项目像本管线风格时再查（候选，非跨项目必存在）
rg -n "DoColorGradingAndToneMapping|ApplyLut2D|GetLutStripValue|_ColorGradingLUT|ColorGrade|_ColorAdjustments|colorLUTResolution|PostFXStack"
```

- 层 2 未命中 ≠ 无调色（常见于 URP/HDRP Volume）。
- 层 2 符号为 Custom SRP / **候选**；命中也不等于「必须按某一固定架构实现」。

## 7. 验收 checklist（通用）

- [ ] 已识别入口类型（含「无入口」），未假设固定类名
- [ ] grading / tonemap / LUT 用通用概念描述；具体算法以项目为准
- [ ] 分层 rg：先层 1，仅在像 本管线风格时才层 2

