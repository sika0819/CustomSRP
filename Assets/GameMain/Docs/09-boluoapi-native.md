# BoluoApi 与 Native 桥（登录换票 / 签名 / Bank 下载 / 混流 HUD）

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

`BoluoApi/` 是与 Boluo 服务端交互的全部代码；`Native/` 是 Unity ↔ 原生宿主的桥。
两者在混流（hybrid）场景下互相配合：**原生能提供的（deviceToken / userToken / 签名 / 渠道）
优先走原生，拿不到才走 HTTP。**

## 关键文件

### BoluoApi

| 文件 | 行数 | 作用 |
|---|---|---|
| [BoluoApi/BoluoApiEnvironment.cs](../BoluoApi/BoluoApiEnvironment.cs) | 130 | 网关环境（dev/test/prerelease/product）+ base url |
| [BoluoApi/BoluoDeviceInfo.cs](../BoluoApi/BoluoDeviceInfo.cs) | 262 | 设备/渠道/appId/版本/品牌等请求头字段 |
| [BoluoApi/BoluoDeviceLoginClient.cs](../BoluoApi/BoluoDeviceLoginClient.cs) | 430 | 换票三步 |
| [BoluoApi/BoluoPhoneLoginClient.cs](../BoluoApi/BoluoPhoneLoginClient.cs) | — | Editor 手机号验证码/密码登录（正式账号） |
| [BoluoApi/Signature/SignatureLib.cs](../BoluoApi/Signature/SignatureLib.cs) | 135 | `libSignKit` 签名 |
| [BoluoApi/BankUpgradeClient.cs](../BoluoApi/BankUpgradeClient.cs) | 361 | `upgrade/bank` 目录拉取 |
| [BoluoApi/BankDownloader.cs](../BoluoApi/BankDownloader.cs) | 1681 | Bank 文件下载（队列 + MD5 + 落盘 + 载入引擎） |
| [BoluoApi/BankUpgradeBootstrap.cs](../BoluoApi/BankUpgradeBootstrap.cs) | 354 | Bank 流水线编排 |
| [BoluoApi/BankUpgradeModels.cs](../BoluoApi/BankUpgradeModels.cs) | — | DTO |
| [BoluoApi/PresetBankCatalog.cs](../BoluoApi/PresetBankCatalog.cs) | — | 内存里的 bank 目录 |
| [BoluoApi/AudioEnginePaths.cs](../BoluoApi/AudioEnginePaths.cs) | 450 | bank 路径解析 / StreamingAssets 解压 / 旧目录迁移 |
| [BoluoApi/BoluoSession.cs](../BoluoApi/BoluoSession.cs) | — | token / userId 存储 |

### Native

