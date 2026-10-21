# YooAsset 资源系统

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

- `YooResourceModule`：**静态门面**，负责默认资源包的初始化、下载、加载协程。
- `YooAssetResourceManager`：实现 GameFramework 的 `IResourceManager`，让 GF 的 `ResourceComponent` 走 YooAsset。
- `Editor/YooAssetCollectorSetup.cs`：一键生成采集规则 / YooAssetSettings / UpdateSetting。
- GameMain 另有 `Sound/WanosSoundResourceManager`——那是**声音专用**的 `IResourceManager`，见 [05-audio.md](05-audio.md)。

## 关键文件

| 文件 | 行数 | 作用 |
|---|---|---|
| [YooAsset/YooResourceModule.cs](../YooAsset/YooResourceModule.cs) | 339 | 包生命周期门面 |
| [YooAsset/YooAssetResourceManager.cs](../YooAsset/YooAssetResourceManager.cs) | 376 | GF `IResourceManager` |
| [YooAsset/YooRemoteServices.cs](../YooAsset/YooRemoteServices.cs) | 29 | 远端地址拼接 |
| [YooAsset/UpdateSetting.cs](../YooAsset/UpdateSetting.cs) | 139 | 远端地址 / 平台名 / 播放模式配置 |
| [YooAsset/YooAssetPlayModePrefs.cs](../YooAsset/YooAssetPlayModePrefs.cs) | 31 | EditorPrefs `EditorPlayMode` |
| [Editor/YooAssetCollectorSetup.cs](../Editor/YooAssetCollectorSetup.cs) | 269 | 采集规则生成 |

## 关键 API

```csharp
// YooResourceModule（static）
const string DefaultPackageName = "DefaultPackage";
static ResourcePackage DefaultPackage { get; }
static bool IsInitialized { get; }
static EPlayMode ActivePlayMode { get; }
static string PackageVersion { get; }
static string LastError { get; }

static void ResetSession();                                   // 会调用 YooAssets.Destroy()
static void InitializeSystem();
static IEnumerator InitDefaultPackageCoroutine(Action<bool,string> onDone = null);
static IEnumerator DownloadDefaultPackageCoroutine(Action<float,string> onProgress, Action<bool,string> onDone);
static IEnumerator LoadAssetCoroutine<T>(string location, Action<T,AssetHandle> onDone) where T : Object;
static IEnumerator InstantiateCoroutine(string location, Action<GameObject,AssetHandle> onDone);
```

```csharp
// YooAssetResourceManager（MonoBehaviour）
static YooAssetResourceManager Instance { get; }
static YooAssetResourceManager Ensure(MonoBehaviour host);
```

`IResourceManager` 里**只有资源加载是真实实现**，其余全是桩：
`LoadScene*` 恒失败（"Scene load via YooGF not used."）、`LoadBinary*` 恒失败、
`HasResourceGroup` 恒 false、`GetAllResourceGroups` 恒空、版本序列化器恒 null。

## 初始化流程（`InitDefaultPackageCoroutine`）

```
1. UpdateSetting.LoadOrDefault() → 播放模式
   Editor: YooAssetPlayModePrefs.GetEditorPlayMode()（EditorPrefs "EditorPlayMode"，默认 EditorSimulateMode）
   Device: EPlayMode.OfflinePlayMode
2. YooAssets.TryGetPackage / CreatePackage / SetDefaultPackage
3. 按模式建参数：
   EditorSimulate → EditorSimulateModeHelper.SimulateBuild("DefaultPackage")
   Offline        → BuildinFileSystemParameters
   Host/Web       → Buildin + Cache 文件系统，YooRemoteServices
4. await initOp.IsDone
5. 【YooAsset 2.x 起】RequestPackageVersionAsync() → UpdatePackageManifestAsync(version)
   （EditorSimulate / Offline 也要走）
6. if (!package.PackageValid) → "Package has no active manifest (collector empty or StreamingAssets missing)."
```

## 采集规则（`YooAssetCollectorSetup.SetupAll`）

生成的采集配置在 `Assets/Editor/AssetBundleCollector/AssetBundleCollectorSetting.asset`，
关键开关：`UniqueBundleName=true`、`EnableAddressable=true`、**`SupportExtensionless=false`**、`LocationToLower=false`。

