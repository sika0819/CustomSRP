using System;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using Game.Native;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Free island camera driven by <see cref="LocomotionInput"/>.
    /// First-person look; invisible <see cref="CharacterController"/> walks on ground and blocks rocks.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class IslandJoystickCamera : MonoBehaviour
    {
        // ─── Inspector ─────────────────────────────────────────────────────────────

        [Header("Locomotion")]
        public float MoveSpeed = 9f;
        public float StickYawSpeed = 70f;
        /// <summary>行走俯仰。平视 0°；预设站定时另有俯仰，开走后插值回这里。</summary>
        public float Pitch = 0f;
        /// <summary>
        /// Eye above ground while walking. Preset <c>PlayerHeight</c> is the Boluo capsule
        /// height (center at the origin), not this offset.
        /// </summary>
        public float EyeHeight = 3;
        public float Gravity = 28f;
        public float TerrainProbeUp = 80f;
        public float TerrainProbeDown = 200f;
        public LayerMask GroundMask = ~0;

        [Header("Body collision")]
        [Tooltip("Capsule radius for rock/cliff blocking. 0 disables CharacterController collision.")]
        public float BodyRadius = 1.1f;
        [Tooltip("Extra capsule height above the eye point.")]
        public float HeadClearance = 0.5f;
        public float SlopeLimit = 50f;
        public float StepOffset = 0.35f;

        [Header("Height transition")]
        [Tooltip("Duration (seconds) for smooth camera height transition when releasing preset or walking. 0 = instant.")]
        public float HeightTransitionDuration = 0.5f;

        [Header("Water")]
        [Tooltip("Reject moves onto the seabed (terrain at or below the ocean surface). Rivers stay walkable.")]
        public bool BlockWaterWalking = true;

        // ─── Constants ─────────────────────────────────────────────────────────────

        /// <summary>Stick magnitude above this counts as walking for render-scale drop.</summary>
        const float WalkRenderScaleThreshold = 0.05f;
        const float InputSmooth = 10f;
        const float GroundStickSpeed = 2f;
        /// <summary>Degrees per pixel at <see cref="RefLookWidth"/> (portrait design 1125).</summary>
        const float LookSensitivity = 0.11f;
        /// <summary>Portrait design width; wider screens scale look down so full-width swipes feel similar.</summary>
        const float RefLookWidth = 1125f;
        const float GroundNormalMinY = 0.45f;
        /// <summary>
        /// 脚底下沉下限相对海平面的容差（防掉进海底）。禁走不靠这个。
        /// </summary>
        const float OceanBelowSurfaceBan = 0.25f;
        /// <summary>
        /// 高度图相对海平面的禁走净空。0 = 不高于海平面即禁（平坦海底≈海面也禁）。
        /// 禁止复用 <see cref="AmbienceZoneCatalog.OceanLandClearanceMeters"/>(0.4)：
        /// 那是听感岸线，会把整段湿岸/海岸判成海里，表现为「站岸上走不了」。
        /// </summary>
        const float OceanWalkMinAboveSea = 0f;
        /// <summary>眼睛离地形矩形边的最小内缩，避免胶囊探出岛外踩上海面碰撞。</summary>
        const float TerrainEdgeMargin = 0.75f;
        /// <summary>
        /// 脚相对甲板顶面的下沉容差。CharacterController 贴薄 Box 会抖；过紧会把站桥判成海里。
        /// </summary>
        const float DeckFootSlop = 1.0f;
        /// <summary>刚离开命名甲板后的放行秒数，盖住桥段缝 / 边缘单帧 miss。</summary>
        const float NamedDeckGraceSeconds = 0.4f;
        /// <summary>宽限期内相对上一甲板脚点的最大水平距离（× BodyRadius）。</summary>
        const float NamedDeckGraceRadiusFactor = 2.5f;

        static readonly RaycastHit[] GroundHits = new RaycastHit[24];
        /// <summary>桥面 footprint：中心 + 四向，避免单点射线落进桥段缝。</summary>
        static readonly Vector2[] NamedDeckProbeOffsets =
        {
            Vector2.zero,
            new Vector2(1f, 0f),
            new Vector2(-1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(0f, -1f),
        };

        // ─── State ─────────────────────────────────────────────────────────────────

        public static IslandJoystickCamera Current { get; private set; }

        Camera _view;

        /// <summary>同物体上的 Camera。OnEnable 缓存，热路径不要再 GetComponent。</summary>
        public Camera View
        {
            get
            {
                if (_view == null)
                    _view = GetComponent<Camera>();
                return _view;
            }
        }

        /// <summary>
        /// 岛上行走相机。还没进岛时退回 <see cref="Camera.main"/>。
        /// 热路径用这个，避免每帧再按 MainCamera 标签查找。
        /// </summary>
        public static Camera ActiveView
        {
            get
            {
                IslandJoystickCamera joy = Current;
                Camera view = joy != null ? joy.View : null;
                if (view != null && view.isActiveAndEnabled)
                    return view;
                return Camera.main;
            }
        }

        /// <summary>当前水平朝向（度）。存档与重贴日志读这个，不读 Transform（含 Pitch）。</summary>
        public float YawDegrees => _yaw;

        CharacterController _cc;
        EnvMeshColliderLod _colliderLod;
        const float ColliderRefreshMinRadius = 48f;
        /// <summary>本次贴地命中的是命名甲板，传送时跳过禁水钳制。</summary>
        bool _sampledNamedDeck;

        Vector3 _lastNamedDeckFoot;
        float _lastNamedDeckY;
        float _namedDeckGraceUntil;

        Vector2 _smoothedMove;
        float _speedRatio;
        float _yaw;
        bool _downloadMoveLock;
        bool _retreatActive;
        Vector3 _retreatDir;
        float _retreatMeters;
        float _retreatDuration;
        float _retreatElapsed;
        float _retreatAlong;
        float _verticalSpeed;

        /// <summary>上一帧可走落点（非海/河禁区）。已在海底时回滚用这个，不能只回 before。</summary>
        Vector3 _lastSafeEye;
        bool _hasLastSafeEye;

        /// <summary>Inspector-authored <see cref="EyeHeight"/>. Cached on first OnEnable so a
        /// preset that bumps EyeHeight for StayBehind gets reverted to this when walking starts.</summary>
        float _defaultEyeHeight = -1f;

        bool _pitchRestoring;
        float _pitchFrom;
        float _pitchTo;
        float _pitchElapsed;
        float _pitchDuration;

        bool _walkRectCached;
        EntityId _walkRectTerrainId;
        float _walkRectInsetRequest = -1f;
        float _walkRectMinX;
        float _walkRectMaxX;
        float _walkRectMinZ;
        float _walkRectMaxZ;

        // Capsule shape last applied to the CC; -1 forces reapply on next EnsureBody.
        float _appliedEyeHeight = -1f;
        float _appliedRadius = -1f;

        float _appliedRenderScale = -1f;
        float _authoredVerticalFov = -1f;
        float _lastAspectForFov = -1f;

        // Camera-Y lerp from current height to ground+EyeHeight (used by walking / preset release).
        bool _heightTransitioning;
        float _heightStart;
        float _heightTarget;
        float _heightDuration;
        float _heightElapsed;

        enum EyeDrive
        {
            Locomotion,
            Push,
            Locked,
        }

        EyeDrive _drive = EyeDrive.Locomotion;

        /// <summary>
        /// 进岛揭罩前锁死位姿：不吃重力、不吃视角输入。
        /// 地形未就绪时的传送和流送改焦点都不能把镜头转走或掉下去。
        /// </summary>
        bool _spawnHold;
        bool _hasHeldPosition;
        Vector3 _heldPosition;

        /// <summary>揭罩前在罩下扫几个朝向，把侧面着色器变体和走路档 RT 先画出来。</summary>
        bool _entryLookWarmup;
        bool _entryLookWalking;
        float _entryLookYaw;
        /// <summary>左右各两档。再多会把「即将进入」拖长；墙钟另有上限。</summary>
        static readonly float[] EntryLookYawOffsets = { -90f, -45f, 45f, 90f };
        const float EntryLookWarmupCapSeconds = 1.5f;

        /// <summary>揭罩后胶囊还没贴地时，把眼睛钉在已摆好的高度，避免第一帧重力下坠。</summary>
        bool _pinUntilGrounded;
        float _pinUntilUnscaled;
        Vector3 _pinnedEye;

        Vector3 _pushFrom;
        Vector3 _pushTo;
        float _pushT;
        /// <summary>推镜开始时眼睛离地高度。整段前推都保持这个空隙，不收成行走眼高。</summary>
        float _pushClearance;

        /// <summary>眼睛。胶囊在下一帧按这个坐标新建，避免启用旧胶囊时弹回。</summary>
        Vector3 _lockedEye;

        const float PushMeters = 2.2f;
        const float PushWalkSpeedScale = 0.4f;

        /// <summary>True while the height lerp is in progress.</summary>
        public bool IsHeightTransitioning => _heightTransitioning;

        // ─── Lifecycle ─────────────────────────────────────────────────────────────

        void OnEnable()
        {
            if (_view == null)
                _view = GetComponent<Camera>();
            Current = this;
            if (_defaultEyeHeight < 0f)
                _defaultEyeHeight = EyeHeight;
            AdoptSceneLook();
            if (_colliderLod == null)
                _colliderLod = FindAnyObjectByType<EnvMeshColliderLod>();
            EnsureBody();
            ApplyRenderScale(false);
        }

        /// <summary>
        /// 场景里已经摆好的朝向要先收进 <see cref="_yaw"/>，
        /// 否则第一帧 LateUpdate 会用 yaw=0 把镜头拧回正前方。
        /// </summary>
        void AdoptSceneLook()
        {
            Vector3 e = transform.eulerAngles;
            float yaw = e.y > 180f ? e.y - 360f : e.y;
            if (Mathf.Abs(yaw) <= 0.01f)
                return;
            _yaw = yaw;
        }

        void OnDisable()
        {
            IslandLocomotionState.IsWalking = false;
            ApplyRenderScale(false);
            if (_cc != null)
                _cc.enabled = false;
            LocomotionInput.ClearAll();
            if (Current == this)
                Current = null;
        }

        // ─── Public pose API ───────────────────────────────────────────────────────

        public void ApplySpawnPose() =>
            Teleport(IslandSpawnPose.DefaultPos, IslandSpawnPose.DefaultYawDegrees);

        /// <summary>恢复存档俯仰。大于 180 的欧拉角折回负角，避免把 348° 当成抬头。</summary>
        public void SetPitchDegrees(float pitchDegrees)
        {
            if (pitchDegrees > 180f)
                pitchDegrees -= 360f;
            Pitch = Mathf.Clamp(pitchDegrees, -85f, 85f);
            _pitchRestoring = false;
        }

        /// <summary>揭罩前锁住当前眼睛位置和朝向。</summary>
        public void HoldSpawnPose()
        {
            _spawnHold = true;
            _pinUntilGrounded = false;
            _verticalSpeed = 0f;
            _heightTransitioning = false;
            _heldPosition = transform.position;
            _hasHeldPosition = true;
            if (_cc != null && _cc.enabled)
                _cc.enabled = false;
            LocomotionInput.ClearAll();
        }

        /// <summary>加载罩已撤。松锁，但在胶囊贴地前不允许重力把眼睛拽离落点。</summary>
        public void ReleaseSpawnHold()
        {
            if (!_spawnHold && !_hasHeldPosition)
                return;
            _spawnHold = false;
            _hasHeldPosition = false;
            _verticalSpeed = 0f;
            _pinUntilGrounded = true;
            _pinUntilUnscaled = Time.unscaledTime + 1.25f;
            _pinnedEye = transform.position;
            LocomotionInput.ClearAll();
            EnsureBody();
        }

        /// <summary>
        /// 罩还盖着、摇杆还没放开时，按出生朝向左右各画几帧。
        /// 侧面物体的着色器变体，以及走路档那套中间 RT，都在这一段里第一次提交。
        /// 墙钟到顶就停，避免把「即将进入」拖成几十秒。调用时必须仍在 <see cref="HoldSpawnPose"/>。
        /// </summary>
        public async UniTask WarmupEntryLookAsync(Func<bool> stale)
        {
            if (!_spawnHold)
                return;

            float wallStart = Time.realtimeSinceStartup;
            int frames = 0;
            Log.Info("[Hitch] EntryLookWarmup begin yaw=" + _yaw.ToString("F1"));
            _entryLookWarmup = true;
            _entryLookWalking = true;
            try
            {
                for (int i = 0; i < EntryLookYawOffsets.Length; i++)
                {
                    if (!_spawnHold || (stale != null && stale()))
                        return;
                    if (Time.realtimeSinceStartup - wallStart >= EntryLookWarmupCapSeconds)
                        break;
                    _entryLookYaw = _yaw + EntryLookYawOffsets[i];
                    // LateUpdate 之前就摆好，原生实例的视锥和管线剔除看到的是同一朝向。
                    SnapHeldYaw(_entryLookYaw);
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
                    frames++;
                }

                if (_spawnHold
                    && (stale == null || !stale())
                    && Time.realtimeSinceStartup - wallStart < EntryLookWarmupCapSeconds)
                {
                    // 最后一帧回到出生朝向和站立分辨率，淡出时看到的是站立画面。
                    _entryLookWalking = false;
                    _entryLookYaw = _yaw;
                    SnapHeldYaw(_yaw);
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
                    frames++;
                }
            }
            finally
            {
                _entryLookWarmup = false;
                _entryLookWalking = false;
                IslandLocomotionState.IsWalking = false;
                if (this != null)
                {
                    SnapHeldYaw(_yaw);
                    ApplyRenderScale(false);
                }
                Log.Info(
                    "[Hitch] EntryLookWarmup end wallMs="
                    + ((Time.realtimeSinceStartup - wallStart) * 1000f).ToString("F0")
                    + " frames=" + frames);
            }
        }

        /// <summary>
        /// 下载弹窗或进度条期间停住平移。视角仍走 <see cref="ConsumeLookInput"/>。
        /// </summary>
        public void SetDownloadMoveLock(bool locked)
        {
            _downloadMoveLock = locked;
            if (!locked)
            {
                CancelDownloadRetreat();
                return;
            }

            HaltHorizontalMotion();
        }

        /// <summary>下载弹窗的后退还在播。</summary>
        public bool IsDownloadRetreatPlaying => _retreatActive;

        /// <summary>
        /// 下载弹窗弹出后，沿当时的水平朝向在 <paramref name="seconds"/> 内后退
        /// <paramref name="meters"/>。朝向在开始时定死，中途转视角不会拐弯。
        /// </summary>
        public void BeginDownloadRetreat(float meters, float seconds)
        {
            if (meters <= 0f || seconds <= 0f)
                return;
            Vector3 back = Quaternion.Euler(0f, _yaw, 0f) * Vector3.back;
            back.y = 0f;
            if (back.sqrMagnitude < 1e-6f)
                return;
            _retreatDir = back.normalized;
            _retreatMeters = meters;
            _retreatDuration = seconds;
            _retreatElapsed = 0f;
            _retreatAlong = 0f;
            _retreatActive = true;
            HaltHorizontalMotion();
        }

        public void CancelDownloadRetreat()
        {
            _retreatActive = false;
            _retreatElapsed = 0f;
            _retreatAlong = 0f;
        }

        /// <summary>沿水平朝向后退，离开挡住路的区域门。</summary>
        public void StepBack(float meters)
        {
            if (meters <= 0f)
                return;
            Vector3 back = Quaternion.Euler(0f, _yaw, 0f) * Vector3.back;
            StepHorizontal(back * meters);
        }

        /// <summary>沿给定水平位移离开区域门。Y 会被丢掉。</summary>
        public void StepHorizontal(Vector3 worldDelta)
        {
            worldDelta.y = 0f;
            if (worldDelta.sqrMagnitude < 1e-8f)
                return;
            Vector3 foot = transform.position - Vector3.up * EyeHeight;
            Teleport(foot + worldDelta, _yaw);
        }

        /// <summary>
        /// 本次 <see cref="Teleport"/> 在水面钳制之前的眼睛高度。
        /// 读档比较落差要用这个：钳制会把桥底抬到水面附近，看起来只掉了一点，保住眼位就会失效。
        /// </summary>
        public float SampledTeleportEyeY { get; private set; }

        /// <summary>脚落到 <paramref name="worldPos"/> 的地面上，眼睛抬到行走高度。出生和读档用。</summary>
        public void Teleport(Vector3 worldPos, float yawDegrees)
        {
            _drive = EyeDrive.Locomotion;
            _yaw = yawDegrees;
            _verticalSpeed = 0f;
            _smoothedMove = Vector2.zero;
            EnsureBody();
            // 先开落点附近碰撞再贴地：桥/码头 MeshCollider 若仍关着，高度图会贴到桥底河床。
            RefreshNearbyCollidersAt(worldPos);
            Physics.SyncTransforms();
            Vector3 ground = SampleGround(worldPos);
            Vector3 eye = ground + Vector3.up * EyeHeight;
            SampledTeleportEyeY = eye.y;
            // 已经贴上命名甲板就不要再被禁水钳制拽回河面，否则下一帧胶囊会从桥底被顶上来。
            SetPose(eye, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: _sampledNamedDeck);
            RefreshNearbyColliders();
        }

        void RefreshNearbyColliders() =>
            RefreshNearbyCollidersAt(transform.position);

        void RefreshNearbyCollidersAt(Vector3 worldPos)
        {
            if (_colliderLod == null)
                _colliderLod = FindAnyObjectByType<EnvMeshColliderLod>();
            float radius = Mathf.Max(ColliderRefreshMinRadius, EnvStreamingConfig.MeshColliderEnableRadius);
            _colliderLod?.ForceRefreshNear(worldPos, radius);
        }

        /// <summary>
        /// 眼睛直接放到世界坐标并锁定。之后的推镜采样会被忽略，直到玩家推动摇杆。
        /// </summary>
        public void TeleportEye(Vector3 worldEye, float yawDegrees, float pitchDegrees)
        {
            _yaw = yawDegrees;
            if (pitchDegrees > 180f)
                pitchDegrees -= 360f;
            Pitch = Mathf.Clamp(pitchDegrees, -85f, 85f);
            _pitchRestoring = false;
            _verticalSpeed = 0f;
            _heightTransitioning = false;
            _smoothedMove = Vector2.zero;
            _speedRatio = 0f;
            LocomotionInput.ClearAll();
            _drive = EyeDrive.Locked;
            _lockedEye = worldEye;
            if (_cc != null && _cc.enabled)
                _cc.enabled = false;
            Quaternion rot = Quaternion.Euler(Pitch, _yaw, 0f);
            transform.SetPositionAndRotation(_lockedEye, rot);
            Physics.SyncTransforms();
            if (_spawnHold)
            {
                _heldPosition = _lockedEye;
                _hasHeldPosition = true;
            }
        }

        /// <summary>
        /// 落点正下方的地面。优先高度图（不吃树冠/石头）；失败再射线。
        /// </summary>
        public bool TryGroundUnder(Vector3 worldPos, out Vector3 ground)
        {
            Vector3 snapped = worldPos;
            EnvGroundFit fit = GameEntry.GroundFit;
            if (fit != null && fit.TrySnapY(ref snapped))
            {
                ground = snapped;
                return true;
            }

            Vector3 origin = worldPos + Vector3.up * TerrainProbeUp;
            float dist = TerrainProbeUp + TerrainProbeDown;
            int count = Physics.RaycastNonAlloc(
                origin, Vector3.down, GroundHits, dist, GroundMask, QueryTriggerInteraction.Ignore);
            float bestY = float.PositiveInfinity;
            Vector3 best = worldPos;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (GroundHits[i].normal.y < GroundNormalMinY)
                    continue;
                if (IsWaterSurfaceCollider(GroundHits[i].collider))
                    continue;
                if (GroundHits[i].point.y >= bestY)
                    continue;
                bestY = GroundHits[i].point.y;
                best = GroundHits[i].point;
                found = true;
            }

            ground = found ? best : worldPos;
            return found;
        }

        /// <summary>落点正下方的地面。采不到时退回传入点。</summary>
        public Vector3 GroundUnder(Vector3 worldPos)
        {
            TryGroundUnder(worldPos, out Vector3 ground);
            return ground;
        }

        /// <summary>
        /// 沿当前朝向前推一段。离地高度保持推镜开始时的空隙，不收成行走眼高。
        /// 已锁定时调用会重新进入推镜。
        /// </summary>
        public void BeginPush()
        {
            _drive = EyeDrive.Push;
            _heightTransitioning = false;
            _verticalSpeed = 0f;
            if (_cc != null && _cc.enabled)
                _cc.enabled = false;
            _pushFrom = transform.position;
            _pushT = 0f;
            _pushClearance = ClearanceAboveGround(_pushFrom);
            Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            Vector3 near = _pushFrom + forward * PushMeters;
            _pushTo = EyeAtClearance(near, _pushClearance);
        }

        /// <summary>推镜插值。锁定之后调用无效，避免盖掉瞬移。</summary>
        public void SamplePush(float t01)
        {
            if (_drive != EyeDrive.Push)
                return;
            t01 = Mathf.Clamp01(t01);
            _pushT = t01;
            Vector3 pos = Vector3.Lerp(_pushFrom, _pushTo, t01);
            SetPose(EyeAtClearance(pos, _pushClearance), Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
        }

        /// <summary>推镜途中顺朝向再挪一点。锁定之后调用无效。</summary>
        public void NudgePush(float dt)
        {
            if (_drive != EyeDrive.Push || dt <= 0f)
                return;
            Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            Vector3 delta = forward * (MoveSpeed * PushWalkSpeedScale * dt);
            _pushFrom += delta;
            _pushTo += delta;
            Vector3 pos = Vector3.Lerp(_pushFrom, _pushTo, _pushT);
            SetPose(EyeAtClearance(pos, _pushClearance), Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
        }

        /// <summary>采不到地面时退回行走眼高，避免把空隙算成 0 后贴地。</summary>
        float ClearanceAboveGround(Vector3 eye)
        {
            if (!TryGroundUnder(eye, out Vector3 ground))
                return EyeHeight;
            float clearance = eye.y - ground.y;
            if (clearance < 0.05f)
                return EyeHeight;
            return clearance;
        }

        /// <summary>水平位置照旧，高度改成该点地面加上推镜开始时的离地空隙。</summary>
        Vector3 EyeAtClearance(Vector3 eye, float clearance)
        {
            if (!TryGroundUnder(eye, out Vector3 ground))
                return eye;
            eye.y = ground.y + clearance;
            return eye;
        }

        /// <summary>
        /// 虹膜揭开结束。已经瞬移锁定的眼睛保持不动，不在这里打开胶囊。
        /// 若黑场没有瞬移、仍停在推镜，则交回行走。
        /// </summary>
        public void EndPush()
        {
            if (_drive != EyeDrive.Push)
                return;
            _verticalSpeed = 0f;
            DropStaleBody(transform.position);
            _drive = EyeDrive.Locomotion;
        }

        /// <summary>
        /// 退登 / 二次揭幕：清推镜和锁定，避免「有声但不能走」。
        /// </summary>
        public void ForceUnlockLocomotion()
        {
            _heightTransitioning = false;
            _verticalSpeed = 0f;
            _smoothedMove = Vector2.zero;
            DropStaleBody(transform.position);
            _drive = EyeDrive.Locomotion;
        }

        void ReleaseLockToWalk()
        {
            Vector3 eye = _lockedEye;
            _verticalSpeed = 0f;
            _smoothedMove = Vector2.zero;

            if (_defaultEyeHeight > 0f && !Mathf.Approximately(EyeHeight, _defaultEyeHeight))
            {
                EyeHeight = _defaultEyeHeight;
                _appliedEyeHeight = -1f;
            }

            DropStaleBody(eye);
            _drive = EyeDrive.Locomotion;
            BeginWalkPitchRestore();

            Vector3 ground = SampleGround(eye);
            float startY = eye.y;
            float targetY = ground.y + EyeHeight;
            if (HeightTransitionDuration > 0f && Mathf.Abs(startY - targetY) > 0.01f)
                StartHeightTransition(startY, targetY);
        }

        /// <summary>
        /// 旧胶囊记着禁用前的坐标。拆掉后在当前眼睛上新建，本帧不 Move。
        /// </summary>
        void DropStaleBody(Vector3 eye)
        {
            if (_cc != null)
            {
                _cc.enabled = false;
                DestroyImmediate(_cc);
                _cc = null;
            }

            _appliedEyeHeight = -1f;
            _appliedRadius = -1f;
            transform.position = eye;
            Physics.SyncTransforms();
            EnsureBody();
        }

        // ─── Per-frame ─────────────────────────────────────────────────────────────

        /// <summary>出生锁定期只改朝向，位置留在已经贴地的眼睛上。</summary>
        void SnapHeldYaw(float yaw)
        {
            Quaternion heldRot = Quaternion.Euler(Pitch, yaw, 0f);
            if (_hasHeldPosition)
                transform.SetPositionAndRotation(_heldPosition, heldRot);
            else
                transform.rotation = heldRot;
        }

        void LateUpdate()
        {
            if (IslandLocomotionState.MirrorTurnGraceFrames > 0)
                IslandLocomotionState.MirrorTurnGraceFrames--;

            ApplyAspectFov();

            if (_spawnHold)
            {
                float yaw = _yaw;
                bool walkingScale = false;
                if (_entryLookWarmup)
                {
                    yaw = _entryLookYaw;
                    walkingScale = _entryLookWalking;
                    // 每帧刷新：侧面几帧都在转，不能让倒影跟着重画一整遍。
                    IslandLocomotionState.MirrorTurnGraceFrames = 2;
                }

                IslandLocomotionState.IsWalking = walkingScale;
                LocomotionInput.ClearAll();
                SnapHeldYaw(yaw);
                if (_cc != null && _cc.enabled)
                    _cc.enabled = false;
                ApplyRenderScale(walkingScale);
                return;
            }

            if (_drive == EyeDrive.Push)
            {
                IslandLocomotionState.IsWalking = false;
                transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
                ApplyRenderScale(false);
                return;
            }

            if (_drive == EyeDrive.Locked)
            {
                IslandLocomotionState.IsWalking = false;
                transform.SetPositionAndRotation(_lockedEye, Quaternion.Euler(Pitch, _yaw, 0f));
                if (_cc != null && _cc.enabled)
                    _cc.enabled = false;
                ApplyRenderScale(false);

                float lockDt = Time.deltaTime;
                if (lockDt > 0f)
                {
                    ConsumeMoveInput(lockDt);
                    if (IsWalking)
                        ReleaseLockToWalk();
                    else
                        ApplyStickYaw(lockDt);
                }

                if (_drive == EyeDrive.Locked)
                    transform.SetPositionAndRotation(_lockedEye, Quaternion.Euler(Pitch, _yaw, 0f));
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            bool looked = ConsumeLookInput();
            ConsumeMoveInput(dt);

            bool walking = IsWalking;
            IslandLocomotionState.IsWalking = walking;

            EnsureBody();
            // 闺蜜机：摇杆有位移（含纯横向转向）或 look 增量时压到走路档 0.5；一停立刻回 0.75。
            bool stickTurning = _smoothedMove.sqrMagnitude > WalkRenderScaleThreshold * WalkRenderScaleThreshold;
            bool lowerScale = walking;
            if (BoluoDeviceInfo.IsSkyworthGmjStandard())
                lowerScale = walking || looked || stickTurning;
            ApplyRenderScale(lowerScale);
            TickHeightTransition(dt);
            TickPitchRestore(dt);

            Vector3 motion = ComputeMotion(dt);
            ApplyDownloadRetreat(dt, ref motion);
            if (_pinUntilGrounded)
                motion.y = -GroundStickSpeed * dt;
            else if (!_heightTransitioning)
                motion.y = IntegrateVertical(dt);
            MoveBody(motion);
            PinEyeUntilGrounded();

            transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
        }

        void PinEyeUntilGrounded()
        {
            if (!_pinUntilGrounded)
                return;

            bool grounded = _cc != null && _cc.enabled && _cc.isGrounded;
            // 没贴地就一直钉在落点。超时后只有玩家已经在走，才交回重力，避免揭罩后自己往下掉。
            bool allowFall = Time.unscaledTime >= _pinUntilUnscaled && IsWalking;
            if (grounded || allowFall)
            {
                _pinUntilGrounded = false;
                if (grounded && _verticalSpeed < 0f)
                    _verticalSpeed = -GroundStickSpeed;
                return;
            }

            _verticalSpeed = 0f;
            Vector3 p = transform.position;
            if (Mathf.Abs(p.y - _pinnedEye.y) <= 0.001f)
                return;
            p.y = _pinnedEye.y;
            bool ccOn = _cc != null && _cc.enabled;
            if (ccOn)
                _cc.enabled = false;
            transform.position = p;
            if (ccOn)
                _cc.enabled = true;
        }

        /// <returns>本帧是否有转头输入（供闺蜜机压 renderScale）。</returns>
        bool ConsumeLookInput()
        {
            if (!LocomotionInput.ConsumeLook(out Vector2 look))
                return false;
            // Screen-relative: same fraction of width → same yaw. Ref width keeps portrait feel.
            float scale = RefLookWidth / Mathf.Max(1f, Screen.width);
            _yaw += look.x * LookSensitivity * scale;
            return Mathf.Abs(look.x) > 0.5f || Mathf.Abs(look.y) > 0.5f;
        }

        void ConsumeMoveInput(float dt)
        {
            if (_downloadMoveLock || HudOverlayGate.BlocksLocomotion())
            {
                HaltHorizontalMotion();
                return;
            }

            // speedRatio defaults to 0 unless input arrived this frame.
            _speedRatio = 0f;
            Vector2 want = Vector2.zero;
            if (LocomotionInput.Consume(out Vector2 dir, out float speed))
            {
                want = dir;
                _speedRatio = Mathf.Clamp01(speed);
            }
            _smoothedMove = Vector2.MoveTowards(_smoothedMove, want, InputSmooth * dt);
        }

        void HaltHorizontalMotion()
        {
            _smoothedMove = Vector2.zero;
            _speedRatio = 0f;
            LocomotionInput.ClearAll();
        }

        /// <summary>按开始时定下的后退方向，在给定时长内走完给定距离。</summary>
        void ApplyDownloadRetreat(float dt, ref Vector3 motion)
        {
            if (!_retreatActive)
                return;
            _retreatElapsed += dt;
            float t = _retreatDuration <= 0f
                ? 1f
                : Mathf.Clamp01(_retreatElapsed / _retreatDuration);
            float eased = t * t * (3f - 2f * t);
            float along = _retreatMeters * eased;
            float step = along - _retreatAlong;
            _retreatAlong = along;
            motion.x = _retreatDir.x * step;
            motion.z = _retreatDir.z * step;
            if (t >= 1f)
                _retreatActive = false;
        }

        bool IsWalking => Mathf.Abs(_smoothedMove.y) > WalkRenderScaleThreshold;

        void ApplyStickYaw(float dt)
        {
            float mag = _smoothedMove.magnitude;
            if (mag <= 1e-4f)
                return;
            float rate = Mathf.Max(_speedRatio, mag);
            _yaw += _smoothedMove.x * StickYawSpeed * rate * dt;
        }

        Vector3 ComputeMotion(float dt)
        {
            float mag = _smoothedMove.magnitude;
            if (mag <= 1e-4f)
                return Vector3.zero;
            ApplyStickYaw(dt);
            float rate = Mathf.Max(_speedRatio, mag);
            Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            return forward * (_smoothedMove.y * MoveSpeed * rate * dt);
        }

        // ─── Height transition ────────────────────────────────────────────────────

        /// <summary>开走后把预设俯仰插值回行走俯仰 0°，时长跟高度过渡一致。</summary>
        void BeginWalkPitchRestore()
        {
            const float target = 0f;
            if (Mathf.Abs(Mathf.DeltaAngle(Pitch, target)) <= 0.05f)
            {
                Pitch = target;
                _pitchRestoring = false;
                return;
            }

            if (HeightTransitionDuration <= 0f)
            {
                Pitch = target;
                _pitchRestoring = false;
                return;
            }

            _pitchRestoring = true;
            _pitchFrom = Pitch;
            _pitchTo = target;
            _pitchElapsed = 0f;
            _pitchDuration = HeightTransitionDuration;
        }

        void TickPitchRestore(float dt)
        {
            if (!_pitchRestoring)
                return;

            _pitchElapsed += dt;
            float t = _pitchDuration <= 0f ? 1f : Mathf.Clamp01(_pitchElapsed / _pitchDuration);
            Pitch = Mathf.LerpAngle(_pitchFrom, _pitchTo, t);
            if (t < 1f)
                return;

            Pitch = _pitchTo;
            _pitchRestoring = false;
        }

        void StartHeightTransition(float from, float to)
        {
            _heightTransitioning = true;
            _heightStart = from;
            _heightTarget = to;
            _heightDuration = HeightTransitionDuration;
            _heightElapsed = 0f;
        }

        void TickHeightTransition(float dt)
        {
            if (!_heightTransitioning)
                return;
            _heightElapsed += dt;
            if (_heightElapsed < _heightDuration)
                return;

            // 过渡结束时把相机贴回 ground + EyeHeight，让 CC 重新建立 isGrounded。
            // 下一帧 gravity 只会产生 -GroundStickSpeed 的贴地速度，不会再把相机往地下拽。
            _heightTransitioning = false;
            _verticalSpeed = 0f;
            Vector3 ground = SampleGround(transform.position);
            SetPose(
                new Vector3(transform.position.x, ground.y + EyeHeight, transform.position.z),
                Quaternion.Euler(Pitch, _yaw, 0f));
        }

        // ─── Capsule / body ────────────────────────────────────────────────────────

        void EnsureBody()
        {
            if (BodyRadius <= 0f)
            {
                if (_cc != null && _cc.enabled)
                    _cc.enabled = false;
                return;
            }

            if (_cc == null)
            {
                _cc = GetComponent<CharacterController>();
                if (_cc == null)
                    _cc = gameObject.AddComponent<CharacterController>();
            }

            if (!_cc.enabled)
                _cc.enabled = true;

            if (!Mathf.Approximately(_appliedEyeHeight, EyeHeight)
                || !Mathf.Approximately(_appliedRadius, BodyRadius))
                ApplyCapsuleShape();
        }

        void ApplyCapsuleShape()
        {
            if (_cc == null)
                return;

            float height = Mathf.Max(1.2f, EyeHeight + HeadClearance);
            // EyeHeight≈6 时默认半径约 1.1，过细容易从悬崖三角缝钻过去。
            float radius = Mathf.Min(
                Mathf.Max(BodyRadius, EyeHeight * 0.15f),
                height * 0.45f);
            // Transform is at the eye; capsule feet sit on the ground under the eye.
            _cc.height = height;
            _cc.radius = radius;
            _cc.center = new Vector3(0f, -EyeHeight + height * 0.5f, 0f);
            _cc.slopeLimit = SlopeLimit;
            _cc.stepOffset = Mathf.Min(StepOffset, radius * 0.85f);
            _cc.skinWidth = Mathf.Clamp(radius * 0.12f, 0.08f, 0.2f);
            _cc.minMoveDistance = 0f;
            _appliedEyeHeight = EyeHeight;
            _appliedRadius = BodyRadius;
        }

        float IntegrateVertical(float dt)
        {
            bool grounded = _cc != null && _cc.enabled && _cc.isGrounded;
            if (grounded && _verticalSpeed <= 0f)
                _verticalSpeed = -GroundStickSpeed;
            else
                _verticalSpeed -= Gravity * dt;

            return _verticalSpeed * dt;
        }

        // ─── Move body ─────────────────────────────────────────────────────────────

        void MoveBody(Vector3 motion)
        {
            motion = ClampMotionToTerrain(motion);
            motion = FilterMotionAwayFromWater(motion);
            if (_cc != null && _cc.enabled)
                MoveViaCharacterController(motion);
            else
                MoveFreeCam(motion);
        }

        /// <summary>
        /// 目标 XZ 已在禁海：本帧水平位移清零，避免 CharacterController 先踩上海底再回滚。
        /// 例外：已在禁区时朝 last-safe 逃离放行；无 last-safe 时不锁死水平移动。
        /// </summary>
        Vector3 FilterMotionAwayFromWater(Vector3 motion)
        {
            if (!BlockWaterWalking)
                return motion;
            if (Mathf.Abs(motion.x) + Mathf.Abs(motion.z) <= 1e-6f)
                return motion;

            Vector3 eye = transform.position;
            Vector3 curFoot = eye;
            curFoot.y = eye.y - EyeHeight;
            Vector3 destFoot = eye + motion;
            destFoot.y = curFoot.y;
            if (!IsFootInForbiddenWater(destFoot))
                return motion;

            // 已在禁区且还没有岸上 safe：不要把摇杆清零，否则海岸误判时整个人冻住。
            if (IsFootInForbiddenWater(curFoot) && !_hasLastSafeEye)
                return motion;

            // 已踩进禁区：允许朝 last-safe 走，交给 ApplyWaterWalkBan 拉回岸。
            if (MotionMovesTowardLastSafe(eye, motion))
                return motion;

            // 轴向拆开：贴岸斜走时尽量沿可走边滑。
            Vector3 onlyX = motion;
            onlyX.z = 0f;
            Vector3 footX = eye + onlyX;
            footX.y = destFoot.y;
            bool xOk = Mathf.Abs(onlyX.x) > 1e-6f && !IsFootInForbiddenWater(footX);

            Vector3 onlyZ = motion;
            onlyZ.x = 0f;
            Vector3 footZ = eye + onlyZ;
            footZ.y = destFoot.y;
            bool zOk = Mathf.Abs(onlyZ.z) > 1e-6f && !IsFootInForbiddenWater(footZ);

            if (xOk && zOk)
                return onlyX.sqrMagnitude >= onlyZ.sqrMagnitude ? onlyX : onlyZ;
            if (xOk)
                return onlyX;
            if (zOk)
                return onlyZ;

            if (MotionMovesTowardLastSafe(eye, onlyX))
                return onlyX;
            if (MotionMovesTowardLastSafe(eye, onlyZ))
                return onlyZ;

            motion.x = 0f;
            motion.z = 0f;
            return motion;
        }

        bool MotionMovesTowardLastSafe(Vector3 eye, Vector3 motion)
        {
            if (!_hasLastSafeEye)
                return false;
            float mx = motion.x;
            float mz = motion.z;
            if (mx * mx + mz * mz <= 1e-8f)
                return false;
            float dx = _lastSafeEye.x - eye.x;
            float dz = _lastSafeEye.z - eye.z;
            if (dx * dx + dz * dz <= 1e-4f)
                return false;
            return dx * mx + dz * mz > 0f;
        }

        /// <summary>本帧水平位移不许离开地形矩形，避免一步踩上海面。</summary>
        Vector3 ClampMotionToTerrain(Vector3 motion)
        {
            if (!TryGetTerrainWalkRect(out float minX, out float maxX, out float minZ, out float maxZ))
                return motion;

            Vector3 eye = transform.position;
            bool outside = eye.x < minX || eye.x > maxX || eye.z < minZ || eye.z > maxZ;
            if (outside)
            {
                // 已经在地形外：本帧不走，交给 ApplyIslandTerrainLimit 拉回岸边。
                motion.x = 0f;
                motion.z = 0f;
                return motion;
            }

            float x = Mathf.Clamp(eye.x + motion.x, minX, maxX);
            float z = Mathf.Clamp(eye.z + motion.z, minZ, maxZ);
            motion.x = x - eye.x;
            motion.z = z - eye.z;
            return motion;
        }

        void MoveViaCharacterController(Vector3 motion)
        {
            Vector3 before = transform.position;
            float savedStep = _cc.stepOffset;
            RaiseStepForOceanDeck(before, motion);

            // 高速单帧位移超过半径时拆步，减轻对 MeshCollider 的隧道穿模。
            float horiz = new Vector2(motion.x, motion.z).magnitude;
            float step = Mathf.Max(0.2f, _cc.radius * 0.85f);
            int steps = horiz > step ? Mathf.Clamp(Mathf.CeilToInt(horiz / step), 1, 8) : 1;
            Vector3 slice = motion / steps;
            CollisionFlags flags = CollisionFlags.None;
            for (int i = 0; i < steps; i++)
                flags |= _cc.Move(slice);
            if ((flags & CollisionFlags.Below) != 0 && _verticalSpeed < 0f)
                _verticalSpeed = -GroundStickSpeed;
            _cc.stepOffset = savedStep;

            // CC 只动 XZ；高度过渡期间 Y 由 lerp 接管，避免与 CC 的贴地反力互拽。
            if (_heightTransitioning)
            {
                float t = Mathf.Clamp01(_heightElapsed / _heightDuration);
                Vector3 p = transform.position;
                p.y = Mathf.Lerp(_heightStart, _heightTarget, t);
                transform.position = p;
            }

            TryClimbOceanDeck(before, motion);
            ApplyWaterWalkBan(before);
            ClampFootNotBelowOcean();
            ApplyIslandTerrainLimit();
        }

        void MoveFreeCam(Vector3 motion)
        {
            Vector3 before = transform.position;
            Vector3 pos = transform.position + motion;
            if (Mathf.Abs(motion.x) + Mathf.Abs(motion.z) <= 1e-4f)
            {
                transform.position = pos;
                ApplyWaterWalkBan(before);
                ClampFootNotBelowOcean();
                ApplyIslandTerrainLimit();
                return;
            }

            Vector3 ground = SampleGround(pos);
            if (_heightTransitioning)
            {
                float t = Mathf.Clamp01(_heightElapsed / _heightDuration);
                float targetY = Mathf.Lerp(_heightStart, _heightTarget, t);
                pos.y = Mathf.Lerp(targetY, ground.y + EyeHeight, t);
            }
            else
            {
                pos.y = ground.y + EyeHeight;
            }
            transform.position = pos;
            TryClimbOceanDeck(before, motion);
            ApplyWaterWalkBan(before);
            ClampFootNotBelowOcean();
            ApplyIslandTerrainLimit();
        }

        /// <summary>
        /// 水平位置锁在地形矩形内。高度图外面是海面碰撞，走出去就踩着海面走远、回不来。
        /// </summary>
        void ApplyIslandTerrainLimit()
        {
            if (!TryGetTerrainWalkRect(out float minX, out float maxX, out float minZ, out float maxZ))
                return;

            Vector3 eye = transform.position;
            float x = Mathf.Clamp(eye.x, minX, maxX);
            float z = Mathf.Clamp(eye.z, minZ, maxZ);
            if (Mathf.Abs(x - eye.x) <= 1e-3f && Mathf.Abs(z - eye.z) <= 1e-3f)
                return;

            eye.x = x;
            eye.z = z;
            if (!_heightTransitioning)
            {
                Vector3 ground = SampleGround(eye);
                eye.y = ground.y + EyeHeight;
                if (_verticalSpeed < 0f)
                    _verticalSpeed = -GroundStickSpeed;
            }

            bool ccOn = _cc != null && _cc.enabled;
            if (ccOn)
                _cc.enabled = false;
            transform.position = eye;
            Physics.SyncTransforms();
            if (ccOn)
                _cc.enabled = true;
        }

        bool TryGetTerrainWalkRect(out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
            Terrain terrain = IslandSceneComponent.Active != null
                ? IslandSceneComponent.Active.Terrain
                : null;
            if (terrain == null || terrain.terrainData == null)
                return false;

            float insetRequest = TerrainEdgeMargin;
            if (_cc != null && _cc.enabled)
                insetRequest = Mathf.Max(insetRequest, _cc.radius + _cc.skinWidth);

            EntityId terrainId = terrain.GetEntityId();
            if (_walkRectCached
                && _walkRectTerrainId == terrainId
                && Mathf.Abs(_walkRectInsetRequest - insetRequest) <= 1e-4f)
            {
                minX = _walkRectMinX;
                maxX = _walkRectMaxX;
                minZ = _walkRectMinZ;
                maxZ = _walkRectMaxZ;
                return maxX > minX && maxZ > minZ;
            }

            Vector3 size = terrain.terrainData.size;
            if (size.x <= 1f || size.z <= 1f)
                return false;

            float inset = Mathf.Min(insetRequest, size.x * 0.5f - 0.05f, size.z * 0.5f - 0.05f);
            if (inset < 0f)
                inset = 0f;

            Vector3 origin = terrain.transform.position;
            minX = origin.x + inset;
            maxX = origin.x + size.x - inset;
            minZ = origin.z + inset;
            maxZ = origin.z + size.z - inset;

            _walkRectCached = true;
            _walkRectTerrainId = terrainId;
            _walkRectInsetRequest = insetRequest;
            _walkRectMinX = minX;
            _walkRectMaxX = maxX;
            _walkRectMinZ = minZ;
            _walkRectMaxZ = maxZ;
            return maxX > minX && maxZ > minZ;
        }

        // ─── Pose / sampling ───────────────────────────────────────────────────────

        /// <param name="unconstrained">
        /// Push / teleport: exact eye — skip the water clamp.
        /// While not in locomotion, the capsule stays off so it cannot restore the previous eye.
        /// </param>
        void SetPose(Vector3 pos, Quaternion rot, bool unconstrained = false)
        {
            _verticalSpeed = 0f;
            if (!unconstrained)
                pos = ClampEyeOutOfWater(pos);

            // Disable CC so Move() / overlap resolve doesn't fight the placement.
            bool wasEnabled = _cc != null && _cc.enabled;
            if (wasEnabled)
                _cc.enabled = false;
            transform.SetPositionAndRotation(pos, rot);
            Physics.SyncTransforms();
            if (_spawnHold)
            {
                _heldPosition = transform.position;
                _hasHeldPosition = true;
                if (_cc != null && _cc.enabled)
                    _cc.enabled = false;
                return;
            }

            if (_drive != EyeDrive.Locomotion)
            {
                RememberSafeEyeIfAllowed();
                return;
            }
            if (wasEnabled && _cc != null)
            {
                _cc.enabled = true;
                transform.SetPositionAndRotation(pos, rot);
                Physics.SyncTransforms();
            }
            RememberSafeEyeIfAllowed();
        }

        void RememberSafeEyeIfAllowed()
        {
            if (!BlockWaterWalking)
                return;
            Vector3 eye = transform.position;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (IsFootInForbiddenWater(foot))
                return;
            // 海床放行点不记 safe，避免踩进海里之后被送回礁石。
            // 人已经站在海水上的桥面时要记：桥是路，探空一帧应该回到桥上，而不是被拉回岸。
            if (TryGetOceanSurfaceY(out float oceanY)
                && TrySampleHeightmapY(foot, out float terrainY)
                && IsOceanTerrainForbidden(terrainY, oceanY)
                && !IsFootPlantedOnNamedOceanDeck(foot))
                return;
            _lastSafeEye = eye;
            _hasLastSafeEye = true;
        }

        /// <summary>海上（高度图未过岸线净空）：回到上一可走岸点，禁止站在海底滑行。河不禁。</summary>
        void ApplyWaterWalkBan(Vector3 beforeEye)
        {
            if (!BlockWaterWalking)
                return;

            Vector3 eye = transform.position;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (!IsFootInForbiddenWater(foot))
            {
                RememberSafeEyeIfAllowed();
                return;
            }

            // 一律优先 last-safe。若回 before，岸→礁石→水 会被 before 钉在礁石上走不回来。
            Vector3 restore = _hasLastSafeEye ? _lastSafeEye : beforeEye;

            bool ccOn = _cc != null && _cc.enabled;
            if (ccOn)
                _cc.enabled = false;
            transform.position = restore;
            Physics.SyncTransforms();
            if (ccOn)
                _cc.enabled = true;
        }

        /// <summary>
        /// 已掉进海底时：优先拽回上一可走点；没有可走记录才抬到浅滩下限防穿地。
        /// 不再把人托到「海平面 − 0.25」当公路走。
        /// </summary>
        void ClampFootNotBelowOcean()
        {
            if (!TryGetOceanSurfaceY(out float oceanY))
                return;

            Vector3 eye = transform.position;
            float footY = eye.y - EyeHeight;
            Vector3 foot = eye;
            foot.y = footY;

            if (BlockWaterWalking && IsFootInForbiddenWater(foot) && _hasLastSafeEye)
            {
                bool ccOn = _cc != null && _cc.enabled;
                if (ccOn)
                    _cc.enabled = false;
                transform.position = _lastSafeEye;
                Physics.SyncTransforms();
                if (ccOn)
                    _cc.enabled = true;
                return;
            }

            float minFootY = oceanY - OceanBelowSurfaceBan;
            if (footY >= minFootY - 1e-3f)
                return;

            bool ccLift = _cc != null && _cc.enabled;
            if (ccLift)
                _cc.enabled = false;
            eye.y = minFootY + EyeHeight;
            transform.position = eye;
            Physics.SyncTransforms();
            if (ccLift)
                _cc.enabled = true;
        }

        Vector3 ClampEyeOutOfWater(Vector3 eye)
        {
            if (!BlockWaterWalking)
                return eye;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (!IsFootInForbiddenWater(foot))
                return eye;
            // 传送落点在海里：有上一可走点就回去；否则抬到可走岸线以上。
            if (_hasLastSafeEye)
                return _lastSafeEye;
            if (TryGetOceanSurfaceY(out float oceanY) && foot.y <= oceanY + OceanWalkMinAboveSea)
                return new Vector3(eye.x, oceanY + OceanWalkMinAboveSea + EyeHeight + 0.5f, eye.z);
            return eye;
        }

        bool IsFootInForbiddenWater(Vector3 foot)
        {
            // 禁水放行只认命名甲板 footprint，不认「柱内最高面」（石头/栏杆会抢命中）。
            bool hasDeck = TryGetNamedDeckStand(foot, out float deckY, out Collider deckCol);

            // 海：高度图不高于海面就禁。海水上的桥/码头放行，岸边比桥面低一截也允许抬上去。
            // 水下结构不算桥面。海里石头不算。
            if (TryGetOceanSurfaceY(out float oceanY))
            {
                float minDeckY = oceanY - NamedDeckSpawnPolicy.OceanDeckBelowSeaSlackMeters;
                bool planted = IsStandingOnNamedDeck(foot, hasDeck, deckY, deckCol, minDeckY);
                bool approaching = !planted
                    && hasDeck
                    && deckCol != null
                    && NamedDeckSpawnPolicy.IsFootNearOceanDeck(foot.y, deckY, oceanY);
                if (!planted
                    && !approaching
                    && !HasNamedDeckGrace(foot, minDeckY))
                {
                    if (TrySampleHeightmapY(foot, out float terrainY))
                    {
                        if (IsOceanTerrainForbidden(terrainY, oceanY))
                            return true;
                    }
                    else if (foot.y <= oceanY + OceanWalkMinAboveSea)
                    {
                        // 高度图外仍贴着/低于可走岸线 → 禁行
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 桥面比岸高、台阶又跨不上去时，这一帧把步高临时抬到桥面。
        /// 调用方负责在 Move 之后把 stepOffset 设回去，避免顺手爬上石头。
        /// </summary>
        void RaiseStepForOceanDeck(Vector3 eye, Vector3 plannedMotion)
        {
            if (_cc == null || !_cc.enabled || _heightTransitioning)
                return;
            Vector3 foot = eye;
            foot.y = eye.y - EyeHeight;
            if (!TryGetOceanSurfaceY(out float oceanY))
                return;
            if (!TryDeckForClimb(foot, foot, plannedMotion, out float deckY, out Collider deckCol))
                return;
            if (!TargetsDeckBounds(foot, foot, plannedMotion, deckCol.bounds))
                return;
            if (!NamedDeckSpawnPolicy.ShouldLiftOntoOceanDeck(foot.y, deckY, oceanY, targetsDeck: true))
                return;
            float rise = deckY - foot.y;
            _cc.stepOffset = Mathf.Min(rise + 0.05f, Mathf.Max(0.05f, _cc.height - 0.01f));
        }

        /// <summary>
        /// 摇杆已经指向海水上的桥面，但胶囊被竖直面挡住：直接把脚放到桥面上。
        /// 只处理高差在上台阶范围内的命名甲板。高桥底下的河床不会被吸上去。
        /// </summary>
        void TryClimbOceanDeck(Vector3 beforeEye, Vector3 plannedMotion)
        {
            if (!BlockWaterWalking || _heightTransitioning)
                return;
            if (!TryGetOceanSurfaceY(out float oceanY))
                return;

            Vector3 beforeFoot = beforeEye;
            beforeFoot.y = beforeEye.y - EyeHeight;
            Vector3 afterEye = transform.position;
            Vector3 afterFoot = afterEye;
            afterFoot.y = beforeFoot.y;

            if (!TryDeckForClimb(beforeFoot, afterFoot, plannedMotion, out float deckY, out Collider deckCol))
                return;
            Bounds box = deckCol.bounds;
            if (!TargetsDeckBounds(beforeFoot, afterFoot, plannedMotion, box))
                return;
            if (!NamedDeckSpawnPolicy.ShouldLiftOntoOceanDeck(beforeFoot.y, deckY, oceanY, targetsDeck: true))
                return;

            Vector3 intent = beforeFoot;
            intent.x += plannedMotion.x;
            intent.z += plannedMotion.z;
            Vector3 stand = ContainsXZ(box, intent)
                ? intent
                : ContainsXZ(box, afterFoot) ? afterFoot : intent;
            stand.x = Mathf.Clamp(stand.x, box.min.x, box.max.x);
            stand.z = Mathf.Clamp(stand.z, box.min.z, box.max.z);

            Vector3 eye = stand;
            eye.y = deckY + EyeHeight + 0.02f;
            bool ccOn = _cc != null && _cc.enabled;
            if (ccOn)
                _cc.enabled = false;
            transform.position = eye;
            Physics.SyncTransforms();
            if (ccOn)
                _cc.enabled = true;
            if (_verticalSpeed < 0f)
                _verticalSpeed = -GroundStickSpeed;
            RememberNamedDeck(new Vector3(stand.x, deckY, stand.z), deckY);
        }

        bool TryDeckForClimb(
            Vector3 beforeFoot, Vector3 afterFoot, Vector3 plannedMotion,
            out float deckY, out Collider deckCol)
        {
            Vector3 intent = beforeFoot;
            intent.x += plannedMotion.x;
            intent.z += plannedMotion.z;
            if (TryGetNamedDeckStand(intent, out deckY, out deckCol) && deckCol != null)
                return true;
            if (TryGetNamedDeckStand(afterFoot, out deckY, out deckCol) && deckCol != null)
                return true;
            return TryGetNamedDeckStand(beforeFoot, out deckY, out deckCol) && deckCol != null;
        }

        static bool TargetsDeckBounds(Vector3 beforeFoot, Vector3 afterFoot, Vector3 planned, Bounds box)
        {
            Vector3 intent = beforeFoot;
            intent.x += planned.x;
            intent.z += planned.z;
            bool overlaps = ContainsXZ(box, beforeFoot)
                || ContainsXZ(box, afterFoot)
                || ContainsXZ(box, intent);
            if (!overlaps)
                return false;
            if (ContainsXZ(box, beforeFoot))
                return true;
            return DistanceToBoundsXZ(beforeFoot, box) <= NamedDeckSpawnPolicy.OceanDeckClimbReachMeters;
        }

        static bool ContainsXZ(Bounds box, Vector3 p)
        {
            return p.x >= box.min.x && p.x <= box.max.x && p.z >= box.min.z && p.z <= box.max.z;
        }

        static float DistanceToBoundsXZ(Vector3 p, Bounds box)
        {
            float dx = 0f;
            if (p.x < box.min.x)
                dx = box.min.x - p.x;
            else if (p.x > box.max.x)
                dx = p.x - box.max.x;
            float dz = 0f;
            if (p.z < box.min.z)
                dz = box.min.z - p.z;
            else if (p.z > box.max.z)
                dz = p.z - box.max.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>脚的水平位置落在海水之上的命名桥面里，不是只被旁边的探针扫到。</summary>
        bool IsFootPlantedOnNamedOceanDeck(Vector3 foot)
        {
            if (!TryGetOceanSurfaceY(out float oceanY))
                return false;
            if (!TryGetNamedDeckStand(foot, out float deckY, out Collider deckCol) || deckCol == null)
                return false;
            if (!NamedDeckSpawnPolicy.IsOceanDeckSurface(deckY, oceanY))
                return false;
            if (foot.y < deckY - DeckFootSlop)
                return false;
            return ContainsXZ(deckCol.bounds, foot);
        }

        /// <summary>
        /// 脚确实站在高出水面的命名码头/桥上。礁石/地形最高点不算甲板。
        /// </summary>
        bool IsStandingOnNamedDeck(
            Vector3 foot, bool hasDeck, float deckY, Collider deckCol, float minDeckY)
        {
            if (!hasDeck || deckCol == null)
                return false;
            if (deckY < minDeckY)
                return false;
            if (foot.y < deckY - DeckFootSlop)
                return false;
            RememberNamedDeck(foot, deckY);
            return true;
        }

        void RememberNamedDeck(Vector3 foot, float deckY)
        {
            _lastNamedDeckFoot = foot;
            _lastNamedDeckY = deckY;
            _namedDeckGraceUntil = Time.time + NamedDeckGraceSeconds;
        }

        /// <summary>
        /// 刚还在甲板上：桥段 Box 缝 / 边缘单帧 miss 时不要清零摇杆或拽回岸。
        /// 只在上一甲板附近且高度仍合理时放行，避免宽限变成海里跑步。
        /// </summary>
        bool HasNamedDeckGrace(Vector3 foot, float minDeckY)
        {
            if (Time.time > _namedDeckGraceUntil)
                return false;
            if (_lastNamedDeckY < minDeckY)
                return false;
            float graceR = Mathf.Max(0.75f, BodyRadius * NamedDeckGraceRadiusFactor);
            float dx = foot.x - _lastNamedDeckFoot.x;
            float dz = foot.z - _lastNamedDeckFoot.z;
            if (dx * dx + dz * dz > graceR * graceR)
                return false;
            if (foot.y < _lastNamedDeckY - DeckFootSlop)
                return false;
            return true;
        }

        static bool IsNamedDeckCollider(Collider c)
        {
            if (c == null)
                return false;

            // 根常被 SealSpawnedNames 改成 envN；海边码头 Box 在根上，Dock 写在 LOD 子节点名里。
            Transform self = c.transform;
            if (NamedDeckSpawnPolicy.MatchesObjectName(self.name))
                return true;

            Transform p = self.parent;
            for (int i = 0; i < 6 && p != null; i++, p = p.parent)
            {
                if (NamedDeckSpawnPolicy.MatchesObjectName(p.name))
                    return true;
            }

            int childCount = self.childCount;
            for (int i = 0; i < childCount; i++)
            {
                if (NamedDeckSpawnPolicy.MatchesObjectName(self.GetChild(i).name))
                    return true;
            }

            return false;
        }

        /// <summary>高度图不高于海平面 → 海里（含平坦海底）。甲板例外在调用方处理。</summary>
        static bool IsOceanTerrainForbidden(float terrainY, float oceanY)
        {
            // 只禁真正没过海面的格子。听感 clearance(0.4) 不能拿来禁走。
            return terrainY <= oceanY + OceanWalkMinAboveSea;
        }

        /// <summary>岛高度图 Y（只认 TerrainData，不吃海面/石头物理面）。</summary>
        static bool TrySampleHeightmapY(Vector3 worldPos, out float y)
        {
            y = 0f;
            Terrain terrain = IslandSceneComponent.Active != null
                ? IslandSceneComponent.Active.Terrain
                : null;
            if (terrain != null && terrain.terrainData != null)
            {
                Vector3 snapped = worldPos;
                if (EnvGroundFit.TrySnapY(
                        ref snapped, terrain.terrainData, terrain.transform.position))
                {
                    y = snapped.y;
                    return true;
                }
            }

            Vector3 viaFit = worldPos;
            EnvGroundFit fit = GameEntry.GroundFit;
            if (fit != null && fit.TrySnapY(ref viaFit))
            {
                y = viaFit.y;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 身体半径内最高的命名甲板（Dock/Pier/Bridge/Qiao/Deck）。
        /// 单点射线容易落进相邻 SWS_DemoPier Box 缝，过桥会被禁水清零位移。
        /// </summary>
        bool TryGetNamedDeckStand(Vector3 foot, out float standY, out Collider standCol)
        {
            standY = float.NegativeInfinity;
            standCol = null;
            const float probeUp = 4f;
            const float probeDown = 6f;
            float dist = probeUp + probeDown;
            // 桥根和岸之间常隔着一截被判成海的高度图。探针要盖住这段，人才能走到板子上。
            float radius = Mathf.Max(
                NamedDeckSpawnPolicy.OceanDeckClimbReachMeters,
                Mathf.Max(0.35f, BodyRadius * 0.85f));
            bool found = false;

            for (int o = 0; o < NamedDeckProbeOffsets.Length; o++)
            {
                Vector2 xz = NamedDeckProbeOffsets[o] * radius;
                Vector3 origin = foot;
                origin.x += xz.x;
                origin.y += probeUp;
                origin.z += xz.y;
                int count = Physics.RaycastNonAlloc(
                    origin, Vector3.down, GroundHits, dist, GroundMask,
                    QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    if (GroundHits[i].normal.y < GroundNormalMinY)
                        continue;
                    if (IsWaterSurfaceCollider(GroundHits[i].collider))
                        continue;
                    if (!IsNamedDeckCollider(GroundHits[i].collider))
                        continue;
                    float y = GroundHits[i].point.y;
                    if (y <= standY)
                        continue;
                    standY = y;
                    standCol = GroundHits[i].collider;
                    found = true;
                }
            }

            return found;
        }

        static bool IsOceanSurfaceCollider(Collider c)
        {
            if (c == null)
                return false;
            Transform t = c.transform;
            for (int i = 0; i < 4 && t != null; i++, t = t.parent)
            {
                if (t.name.IndexOf("Ocean", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        static bool IsWaterSurfaceCollider(Collider c)
        {
            if (IsOceanSurfaceCollider(c))
                return true;
            if (c == null)
                return false;
            Transform t = c.transform;
            for (int i = 0; i < 4 && t != null; i++, t = t.parent)
            {
                if (t.name.IndexOf("InlandWater", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        static bool TryGetOceanSurfaceY(out float oceanY)
        {
            ISeaLevel sea = IslandSceneComponent.Active != null
                ? IslandSceneComponent.Active.Sea
                : null;
            if (sea != null)
            {
                oceanY = sea.Level;
                return true;
            }

            GameObject ocean = IslandWorldService.Instance != null
                ? IslandWorldService.Instance.OceanInstance
                : null;
            if (ocean != null)
            {
                oceanY = ocean.transform.position.y;
                return true;
            }

            // 海面尚未登记时也不能放行走海底：退回岸线常量。
            oceanY = AmbienceZoneCatalog.OceanSurfaceY;
            return true;
        }

        Vector3 SampleGround(Vector3 worldPos)
        {
            // 高度图贴地，不吃树冠/石头。射线先命中树会把切预设的眼睛抬飞。
            // 禁止把海底抬成「湿沙公路」——禁走只认高度图相对海平面。
            _sampledNamedDeck = false;
            TryGroundUnder(worldPos, out Vector3 ground);
            // 桥/码头：高度图是河床或桥下地面时，提示高度已在甲板上就改贴命名甲板。
            if (TryPreferNamedDeckOverWater(worldPos, ground, out Vector3 deck))
            {
                _sampledNamedDeck = true;
                return deck;
            }

            return ground;
        }

        /// <summary>
        /// 提示点已在命名甲板高度带内时改用甲板，不论脚下高度图算不算禁水。
        /// 走路误入桥下不会被拽上桥（提示高度仍在河床一带，策略会拒绝）。
        /// </summary>
        bool TryPreferNamedDeckOverWater(Vector3 hint, Vector3 terrainGround, out Vector3 deck)
        {
            deck = terrainGround;
            if (!TryNamedDeckUnder(hint, out deck))
                return false;
            return NamedDeckSpawnPolicy.ShouldPreferDeckOverWater(
                hintY: hint.y,
                terrainY: terrainGround.y,
                deckY: deck.y,
                eyeHeight: EyeHeight);
        }

        /// <summary>落点 footprint 内最高的命名码头/桥面（Dock/Pier/Bridge/Qiao/Deck）。</summary>
        bool TryNamedDeckUnder(Vector3 worldPos, out Vector3 deck)
        {
            deck = worldPos;
            // 读档/贴地：竖直范围比走路禁水探针更大，提示点可能远高于或略低于桥面。
            const float probeUp = 8f;
            const float probeDown = 24f;
            float dist = probeUp + probeDown;
            float radius = Mathf.Max(0.35f, BodyRadius * 0.85f);
            float bestY = float.NegativeInfinity;
            bool found = false;
            for (int o = 0; o < NamedDeckProbeOffsets.Length; o++)
            {
                Vector2 xz = NamedDeckProbeOffsets[o] * radius;
                Vector3 origin = worldPos;
                origin.x += xz.x;
                origin.y += probeUp;
                origin.z += xz.y;
                int count = Physics.RaycastNonAlloc(
                    origin, Vector3.down, GroundHits, dist, GroundMask,
                    QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    if (GroundHits[i].normal.y < GroundNormalMinY)
                        continue;
                    if (IsWaterSurfaceCollider(GroundHits[i].collider))
                        continue;
                    if (!IsNamedDeckCollider(GroundHits[i].collider))
                        continue;
                    float y = GroundHits[i].point.y;
                    if (y <= bestY)
                        continue;
                    bestY = y;
                    found = true;
                }
            }

            if (!found)
                return false;
            deck = new Vector3(worldPos.x, bestY, worldPos.z);
            return true;
        }

        // ─── Render-scale / FOV ────────────────────────────────────────────────────

        void ApplyRenderScale(bool walking)
        {
            // 预设面板固定 0.375（含闺蜜机标准版）。其它弹窗 / 原生半层 / 闲置沉浸同档。
            // 定时窗只抬 BottomIconsHidden、不抬 OverlayPresent，保持场景正常分辨率。
            float scale = PresetForm.IsOpen
                ? IslandCameraSrpSetup.PresetPanelRenderScale
                : HudOverlayGate.HasPopup() || NativeOverlayGate.OverlayPresent
                ? IslandCameraSrpSetup.PopupRenderScale
                : IslandIdleImmersive.IsActive
                    ? IslandCameraSrpSetup.ImmersiveRenderScale
                    : walking
                        ? IslandCameraSrpSetup.WalkingRenderScale
                        : IslandCameraSrpSetup.DefaultRenderScale;
            if (!Mathf.Approximately(scale, _appliedRenderScale))
            {
                IslandCameraSrpSetup.SetRenderScale(gameObject, scale);
                _appliedRenderScale = scale;
                // 这一帧会重建中间 RT。转头强制倒影让到后面两帧，避免叠在同一帧。
                IslandLocomotionState.MirrorTurnGraceFrames = 2;
            }

            // 默认 60；闲置沉浸 25 省电。原生半层 YieldsGpu 时由 NativeOverlayGate 接管。
            IslandCameraSrpSetup.ApplyTargetFrameRate();
        }

        void ApplyAspectFov()
        {
            if (GameEntry.IsTransientPortraitReport)
                return;

            float aspect = Screen.width / Mathf.Max(1f, Screen.height);
            if (_lastAspectForFov >= 0f && Mathf.Abs(aspect - _lastAspectForFov) <= 0.01f)
                return;

            Camera cam = View;
            if (cam == null)
                return;

            if (_authoredVerticalFov < 0f)
                _authoredVerticalFov = cam.fieldOfView;
            _lastAspectForFov = aspect;
            cam.ResetAspect();

            // Landscape: 横向视野锁 90°，按 aspect 反算 vertical FOV。
            if (aspect > 1f)
            {
                const float horizontalFovDeg = 90f;
                float half = horizontalFovDeg * Mathf.Deg2Rad * 0.5f;
                cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(half) / aspect) * Mathf.Rad2Deg;
                return;
            }

            // Portrait: 竖向视野取 authored；窄屏（<1080:1920）下按参考比例横向撑满。
            const float refAspect = 1080f / 1920f;
            cam.fieldOfView = _authoredVerticalFov;
            if (aspect >= refAspect)
                return;
            float tanHalfHorizontal = Mathf.Tan(_authoredVerticalFov * Mathf.Deg2Rad * 0.5f) * refAspect;
            cam.fieldOfView = 2f * Mathf.Atan(tanHalfHorizontal / aspect) * Mathf.Rad2Deg;
        }

    }
}