| 文件 | 行数 | 作用 |
|---|---|---|
| [Native/NativeBridgeTransport.cs](../Native/NativeBridgeTransport.cs) | 218 | U→N 传输：`Notify` / `Query` |
| [Native/NativeSession.cs](../Native/NativeSession.cs) | 168 | 宿主身份 / token / 签名环境 |
| [Native/NativeBridgeFacade.cs](../Native/NativeBridgeFacade.cs) | 110 | 业务门面 |
| [Native/NativeBridgeBootstrap.cs](../Native/NativeBridgeBootstrap.cs) | — | 桥初始化 |
| [Native/NativeTimerService.cs](../Native/NativeTimerService.cs) | 100 | 原生计时 → HUD 计时环 |
| [Native/NativeEditorHud.cs](../Native/NativeEditorHud.cs) | 234 | Editor 模拟原生 HUD（`#if UNITY_EDITOR`） |
| [Native/NativeOverlayGate.cs](../Native/NativeOverlayGate.cs) | — | 原生 overlay 状态 |
| [Native/MessageRouter.cs` / `MsgType.cs` / `NativePushReceiver.cs` / `NativeMessages.cs` | — | 消息路由与常量 |

## 关键 API

### 环境与设备

```csharp
// BoluoApiEnvironment
const string PrefsKey = "BoluoApiEnvironmentType";
static void SetCached(string environmentType);
static void ClearCached();                      // 只清内存缓存，不清 PlayerPrefs
static string GetEnvironmentType();             // 内存 → EditorPrefs 覆盖 → 原生 → PlayerPrefs → 编译宏
static string ResolveBoluoBaseUrl();
static string ResolveUserApiBase();

// BoluoDeviceInfo
static string GetIdentifier() / GetChannel() / GetAppId() / GetVersion() / GetOsType() / GetOsVersion()
static string GetBrand() / GetModel() / GetDeviceType() / GetCompanyName()
static string GetOriginEnd() / GetHealthChannel() / GetThirdPartyId()
static bool IsSkyworthGmj();
```

环境 URL：

| 环境 | Boluo base | User base |
|---|---|---|
| product / prerelease | `boluo.boluoapp.com` | `wanos-common.wanos.vip/wanosuser` |
| test | `boluo-test.wanos.vip` | `wanos-common-test3.wanos.vip/wanosuser` |
| dev | `boluo-dev.wanos.vip` | `wanos-common-dev.wanos.vip/wanosuser` |

### 换票三步（`BoluoDeviceLoginClient`）

```
1. boluoRsa       → cipherText
2. boluoDeviceLogin → deviceToken
3. touristLogin   → userToken（游客）
   或 mobileLogin / passwordLogin → userToken（正式账号，Editor 联调）
```

```csharp
static UniTask<bool> EnsureUserTokenAsync();
static UniTask<bool> EnsureUserTokenAsync(bool failFast);
static UniTask<bool> EnsureDeviceTokenAsync();
static UniTask<bool> EnsureDeviceTokenAsync(bool failFast);

// BoluoPhoneLoginClient（Editor 窗口 GameMain/Boluo/编辑器登录 → 手机登录）
static UniTask<LoginResult> RequestVerifyCodeAsync(mobile, nation = "+86");
static UniTask<LoginResult> MobileLoginAsync(mobile, verifyCode, nation = "+86");
static UniTask<LoginResult> PasswordLoginAsync(mobile, password, nation = "+86");
```

请求头：`deviceToken` `userToken` `deviceId` `channel` `version` `healthChannel`
`thirdPartyId` `originEnd` `notCheckSign=0` `sign` `nonceStr`。
票据流程最多重试 `MaxTicketAttempts=3`（间隔 0.4s）。

### 签名（`SignatureLib`）

```csharp
static Func<string,string> NativeSignProvider { get; set; }
static void Init();
static void SetEnvironment(SignEnvType env);           // EnvDev / EnvTest / EnvProduct
static void SignatureGeneration(string jsonStr, out string sign, out string nonceStr);
```

期望响应信封：`{"code":0,"data":{"sign":"…","nonceStr":"…"}}`。

### Bank 目录与下载

```csharp
// BankUpgradeClient
static UniTask<BankUpgradeData> RequestUpgradeBank();
static UniTask<BankUpgradeData> RequestUpgradeBank(bool failFast);
static UniTask<Dictionary<string,string>> BuildSignedHeaders(string bodyJson);

// BankDownloader
enum BankDownloadState { Pending, Downloading, Ready, Failed, Skipped }
enum BankQueueKind : byte { Normal, Startup, User }
class BankDownloadItem { BankName, Url, Md5, SizeKb, Guid, LocalPath, State, Progress, Error, SkipReason, AttemptCount }

static void ResetSession();
static string ResolveLocalPath(string bankName);
static bool IsLocalReady(string bankName);
static bool TryGetActiveItem(string bankName, out BankDownloadItem item);
static void SetStartupParallelSlots(int slots);
static UniTask<IReadOnlyList<BankDownloadItem>> DownloadBanks(
    IList<BankRemoteInfo> banks, bool loadIntoEngine = true,
    Action<IReadOnlyList<BankDownloadItem>> onComplete = null,
    Action<BankDownloadItem> onItemDone = null, Action<BankDownloadItem> onItemProgress = null,
    bool prioritize = false, bool preempt = false);
static UniTask<BankDownloadItem> DownloadBank(
    BankRemoteInfo remote, bool loadIntoEngine = true,
    Action<BankDownloadItem> onDone = null, bool prioritize = false, bool preempt = false);

// BankUpgradeBootstrap
static readonly string[] EssentialLocalBankNames = { "Common_Init", "Common_Music" };
static readonly string[] CriticalBankNames = {
    "Music1_1","Ocean_Calm_Quad","River_ADD","Boluodao_Forest","Boluodao_Grassland",
    "Boluodao_Plateau","Lake_Quad","Campfire","Waterfall_Big_Quad","Waterfall_Small_Quad",
    "Wind","Rain","Rain_Water" };
static void Start(bool loadIntoEngine = false);
static UniTask WaitCatalogAsync();
static UniTask WaitCriticalAsync();
static UniTask<bool> WaitCriticalThenCueAsync(float timeoutSeconds = 45f);
static UniTask EnsureLocalBanksReadyAsync();
static void EnsureEssentialBanksLoaded();
static void ResetSession();
```

### Native 桥

```csharp
// NativeBridgeTransport
static bool IsHybridLikely { get; }
static bool IsHybridAvailable();
static void Notify(int type, int a = 0, int b = 0, int c = 0);
static int Query(int type, int a, int b, int c, string inBlob, out string outBlob);  // 同步阻塞
static string GetHostChannel() / GetHostOriginEnd() / GetHostApiEnvironmentType();
static int GetHostAppId(); static string GetHostDeviceId();

// NativeSession
static bool EnsureDeviceToken(bool forceRefresh = false);
static bool TryGetUserToken(out string token);  // → TryGetUserSession
static bool TryGetUserSession(out string token, out bool isGuest, out string userId);
static bool TryParseUserSessionBlob(blob, out expire, out token, out isGuest, out userId);
static void SetSignEnvironment(int env);
static string GenerateSign(string jsonBody, string fixedNonceStr = null);
```

**User token blob（`SessionGetToken` + `TokenKindUser`）**

| blob | 行为 |
|---|---|
| `token` 或 `expire\ttoken` | 旧格式 → `isGuest=true`，常无 userId → **不写进度盘** |
| `expire\ttoken\tisGuest\tuserId` | 新格式；进度盘按 `userId` 分桶（有 userId 即可读写，不因 `isGuest=1` 拒写） |

**`HostLeaveIsland`（107）**

| `a` | 含义 |
|---|---|
| `0`（默认） | 普通退登 → `BoluoLogout.ExecuteLeave()`，保留 `*.{userId}` 进度桶 |
| `1` | 注销成功 → `BoluoLogout.ExecuteAccountDeleted()`，清空当前桶 |

```csharp
// NativeBridgeFacade
static bool OverlayPresent { get; } static bool BottomIconsHidden { get; } static bool BlocksLocomotion { get; }
static void Init(); static void ResetSession();
static void HudProfile(); static void HudTimer(); static void HudHealth();
static void NotifyIslandReady();
static void PublishNowPlaying(bool playing, int album, int track, string title, string albumName,
                              int positionMs = 0, int durationMs = 0);
static void SetStatusBarHidden(bool hidden);

// NativeTimerService
static int LastRemaining { get; } static int LastTotal { get; } static float FillRatio { get; }
static void Reset(); static void ApplyProgress(int remainingSeconds, int totalSeconds);
static void AttachTo(GameObject host);
```

## 日志 tag

`[DeviceLogin]` `[BankUpgrade]` `[BankDownloader]` `[BankUpgradeBootstrap]` `[AudioEnginePaths]` `[SignatureLib]`
`[NativeSession]` `[IMP]`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 签名失败但请求发出去了 | `SignatureLib` 非主线程调用抛 `InvalidOperationException` 并被吞掉 → 签名空字符串 | 请求必须在主线程构造；看 `[SignatureLib] GenerateSign failed` |
| 票据流程拿不到 token | `SignatureLib.Init()` 首次调用若不在主线程，`_mainThreadId` 记错 | 确保 init 在主线程 |
| deviceToken 一直无效 | `TryParseExpireToken` 解析不出过期时间时写 24h 占位 | 看 `[NativeSession]` 日志 |
| 渠道配置错导致 400 | `channel == "BOLUOCWGMJ"` 且 `originEnd == "boluoapp"` 的组合会报错；应用宝须正写 `BOLUOANDROIDAPPSTORE`（旧拼写少 P 会错） | Editor：`GameMain/Boluo/环境·渠道·版本`（预设对齐渠道.md §3） |
| 环境串了（连到 dev） | 未知环境字符串**默认落 dev**；`PlayerPrefs` 会跨版本残留 | 用 `Boluo.EditorTest.ApiEnvOverride`（Editor） |
| Bank 一直下载不下来 | `DownloadBanks` 与 `DownloadBank` **参数顺序不同**，位置传参会错位 | 一律用命名参数 |
| 同一个 bank 请求两次只回调一次 | `EnqueueOrBump` 会合并已排队项 | 避免重复入队 |
| bank 每次启动都重下 | MD5 缓存文件读写失败只打 warning | 看 `[BankDownloader] md5 cache ... failed` |
| `WaitCriticalThenCueAsync` 返回 true 但没起播 | 返回值反映的是 **Critical 波**，不是「cue 成功」 | 看 `[BankUpgradeBootstrap]` 日志 |
| 混流 HUD 不出现 | `NativeBridgeFacade.Init()` 跑在 `GameEntry.Event` 就绪之前 → 订阅被跳过 | 看 `[IMP]` 日志 |
| Editor 里没有原生 HUD | 需要 `NativeEditorHud`（它用 `OnGUI`，仅 Editor） | Editor 菜单里开 |
| 计时环不动 | `NativeTimerService.AttachTo(null)` 静默失败 | 确认 `BoluoNativeBridge.Instance` 存在 |
| 远端事件把主线程卡住 | `NativeBridgeTransport.Query` 是同步 JNI 调用 | 尽量少在主线程 Query |