| Group | 路径 | 地址规则 | 打包 |
|---|---|---|---|
| Configs / Audios / UIRaw / Scenes / Island / Ocean / SkyWeather / Effects / Fonts / DLL | `Assets/AssetRaw/*` | AddressByFileName | PackDirectory / UIRaw=PackSeparately |
| GameUI | `Assets/GameMain/UI` | **AddressByFolderAndFileNameAndExt** | PackSeparately |
| GameEntity | `Assets/GameMain/Entity` | AddressByFolderAndFileName | PackDirectory |
| IslandPresets | `Assets/GameMain/Environment/IslandPresets` | AddressByFileName | PackDirectory |
| DaoYu1MapItem | `Assets/GameMain/Environment/Prefabs/DaoYu1` | AddressByFolderAndFileName | PackSeparately |
| IslandFX | `Assets/GameMain/Environment/FX` | AddressByFileName | PackSeparately |
| IslandFXMats | `.../FX/FX Mats` | AddressByFileName | PackDirectory |
| IslandUnityFire | `.../FX/UnityFire` | AddressByFileName | PackDirectory |

自定义规则在 `Packages/YooAsset/EditorExtension/CustomCollectRules/CustomAdressRule.cs`：

- `AddressByFileNameAndExt` → `fileName.ext`；`.asset` 再追加类型名（`Foo.asset` → `Foo.scriptableobject`）
- `AddressByFolderAndFileNameAndExt` → `dirName_fileNameWithExt`（`Forms_LoadingForm.prefab`）
- `AddressByDaoYu1Relative` → `Prefabs/DaoYu1/` 下的相对路径，去扩展名（`Lvshu/Maple 1`）

## 关键配置

`Assets/GameMain/Settings/Resources/UpdateSetting.asset`：

| 字段 | 默认值 |
|---|---|
| `projectName` | `CustomSRP` |
| `LogicMainDllName` | `Game.dll` |
| `ResDownLoadPath` | `http://127.0.0.1:8081` |
| `FallbackResDownLoadPath` | `http://127.0.0.1:8082` |

`GetResDownLoadPath()` = `Path.Combine(ResDownLoadPath, projectName, GetPlatformName())`。

## 日志 tag

- `[YooAsset]`：包初始化、SimulateBuild、版本请求、下载、`Invalid location`
- `[YooGF]`：GF 侧 `LoadAsset begin/ok/fail`、`Package not ready for:`、`Invalid location:`、`Success callback threw`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| Editor 能加载、真机加载失败 | Editor 走 `EditorResourceHelper`（路径查找），真机走 YooAsset 地址。两者规则不同 | 对照上表确认地址规则；UI 资源在真机是 `Forms_LoadingForm.prefab` 这种**短地址**，不是 `Assets/...` 路径 |
| `[YooAsset] Invalid location: X` | 地址拼错 / 大小写不符 / 带扩展名 | `SupportExtensionless=false`，`LocationToLower=false` |
| `Package has no active manifest` | 采集为空或 StreamingAssets 缺包 | 重新跑 `Game/YooAsset/Setup AssetRaw + Collector + Settings` + 构建 |
| `SimulateBuild failed` | Editor 模拟模式没构建 | 看日志里的具体错误 |
| 资源卸载了但内存没降 | `UnloadAsset` 必须传**同一个 asset 对象**；`YooResourceModule.LoadAssetCoroutine` 拿到的 handle 不在 `_handles` 里 | 调用方自己 `handle.Release()`；注意 `LoadAssetCoroutine` 失败时也会把 handle 传出来，别漏 |
| `ResetSession` 后访问包抛异常 | `ResetSession` 调了 `YooAssets.Destroy()`，其它系统还持有 `ResourcePackage` 引用 | 复位顺序放在所有使用者之前 |
| 改了 UpdateSetting 不生效 | `LoadOrDefault()` 有静态缓存 | 需要 domain reload |
| URL 出现双斜杠 | `YooRemoteServices` 不做尾斜杠归一化 | 远端地址不要带尾斜杠 |
