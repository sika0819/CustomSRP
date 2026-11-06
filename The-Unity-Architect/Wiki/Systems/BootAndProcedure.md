# 系统 — 启动与 Procedure

**路径：** `Assets/GameMain/Procedure/` · `Assets/GameMain/Boot/`  
**详解：** [`01-boot-procedure.md`](../../../Assets/GameMain/Docs/01-boot-procedure.md)

## 流程链

```
ProcedureLaunch
  → ProcedureInitYooAsset
    → ProcedureGuestLogin   （原生桥 + device/user 换票 + 踢起 Bank 升级）
      → ProcedureIsland     （服务 → 世界 → 相机 → HUD → 音频 Uncover）
```

## 加载遮罩

`BootLoadingService` + `BootEarlyCover` / `LoadingForm` 把进度从约 0.05 推到 1.0，覆盖 YooAsset、登录、世界、相机、道具等阶段。

## Doctor 备注

Boot / Loading 里用 opacity 隐藏会被 `unity-doctor` UI-002 标出来；若是故意淡出，不要盲目改成 `DisplayStyle.None`，先核对动画约定。
