# 输入 / 相机 / 实体

模块文档索引见 [00-overview.md](00-overview.md)。

## 职责与边界

| 文件 | 行数 | 作用 |
|---|---|---|
| [Input/LocomotionInput.cs](../Input/LocomotionInput.cs) | 58 | 静态信箱：HUD → 玩家 |
| [Input/IslandLocomotionInput.cs](../Input/IslandLocomotionInput.cs) | 118 | 摇杆 / WASD → 信箱；二指视角手势 |
| [Input/IslandVirtualJoystick.cs](../Input/IslandVirtualJoystick.cs) | 389 | 代码构建的 UITK 虚拟摇杆 |
| [Input/HudOverlayGate.cs](../Input/HudOverlayGate.cs) | 27 | 「有弹窗」全局门 |
| [Entity/IslandJoystickCamera.cs](../Entity/IslandJoystickCamera.cs) | 875 | 第一人称相机 rig |
| [Entity/IslandCameraSrpSetup.cs](../Entity/IslandCameraSrpSetup.cs) | 184 | CRP 相机反射桥 |
| [Entity/IslandSpawnPose.cs](../Entity/IslandSpawnPose.cs) | 73 | 默认出生位姿 |
| [Entity/IslandSpawnPoseAsset.cs](../Entity/IslandSpawnPoseAsset.cs) | — | 出生位姿资产 |

## 关键 API

### LocomotionInput（静态信箱）

```csharp
static void Set(Vector2 dir, float speedRatio);
static void SetLook(Vector2 pixelDelta);       // 累加
static void Clear();                           // 不清 look
static void ClearAll();                        // 全清（热重载必须用这个）
static bool ConsumeLook(out Vector2 pixelDelta);
static bool Consume(out Vector2 dir, out float speedRatio);
```

### IslandLocomotionInput

```csharp
bool IsMoveInputActive { get; }
bool IsJoystickActive { get; }
void Bind(IslandVirtualJoystick joystick);
void ForceEnd();
void ApplyLayoutScale();
void Tick();      // 每帧
```

`Tick()` 顺序：
```
canMove = !HudOverlayGate.BlocksLocomotion() && !(GameEntry.Presets?.IsTransitioning ?? false)
→ 被阻挡：LocomotionInput.ClearAll() 并 return
→ TickSecondFingerLook()
→ 摇杆方向 → LocomotionInput.Set(dir, speed)
→ Editor/Standalone：WASD 覆盖
```

### IslandVirtualJoystick

```csharp
static IslandVirtualJoystick Attach(VisualElement formRoot);
bool IsActive { get; }
void ApplyLayoutScale();
void TickFrame(bool allowBegin);
bool TryGetMove(out Vector2 direction, out float speedRatio);
void ForceEnd();
bool OwnsPointer(int fingerId);
void Dispose();
```

常量：`DeadZone=14`、`RingDesign=444`、`KnobDesign=180`、`RimMargin=18`。
摇杆用**名字黑名单**排除 HUD 控件（`weatherBackground` / `musicBackground` …），
改名或新增 HUD 图标可能被摇杆吃掉点击。

### HudOverlayGate

```csharp
static bool HasPopup();                 // Panel 或 Dialog 组非空
static bool BlocksLocomotion();         // HasPopup() || NativeOverlayGate.BlocksLocomotion
```

### IslandJoystickCamera

```csharp
static IslandJoystickCamera Current { get; }

void ApplySpawnPose();
void Teleport(Vector3 worldPos, float yawDegrees);
void ApplyPreset(IslandPresetAsset preset);
void ApplyPresetForReveal(IslandPresetAsset preset);
void BeginExitDolly();
void SampleExitDolly(float t01);
void SampleEnterDolly(float t01);
void NudgeExitWalk(float dt);
void EndTransitionDrive();
bool IsHeightTransitioning { get; }
void RefreshNearbyColliders();
```

Inspector 默认值：`MoveSpeed=9`、`StickYawSpeed=70`、`Pitch=12`、`EyeHeight=3`、`Gravity=28`、
`BodyRadius=1.1`、`HeadClearance=0.5`、`SlopeLimit=50`、`StepOffset=0.35`、
`HeightTransitionDuration=0.5`、`UseNavMeshClamp=true`、`BlockWaterWalking=true`。

