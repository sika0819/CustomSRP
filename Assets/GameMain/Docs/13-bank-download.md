# Bank 下载策略（同意链 / 队列 / 三入口 / 红点 / 自动进场）

模块文档索引见 [00-overview.md](00-overview.md)。底层 HTTP / MD5 / 路径细节见 [09-boluoapi-native.md](09-boluoapi-native.md)；会话离岛暂停见 [12-session-lifecycle.md](12-session-lifecycle.md)；UI 红点与卡片态见 [06-ui.md](06-ui.md)。

本文只写**策略**：谁触发、先过哪些门、怎么排队、完成后怎么跳 / 怎么画红点。

---

## 1. 架构总览

三套触发入口共用同一条用户流水线与同一套磁盘 / 队列：

| 入口 | 触发 | Flow 参数要点 | 完成后 |
|---|---|---|---|
| **区域门** `SceneBankGateSpawner` | 走进 Trigger | `ShowRequestFirst` + 锁移动 + **有**进度窗；`prioritize=true` `preempt=true` | 开固墙 / toast 失败 |
| **预设** `PresetForm` | 点未就绪卡 | **无**进度窗（卡片环）；`prioritize=false` `preempt=true` | pending 命中则自动进预设；否则红点 |
| **音乐** `MusicForm` | 点未就绪曲 | **有**进度窗；`prioritize=true` `preempt=true` | pending 命中则解码后播 |

另有一条**静默启动流水线** `BankUpgradeBootstrap`（Critical / Rest），不走用户流量窗。

```mermaid
flowchart TB
  subgraph entry [用户入口]
    Gate[区域门 SceneBankGate]
    Preset[预设 PresetForm]
    Music[音乐 MusicForm]
  end

  subgraph flow [用户统一流水线]
    Filter[FilterPending<br/>跳过 IsLocalReady]
    Consent[DownloadConsentSession<br/>无网 / 存储 / 流量]
    UFlow[UserBankDownloadFlow]
    Prog[可选 DownloadProgress 窗]
  end

  subgraph core [传输与落盘]
    DL[BankDownloader<br/>User / Startup / Normal 队列]
    Disk[(BoluoAudioBanks/*.wsbk)]
    Cat[PresetBankCatalog]
  end

  subgraph silent [静默启动]
    Boot[BankUpgradeBootstrap<br/>Critical + Rest]
  end

  Gate --> UFlow
  Preset --> UFlow
  Music --> UFlow
  UFlow --> Filter --> Consent --> Prog --> DL
  Boot --> DL
  Cat --> DL
  DL --> Disk
```

---

## 2. 分层职责

```mermaid
flowchart LR
  UI[Preset / Music / Gate UI] --> Intent[PendingDownloadIntent]
  UI --> Flow[UserBankDownloadFlow]
  Flow --> Consent[DownloadConsentSession]
  Consent --> Net[DownloadNetworkGate 体积/离线/存储]
  Consent --> Cell[DownloadCellularGate 蜂窝闸]
  Flow --> DL[BankDownloader]
  DL --> Paths[AudioEnginePaths]
  DL --> Cat[PresetBankCatalog]
  UI --> Ready[IsLocalReady / MarkLocalReady]
  UI --> Red[DownloadedUnplayedManager]
```

| 层 | 类型 | 职责 |
|---|---|---|
| 意图 | `PendingDownloadIntent` | 最后一次用户点的「下完做什么」：进预设 / 播音乐；两者互斥覆盖 |
| 流水线 | `UserBankDownloadFlow` | FilterPending → 同意链 →（可选进度窗）→ DownloadBanks → onBanksDone → Revoke |
| 同意 | `DownloadConsentSession` | **唯一**弹窗所有者；用户 `EnsureReady` 才弹流量窗 |
| 蜂窝 | `DownloadCellularGate` | WifiOnly / AllowAlways；批次结束 `RevokeCellularAllow` |
| 门禁计算 | `DownloadNetworkGate` | 离线 / 存储×2 / PendingKilobytes 文案 |
| 传输 | `BankDownloader` | 三队列、HTTP、MD5、落盘、载入引擎、`IsLocalReady` 缓存 |
| 目录 | `PresetBankCatalog` + `BankUpgradeClient` | 服务端 upgrade/bank → url/size/md5/version |
| 红点 | `DownloadedUnplayedManager` | 未播 + 24h 首次展示窗；内存始终可判，落盘走账号门控 |

---

## 3. 用户下载主流程

