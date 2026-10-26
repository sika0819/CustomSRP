# 存档 / 持久化 / 游客隔离

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

- `BoluoGameSave`：内存态存档模型 + setter（**UI 层 0–1**）。
- `BoluoGamePersistence`：PlayerPrefs 键层（**落盘层 0–100**），游客隔离 + **按 userId 分桶**。
- `IslandPlayerPosSave`：相机坐标周期保存（10s 一并落盘天气 / BGM）/ 出生优先级。
- `DownloadedUnplayedManager`：红点「已下载未试听」集合。
- `AccountPasswordLoginThrottle`：账号密码失败锁定。
- `BoluoLogout`：普通退登保留分桶进度；注销清空当前 userId 桶。

## 关键文件

| 文件 | 行数 | 作用 |
|---|---|---|
| [Persistence/BoluoGameSave.cs](../Persistence/BoluoGameSave.cs) | 181 | 存档模型 + setter |
| [Persistence/BoluoSaveData.cs](../Persistence/BoluoSaveData.cs) | 23 | `[Serializable]` DTO |
| [Persistence/BoluoGamePersistence.cs](../Persistence/BoluoGamePersistence.cs) | 244 | PlayerPrefs 键层 |
| [Persistence/IslandPlayerPosSave.cs](../Persistence/IslandPlayerPosSave.cs) | 146 | 坐标保存 |
| [Persistence/DownloadedUnplayedManager.cs](../Persistence/DownloadedUnplayedManager.cs) | 143 | 红点集合 |
| [Persistence/AccountPasswordLoginThrottle.cs](../Persistence/AccountPasswordLoginThrottle.cs) | 95 | 登录失败锁定 |
| [Persistence/BoluoLogout.cs](../Persistence/BoluoLogout.cs) | 19 | 登出 |
| [Services/GuestSession.cs](../Services/GuestSession.cs) | 89 | 游客标记薄包装 |
| [BoluoApi/BoluoSession.cs](../BoluoApi/BoluoSession.cs) | — | token / userId 存储（SSOT） |

## 关键 API

### BoluoGameSave

```csharp
static BoluoSaveData Data { get; }
static bool IsLoaded { get; }
static void EnsureLoaded();
static void Load();
static void Persist();
static void ResetSession();

// 音量：UI 层 0–1（内部除以 100）
static float MusicVolume01  { get; }   // = bgmVolume / 100f
static float EffectVolume01 { get; }
static void SetMusicVolume01(float v);   // 0–1 → clamp 0–100 → 立刻 Persist
static void SetEffectVolume01(float v);

static void SetWeatherLinkage(bool v);
static void SetHealthTip(bool v);
static void SetRecommend(bool v);
static void SetMaxHealthLevel(int level, uint dateUnix);
static void SetNoEnquireHealthLevel(bool v);
static void SetLastPressureLevel(int level, uint dateUnix);

static bool HasIslandAtmosphere { get; }
static void CaptureIslandAtmosphere(kind, level, hours, period, look, fog);
static bool TryApplyIslandAtmosphere(WeatherService weather);
static void CaptureMusicTrack(bankName, eventName);
static void CaptureMusicFromPlaylist(MusicPlaylist music);
static bool TryGetSavedMusicTrack(out bankName, out eventName);
```

岛上大气（天气 kind/level、hours、period、look、fog）写在 `BoluoSaveData.hasIslandAtmosphere` 等字段；`WeatherService` 在稳定变更后 Capture，进岛 `RestoreOrApplyLocalClock` 恢复。
BGM 用 `hasMusicTrack` + `musicBankName` / `musicEventName`（避免专辑顺序漂移）；揭罩 `WaitCriticalThenCueAsync` 优先恢复存档曲，否则 `Music1_1`。

### BoluoGamePersistence

进度键按 `userId` 分桶：`BoluoSaveData.{userId}`、`PlayerPos.{userId}` 等。旧无后缀键在首次读时迁入当前桶。

```csharp
// AllowsAccountProgressDisk = IsLoggedIn && !IsGuest && !string.IsNullOrEmpty(UserId)
static void ClearAccountBoundProgress(string userId = null);  // 注销：只删该桶 + 旧无后缀键
static string LoadBoluoSaveDataRaw();
static void PersistBoluoSaveDataRaw(string json);
static void PersistPlayerPositionJson(string json);
static bool HasPlayerPositionSaved();
static string LoadPlayerPositionJson();
static bool HasDaoYuFirstRunCompleteMarker();
static void SetDaoYuFirstRunCompleteMarker();
static void PlayerPrefsSave();
static string BuildDebugSnapshot(int maxValuePreviewChars = 80);
```

