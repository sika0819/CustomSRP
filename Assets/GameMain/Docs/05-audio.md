# 音频（BGM / 环境音 / 天气音 / 雨水面）

模块文档索引见 [00-overview.md](00-overview.md)。
**音量 RTPC 硬规则**见 [`.cursor/rules/wanos-audio.mdc`](../../../.cursor/rules/wanos-audio.mdc) 与
[`Assets/WanosAudio/README.md`](../../WanosAudio/README.md)。

## 职责与边界

- `Services/MusicPlaylist.cs` + `MusicCatalog.cs`：BGM 播放状态机 + 专辑目录（服务器优先，本地 JSON 兜底）。
- `Services/NowPlayingService.cs` + `BackgroundTransportSync.cs`：负一屏媒体卡片 + 后台传输计划（Android）。
- `Services/Audio/`：3D 环境音区、天气音、雨水面音、前后台恢复。
- `Sound/`：GameFramework 声音组件的 Wanos 适配层。
- `Assets/WanosAudio/`：真正的引擎封装（`WanosEngine` / `WanosInteraction` / `WanosBankStore`）。

同一时刻只有一个静音原因：`PlaybackGate.Silence`。
卡片是否在播只看 `PlaybackGate.CardShouldShowPlaying`。
`UserManuallyPaused` / `PausedForTimerEnd` 从原因派生，不再另写布尔。

负一屏播放、暂停、切歌只进 `BoluoNowPlayingRemoteSession`（250ms 合并、400ms 同向去抖、切歌作废尚未落地的暂停）。
前台 MediaSession 在 Java 合并完，只把最后一次意图交给 Unity，Unity 不再开第二扇窗口。
后台 Unity 不跑，同一个 Java 会话直接 `StopPaired` / `ResumePaired`。

停是 `Pause_Music` + `Pause_ALL` + 内容声闸，禁止 `forceStop`，播放头留在暂停处。
续是 `Resume_ALL` + `Resume_Music` + 打开内容声闸。系统打断和负一屏暂停都是两边一起停、一起续。
实例已经没了（曲终、进程重启）才重发当前曲。切歌才换一首新的。

```
出声中
  ├─ 负一屏 / 岛内 / IMP 暂停 → UserPause
  │    回前台：保持静音 + 粘滞 tip「菠萝被暂停，滑动屏幕恢复」
  │    滑动或播放键 → ResumePaired（从暂停处，音乐+音效）
  ├─ 定时到点 → TimerEnd
  │    回前台、滑动、负一屏播放：保持静音，不弹滑动 tip
  │    弹窗取消 / 重复 / 关窗 → ResumePaired
  └─ 系统打断 → SystemInterrupt
       自动续上：ResumePaired，不弹提示
       没有自动续上：回前台保持静音 + 同一条粘滞 tip，滑动后续两边
```

会自动续（成功则无提示；失败或焦点被拒才落入提示）：

- 来电、微信语音结束后的 GAIN
- 微信 MAY_DUCK、通知音：GAIN 或 1.8 秒看门狗
- 有外来媒体的永久丢焦：对方停后静默确认（前台 2 秒 / 后台 30 秒）；人已回前台且对方还在播时抢回焦点，抢到就续
- 拔耳机：约 700 毫秒后自续；还在通话或别的播放器仍在响则不抢

不会自动续，回前台弹提示，滑动从暂停处续两边：

- 负一屏、锁屏、通知栏、岛内、IMP 的暂停
- 回前台申请焦点被拒绝（电话还没挂、独占被拒）
- 拔耳机时对方仍在播，或通话未结束，回到前台仍然无声
- 其它播放器一直占着，前台抢焦失败
- 自动续播跑过但仍有一边停着

不是打断，不停、不提示：系统更新弹窗（没有外来媒体、没有通话）、允许后台播时的上滑任务栏和 HOME、预设虹膜压低音量、用户自己拖音量、曲终自动下一首、离岛或登出。禁止后台播放时回前台要自动恢复，恢复失败才提示。

