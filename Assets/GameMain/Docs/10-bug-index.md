# 已知坑总表与排障流程

模块文档索引见 [00-overview.md](00-overview.md)。

本表汇总**已在代码中核实**的缺陷与设计陷阱。标 ✅ 的已在本次整理中修复；
标 ⚠ 的是仍然存在的已知问题或有意为之的设计约束。

## 1. 症状 → 定位 速查表

| 症状 | 第一嫌疑 | 去看 |
|---|---|---|
| 卡在加载遮罩 | YooAsset 未初始化 / LoadingForm 没打开 | [01](01-boot-procedure.md) [02](02-yooasset.md) |
| Editor 正常、真机资源加载失败 | YooAsset 地址 vs 编辑器路径不一致 | [02](02-yooasset.md) |
| 完全没声音 | 音量 RTPC 没推 / 量纲错 / 名字写成 `SFX_Volume` / Android AudioFocus | [05](05-audio.md) |
| 音量条能拖但引擎不变 | 推的不是 `Music_Volume` / `Fx_Volume`，或没走 `SetVolumeParameter` | [05](05-audio.md) |
| 后台负一屏切歌没反应 | 传输计划未推 / bank 未就绪 / `valid=false` | [05](05-audio.md#后台传输计划android-负一屏) |
| 回前台曲名错 / Next 跳旧曲 | 没 `ResyncFromNative` 对账原生切过的曲 | [05](05-audio.md#后台传输计划android-负一屏) |
| 回前台听不到 / 音量变默认 | 没走 `WanosEngine.RestoreForeground`（解 mute + 重推 RTPC） | [05](05-audio.md) |
| BGM 播了但环境音没了 | 预设切换 `ClearContent3D()`，或音频交互没启动 | [04](04-services.md) [05](05-audio.md) |
| 进岛没 HUD | `EnsureUiGroups` / `IslandMainForm` 加载超时（10s） | [06](06-ui.md) [01](01-boot-procedure.md) |
| 天气改了画面不变 | SRP driver 缓存 / `IAtmosphereVisualDriver` 未实现 | [04](04-services.md) |
| 真机已是「雨」仍无雨丝 | `Shader.Find` strip / Yoo 双份 Settings / 缺 `streakMaterial` | [11](11-rain-device-visual.md) |
| 雨预设（如委屈泛滥）雨丝粉/洋红 | SubShader `RenderPipeline=CustomSRP` 与管线 tag 不匹配被整包 strip | [11](11-rain-device-visual.md) §3.1b |
| 真机完全没云 | **有意为之**：`IslandMobileTune.TuneCloudsForMobile` | [11](11-rain-device-visual.md) [04](04-services.md) |
| 天气图标档位错 | 图标数组顺序 | [06](06-ui.md) |
| 走远后物体穿模 / 掉出地面 | 碰撞体 LOD 误清空 | [07](07-environment.md) |
| 走动时帧率抖 | 流式生成预算 / 碰撞体全表刷新 | [07](07-environment.md) |
| 相机 render scale 不变 | 反射找不到 `CustomRenderPipelineCamera` | [08](08-input-camera.md) |
| 存档不落盘 / 红点不出现 | 游客隔离拦截 | [03](03-persistence.md) |
| 坐标不再保存 | `PushSaveSuppress` 未出栈 | [06](06-ui.md) [03](03-persistence.md) |
| 预设切换卡在「切换中」 | `SchemeState` 卡在 1，`MarkPlaying()` 没调 | [04](04-services.md) |
| Editor 有黑场、真机切预设无黑场渐变 | `Hidden/CustomSRP/Preset Iris` 被 Player 剥离；`presetVeil` 故意透明无兜底 | 下文 §2 #6；[06](06-ui.md) |
| 登录拿不到 token | 签名非主线程 / 渠道配置错 | [09](09-boluoapi-native.md) |
| Bank 一直重下 | MD5 缓存读写失败 | [09](09-boluoapi-native.md) |
| 第二次进游戏状态残留 | `GameEntry.Awake` 复位清单缺项 | [00](00-overview.md) |

## 2. 已修复（本轮）

| # | 位置 | 问题 | 修复 |
|---|---|---|---|
| 1 | `Environment/Runtime/EnvMeshColliderLod.cs` | `Unregister` 用 `transform.root` 比较，所有 MapItem 共用 root → 卸载任一实例会清空**全岛**碰撞体表，距离 LOD 失效 | 改为只摘该实例自身与子节点；新增 `_colliderSet` 做登记去重与 O(1) 移除 |
| 2 | `UI/Forms/IslandMainForm.cs` | 天气云层数组为 `{晴天, 中云, 小云}` → 「多云 1 级」显示晴天图标，「多云 3 级」显示小云 | 改为 `{小云, 中云, 中云}`（无「大云」贴图）并加注释 |
| 3 | `Services/IslandWorldService.cs` | `Release()` 未复位静态 `InlandWaterReady` → 再次进岛时音频侧误判「水体已就绪」并沿用过期探针 | `Release()` 中置回 `false` 并触发 `OnInlandWaterReady(false)` |
| 4 | `Services/MusicCatalog.cs` | `SetServerData` 遇 `_albums != null` 直接 return；启动时本地 JSON 先加载 → 服务器歌单永远不生效 | 新增 `_fromServer` 标记：服务器数据覆盖本地并重新广播；本地解析时若服务器已到则丢弃本地结果 |
| 5 | `UI/Forms/MusicForm.cs`、`UI/Forms/PresetForm.cs` | 下载用 `.Forget()`，抛异常时 `onComplete` 不触发 → `PushSaveSuppress()` 永不出栈 → 坐标永久不落盘 | 改为 `async UniTaskVoid` 包装，`finally` 中保证出栈一次；回调里不再重复 Pop |
| 6 | `ProjectSettings/GraphicsSettings.asset`、`PresetIrisPass.cs` | 真机切预设无黑场：`Shader.Find("Hidden/CustomSRP/Preset Iris")` 无 Always Included 时被 strip → Pass 静默跳过；UI `presetVeil` 透明 | Always Included 加入 Preset Iris（及同批 rain shaders）；缺 shader 时打 `[PresetIris] ... not found`；**须重打真机包** |
| 7 | `OilRainStreakQuad` / `OilRainSettings` | 真机已是雨仍无雨丝：Runtime `Shader.Find` 不可靠；Yoo 可能复制第二份 Settings，天气写 Current、帘子读 prefab SO；L1=`level/3` 过淡 | 预烘焙 `OilRainCurtain.mat` → `streakMaterial`；强度读 `Current`；L1/L2/L3→0.4/0.7/1.0；相机优先 `IslandJoystickCamera`。详见 [11](11-rain-device-visual.md)。**须重打 Yoo + 真机包** |

## 3. 仍在库里的已知问题 ⚠

| # | 位置 | 问题 | 影响 | 备注 |
|---|---|---|---|---|
| A | `Sound/WanosSoundAgentHelper.cs` | 非音乐资源 `Pause`/`Resume` 调 `PauseAll`/`ResumeAll` | 一个音效暂停会静音整个引擎 | 设计取舍，注意调用 |
| B | `Services/Audio/RainWaterProximityService.cs` / `WeatherAudioBridge.cs` | ~~首装切风雨 miss bank 后不重试~~ → 已订 `WanosBankStore.OnBankLoaded` 重试 | 首装切雨无雨声 | 已修 |
| C | `Services/Audio/WaterSurfaceProbe.cs` | 只采样内陆水的**直接子节点** | 单 prefab 路径下采不到水面点，河声退化 | 见 [05](05-audio.md) |
| D | `Services/Audio/AmbienceZoneService.cs` | `TransferPlayOwnership` 里 emitter 池存在同 GO 双槽的可能 | 活跃 emitter 被误回收 | 距离边界快速切换时可能复现 |
| E | `Services/Audio/AmbienceZoneService.cs` | 池长度在构建时锁定，新增 source 会整体重分配 | 已发放的 emitter 不回收 | MapItem extras 迟到时可能触发 |
| F | `Services/NowPlayingService.cs` | `Tick()` 依赖 `Native/BoluoNativeBridge` 泵送 | 非原生宿主下远程播放/暂停不生效 | 需自行泵送 |
| G | `Services/PresetService.cs` | 用 `Resources.LoadAll` / `Resources.Load` 读预设与封面 | 违反「运行时资源走 YooAsset」规则 | 现存例外 |
| H | `Services/IslandMobileTune.cs` | `TuneTerrainShadowReceive` 实例化材质但不销毁 | 每次重进岛多一份实例 | 内存缓慢增长 |
| I | `Environment/Runtime/EnvNativeInstancing.cs` | ~~`RenderParams.worldBounds` 用局部 bounds~~ → 已改为批次世界 AABB 并集 | 真机树/灌木整批不可见 | 已修 |
| J | `Environment/Runtime/EnvItemStreamerLite.cs` | `EvaluateQueues` 用结构体副本，仅在 `UpdateShadowRange` 返回 true 时写回 | 后续任何「改了但不返回 true」的修改会丢失 | 扩展时注意 |
| K | `Environment/Runtime/EnvStreamingConfig.cs` | 所有半径是 `public static` 可变字段 | 任何运行时修改都可能被 `ApplyMobileTierDefaults()` 覆盖 | 建议改成只读 + 显式 setter |
| L | `Environment/Runtime/EnvBridgePillarFit.cs` | 地形高度按顶点聚类中心采样 | 斜坡上桥墩一端悬空 | 已知限制 |
| M | `UI/Forms/MusicForm.cs` / `PresetForm.cs` | 移动流量确认门禁是 TODO，当前放行 | 静默消耗移动流量 | 合规风险 |
| N | `UI/Forms/VolumeForm.cs` | `SaveVolume` 只在 `PointerUp`/`PointerCaptureOut` 保存 | 拖动中途关面板则不落盘 | 建议补 `OnClose` 保存 |
| O | `Native/NativeEditorHud.cs` | 用 `OnGUI` 画调试窗 | 违反「不用 OnGUI 做界面」的规则 | 仅 Editor，不进包 |
| P | `BoluoApi/Signature/SignatureLib.cs` | 非主线程调用抛异常被吞 → 请求**无签名**发出 | 服务端返回业务错误而非传输错误 | 请求必须在主线程构造 |
| Q | `BoluoApi/BoluoApiEnvironment.cs` | 未知环境字符串默认落到 dev | 手拼错环境名会连到开发服 | 用 Editor 覆盖键 |
| R | `BoluoApi/BankDownloader.cs` | `DownloadBanks` 与 `DownloadBank` 参数顺序不同 | 位置传参会错位 | 一律用命名参数 |
| S | `Entity/IslandCameraSrpSetup.cs` | 反射类型名硬编码 `Assembly-CSharp` | SRP 一旦独立成 asmdef，相机设置全失效且只有日志 | 编译期无感知 |
| T | `Services/WeatherService.cs` | driver 缓存 2s，新挂载的 driver 收不到推送 | 视觉不同步 | 世界加载后有 `PushVisuals()` 补偿 |

## 4. 标准排障流程

### 4.1 音频类

1. 日志里有没有 `SetVolumeParameter ... = 80.0 (ui=0.800)`？没有 → 音量根本没推。
2. 有推但数值是 `0.8` → 量纲错，必须走 `WanosEngine.SetVolumeParameter`。
3. 参数名对但没效果 → 名字写错（`SFX_Volume` 会被引擎静默丢弃）。
4. 音量对、`PostEvent` 有日志、真机没声 → 查 Android AudioFocus / mute 影子态，调 `EnsureAudibleForUnityPlayback()`。
5. `PlaybackState` 不是 `Started` → 查 `WanosEngine.IsPlaybackEnabled` / `IsLoaded`。**不要**据此反复 `PlayIndex(restart)`：Editor/macOS 上该状态常与真实发声不同步，会表现为启动后音乐反复 `Stop_Music`/`Play_Music`（见 `AudioAppLifecycle.NeedsRestore` 只认后台 latch / `IsInterrupted`）。
6. 后台负一屏：Java `setBackgroundTransportPlan tracks=N valid=` → 后台 `bg transport` / `bg skip` → 回前台 `[BackgroundTransportSync] Resync adopted`。缺任一环就对不上。

### 4.2 资源类

1. `[YooAsset] Invalid location: X` → 地址规则不匹配（大小写 / 扩展名 / 采集组）。
2. `[YooGF] Package not ready for:` → 包没初始化或 manifest 无效。
3. Editor 能、真机不能 → 对比「采集规则地址」与「代码里传的字符串」。
4. 资源加载成功但表现不对 → 检查 `Resources.Load` 路径的资源是否被打进包（编辑器资源目录不参与 YooAsset 采集）。

### 4.3 卡顿 / 性能类

1. Profiler 看 `LateUpdate`：`EnvNativeInstancing.DrawAll` 与 `IslandJoystickCamera` 是常驻热点。
2. 看 `EnvMeshColliderLod.Refresh`（0.25s 全表扫描）与 `EnvItemStreamerLite.EvaluateQueues`（每帧 256 个）。
3. 看 `HudOverlayGate.HasPopup`（已改 `UIFormCount`；旧版 `GetAllUIForms` 每帧分配）。
4. 开预设面板掉帧：查 `PresetForm` 进度 tick + `BankDownloader.LocalSatisfiesCatalog` 磁盘探测。
5. 编辑器与真机差异：真机走 `ApplyMobileTierDefaults()`，半径更小、预算更低。

### 4.4 状态残留类（第二次进游戏异常）

1. 对照 [00-overview.md 的复位清单](00-overview.md#4-热重载复位清单disable-domain-reload) 检查 `GameEntry.Awake`。
2. 看是不是某个静态 `HashSet` / `List` / `Dictionary` 没清（Unity 6 用 `EntityId` 作键，别用 `GetInstanceID()`）。
3. `Bootstrap` 类（`BankUpgradeBootstrap`）注意 `_started` 守卫和 TCS 重建。

## 5. 快速 grep 命令

```bash
cd Assets/GameMain

# 按 tag 过滤
grep -rn "AmbienceZone\|MusicPlaylist\|BankDownloader" --include="*.cs" .

# 找所有日志 tag
grep -rhoE '"\[(GameEntry|InitYooAsset|GuestLogin|ProcedureIsland|YooAsset|YooGF|IslandWorld|AmbienceZone|MusicCatalog|MusicPlaylist|BankDownloader|SignatureLib|IMP)\]"' --include="*.cs" . | sort -u

# 危险 API 扫描
grep -rn "GetInstanceID()\|Screen.SetResolution\|Resources.Load(" --include="*.cs" .
```

> `GetInstanceID()` 在 Unity 6 已 Obsolete（CS0619），一律用 `GetEntityId()`。
> `Screen.SetResolution` 在移动端禁止使用，固定 16:9 缓冲会被 `FullScreenWindow` 拉到 20:9 真机。
