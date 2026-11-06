# 游戏表单只用 UI Toolkit

**背景：** 移动端 HUD / 面板需要统一 UI 栈，并接入 GF `UIFormLogic`。

**决策：** 全部运行时表单用 UI Toolkit（`UIDocument` / UXML / USS / `GameFormLogic`）。新工作禁止 `UnityEngine.UI` Canvas 表单。深度用 `UIDocument.sortingOrder`。

**理由：** 对齐 Unity 6 方向与现有 Form 基建。代价：遗留场景（如 `CustomRPTest`）可能还有 uGUI 缺脚本 —— 可清可忽略，不要当新模式复活。

**状态：** 已采纳