原生 `onFocusResume` 已经续过 → `SyncAfterNativeFocusResume` 只对账，不再 Unmute / 再申请焦点，也不再弹提示。
`OnNowPlayingPlay(ui)` 只刷卡，不驱引擎。

## 关键文件

| 文件 | 作用 |
|---|---|
| [Services/MusicPlaylist.cs](../Services/MusicPlaylist.cs) | BGM 播放状态机；`AdoptTrack` / `CurrentEventName` 给后台对账用 |
| [Services/MusicCatalog.cs](../Services/MusicCatalog.cs) | 专辑 / 曲目元数据；`TryFindByEventName` |
| [Services/NowPlayingService.cs](../Services/NowPlayingService.cs) | 系统媒体卡片；每次 Publish 顺带 `PublishPlan` |
| [Services/BackgroundTransportSync.cs](../Services/BackgroundTransportSync.cs) | 前台推计划 / 回前台对账（Android only；iOS no-op） |
| [Services/Audio/AudioAppLifecycle.cs](../Services/Audio/AudioAppLifecycle.cs) | App 前后台统一恢复；`CommitPairedHardResume` / `TryPairedAutoRestore` / `SyncAfterNativeFocusResume` |
| [Services/Audio/PlaybackGate.cs](../Services/Audio/PlaybackGate.cs) | 播放展示 Gate + LifecycleKind SSOT |
| [Services/Audio/AudioInteractionBootstrap.cs](../Services/Audio/AudioInteractionBootstrap.cs) | 音频交互总开关 |
| [Services/Audio/AmbienceZoneService.cs](../Services/Audio/AmbienceZoneService.cs) | 3D 环境音区 |
| [Services/Audio/AmbienceZoneCatalog.cs](../Services/Audio/AmbienceZoneCatalog.cs) | 环境音锚点数据 |
| [Services/Audio/WeatherAudioBridge.cs](../Services/Audio/WeatherAudioBridge.cs) | 天气 → 风雨循环 + RTPC |
| [Services/Audio/RainWaterProximityService.cs](../Services/Audio/RainWaterProximityService.cs) | 雨中近水面音 |
| [Services/Audio/WaterSurfaceProbe.cs](../Services/Audio/WaterSurfaceProbe.cs) | 运行期水面最近点采样（已取代烘焙水域表） |
| [Sound/WanosSoundAgentHelper.cs](../Sound/WanosSoundAgentHelper.cs) | GF `SoundAgentHelperBase` 实现 |
| [Sound/WanosSoundResourceManager.cs](../Sound/WanosSoundResourceManager.cs) | 声音专用 `IResourceManager` |
| [Sound/WanosSoundSetup.cs](../Sound/WanosSoundSetup.cs) | 绑定辅助类 / 建声音组 |
| [`WanosAudio/.../AndroidBackgroundTransport.cs`](../../WanosAudio/Runtime/Platform/AndroidBackgroundTransport.cs) | JNI 薄封装：`SetPlan` / `ClearPlan` / `GetStateJson` |
| [`WanosMusicPlayerNative.java`](../../WanosAudio/Plugins/Android/WanosMusicPlayerNative.java) | 原生侧执行后台 pause/resume/skip + bank 热加载 |

## 关键 API

### MusicPlaylist

```csharp
const string MusicGroup = "Music";
const string SoundGroup = "Sound";
const string AmbientGroup = "Ambient";
const string PlayModeKey = "Music.PlayMode";

static MusicPlaylist Instance { get; }
static void ResetSession();

void ApplySavedVolumes();               // 存档音量 → 引擎 RTPC
void SetMusicVolume(float v01);         // UI 0–1
void SetEffectVolume(float v01);
void SetPlayMode(int mode);
int CyclePlayMode();

void PlayIndex(int albumIndex, int trackIndex, bool restart = false);
void PlayIndex(int trackIndex, bool restart = false);   // ⚠ 平铺旧接口，重载易误用
void Next();
void Prev();
void Pause();                                           // 只停引擎和内容声，不改 Silence
void Resume();                                          // Resume_Music，不改闩
void ResumeFromUserPause(string reason);                // ResumePaired：从暂停处续音乐和音效
void ResumeAfterTimerEnd(string reason = "timer-end");  // 清到点闩 → ResumePaired
bool TrySoftResume(string reason);                      // 系统打断软续；agent 已丢返回 false
void Stop(bool publish = true);
void ForceRetargetAtLanding(string preferredBankName = null);

string CurrentEventName { get; }                        // 与原生传输计划 event 对齐
void AdoptTrack(int albumIndex, int trackIndex);        // 回前台对账：只改索引，不发 Play
```