```mermaid
flowchart TD
  Start([UserBankDownloadFlow.Start]) --> Snap[Snapshot banks]
  Snap --> FP{FilterPending<br/>去掉 IsLocalReady}
  FP -->|pending 空| ReadyPath[CompleteAlreadyReady<br/>onDownloadStarting → onBanksDone → onComplete]
  FP -->|有待下| ER[DownloadConsentSession.EnsureReady]
  ER --> Offline{离线?}
  Offline -->|是| Toast1[无网 toast → OnCancel]
  Offline -->|否| Stor{可用空间 ≥ 需求×2?}
  Stor -->|否| Toast2[存储不足 → OnCancel]
  Stor -->|是| Cell{NeedsMobileDataPrompt?}
  Cell -->|Wi‑Fi| Go[onReady → onDownloadStarting]
  Cell -->|蜂窝| MD[流量确认窗]
  MD -->|继续| Allow[UserAllowCellularAlways] --> Go
  MD -->|切 Wi‑Fi| Wifi[UserRequireWifiOnly → OnCancel]
  Go --> Busy{_busy?}
  Busy -->|否| DL1[开进度窗可选 → DownloadBanks]
  Busy -->|是| Merge[MergeIntoBusyDownload<br/>并入队列不 Revoke]
  DL1 --> Done[onBanksDone → MarkResultLocalReady]
  Merge --> Done
  Done --> Fin[关进度窗 → RevokeCellularAllow → onComplete]
  ReadyPath --> End([结束])
  Fin --> End
  Toast1 --> End
  Toast2 --> End
  Wifi --> End
```

### 策略要点

1. **已落盘不进同意链 / HTTP**：`FilterPending` 用 `BankDownloader.IsLocalReady`；全齐走 `CompleteAlreadyReady`（仍调 `onDownloadStarting` / `onBanksDone`，以便预设自动进场、音乐开播）。
2. **流量窗只服务用户点击**：Rest / 传输层 `NotifyNeedCellularConsent` **只记账不开窗**。
3. **每次 EnsureReady 先 `RevokeCellularAllow`**：上一批发完的 AllowAlways 不跨批次复用。
4. **`_busy` 重叠并入队列**：第二路不丢单、不开第二进度窗、不提前 Revoke；由持有进度窗的那一路 finally 收尾。
5. **完成先 `onBanksDone` 再写正向 ready 缓存**：禁止完成后全量 `InvalidateLocalReadyCache`（多预设并下会把已完成卡刷回「未下载」）。

---

## 4. 时序：单次用户下载（预设为例）

```mermaid
sequenceDiagram
  actor User
  participant PF as PresetForm
  participant Intent as PendingDownloadIntent
  participant Flow as UserBankDownloadFlow
  participant Consent as DownloadConsentSession
  participant Cell as DownloadCellularGate
  participant DL as BankDownloader
  participant Disk as Disk

  User->>PF: 点击未就绪预设
  PF->>Flow: Start(banks, SuppressProgress, preempt)
  Flow->>Flow: FilterPending
  alt 全已 IsLocalReady
    Flow->>PF: onDownloadStarting → SetPreset
    Flow->>PF: onBanksDone → 自动进场或红点
  else 有待下
    Flow->>Consent: EnsureReady(pending)
    Consent->>Cell: RevokeCellularAllow
    opt 蜂窝
      Consent->>User: 流量窗
      User->>Cell: UserAllowCellularAlways
    end
    Consent->>Flow: onReady
    Flow->>PF: onDownloadStarting → SetPreset + 卡片转圈
    Flow->>DL: DownloadBanks(pending, preempt=User)
    DL->>Disk: HTTP / promote .tmp → .wsbk
    DL-->>Flow: await 完成
    Flow->>PF: onBanksDone
    PF->>PF: WaitPresetBanksSettled
    alt Intent 仍指向本预设
      PF->>PF: MarkHeard + RequestTransition
    else 被其它点击覆盖
      PF->>PF: Refresh → RedDot（未跳转）
    end
    Flow->>Cell: RevokeCellularAllow
    Flow->>PF: onComplete → PopSaveSuppress
  end
```

---

## 5. 时序：多预设并下

