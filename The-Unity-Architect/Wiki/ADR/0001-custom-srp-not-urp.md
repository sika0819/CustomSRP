# 自建 Custom SRP 油画 NPR，不用 URP/HDRP

**背景：** 要做移动端优先的油画风（量化光照、笔触本影、墨线描边），又不想背 URP Renderer Feature / 包体重量，也不想跟 URP 默认行为打架。

**决策：** 在 `com.unity.render-pipelines.core` 上自建 `CustomRenderPipeline` / `CameraRenderer`，配 OilNPR Shader 和 Render Graph。

**理由：** 完全控制 LightMode、合批、移动端关键字（`OIL_MOBILE_PERF`）、Outline LOD。上 URP 仍要写自定义 Shader，还得多一层 Feature 栈。代价：不能直接吃 URP 资源商店方案；Agent 禁止按 URP API 乱改。

**状态：** 已采纳
