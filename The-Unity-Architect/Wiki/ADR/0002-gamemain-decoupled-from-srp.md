# GameMain 与 CustomSRP 编译解耦

**背景：** 玩法与渲染管线节奏不同；`Game` 程序集要能独立编译、测试，不能被 SRP 内部类型拖死。

**决策：** `Game` 永不引用具体的 `CustomSRP.*` 类型。跨边界用 `IAtmosphereVisualDriver` / `IOilWarpVisualDriver`，草地 / 相机等少量桥走有意的反射。见 `Assets/GameMain/Docs/00-overview.md`。

**理由：** 避免循环依赖，油画驱动可替换。代价：反射改名只会打日志、不编译失败 —— 改名要有纪律。

**状态：** 已采纳
