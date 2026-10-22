# 存档 / 持久化 / 游客隔离

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

- `BoluoGameSave`：内存态存档模型 + setter（**UI 层 0–1**）。
- `BoluoGamePersistence`：PlayerPrefs 键层（**落盘层 0–100**），并做游客隔离。
- `IslandPlayerPosSave`：相机坐标的周期保存 / 出生优先级。
- `DownloadedUnplayedManager`：红点「已下载未试听」集合。
- `AccountPasswordLoginThrottle`：账号密码失败锁定。
- `BoluoLogout`：登出清理。

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
```

岛上大气（天气 kind/level、hours、period、look、fog）写在 `BoluoSaveData.hasIslandAtmosphere` 等字段；`WeatherService` 在稳定变更后 Capture，进岛 `RestoreOrApplyLocalClock` 恢复。

### BoluoGamePersistence

```csharp
public static class Keys
{
    public const string BoluoSaveData = "...";
    public const string PlayerPos = "...";
    public const string DaoYuFirstRunComplete = "...";
    public const string TutorialType = "...";
    public const string GongNengView = "...";
    public const string LocationPermissionRequested = "...";
    public const string DownloadedUnplayed = "...";
    public const string AppleUserIdKey = "...";
    public const string BoluoFirstNetworkProbeComplete = "...";
    // 账号密码锁定：前缀 "Boluo.AccPwLogin.Fail." / ".LockUtcTicks." / ".PhoneRegistry"
}

static void ClearAccountBoundProgress();   // 切账号/退登：删位置/大气/首进岛/教程/红点等
static string LoadBoluoSaveDataRaw();
static void PersistBoluoSaveDataRaw(string json);
static void PersistPlayerPositionJson(string json);
static bool HasPlayerPositionSaved();
static string LoadPlayerPositionJson();
static bool HasDaoYuFirstRunCompleteMarker();
static void SetDaoYuFirstRunCompleteMarker();
static bool TrySetTutorialTypeMarker(int type);
static bool TrySetGongNengViewMarker(int type);
static bool TrySetLocationPermissionRequestedFlag();
static string LoadDownloadedUnplayedRaw();
static void PersistDownloadedUnplayedRaw(string raw);
static bool LoadActivityWorldSleepPopupDate(out DateTime date);
static bool TryPersistActivityWorldSleepPopupDateToday();
static string AccPwFailKey(string phoneDigits);
static string AccPwLockKey(string phoneDigits);
static void PlayerPrefsSave();
static string BuildDebugSnapshot(int maxValuePreviewChars = 80);   // 打印所有键值 → 排查游客隔离最快
```

### IslandPlayerPosSave

```csharp
const float SaveIntervalSeconds = 10f;
const float SaveStartDelaySeconds = 10f;

static bool UseDefaultPlayerPosNextLoad { get; set; }   // 登出时置 true
static bool ShouldSuppressSave { get; }
static void PushSaveSuppress();      // 计数 +1（下载资源时冻结坐标保存）
static void PopSaveSuppress();       // 计数 -1（下限 0）
static void ClearSaveSuppress();
static void ResetSession();
static void InitAndArm(IslandJoystickCamera cam);
static void Tick();                  // ProcedureIsland 每帧调用
static void TrySaveCurrent();
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
1. UseDefaultPlayerPosNextLoad 为 true  → 默认出生点（登出后）
2. 首次运行（无 DaoYuFirstRunComplete） → 默认出生点 + 打标记
3. 有存档坐标                          → 恢复坐标
4. 其它                                → 默认出生点
```

默认出生点来自 `Entity/IslandSpawnPose.cs`：兜底 `(610.0767, 12.80065, 706.1781)`，yaw `130.443`。

## 游客隔离（关键）

`BoluoGamePersistence` 的读写**全部先判 `GuestSession.IsGuest`**：游客一律返回空值 / 不写盘。
游客仍然会写的例外：`PersistAppleUserId` / `LoadAppleUserId` /
`HasFirstNetworkProbeComplete` / `SetFirstNetworkProbeComplete` /
`HasTutorialTypeMarker` / `HasGongNengViewMarker` / `GetLocationPermissionRequestedFlag`。

## 日志 tag

`[BoluoGameSave]` `[IslandPlayerPosSave]` `[DownloadedUnplayed]` `[BoluoLogout]`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 存档不落盘 / 红点不出现 / 坐标不恢复 | 游客隔离：登录会话还没建立（`IsGuest == true`）就写了 | 看 `[GuestLogin]` 日志确认换票成功；用 `BuildDebugSnapshot()` 看键值 |
| 音量条能动但引擎不变 | 0–1 / 0–100 量纲 | `SetMusicVolume01` 内部已 ×100；推引擎必须 `WanosEngine.SetVolumeParameter` |
| 坐标保存被永久冻结 | `PushSaveSuppress` 没有对应 `Pop`（计数器只减不增） | 见 [06-ui.md](06-ui.md) 中下载流程；现在 Music/Preset 表单都由 `finally` 保证出栈 |
| 崩溃后存档丢失 | `PersistBoluoSaveDataRaw` 不一定调 `PlayerPrefs.Save()`，依赖 Unity 退出时自动保存 | 关键节点可显式 `PlayerPrefsSave()` |
| `JsonUtility` 解析出 null | 根节点类型不对 / JSON 是数组 | `ParseBootManifestOrDefault` 同款问题 |
| 老存档缺字段 | iOS 迁移逻辑只在 `UNITY_IOS` 编译 | 跨平台行为差异 |
| 首次进岛不按存档走 | `DaoYuFirstRunComplete` 标记未打 | 清 `PlayerPrefs` 复现 |
| 登出后仍读旧坐标 | `BoluoLogout.Execute()` 会置 `UseDefaultPlayerPosNextLoad` 并清会话 | 确认登出走的是 `BoluoLogout.Execute()` |
| 切账号仍恢复上一账号天气/位置 | 退登只清内存、未删 `BoluoSaveData`/`PlayerPos` | `BoluoLogout.ClearAccountLocalProgress` + `ClearAccountBoundProgress`；`ApplyUserSession` 在 userId 变化时也会清 |
