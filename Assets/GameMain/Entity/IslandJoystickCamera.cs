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
        [Tooltip("Reject moves onto seabed (below ocean surface) or into river channels.")]
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
        /// 高度图须至少高于海平面此值才可走。与 <see cref="AmbienceZoneCatalog.OceanLandClearanceMeters"/> 对齐。
        /// 旧逻辑用 oceanY-0.25 当湿沙放行，平坦海底≈海平面时整片海湾都能走。
        /// </summary>
        const float OceanWalkMinAboveSea = AmbienceZoneCatalog.OceanLandClearanceMeters;
        /// <summary>眼睛离地形矩形边的最小内缩，避免胶囊探出岛外踩上海面碰撞。</summary>
        const float TerrainEdgeMargin = 0.75f;
        /// <summary>河面水平距离 / 竖直容差（米）。</summary>
        const float RiverBanRadiusXZ = 5f;
        const float RiverBanRadiusY = 4f;
        const float RiverQueryRadius = 8f;

        static readonly RaycastHit[] GroundHits = new RaycastHit[24];

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

        /// <summary>脚落到 <paramref name="worldPos"/> 的地面上，眼睛抬到行走高度。出生和读档用。</summary>
        public void Teleport(Vector3 worldPos, float yawDegrees)
        {
            _drive = EyeDrive.Locomotion;
            _yaw = yawDegrees;
            _verticalSpeed = 0f;
            _smoothedMove = Vector2.zero;
            EnsureBody();
            Vector3 ground = SampleGround(worldPos);
            SetPose(ground + Vector3.up * EyeHeight, Quaternion.Euler(Pitch, _yaw, 0f));
            // 立刻打开落点附近 MeshCollider，避免走路穿大石。
            RefreshNearbyColliders();
        }

        void RefreshNearbyColliders()
        {
            if (_colliderLod == null)
                _colliderLod = FindAnyObjectByType<EnvMeshColliderLod>();
            float radius = Mathf.Max(ColliderRefreshMinRadius, EnvStreamingConfig.MeshColliderEnableRadius);
            _colliderLod?.ForceRefreshNear(transform.position, radius);
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
            if (_cc != null && _cc.enabled)
                MoveViaCharacterController(motion);
            else
                MoveFreeCam(motion);
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

            // CC 只动 XZ；高度过渡期间 Y 由 lerp 接管，避免与 CC 的贴地反力互拽。
            if (_heightTransitioning)
            {
                float t = Mathf.Clamp01(_heightElapsed / _heightDuration);
                Vector3 p = transform.position;
                p.y = Mathf.Lerp(_heightStart, _heightTarget, t);
                transform.position = p;
            }

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
                return;
            if (wasEnabled && _cc != null)
            {
                _cc.enabled = true;
                transform.SetPositionAndRotation(pos, rot);
                Physics.SyncTransforms();
            }
        }

        /// <summary>海上（高度图未过岸线净空）/ 河里：水平位移回滚。</summary>
        void ApplyWaterWalkBan(Vector3 beforeEye)
        {
            if (!BlockWaterWalking)
                return;
            Vector3 eye = transform.position;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (!IsFootInForbiddenWater(foot))
                return;

            bool ccOn = _cc != null && _cc.enabled;
            if (ccOn)
                _cc.enabled = false;
            eye.x = beforeEye.x;
            eye.z = beforeEye.z;
            transform.position = eye;
            Physics.SyncTransforms();
            if (ccOn)
                _cc.enabled = true;
        }

        /// <summary>
        /// 脚底不得沉到海底：最低只允许到海平面 − 浅滩容差（湿沙）。
        /// 不抬到海平面本身，避免被托上海面盒子后一直走。
        /// </summary>
        void ClampFootNotBelowOcean()
        {
            if (!TryGetOceanSurfaceY(out float oceanY))
                return;

            float minFootY = oceanY - OceanBelowSurfaceBan;
            Vector3 eye = transform.position;
            float footY = eye.y - EyeHeight;
            if (footY >= minFootY - 1e-3f)
                return;

            bool ccOn = _cc != null && _cc.enabled;
            if (ccOn)
                _cc.enabled = false;
            eye.y = minFootY + EyeHeight;
            transform.position = eye;
            Physics.SyncTransforms();
            if (ccOn)
                _cc.enabled = true;
        }

        Vector3 ClampEyeOutOfWater(Vector3 eye)
        {
            if (!BlockWaterWalking)
                return eye;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (!IsFootInForbiddenWater(foot))
                return eye;
            // 传送落点在海里：抬到可走岸线以上。河面没有可站立碰撞时保持原位，走路时再由禁行回滚。
            if (TryGetOceanSurfaceY(out float oceanY) && foot.y <= oceanY + OceanWalkMinAboveSea)
                return new Vector3(eye.x, oceanY + OceanWalkMinAboveSea + EyeHeight + 0.5f, eye.z);
            return eye;
        }

        bool IsFootInForbiddenWater(Vector3 foot)
        {
            bool hasStand = TryGetHighestStandY(foot, out float standY);

            // 海：高度图必须明显高于海面；海面盒是 Trigger 不能托人。码头/桥按甲板放行。
            if (TryGetOceanSurfaceY(out float oceanY))
            {
                const float deckAboveSea = 0.5f;
                bool onDeckAboveSea = hasStand && standY >= oceanY + deckAboveSea;
                if (!onDeckAboveSea)
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

            // 河：水平近河道且竖直也在河面容差内；已站在桥/码头甲板（明显高于河面）则放行。
            WaterSurfaceProbe probe = GameEntry.World?.WaterProbe;
            if (probe != null && probe.TryNearestRiver(
                    foot, RiverQueryRadius, out Vector3 nearest, out _))
            {
                float dx = foot.x - nearest.x;
                float dz = foot.z - nearest.z;
                float xzSq = dx * dx + dz * dz;
                if (xzSq <= RiverBanRadiusXZ * RiverBanRadiusXZ
                    && Mathf.Abs(foot.y - nearest.y) <= RiverBanRadiusY)
                {
                    const float deckAboveRiver = 0.5f;
                    if (hasStand && standY >= nearest.y + deckAboveRiver)
                        return false;
                    return true;
                }
            }

            return false;
        }

        /// <summary>高度图未过岸线净空 → 海里（含平坦海底）。甲板例外在调用方处理。</summary>
        static bool IsOceanTerrainForbidden(float terrainY, float oceanY)
        {
            // 与岸线 land 判定对齐：land 须 h > ocean+clearance，故 <= 一律禁。
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
        /// 脚附近最高的朝上碰撞面（码头 BoxCollider 高于海床）。忽略海/河碰撞体本身。
        /// </summary>
        bool TryGetHighestStandY(Vector3 foot, out float standY)
        {
            standY = float.NegativeInfinity;
            const float probeUp = 4f;
            const float probeDown = 6f;
            Vector3 origin = foot + Vector3.up * probeUp;
            float dist = probeUp + probeDown;
            int count = Physics.RaycastNonAlloc(
                origin, Vector3.down, GroundHits, dist, GroundMask,
                QueryTriggerInteraction.Ignore);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (GroundHits[i].normal.y < GroundNormalMinY)
                    continue;
                if (IsWaterSurfaceCollider(GroundHits[i].collider))
                    continue;
                float y = GroundHits[i].point.y;
                if (y <= standY)
                    continue;
                standY = y;
                found = true;
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
            oceanY = 0f;
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
            if (ocean == null)
                return false;
            oceanY = ocean.transform.position.y;
            return true;
        }

        Vector3 SampleGround(Vector3 worldPos)
        {
            // 高度图贴地，不吃树冠/石头。射线先命中树会把切预设的眼睛抬飞。
            TryGroundUnder(worldPos, out Vector3 ground);
            // 不抬到海平面（否则人站上海面盒）；只托住过深海底到浅滩下限。
            if (TryGetOceanSurfaceY(out float oceanY))
            {
                float minY = oceanY - OceanBelowSurfaceBan;
                if (ground.y < minY)
                    ground.y = minY;
            }

            return ground;
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