### BoluoLogout

```csharp
static void Execute();                 // 兼容 → ExecuteLeave
static void ExecuteLeave();            // 普通退登：清会话，保留分桶进度
static void ExecuteAccountDeleted();   // 注销：清当前 userId 桶 + 会话
static void ClearAccountLocalProgress(string reason);
static void ResetInMemoryForAccountSwitch(string reason);  // 热切账号：只重置内存
```

宿主 `HostLeaveIsland`：`a=0` → `ExecuteLeave`；`a=1` → `ExecuteAccountDeleted`。

### IslandPlayerPosSave

```csharp
const float SaveIntervalSeconds = 10f;
const float SaveStartDelaySeconds = 10f;

static bool UseDefaultPlayerPosNextLoad { get; set; }   // 注销后置 true
static void InitAndArm(IslandJoystickCamera cam);
static void Tick();                  // ProcedureIsland 每帧；到期 TrySaveCurrent(flush:true)
static bool TrySaveCurrent(flush=false, ignoreSuppress=false);  // 位置 + 天气 + BGM
static bool TrySaveCurrentAndFlush(); // force + flush（退登前 / pause / OnLeave）
```


### DownloadedUnplayedManager

```csharp
static void EnsureLoaded();
static void ResetSession();
static void MarkPlayed(string key);
static bool ShouldShowRedDot(string key);      // 24 小时保护窗口
static string MusicKey(int album, int music);  // "m:{album}|{music}"
static string PresetKey(string displayName);   // "p:{name}"
```

## 出生点优先级（`IslandPlayerPosSave.ApplySpawnPriority`）

```
1. UseDefaultPlayerPosNextLoad 为 true  → 默认出生点（注销后）
2. 首次运行（无 DaoYuFirstRunComplete.{userId}） → 默认出生点 + 打标记
3. 有存档坐标                          → 恢复坐标
4. 其它                                → 默认出生点
```

默认出生点来自 `Entity/IslandSpawnPose.cs`：兜底 `(610.0767, 12.80065, 706.1781)`，yaw `130.443`。

## 游客隔离与分桶（关键）

读写进度要求：`IsLoggedIn && !IsGuest && !string.IsNullOrEmpty(UserId)`。
- 游客 / 旧 blob 无 userId：一律不读写进度键。
- 正式账号：键为 `BaseKey.{userId}`；普通退登保留；注销 `ClearAccountBoundProgress(userId)`。
- Hybrid 宿主须下发 `expire\ttoken\tisGuest\tuserId`（见 [09-boluoapi-native.md](09-boluoapi-native.md)）。

## 日志 tag

`[BoluoGameSave]` `[IslandPlayerPosSave]` `[DownloadedUnplayed]` `[BoluoLogout]`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 存档不落盘 / 坐标不恢复 | 仍被标游客，或 blob 无 userId | 看 `[DeviceLogin] guest=` / `userId=`；`BuildDebugSnapshot()` |
| 退登后再登同账号丢进度 | 误走了 `ExecuteAccountDeleted` / `a=1` | 普通退登用 `a=0` / `ExecuteLeave` |
| 注销后仍恢复旧位置 | 未走 `a=1` 或未清桶 | 宿主注销成功推 `HostLeaveIsland a=1` |
| 音量条能动但引擎不变 | 0–1 / 0–100 量纲 | `SetMusicVolume01` 内部已 ×100 |
| 坐标保存被永久冻结 | `PushSaveSuppress` 没有对应 `Pop` | 见 [06-ui.md](06-ui.md) |
| 崩溃后存档丢失 | 旧版周期写未 `PlayerPrefs.Save()` | 现周期 / 退登 / pause 均 flush；看 `[IslandPlayerPosSave] saved ok` |
| 退登后再登丢最后位置 | 旧版先 `GuestSession.Clear` 再 OnLeave 存 | `ExecuteLeave` 清会话前先 `TrySaveCurrentAndFlush` |
| 首次进岛不按存档走 | `DaoYuFirstRunComplete.{userId}` 标记未打 | 清对应桶 Prefs 复现 |
| 正式账号仍不存 | 宿主 blob 仍游客 / 无 userId | `[IslandPlayerPosSave] skip save — disk gated`；`BuildDebugSnapshot()` |