```mermaid
sequenceDiagram
  participant A as Preset A
  participant B as Preset B
  participant Flow as UserBankDownloadFlow
  participant DL as BankDownloader
  participant Intent as PendingDownloadIntent

  A->>Flow: Start(A banks)
  Flow->>Intent: SetPreset(A)
  Flow->>DL: DownloadBanks A（_busy=true）
  B->>Flow: Start(B banks)
  Flow->>Intent: SetPreset(B)（覆盖 A）
  Flow->>DL: MergeIntoBusyDownload(B pending)
  Note over DL: 同名 bank EnqueueOrBump 去重<br/>多路 OnDone 回调
  DL-->>Flow: A 批次 await 返回
  Flow->>A: onBanksDone(A)
  Note over A: Wait 直到 A 的 banks 齐<br/>Intent≠A → 不跳转 → 红点 + MarkLocalReady
  DL-->>Flow: B 批次 await 返回
  Flow->>B: onBanksDone(B)
  Note over B: Intent==B → MarkHeard → 自动进场（不显示红点）
```

### 策略要点

| 规则 | 说明 |
|---|---|
| Intent 最后点击优先 | 只有 `PendingDownloadIntent.IsPresetMatch` 才自动 `RequestTransition` |
| 未跳转保持完成态 | banks 齐 → `MarkLocalReady` + `ShouldShowRedDot` → `statedownloadsuccess` |
| 已跳转无红点 | 进场前 `MarkHeard` / `MarkPlayed` |
| 未齐且仍 busy | **不清** `_userTriggered`，继续转圈；勿因 2s 超时刷回未下载 |
| Merge 不 Revoke | 第一路 finally 才 `RevokeCellularAllow` + `_busy=false` |

---

## 6. 同意链状态机

```mermaid
stateDiagram-v2
  [*] --> Idle
  Idle --> RequestPrompt: ShowRequestFirst（区域门）
  RequestPrompt --> Checking: 立即下载
  Idle --> Checking: EnsureReady 直跑门禁
  Checking --> OfflineToast: 离线
  Checking --> StorageBlock: 空间不足
  Checking --> MobileConfirm: 蜂窝需确认
  Checking --> [*]: Wi‑Fi / 已开闸 → FireReady
  MobileConfirm --> [*]: 继续 → AllowAlways → FireReady
  MobileConfirm --> Idle: 切 Wi‑Fi / 关闭
  OfflineToast --> Idle
  StorageBlock --> Idle
```

| API | 谁调用 | 是否弹流量窗 |
|---|---|---|
| `EnsureReady` | Flow（用户点下） | 蜂窝时弹 |
| `NotifyNeedCellularConsent` | Rest / 传输打断 | **永不弹**，只合并 bank |
| `ResetForLeaveIsland` | 离岛 / Spawner 重建 | 清会话 |

---

## 7. BankDownloader 队列与就绪判定

### 7.1 三队列优先级

```mermaid
flowchart TB
  Pump[Runner 泵] --> U{UserQueue 非空?}
  U -->|是| UserSlot[User 槽 preempt<br/>插队头 / 可打断非 User]
  U -->|否| S{StartupQueue?}
  S -->|是| StartSlot[Startup 并行槽≤3]
  S -->|否| N[Normal 背景]
```

| `DownloadBanks` 参数 | 队列 | 典型调用方 |
|---|---|---|
| `preempt=true` | **User** | 区域门、音乐、预设（点下载） |
| `prioritize=true` 且非 preempt | **Startup** | Bootstrap Critical |
| 两者 false | **Normal** | Bootstrap Rest；预设 `prioritize=false` 但 `preempt=true` 仍走 User |

预设：`prioritize: false, preempt: true` → **User 队列**（插队，但不把 Startup 语义用在卡片上）。

### 7.2 `IsLocalReady` 策略

```mermaid
flowchart TD
  Q[IsLocalReady / LocalSatisfiesCatalog] --> Active{Entries 有该项?}
  Active -->|Ready/Skipped| T[true]
  Active -->|Downloading| Probe1[Probe 盘上完整文件?]
  Probe1 -->|是| T
  Probe1 -->|否| F[false + 负缓存 TTL 1s]
  Active -->|无| Cache{LocalReadyCache?}
  Cache -->|Ready=true| T
  Cache -->|负缓存未过期| F
  Cache -->|无/过期| Probe2[Probe：persistent .wsbk<br/>或 .wb / Streaming 回退]
  Probe2 -->|满足 size/version| Mark[Cache Ready=∞] --> T
  Probe2 -->|否| Neg[Cache 负 TTL] --> F
```

- **Downloading 不盖住完整本地文件**：升级重下时 UI 仍可显示已下载。
- **残缺 `.wsbk` 回退**其它路径，避免半截文件永久「未下载」。
- **`MarkLocalReady`**：下载完成确认后写正向缓存，防止 Entries.Remove / 误 Invalidate 闪回未下载。