### MusicCatalog

```csharp
static event Action OnCatalogReady;
static bool IsReady { get; }
static int AlbumCount { get; }
static IReadOnlyList<MusicAlbum> Albums { get; }

static void SetServerData(List<MusicListServerData> serverData);  // 服务器优先
static void LoadAsync();                                          // 本地 JSON 兜底
static UniTask EnsureReadyAsync(float timeoutSeconds = 8f);
static string[] GetAlbumNames();
static MusicAlbum GetAlbum(int index);
static MusicTrack[] GetTracks(int albumIndex);
static bool TryFindByBankName(string bankName, out int albumIdx, out int trackIdx);
```

目录来源优先级（已修正）：**服务器 `musicLists` > 本地 `MusicLists.json`**。
服务器数据在本地 JSON 读取期间到达时，本地结果被丢弃（`ParseJson` 里 `_fromServer` 判定）。

### AudioInteractionBootstrap

```csharp
static bool IsStarted { get; }
static void ResetSession();
static void StartAfterUncover();     // 顺序固定，见下
static void Shutdown();
static void SetContentAudioPaused(bool);
static void ClearContent3D();        // 掐掉所有 3D 内容音
static void RetriggerContentAtListener();
```

`StartAfterUncover()` 的固定顺序（不能改）：

```
WanosInteraction.EnsureLogicalObjects()   // 必须在 WanosEngine.EnablePlayback 之后
WanosInteraction.StartListenerFollow()
AmbienceZoneService.Start()
WeatherAudioBridge.Start()
RainWaterProximityService.Start()
```

### AmbienceZoneService

```csharp
const float EvalIntervalSeconds = 0.25f;
const int VoiceStopFadeMs = 250;

static bool IsReady { get; }
static void Start();
static void Shutdown();
static void SetContentAudioPaused(bool);
static void SetPresetTransitionHeld(bool);
static void ClearAllPlaying();
static void EvaluateNow();
static void OnBankLoaded(string bank);
static void OnInlandWaterReady(bool);
static void OnVoiceStateChanged(ulong voiceId, AudioVoiceState state);
```

评估流程（每 0.25s）：
```
FlushDeferredEmitterReleases
→ 取 listener 位置（位移 < 0.09m 直接跳过本 tick）
→ 逐 source 求 ShouldPlay（水体类走 EvaluateWaterSource）
→ CollapseExclusiveSharedPlayWants（共享 bank 只留最近的一床）
→ ApplyLakeWaterfallExclusion（湖 vs 瀑布，最近者胜）
→ TransferExclusiveSharedPlays（交接 emitter 所有权，不做 Stop→Play 重叠）
→ KeepSourcePlaying / StopSource
```

增益模型：`GainDbForDistance` 走 Boluo 曲线（`ForestGainCurve` / `OceanGainCurve` / `CampfireGainCurve` …），
再经 `GainInvTransform` 把显示 dB 转成引擎 dB（显示 −17.25 → 引擎 −96）。

### AmbienceZoneCatalog

```csharp
enum AmbienceEmitterKind { Ocean, River, Forest, Campfire, Waterfall, Lake, Grassland, Plateau }
class AmbienceEmitterSpec { Id, Kind, Bank, PlayEvent, StopEvent, Position,
                            EnterRadius, ExitRadius, UseXzDistance, RiverCatalogIndex, RiverMeshName }
static AmbienceEmitterSpec[] StaticEmitters;                 // 8 个硬编码世界锚点
static bool TryCreateFromMapItem(MapItemData item, int catalogIndex, out AmbienceEmitterSpec spec);
static void AppendNativeRivers(Action<AmbienceEmitterSpec> append);
static bool IsWaterfallPrefab(string name);
```

