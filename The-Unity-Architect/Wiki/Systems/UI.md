# 系统 — UI Toolkit 表单

**路径：** `Assets/GameMain/UI/`  
**详解：** [`06-ui.md`](../../../Assets/GameMain/Docs/06-ui.md) · ADR [0003](../ADR/0003-ui-toolkit-only.md)

## 硬规则

- 只用 UI Toolkit（`UIDocument`、UXML、USS、`VisualElement`）。
- 表单派生 `Game.UI.GameFormLogic : UIFormLogic`。
- 深度用 `UIDocument.sortingOrder` —— 不要改 `PanelSettings.sortingOrder`。
- Group：Loading / Main / Panel / Dialog（`GameEntry.EnsureUiGroups`）。

## 运行时表单（不完全列举）

岛上 HUD 与面板：`IslandMainForm`、`WeatherForm`、`MusicForm`、`VolumeForm`、`PresetForm`、`TimerPauseForm`、`LoadingForm`，以及各类对话框 / Toast（`CommonDialogForm`、`TipToastForm`、退登 / 协议 / 存档确认等）。

## Doctor 备注

多处 `style.opacity = 0` —— 常常是淡出终点。动效用 GPU 侧 `translate` / `scale`；意图是「彻底拿掉」时才用 `display` / `visibility`。