### 7.3 落盘与校验（摘要）

1. HTTP → `stem.wsbk.tmp`
2. size 对齐 catalog → promote 为 `.wsbk`
3. 可选 MD5（`BankMd5Cache`）；Streaming `version` 一致可跳过重下
4. `loadIntoEngine` → 载入 Wanos；完成后 `Entries.Remove`

磁盘根：`AudioEnginePaths.PersistentBankPath`（`BoluoAudioBanks`），与音乐 / 区域 / 预设**同目录同 stem**。

---

## 8. 三入口策略对照

```mermaid
flowchart LR
  subgraph gate [区域门]
    G1[Trigger 进入] --> G2[申请窗 前方区域未下载]
    G2 --> G3[同意链 + 进度窗]
    G3 --> G4[开固墙 / 失败 toast]
  end

  subgraph preset [预设]
    P1[点卡片] --> P2[无进度窗 卡片环]
    P2 --> P3{Intent 命中?}
    P3 -->|是| P4[MarkHeard + 切预设]
    P3 -->|否| P5[红点 已下载未播]
  end

  subgraph music [音乐]
    M1[点曲目] --> M2[进度窗]
    M2 --> M3{Intent 命中?}
    M3 -->|是| M4[解码后播放]
    M3 -->|否| M5[Succeeded 红点]
  end
```

| 维度 | 区域门 | 预设 | 音乐 |
|---|---|---|---|
| 申请窗 | 有（`ShowRequestFirst`） | 无 | 无 |
| 进度窗 | 有 | **无**（`SuppressProgressDialog`） | 有 |
| 锁移动 | 有 + 后退 1m | 无 | 无 |
| 保存抑制 | — | `IslandPlayerPosSave` Push/Pop | 同左 |
| 自动后续 | 开门 | 切预设 | 播放 |
| UI 就绪优先 | `BanksSatisfied` | `IsLocalReady` → RedDot/AllReady/Playing | `IsLocalReady` → Succeeded/Ready |

---

## 9. 预设卡片状态机

```mermaid
stateDiagram-v2
  [*] --> NotDownloaded: 缺 bank
  NotDownloaded --> Downloading: 用户点击且排队/传输中
  Downloading --> Failed: Failed / 仅无 Url
  Failed --> Downloading: 再点重试
  Downloading --> AllReady: 全部 IsLocalReady
  NotDownloaded --> AllReady: 开窗时盘上已齐
  AllReady --> RedDot: ShouldShowRedDot
  AllReady --> Playing: 当前预设 SchemeState=2
  RedDot --> Playing: 点击或自动进场 MarkHeard
  Playing --> AllReady: 切走其它预设且已听过
```

| 状态 | 图标 | 条件 |
|---|---|---|
| NotDownloaded | `statewaitdownload` | 未齐且非本卡用户触发下载 |
| Downloading | 进度环 | `_userTriggered` 且仍有 Pending/Downloading |
| Failed | `stateerror` | Failed 或仅无 Url |
| RedDot | `statedownloadsuccess`（三角+红点） | 全齐 + 未 MarkPlayed + 24h 内 |
| AllReady | `stateready` | 全齐但不显示红点（已听 / 保护过期） |
| Playing | equalizer | `CurrentId` 且 `SchemeState==2` |

红点：`DownloadedUnplayedManager` — **内存始终可显示**；`Persist*` 仍受 `AllowsAccountProgressDisk` 门控（游客不落盘，会话内仍有红点）。

---

## 10. 静默启动（Bootstrap）与用户下载关系

```mermaid
sequenceDiagram
  participant Login as ProcedureGuestLogin
  participant Boot as BankUpgradeBootstrap
  participant DL as BankDownloader
  participant Cell as DownloadCellularGate
  participant User as 用户点预设/音乐/门

  Login->>Boot: Start(userToken ready)
  Boot->>DL: Critical（Startup 队列）
  Boot->>DL: Rest（Normal，可被蜂窝打断）
  Note over Boot,Cell: Rest 蜂窝 → NotifyNeedCellularConsent<br/>不开流量窗
  User->>DL: User 队列 preempt
  Note over DL: User 优先；可 Abort 非 User 活跃槽
```