### WeatherAudioBridge

```csharp
// bank: Wind / Rain ; 事件: Play_Wind / Stop_Wind / Play_Rain / Stop_Rain
// RTPC: Wind / Rain / Foggy，强度 = level * 30（0/30/60/90，不 clamp 到 0–1）
static void Start();
static void Shutdown();
static void SetContentAudioPaused(bool);
static void ClearPlaying();
static void Refresh();
```

### RainWaterProximityService / WaterSurfaceProbe

```csharp
// RainWater：EnableMeters=28，DisableMeters=32（迟滞）；bank Rain_Water
static void Start(); static void Shutdown();
static void SetContentAudioPaused(bool); static void ClearPlaying(); static void EvaluateNow();

// WaterSurfaceProbe：运行期从场景网格采样水面点（已取代旧的烘焙水域表）
enum SurfaceKind { River, Waterfall }
static bool IsReady { get; }
static void Clear();
static void Rebuild();
static bool TryNearest(SurfaceKind kind, Vector3 listener, float maxDist, out Vector3 closest, out float dist);
static bool TryNearestAny(SurfaceKind kind, Vector3 listener, out Vector3 closest, out float dist);
static bool TryNearestRiver(Vector3 listener, float maxDist, out Vector3 closest, out float dist);
static void GetAllPoints(List<Vector3> dst);
```

## 后台传输计划（Android 负一屏）

Unity 进后台会被宿主 pause，`UnitySendMessage` / C# 回调都停转。负一屏的播放 / 暂停 / 切歌必须由**原生直接驱动引擎**。

### 数据流

1. **前台推计划**：`NowPlayingService.PublishFromPlaylist` → `BackgroundTransportSync.PublishPlan`
   - **任意 mode** 推**当前专辑曲序**（未就绪占位 `path` 空；当前曲必须可播否则 Clear）；禁止扩全库。
   - JSON：`{"mode":2,"current":0,"tracks":[{"name":"...","event":"Play_Music1_2","bank":"Music1_2","guid":"...","path":"/abs/...wsbk","loaded":true}]}`
   - `mode` 对齐 `MusicPlaylist.PlayMode`（1 单曲 / 2 列表循环 / 3 随机），**只约束曲终 auto**；负一手动切歌不读 mode。
2. **后台执行**：`WanosMusicPlayerNative` 的 transport 回调里，若 `appInBackground`：
   - 播放/暂停 → `handleTransportInBackground`（`Pause_Music` / `Resume_Music`）
   - **手动切歌** → `bg_skip`：本专辑取模；邻接未就绪 → 重播当前；已就绪 → 切过去
   - **曲终** → `bg_ended`：mode 2/3 只落可播曲；mode 1 不自动切
3. **回前台对账**：`AudioAppLifecycle.RestoreForegroundAudio` → `BackgroundTransportSync.ResyncFromNative`
   - 读 `getBackgroundTransportState()` 的 `event` → `MusicCatalog.TryFindByEventName` → `MusicPlaylist.AdoptTrack`（**必须** `TrackChanged`）
   - **只改索引，不再发 Play**：引擎里已经是原生起播的那一轨。
4. **退岛清计划**：`NowPlayingService.CleanupAfterLeaveIsland` → `ClearPlan`，避免原生继续切失效曲目。

**切歌入口拆分**

- 负一屏 / remote：`AcceptSkip` → `MusicPlaylist.SkipAdjacentInAlbum`（本专辑环绕；邻接未下载完 → 重播当前，经 skipInFlight 闸）
- 岛内 UI 上一首/下一首：`Next`/`Prev`（跟 mode：列表本专辑环绕、单曲边界、随机可跨专辑）
- 岛内曲终：`AdvanceAfterTrackEnded` — 单曲重播同曲；列表 `Next` 末→本专辑首；随机 `PickShuffle`

