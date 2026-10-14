# 环境流式状态

## 本阶段已接入（MapItem）

- `AllMapItem.bytes`（~2467 摆放）经 YooAsset `Configs` / Editor 路径加载
- 运行时根节点 `MapItem` + 分类子节点；`EnvMapItemSpawner` + `EnvItemStreamerLite` 距离流式
- 分帧生成预算、MeshCollider LOD、Android `EnvNativeInstancing`（DrawMeshInstanced）
- 材质 `CustomSRP/OilNPR` + SRP Batcher；石/桥 skip Native 合批

菜单：`GameMain/Boluo/Validate MapItem Data + DaoYu1 Prefabs`、`Build MapItem Hierarchy Prefab`  
收集：`Game/YooAsset/Setup` → 组 `DaoYu1MapItem`（`AddressByDaoYu1Relative`）

## 本阶段已接入（Hybrid / 登录 / Bank 下载）

- IMP Native 桥（Notify/Query/Push、HUD/Overlay/Timer/Media、SignKit）
- `boluoRsa` → `boluoDeviceLogin` / `touristLogin` + 环境·渠道·版本 Editor 窗
- `upgrade/bank` catalog + `BankDownloader` 落盘到 `persistentDataPath/BoluoAudioBanks`

## 仍延后

- DaoYu 地形 16 切片流式
- GPU Instancer Pro / BoluoInstancing GPUI 后端
- Amplify Impostor 运行时启用
- VIP / 原生宿主账号 UI 完整契约
- **Wwise 播放**（Bank 仅下载落盘，不灌引擎）
- GameFramework 可更新资源热更完整契约