| 波次 | 内容 | 队列 | 流量窗 |
|---|---|---|---|
| Essential 本地 | Common_Init / Common_Music | 不解压 HTTP | — |
| Critical | Music1_1、区域氛围、风雨等 | Startup | 不弹；缺网可 defer |
| Rest | catalog 其余 | Normal | 不弹；记账等用户点下 |

用户点下后与 Rest **共享同一 `BankDownloader` / 同一磁盘**；`FilterPending` 跳过已 Critical/Rest/音乐下过的 stem。

---

## 11. 离岛 / 会话

| 事件 | 下载侧动作 |
|---|---|
| 离岛 | `BankDownloader.PauseForLeave`（Abort 活跃，保留 .tmp）；`PendingDownloadIntent.Clear`；`DownloadConsentSession.ResetForLeaveIsland` |
| 换票后再进 | `ResumeAfterLeave`；`BankUpgradeBootstrap.InvalidateAfterLeave` 后可再 `Start` |
| 退登 | Clear Intent；`DownloadedUnplayedManager.ResetSession` |
| Flow 批次结束 | `RevokeCellularAllow`（不 Abort 队列） |

详见 [12-session-lifecycle.md](12-session-lifecycle.md)。

---

## 12. 关键文件速查

| 文件 | 角色 |
|---|---|
| [Services/UserBankDownloadFlow.cs](../Services/UserBankDownloadFlow.cs) | 用户统一流水线 |
| [Services/DownloadConsentSession.cs](../Services/DownloadConsentSession.cs) | 同意链 / 弹窗唯一所有者 |
| [Services/DownloadUiOptions.cs](../Services/DownloadUiOptions.cs) | 申请窗 / 锁移动 / 抑进度窗 |
| [Services/DownloadNetworkGate.cs](../Services/DownloadNetworkGate.cs) | 离线 / 存储 / 体积 |
| [Services/PendingDownloadIntent.cs](../Services/PendingDownloadIntent.cs) | 下完进预设或播音乐 |
| [BoluoApi/DownloadCellularGate.cs](../BoluoApi/DownloadCellularGate.cs) | 蜂窝闸 |
| [BoluoApi/BankDownloader.cs](../BoluoApi/BankDownloader.cs) | 队列 + HTTP + IsLocalReady |
| [BoluoApi/BankUpgradeBootstrap.cs](../BoluoApi/BankUpgradeBootstrap.cs) | Critical / Rest |
| [BoluoApi/PresetBankCatalog.cs](../BoluoApi/PresetBankCatalog.cs) | 内存 catalog |
| [UI/Forms/PresetForm.cs](../UI/Forms/PresetForm.cs) | 预设下载 / 红点 / 自动进场 |
| [UI/Forms/MusicForm.cs](../UI/Forms/MusicForm.cs) | 音乐下载 / 红点 / 下完播 |
| [Environment/Runtime/SceneBankGateSpawner.cs](../Environment/Runtime/SceneBankGateSpawner.cs) | 区域门下载 |
| [Persistence/DownloadedUnplayedManager.cs](../Persistence/DownloadedUnplayedManager.cs) | 红点集合 |

---

## 13. 日志 tag

`[UserBankDownloadFlow]` `[DownloadCellularGate]` `[BankDownloader]` `[BankUpgrade]` `[BankUpgradeBootstrap]` `[SceneBankGate]` `[PresetForm]` `[MusicForm]` `[DownloadedUnplayed]`

---

## 14. 已知坑（下载专表）

| 现象 | 原因 | 处理 |
|---|---|---|
| 多预设下完闪回未下载 | 完成回调全量 Invalidate / 未齐就清 `_userTriggered` | `MarkLocalReady`；busy 时保持转圈；见 §5 |
| 下完不自动进预设 | Intent 被其它点击覆盖；或 `AreBanksLocallyReady` 偶发 false | 只最后点击进场；`WaitPresetBanksSettled` |
| 已下 bank 仍显示未下载 | 队列 Pending 盖住磁盘；Downloading 强 false | disk-first + Downloading 时 Probe |
| 游客无红点 | 旧逻辑用 `AllowsAccountProgressDisk` 禁显示 | 内存可显示；仅落盘门控 |
| 换票后银行不再下 | Bootstrap `_started` 早置位 | [12](12-session-lifecycle.md) |
| `DownloadBanks` 参数错位 | 与 `DownloadBank` 顺序不同 | **命名参数** |
| Rest 弹流量窗 | 误调 `EnsureReady` | Rest 只用 `NotifyNeedCellularConsent` |

排障总表见 [10-bug-index.md](10-bug-index.md)。