iOS 无对等 API → `BackgroundTransportSync` 三个方法全部 no-op（负一手动仍走 Unity `SkipAdjacentInAlbum`）。

### 关键 API

```csharp
// Game
BackgroundTransportSync.PublishPlan();       // 前台：推本专辑曲序（未就绪占位 path 空）
BackgroundTransportSync.ClearPlan();         // 退岛 / 无曲 / 当前曲不可播
BackgroundTransportSync.ResyncFromNative();  // 回前台：AdoptTrack + 同步暂停态

MusicPlaylist.SkipAdjacentInAlbum(+1/-1);    // 负一手动：本专辑环绕；邻接未就绪重播
MusicPlaylist.Next()/Prev();                 // 岛内按钮：跟 PlayMode
MusicPlaylist.CurrentEventName;              // 与计划里的 event 对齐
MusicPlaylist.AdoptTrack(album, track);      // 只更新索引，不发 Play；须 TrackChanged

// WanosAudio（Android only）
AndroidBackgroundTransport.SetPlan(json);
AndroidBackgroundTransport.ClearPlan();
AndroidBackgroundTransport.GetStateJson();   // {"event","name","paused","index","valid","inBackground"}
```

### 硬规则

- **前台每次发媒体卡片都必须顺带 `PublishPlan`**（`PublishFromPlaylist` 已做）。漏推 → 后台负一屏切歌无响应 / 仍转发到已被 pause 的 Unity。
- **回前台必须 `ResyncFromNative` 再 `PublishFromPlaylist`**，否则岛内 UI 与「下一曲」会错位。
- **`AdoptTrack` 禁止再发 Play 事件**——原生已经起播，C# 只认账；但**必须** `TrackChanged`，否则列表/播放栏曲名不跟。
- **切歌所有权拆开**：Unity 前台 `AcceptSkip`→`PlayIndex` **禁止**随后 `Resync`（原生索引滞后会盖回旧曲）；原生后台 `bg_skip`→`OnNativeTrackChanged`→`Resync`→`AdoptTrack`，并清暂停闩切到 Playing。
- **FG skip inFlight**：`AcceptSkip` / `skipToUnity` / iOS remote 接受后置 `skipInFlight`，直到引擎 `Playing`（`NoteSkipSettled`）或 `SkipInFlightTimeoutMs=2000`。仅 400ms 同向防抖挡不住 ~1.4s 连切撞 `libWIAEngine` `AudioGroup::createInst`←`SequenceGroupInst::startNextSubInst`（见 `Logs/skip_crash_rca_20261104.md`）。inFlight 期间任意方向忽略；超时后必须放行。岛内列表点歌走 `PlayIndex`，不经 `AcceptSkip`。
- **`notifyUnityTrackChanged` / `notifyUnityPlaybackState` 必须发到 `BoluoMusicPlayerAndroid`**，默认 `unityObjectName=WanosMusicPlayer` 没有这些回调。
- **负一手动邻接未下载完 → 重播当前**（`ResolveManualSkipIndex` / 原生 `bg_skip`）；邻接已就绪才切过去。应对后台开下载弹窗、须用户点下。计划含未就绪占位（`path` 空）；曲终 auto 只落可播曲，禁止切到空 bank。
- Java 侧加载 bank 必须过 `looksLikeWanosBank` 头校验（拒绝 BKHD / 文本 / 半截文件，否则 native 抛 → SIGABRT）。

## 音量正确写法

```csharp
// ✅ UI 0–1，引擎 0–100 由 WanosEngine 内部换算
WanosEngine.SetVolumeParameter(WanosEngine.MusicVolumeParam, ui01);
WanosEngine.SetVolumeParameter(WanosEngine.FxVolumeParam, ui01);

// ❌ 写 SFX_Volume：引擎静默丢弃，音量永远不变
// ❌ 直接 WanosNative.Instance.SetParameter(...)：裸 0–100 通道，绕过量纲保护
// ❌ 把 0.8 原样透传：引擎收到 0.8 → 0.8% 音量 → 有播放但听不到
```