每帧（`LateUpdate`）顺序：
```
ApplyAspectFov
→ 若 _transitionDriven（预设过场）只更新旋转
→ ConsumeLookInput（yaw += look.x × 0.11）/ ConsumeMoveInput（MoveTowards 平滑，InputSmooth=7）
→ _holdPresetFraming 时只转不位移
→ EnsureBody / ApplyRenderScale / TickHeightTransition / ComputeMotion / IntegrateVertical / MoveBody
```

- 移动被切成**最多 8 个子步**，避免高速穿 `MeshCollider`。
- 地面采样优先 `EnvGroundFit.TrySnapY`（高度图），否则 `Physics.RaycastNonAlloc`（24 命中，取法线朝上且最低的一个）。
- 渲染缩放（`IslandCameraSrpSetup` ← `DeviceGraphicsPolicy.Profile`）：
  全档 `弹窗/沉浸 0.375` / `走动 0.375` / `静止 0.5`；桌面 High 为 Inherit CRP。分档见 [12](12-device-graphics-tiers.md)。
- 水面禁行：仅海面（`oceanY - 0.25` 以下）。河可走；近河（XZ 5m / Y ±4m）跳过 NavMesh 钳位。
- 悬崖 / 大石顶：下方 80m 内有可站立落点（非海）时允许走出蓝面往下跳；下落途中不把 XZ 钳回崖边。

### 相机 → SRP 桥（`IslandCameraSrpSetup`）

```csharp
// Default/Walking/ImmersiveRenderScale ← DeviceGraphicsPolicy.Profile
static float DefaultRenderScale => …StandRenderScale;
static float WalkingRenderScale => …WalkRenderScale;
static float ImmersiveRenderScale => …ImmersiveRenderScale;
static float PopupRenderScale => ImmersiveRenderScale;

static void ApplyForIsland(Camera camera);
static Component EnsureCustomRenderPipelineCamera(GameObject cameraGo);
static void SetRenderScale(GameObject cameraGo, float scale);
```

反射目标：`Type.GetType("CustomSRP.CustomRenderPipelineCamera, Assembly-CSharp")`，
设置 `copyColor/copyDepth/postFXSettings/renderScaleMode`（桌面 Inherit / 手机 Override）`/renderScale/allowFXAA/keepAlpha/maskLights`。

## 日志 tag

`[IslandCamera]` `[IslandSpawnPose]` `[IslandIdleImmersive]` `[IslandNavMesh]`

## 已知坑与排查手法

| 现象 | 原因 | 处理 |
|---|---|---|
| 停手后视角还在转 | `LocomotionInput.SetLook` 是累加，相机没消费（被禁用） | 相机恢复时会一次性应用；`ClearAll()` 可清 |
| 摇杆在死区内也算「在移动」 | `TryGetMove` 死区内仍返回 true（速度 0） | 消费方按 `speedRatio` 判断 |
| 点 HUD 图标会同时拉出摇杆 | 摇杆用名字黑名单排除 HUD | 改名后需同步黑名单 |
| 相机卡在地面里 | 地形采样用 AABB 最近点 + 法线朝上筛选；大 mesh 可能取不到 | 看 `GroundNormalMinY=0.45` |
| 走快了穿过石头 | 已用子步拆分；仍有漏判时检查石头碰撞体是否被 LOD 关掉 | 见 [07-environment.md](07-environment.md) |
| 相机 render scale 不变 | 反射找不到 `CustomRenderPipelineCamera`（SRP 改程序集名） | 日志 `[IslandCamera] CustomRenderPipelineCamera type not found` |
| 竖屏 FOV 不对 | `ApplyAspectFov` 先 `ResetAspect()` 再设 FOV；`_authoredVerticalFov` 首次调用时捕获 | 若首次调用前别的系统改过 FOV，基准就错 |
| 预设后眼睛高度变怪 | `ApplyPreset` 用预设的 `PlayerHeight` 覆盖 `EyeHeight`，走出预设才恢复 | 已知设计 |
| 传送后看不到东西 | `ClampEyeToNavMesh` / `ClampEyeOutOfWater` 会把眼位拉回合法区 | 正常 |
| `LocomotionInput` 残留 | 场景重载后静态信箱不清 | `GameEntry.Awake` 调 `ClearAll()`（不是 `Clear()`） |
| 每帧 GC | `HudOverlayGate.GroupHasForms` 每次分配数组 | 已记录的优化点 |