非音量 RTPC（天气）走 `WanosEngine.SetGlobalParameter(name, value)`，量纲按业务（0/30/60/90）。

回前台必须走 `WanosEngine.RestoreForeground(reason)`（解 mute + `ReapplyVolumes`）：
引擎 mute/unmute、设备重建后 RTPC 可能回到默认值。

## 日志 tag

`[MusicPlaylist]` `[MusicCatalog]` `[NowPlaying]` `[BackgroundTransportSync]` `[AudioAppLifecycle]`
`[AmbienceZone]` `[WeatherAudio]` `[RainWater]` `[WaterProbe]` `[AudioInteraction]`
（引擎侧另有 `[WanosAudio]` / `[WanosInteraction]` / `[AndroidBackgroundTransport]`；
原生侧 `WanosMusicPlayerNative` 打 `setBackgroundTransportPlan` / `bg transport` / `bg skip`）

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 有播放但听不到 | ① 音量没推（无 `SetVolumeParameter ... = 80.0 (ui=0.800)` 日志）② 量纲错 ③ 名字写成 `SFX_Volume` ④ Android AudioFocus / mute 影子态 | 按 [WanosAudio README](../../WanosAudio/README.md) 的五步顺序排查 |
| Unity 直发 PostEvent 真机无声 | 绕过了原生 `nativePlayTrackAt` 的「抢 AudioFocus + unmute」门禁 | Android 必须调 `BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback()`，并重推音量、调 `WanosEngine.Unmute("music-play")` |
| 后台负一屏切歌没反应 | 传输计划未推 / `valid=false` / bank 文件未就绪 | 看 Java `setBackgroundTransportPlan tracks=N valid=`；确认 `PublishFromPlaylist` 在播 |
| 负一屏切歌出声但岛内曲名/播放态不动 | `OnNativeTrackChanged` 发错对象；或 `AcceptSkip` 后 Resync 盖回；`AdoptTrack` 未发 `TrackChanged` | 确认发到 `BoluoMusicPlayerAndroid`；前台 skip 勿 Resync；Adopt 必 `TrackChanged` + Gate=Playing |
| 回前台岛内 UI 曲名错 / Next 跳到旧曲 | 没对账原生切过的曲 | 确认 `RestoreForegroundAudio` 调了 `ResyncFromNative`；看 `[BackgroundTransportSync] Resync adopted` |
| 回前台听不到 / 音量回到默认 | 引擎 mute 未解 / RTPC 被重置 | 走 `WanosEngine.RestoreForeground`（内部 `ReapplyVolumes`） |
| 负一屏暂停后点/滑场景无声 | 曾走软 Resume；或推播放卡误清暂停闩 | 点场景走 `CommitPairedHardResume`；KeepMute 武装 `_pendingUserResume`；playing 只跟 Gate |
| 切其他播放器后负一屏仍 PLAYING | 抢焦只 mute 不写卡 | 原生 `systemSuspended` + `updateTransportNowPlaying(false)` 同栈；Gate=SystemSuspended |
| 冷启动上滑任务栏误暂停 | `ShouldYield` 用 !AudibleCommitted / AllowUnmute；`yieldStartupPlaceholderCard` 停 FGS | LifecycleKind=KeepPlaying；BackgroundYieldGate 拆开启动 unmute 门 |
| 上滑任务栏声停、负一屏仍 PLAYING | `GameEntry` ResetSession→`teardownForLogout` 双钉 shouldStop+userPaused；Arm 只 `setPlaybackActive` 不清闩；宿主 `isUserStoppedPlayback` 误判 → `unity_pause_preview` 静音且不写卡 | Arm/`InitializeAfterLogin` 调 `clearLogoutStopLatch`；出播放卡清陈旧闩；keep-playing 判别对齐 `BoluoKeepPlayingPolicy` |
| 后台切歌崩溃（SIGABRT） | 给 native 塞了坏 bank（BKHD / 半截文件） | Java `looksLikeWanosBank` 必须拦住；C# 侧只推 `TryResolve` 成功的 path |
| 暂停一个音效后 BGM 也停了 | `WanosSoundAgentHelper.Pause` 对非音乐资源调 `WanosEngine.PauseAll()`（整个引擎） | 已知设计缺陷，避免对 SFX 用 GF 的 Pause/Resume |
| 环境音只有一个河流在响 | 共享 bank（`River_ADD` / `River_Quad`）只保留最近的一床 | 设计如此（exclusive share） |
| 站桥上听不到河声 | 曾用探针 **3D** 距离；桥面高于水面被判出区 | 河/瀑 `UseXzDistance` + want 用 `Distance`（对齐 Boluo 水平丝带） |
| 瀑布/河流完全没声 | ① 缺物体级钉正午 `Time of A Day`（夜间全局会掐水域）② `WaterfallMist` 被当瀑特征 → 湖↔瀑互斥 | Post/每 tick 钉 12h；Mist 不进音频分类；贴瀑 28m 内优先瀑 |
| 环境音越走越小声到突然换位置 | `TransferExclusiveSharedPlays` 交接所有权 | 听 `[AmbienceZone] transfer bank=` 日志 |
| 雨声突然不再触发 | `RainWaterProximityService._bankMissing` 是一次性熔断，bank 加载失败后整个雨季都不再恢复 | 需要 `ReleaseBank`（雨停）才复位 |
| 河声音效位置很怪 | `WaterSurfaceProbe` 只采样**直接子节点**；单 prefab 内陆水路径下采样不到 | 用 `WaterSurfaceProbe.Rebuild()` 看 `[WaterProbe] rebuilt surfaces=…` |
| 河岸距离算得远 | 网格未开 Read/Write 时退化成 bounds 中心点 | 导入模型时开 Read/Write Enabled |
| 切预设后环境音没了 | `PresetService.Apply` 会 `ClearContent3D()` | 正常，切换后会 `RetriggerContentAtListener()` |
| 目录里没有服务器的新专辑 | 本地 JSON 先加载导致服务器数据被忽略 | 已修复（服务器优先） |
| `Next()` 跳过一首歌 | `PlayIndex` 先推进索引再尝试播放，bank 缺失时不播但索引已变 | 看 `[MusicPlaylist]` 的 bank 缺失日志 |
| 媒体卡片不更新 | `NowPlayingService` 需要 `IslandWorldReadyEventArgs` 才 `ArmSession()`；首播须 `PublishPlayingForced`（`TryStartTrack` 成功路径） | 确认世界就绪 + `[NowPlaying] PublishPlayingForced`；`!_armed` 时 Publish 只 skip 不清卡 |
| 听得到歌但负一屏空白 | ① Android 13+ `POST_NOTIFICATIONS` 被拒 → MediaSession 在、FGS MediaStyle 被压掉 ② `installMediaButtonReceiver` 在 `sMediaSession=` 之前调 → NPE 静默失败 → `mediaButtonReceiver=null`（须 BroadcastReceiver） ③ **vivo OriginOS 6 原子随身听/负一屏有音源白名单**：`com.vivo.musicwidgetmix` 只认 QQ/网易云等；标准 MediaSession 进不了负一屏媒体栏（`Last VivoMediaButtonReceiver` 仍挂 QQ）。OriginOS 7 起可在「原子随身听 → 音源应用管理」手动开 ④ 旧逻辑 `!_armed` Publish 会 `ClearCard` | ① 通知权限 ② dumpsys `mediaButtonReceiver=MBR …BoluoMediaButtonReceiver` ③ vivo：白名单/升级 OriginOS7 开音源；创维/华为不走这套白名单 ④ 勿误打 `all empty, cleared` |
| 远程暂停/播放无效（前台） | `NowPlayingService.Tick()` 依赖 `Native/BoluoNativeBridge` 每帧泵送 | 非原生宿主环境需要自己泵 `Tick()` |
