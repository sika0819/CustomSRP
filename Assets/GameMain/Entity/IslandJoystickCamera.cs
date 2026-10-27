using Game.Native;
using UnityEngine;

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
        public float Pitch = 12f;
        /// <summary>
        /// Eye above ground. Boluo <c>Player.selfHeight</c> / preset <c>playerHeight</c> is 6;
        /// this rig puts the transform at the eye, so the value is used directly.
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

        [Header("NavMesh clamp")]
        [Tooltip("Clamp horizontal motion to baked IslandNavMesh (sea/cliff edges; rivers skipped). Off or missing data = no clamp.")]
        public bool UseNavMeshClamp = true;
        [Tooltip("Reject moves onto seabed (below ocean surface). Rivers stay walkable.")]
        public bool BlockWaterWalking = true;

        // ─── Constants ─────────────────────────────────────────────────────────────

        /// <summary>Stick magnitude above this counts as walking for render-scale drop.</summary>
        const float WalkRenderScaleThreshold = 0.05f;
        const float InputSmooth = 7f;
        const float GroundStickSpeed = 2f;
        /// <summary>Degrees per pixel at <see cref="RefLookWidth"/> (portrait design 1125).</summary>
        const float LookSensitivity = 0.11f;
        /// <summary>Portrait design width; wider screens scale look down so full-width swipes feel similar.</summary>
        const float RefLookWidth = 1125f;
        const float ColliderRefreshMinRadius = 48f;
        const float GroundNormalMinY = 0.45f;
        /// <summary>脚底低于海平面超过此值视为走进海里（允许湿沙滩浅滩）。</summary>
        const float OceanBelowSurfaceBan = 0.25f;
        /// <summary>近河判定：跳过 NavMesh 钳位（旧烤盘河洞），允许沿河道行走。</summary>
        const float RiverWalkRadiusXZ = 5f;
        const float RiverWalkRadiusY = 4f;
        const float RiverQueryRadius = 8f;
        /// <summary>悬崖 / 大石顶允许落下的最大高度；超出仍钳回蓝面，避免掉进虚空。</summary>
        const float MaxDropHeight = 80f;
        /// <summary>落点须明显低于当前脚底，才算「往下跳」而非同平面蓝面修正。</summary>
        const float DropMinDeltaY = 0.45f;

        static readonly RaycastHit[] GroundHits = new RaycastHit[24];

        // ─── State ─────────────────────────────────────────────────────────────────

        public static IslandJoystickCamera Current { get; private set; }

        CharacterController _cc;
        EnvMeshColliderLod _colliderLod;

        Vector2 _smoothedMove;
        float _speedRatio;
        float _yaw;
        float _verticalSpeed;

        /// <summary>Inspector-authored <see cref="EyeHeight"/>. Cached on first OnEnable so a
        /// preset that bumps EyeHeight for StayBehind gets reverted to this when walking starts.</summary>
        float _defaultEyeHeight = -1f;

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

        /// <summary>After <see cref="ApplyPreset"/>, keep the eye planted (no gravity step)
        /// until the player walks (stick Y).</summary>
        bool _holdPresetFraming;

        /// <summary>Preset iris transition owns pose; LateUpdate only keeps rotation.</summary>
        bool _transitionDriven;

        Vector3 _exitDollyFrom;
        Vector3 _exitDollyTo;
        float _exitDollyT;
        Vector3 _enterDollyFrom;
        Vector3 _enterDollyTo;
        float _enterDollyYawFrom;
        float _enterDollyYawTo;
        bool _enterDollyActive;

        const float ExitPullMeters = 2.2f;
        const float ExitWalkSpeedScale = 0.4f;
        const float RevealFarMinDistance = 6f;
        const float RevealFarDistanceScale = 2.5f;

        /// <summary>True while the height lerp is in progress.</summary>
        public bool IsHeightTransitioning => _heightTransitioning;

        // ─── Lifecycle ─────────────────────────────────────────────────────────────

        void OnEnable()
        {
            Current = this;
            if (_defaultEyeHeight < 0f)
                _defaultEyeHeight = EyeHeight;
            if (_colliderLod == null)
                _colliderLod = FindAnyObjectByType<EnvMeshColliderLod>();
            EnsureBody();
            ApplyRenderScale(false);
        }

        void OnDisable()
        {
            IslandLocomotionState.IsWalking = false;
            ApplyRenderScale(false);
            if (Current == this)
                Current = null;
        }

        // ─── Public pose API ───────────────────────────────────────────────────────

        public void ApplySpawnPose() =>
            Teleport(IslandSpawnPose.DefaultPos, IslandSpawnPose.DefaultYawDegrees);

        /// <summary>Current horizontal look (degrees). Used by startup look warmup.</summary>
        public float YawDegrees => _yaw;

        /// <summary>
        /// Look-only pose tweak (no ground resample / collider refresh). Used under the
        /// loading cover to force a few frusta through CRP before first player turn.
        /// </summary>
        public void PeekYaw(float yawDegrees)
        {
            _yaw = yawDegrees;
            transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
        }

        public void Teleport(Vector3 worldPos, float yawDegrees)
        {
            _holdPresetFraming = false;
            _yaw = yawDegrees;
            _verticalSpeed = 0f;
            EnsureBody();
            Vector3 ground = SampleGround(worldPos);
            SetPose(ground + Vector3.up * EyeHeight, Quaternion.Euler(Pitch, _yaw, 0f));
            // 对齐 Boluo 切落点：立刻打开落点附近 MeshCollider，避免走路穿大石。
            RefreshNearbyColliders();
        }

        public void ApplyPreset(IslandPresetAsset preset)
        {
            if (preset == null)
                return;
            if (preset.PlayerHeight > 0.1f)
                EyeHeight = preset.PlayerHeight;

            ComputePresetPose(preset, preset.CameraToTargetDis, out Vector3 camPos, out float yaw);
            _yaw = yaw;
            // 切预设只改左右朝向。眼睛高于注视点，LookAt 会算出低头，把俯仰钉在水平。
            Pitch = 0f;
            _verticalSpeed = 0f;
            _holdPresetFraming = true;
            _enterDollyActive = false;

            SetPose(camPos, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
            RefreshNearbyColliders();
            // Screen-UV planar mirror is pose-locked; Gmj throttle can swallow the
            // snap then idle-reuse the old RT → water shows no reflection.
            IslandLocomotionState.ForceMirrorRedraw = true;
        }

        /// <summary>Iris exit: plant dolly from current eye toward a closer point along look.</summary>
        public void BeginExitDolly()
        {
            _transitionDriven = true;
            _heightTransitioning = false;
            _verticalSpeed = 0f;
            SetBodyCollisionEnabled(false);
            _exitDollyFrom = transform.position;
            _exitDollyT = 0f;
            Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            Vector3 near = _exitDollyFrom + forward * ExitPullMeters;
            Vector3 ground = SampleGround(near);
            _exitDollyTo = new Vector3(near.x, ground.y + EyeHeight, near.z);
        }

        public void SampleExitDolly(float t01)
        {
            t01 = Mathf.Clamp01(t01);
            _exitDollyT = t01;
            Vector3 pos = Vector3.Lerp(_exitDollyFrom, _exitDollyTo, t01);
            SetPose(pos, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
        }

        /// <summary>
        /// Non-low-end exit: small forward walk baked into the dolly path.
        /// Free cinematic motion — no CharacterController / NavMesh.
        /// </summary>
        public void NudgeExitWalk(float dt)
        {
            if (dt <= 0f)
                return;
            Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            Vector3 delta = forward * (MoveSpeed * ExitWalkSpeedScale * dt);
            _exitDollyFrom += delta;
            _exitDollyTo += delta;
            Vector3 pos = Vector3.Lerp(_exitDollyFrom, _exitDollyTo, _exitDollyT);
            SetPose(pos, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
        }

        /// <summary>
        /// Black-swap teleport: land at a farther framing, then <see cref="SampleEnterDolly"/>
        /// eases to the preset everyday distance.
        /// </summary>
        public void ApplyPresetForReveal(IslandPresetAsset preset)
        {
            if (preset == null)
                return;
            if (preset.PlayerHeight > 0.1f)
                EyeHeight = preset.PlayerHeight;

            Pitch = 0f;
            _verticalSpeed = 0f;
            _heightTransitioning = false;
            _holdPresetFraming = true;
            _transitionDriven = true;
            SetBodyCollisionEnabled(false);

            float finalDist = preset.CameraToTargetDis;
            if (finalDist <= 0f)
            {
                ComputePresetPose(preset, finalDist, out Vector3 camPos, out float yaw);
                _yaw = yaw;
                _enterDollyActive = false;
                SetPose(camPos, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
                RefreshNearbyColliders();
                IslandLocomotionState.ForceMirrorRedraw = true;
                return;
            }

            float farDist = Mathf.Max(finalDist * RevealFarDistanceScale, RevealFarMinDistance);
            ComputePresetPose(preset, farDist, out Vector3 farPos, out float farYaw);
            ComputePresetPose(preset, finalDist, out Vector3 nearPos, out float nearYaw);
            _yaw = farYaw;
            _enterDollyFrom = farPos;
            _enterDollyTo = nearPos;
            _enterDollyYawFrom = farYaw;
            _enterDollyYawTo = nearYaw;
            _enterDollyActive = true;

            SetPose(farPos, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
            RefreshNearbyColliders();
            IslandLocomotionState.ForceMirrorRedraw = true;
        }

        public void SampleEnterDolly(float t01)
        {
            t01 = Mathf.Clamp01(t01);
            if (!_enterDollyActive)
                return;
            Vector3 pos = Vector3.Lerp(_enterDollyFrom, _enterDollyTo, t01);
            _yaw = Mathf.LerpAngle(_enterDollyYawFrom, _enterDollyYawTo, t01);
            SetPose(pos, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
        }

        public void EndTransitionDrive()
        {
            if (_enterDollyActive)
            {
                _yaw = _enterDollyYawTo;
                SetPose(_enterDollyTo, Quaternion.Euler(Pitch, _yaw, 0f), unconstrained: true);
                _enterDollyActive = false;
            }

            _transitionDriven = false;
            _holdPresetFraming = true;
            _verticalSpeed = 0f;
            EnsureBody();
            // Final plant after iris enter-dolly — refresh planar RT before idle hold.
            IslandLocomotionState.ForceMirrorRedraw = true;
        }

        /// <summary>
        /// 退登 / 二次揭幕：清 iris 驱动与 StayBehind 闩，避免「有声但不能走」。
        /// </summary>
        public void ForceUnlockLocomotion()
        {
            _transitionDriven = false;
            _enterDollyActive = false;
            _holdPresetFraming = false;
            _heightTransitioning = false;
            _verticalSpeed = 0f;
            SetBodyCollisionEnabled(true);
            EnsureBody();
        }

        void ComputePresetPose(IslandPresetAsset preset, float cameraToTargetDis, out Vector3 camPos, out float yaw)
        {
            Vector3 ground = SampleGround(preset.TargetPos);
            float yawOffset = Mathf.DeltaAngle(0f, preset.CameraOffset.y);

            // CameraToTargetDis <= 0：眼睛落在 TargetPos 的 XZ。
            // TargetPos.y > 1 时用该高度（准备入睡），否则 y 是占位，高度 = 地面 + EyeHeight。
            if (cameraToTargetDis <= 0f)
            {
                float eyeY = ground.y + EyeHeight;
                if (preset.TargetPos.y > 1f)
                {
                    eyeY = preset.TargetPos.y;
                    float stand = eyeY - ground.y;
                    if (stand > 0.5f && stand < 40f)
                        EyeHeight = stand;
                }

                camPos = new Vector3(preset.TargetPos.x, eyeY, preset.TargetPos.z);
                yaw = preset.TargetRotateY + yawOffset;
                return;
            }

            // StayBehind：眼睛在 behind 点贴地 + EyeHeight。behind.y 是注视点半高，
            // 用目标处地表高度重新采样 behind 的 XZ，避免 behind.y + EyeHeight 把眼睛抬飞。
            // Unity yaw：forward = (sin(y), 0, cos(y))，behind = lookAt − forward * dist。
            // 严格按注释：behind = lookAt − (sin·d, 0, cos·d)。旧版写成了 +sin·d，
            // 落在注视点同一侧而不是正后方，HorizontalYaw 会把 yaw 翻 ~180°（取负）。
            float halfHeight = Mathf.Max(0.5f, EyeHeight * 0.5f);
            Vector3 lookAt = ground + Vector3.up * halfHeight;
            float dist = Mathf.Max(0.05f, cameraToTargetDis);
            float yawRad = preset.TargetRotateY * Mathf.Deg2Rad;
            Vector3 forwardXZ = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
            Vector3 behind = lookAt - forwardXZ * dist;
            Vector3 camGround = SampleGround(new Vector3(behind.x, ground.y, behind.z));
            camPos = new Vector3(behind.x, camGround.y + EyeHeight, behind.z);
            yaw = HorizontalYaw(camPos, lookAt) + yawOffset;
        }

        void ReleasePresetFraming()
        {
            _holdPresetFraming = false;
            _verticalSpeed = 0f;

            // Preset 期间可能临时把 EyeHeight 拉高（StayBehind 鸟瞰 / 高位置注视点）。
            // 走路时还原为 Inspector 默认值，让相机自然回到 FP 眼睛高度。
            if (_defaultEyeHeight > 0f && !Mathf.Approximately(EyeHeight, _defaultEyeHeight))
            {
                EyeHeight = _defaultEyeHeight;
                _appliedEyeHeight = -1f; // force EnsureBody to rebuild the capsule
            }

            Vector3 ground = SampleGround(transform.position);
            EnsureBody();
            float startY = transform.position.y;
            float targetY = ground.y + EyeHeight;

            if (HeightTransitionDuration > 0f && Mathf.Abs(startY - targetY) > 0.01f)
            {
                // Keep current (preset) Y visible; the height lerp in LateUpdate will drop it.
                StartHeightTransition(startY, targetY);
            }
            else
            {
                SetPose(
                    new Vector3(transform.position.x, targetY, transform.position.z),
                    Quaternion.Euler(Pitch, _yaw, 0f));
            }

            RefreshNearbyColliders();
        }

        // ─── Per-frame ─────────────────────────────────────────────────────────────

        void LateUpdate()
        {
            ApplyAspectFov();

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            if (_transitionDriven)
            {
                // Form owns world position during iris exit / enter.
                IslandLocomotionState.IsWalking = false;
                transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
                ApplyRenderScale(false);
                return;
            }

            // 地形未就绪（含退登 World.Release 后、二次进岛加载中）：禁止重力下坠。
            // InitAndArm 在 Services 就 Teleport，若此处仍积分垂直速度，会在揭罩前掉进地底。
            if (!IsWorldGroundReady())
            {
                _verticalSpeed = 0f;
                IslandLocomotionState.IsWalking = false;
                LocomotionInput.ClearAll();
                ApplyRenderScale(false);
                transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
                return;
            }

            ConsumeLookInput();
            ConsumeMoveInput(dt);

            bool walking = IsWalking;
            IslandLocomotionState.IsWalking = walking;

            if (_holdPresetFraming)
            {
                // StayBehind pose: allow yaw only; walking releases into FP locomotion.
                if (walking)
                    ReleasePresetFraming();
                else
                {
                    IslandLocomotionState.IsWalking = false;
                    ApplyStickYaw(dt);
                    ApplyRenderScale(false);
                    transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
                    return;
                }
            }

            EnsureBody();
            ApplyRenderScale(walking);
            TickHeightTransition(dt);

            Vector3 motion = ComputeMotion(dt);
            if (!_heightTransitioning)
                motion.y = IntegrateVertical(dt);
            MoveBody(motion);

            transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
        }

        /// <summary>
        /// Terrain+ocean 已挂到场景；退登 Release 或尚未 Load 完时为 false。
        /// </summary>
        static bool IsWorldGroundReady()
        {
            IslandWorldService world = IslandWorldService.Instance ?? GameEntry.World;
            return world != null && world.IsTerrainReady;
        }

        void ConsumeLookInput()
        {
            if (!LocomotionInput.ConsumeLook(out Vector2 look))
                return;
            // Screen-relative: same fraction of width → same yaw. Ref width keeps portrait feel.
            float scale = RefLookWidth / Mathf.Max(1f, Screen.width);
            _yaw += look.x * LookSensitivity * scale;
        }

        void ConsumeMoveInput(float dt)
        {
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
            if (_cc != null && _cc.enabled)
                MoveViaCharacterController(motion);
            else
                MoveFreeCam(motion);
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

            ApplyNavMeshClampXZ(before);
            ApplyWaterWalkBan(before);
        }

        void MoveFreeCam(Vector3 motion)
        {
            Vector3 before = transform.position;
            Vector3 pos = transform.position + motion;
            if (Mathf.Abs(motion.x) + Mathf.Abs(motion.z) <= 1e-4f)
            {
                transform.position = pos;
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
            ApplyNavMeshClampXZ(before);
            ApplyWaterWalkBan(before);
        }

        // ─── Pose / sampling ───────────────────────────────────────────────────────

        /// <param name="unconstrained">
        /// Preset / iris dolly: exact authored eye — skip NavMesh and water clamps.
        /// While <see cref="_transitionDriven"/>, CharacterController stays off so PhysX
        /// cannot shove the capsule mid-animation.
        /// </param>
        void SetPose(Vector3 pos, Quaternion rot, bool unconstrained = false)
        {
            _verticalSpeed = 0f;
            if (!unconstrained)
            {
                pos = ClampEyeToNavMesh(pos);
                pos = ClampEyeOutOfWater(pos);
            }

            // Disable CC so Move() / overlap resolve doesn't fight the placement.
            bool wasEnabled = _cc != null && _cc.enabled;
            if (wasEnabled)
                _cc.enabled = false;
            transform.SetPositionAndRotation(pos, rot);
            if (_transitionDriven)
                return;
            if (wasEnabled)
                _cc.enabled = true;
        }

        void SetBodyCollisionEnabled(bool on)
        {
            if (!on)
            {
                if (_cc != null && _cc.enabled)
                    _cc.enabled = false;
                return;
            }

            EnsureBody();
        }

        /// <summary>
        /// 水平位移后把脚底 XZ 钳回 NavMesh；采样失败则整帧水平位移回滚到 <paramref name="beforeEye"/>。
        /// 近河跳过（河道预期可走；旧烤盘可能仍挖了河洞）。Y 仍由 CC / SampleGround 负责。
        /// 悬崖 / 大石顶：下方有可站立落点（非海）时允许走出蓝面下落；下落途中不再把 XZ 拽回崖边。
        /// </summary>
        void ApplyNavMeshClampXZ(Vector3 beforeEye)
        {
            if (!UseNavMeshClamp)
                return;
            IslandNavMeshService svc = IslandNavMeshService.Instance;
            if (svc == null || !svc.IsReady)
                return;

            Vector3 eye = transform.position;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (IsFootNearRiver(foot))
                return;

            // 已离地下落：禁止把 XZ 钳回高处蓝面（否则会贴回崖边落不下去）。
            if (IsFalling())
                return;

            float maxDist = Mathf.Max(0.5f, BodyRadius * 2f);

            if (IslandNavMeshService.TryClampFootXZ(foot, maxDist, out Vector3 clampedFoot))
            {
                float dx = clampedFoot.x - foot.x;
                float dz = clampedFoot.z - foot.z;
                if (dx * dx + dz * dz < 1e-8f)
                    return;
                // 会被拽回崖边：若脚下方有落点则放行，让重力接下落。
                if (CanDropToGroundBelow(foot))
                    return;
                eye.x = clampedFoot.x;
                eye.z = clampedFoot.z;
                transform.position = eye;
                return;
            }

            // 走出蓝面：有落点则允许往下跳；否则水平回到本帧起点。
            if (CanDropToGroundBelow(foot))
                return;

            eye.x = beforeEye.x;
            eye.z = beforeEye.z;
            transform.position = eye;
        }

        bool IsFalling()
        {
            if (_verticalSpeed >= -GroundStickSpeed)
                return false;
            if (_cc != null && _cc.enabled)
                return !_cc.isGrounded;
            return true;
        }

        /// <summary>
        /// 脚底下方 <see cref="MaxDropHeight"/> 内有更低的朝上可站立面（跳过当前石顶/崖面），且落点不是禁走海床。
        /// </summary>
        bool CanDropToGroundBelow(Vector3 foot)
        {
            Vector3 origin = foot + Vector3.up * 0.25f;
            float dist = MaxDropHeight + 0.25f;
            int count = Physics.RaycastNonAlloc(
                origin, Vector3.down, GroundHits, dist, GroundMask,
                QueryTriggerInteraction.Ignore);
            // 取「明显低于脚底」的最高落点，避免把当前大石顶当成落点而误判不能跳。
            float bestY = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (GroundHits[i].normal.y < GroundNormalMinY)
                    continue;
                if (IsWaterSurfaceCollider(GroundHits[i].collider))
                    continue;
                float y = GroundHits[i].point.y;
                if (foot.y - y < DropMinDeltaY)
                    continue;
                if (y <= bestY)
                    continue;
                bestY = y;
                found = true;
            }

            if (!found)
                return false;

            Vector3 landing = new Vector3(foot.x, bestY, foot.z);
            return !IsFootInForbiddenWater(landing);
        }

        /// <summary>海上（脚底落在海平面下的海底）：水平位移回滚。河不禁。</summary>
        void ApplyWaterWalkBan(Vector3 beforeEye)
        {
            if (!BlockWaterWalking)
                return;
            Vector3 eye = transform.position;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (!IsFootInForbiddenWater(foot))
                return;
            eye.x = beforeEye.x;
            eye.z = beforeEye.z;
            transform.position = eye;
        }

        Vector3 ClampEyeToNavMesh(Vector3 eye)
        {
            if (!UseNavMeshClamp)
                return eye;
            IslandNavMeshService svc = IslandNavMeshService.Instance;
            if (svc == null || !svc.IsReady)
                return eye;

            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (IsFootNearRiver(foot))
                return eye;
            // 传送 / 预设允许更大搜索半径，避免出生点略偏蓝面外。
            float maxDist = Mathf.Max(8f, BodyRadius * 8f);
            if (!IslandNavMeshService.TryClampFootXZ(foot, maxDist, out Vector3 clampedFoot))
                return eye;
            return new Vector3(clampedFoot.x, eye.y, clampedFoot.z);
        }

        Vector3 ClampEyeOutOfWater(Vector3 eye)
        {
            if (!BlockWaterWalking)
                return eye;
            Vector3 foot = eye - Vector3.up * EyeHeight;
            if (!IsFootInForbiddenWater(foot))
                return eye;
            // 传送落点在海里：抬出海平面。
            if (TryGetOceanSurfaceY(out float oceanY) && foot.y < oceanY)
                return new Vector3(eye.x, oceanY + EyeHeight + 0.5f, eye.z);
            return eye;
        }

        /// <summary>近河：旧烤盘河洞不再钳走；河床由 CC / 高度图贴地。</summary>
        bool IsFootNearRiver(Vector3 foot)
        {
            if (!WaterSurfaceProbe.TryNearestRiver(
                    foot, RiverQueryRadius, out Vector3 nearest, out _))
                return false;
            float dx = foot.x - nearest.x;
            float dz = foot.z - nearest.z;
            return dx * dx + dz * dz <= RiverWalkRadiusXZ * RiverWalkRadiusXZ
                && Mathf.Abs(foot.y - nearest.y) <= RiverWalkRadiusY;
        }

        bool IsFootInForbiddenWater(Vector3 foot)
        {
            // 站立面优先用物理朝上碰撞（码头/桥面）；高度图在桥下是海底，不能当站立面。
            bool hasStand = TryGetHighestStandY(foot, out float standY);

            // 海：站立面（或无碰撞时的高度图）低于海平面 → 海底。河允许走。
            if (TryGetOceanSurfaceY(out float oceanY))
            {
                float groundY = hasStand ? standY : SampleGround(foot).y;
                if (groundY < oceanY - OceanBelowSurfaceBan)
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

        static bool IsWaterSurfaceCollider(Collider c)
        {
            if (c == null)
                return false;
            Transform t = c.transform;
            for (int i = 0; i < 4 && t != null; i++, t = t.parent)
            {
                string n = t.name;
                if (n.IndexOf("Ocean", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (n.IndexOf("InlandWater", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        static bool TryGetOceanSurfaceY(out float oceanY)
        {
            oceanY = 0f;
            GameObject ocean = IslandWorldService.Instance != null
                ? IslandWorldService.Instance.OceanInstance
                : null;
            if (ocean == null)
                return false;
            oceanY = ocean.transform.position.y;
            return true;
        }

        void RefreshNearbyColliders()
        {
            float radius = Mathf.Max(ColliderRefreshMinRadius, EnvStreamingConfig.MeshColliderEnableRadius);
            _colliderLod?.ForceRefreshNear(transform.position, radius);
        }

        Vector3 SampleGround(Vector3 worldPos)
        {
            // 高度图贴地，不吃树冠/石头。射线先命中树会把切预设的眼睛抬飞。
            Vector3 snapped = worldPos;
            if (EnvGroundFit.TrySnapY(ref snapped))
                return snapped;

            Vector3 origin = worldPos + Vector3.up * TerrainProbeUp;
            float dist = TerrainProbeUp + TerrainProbeDown;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, GroundHits, dist, GroundMask,
                QueryTriggerInteraction.Ignore);
            float bestY = float.PositiveInfinity;
            Vector3 best = worldPos;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                // 树干/墙面法线接近水平；只取朝上的表面，避免落在树冠上。
                if (GroundHits[i].normal.y < GroundNormalMinY)
                    continue;
                // 海/河碰撞体是床底，取最低点会把 Teleport 钉进地底。
                if (IsWaterSurfaceCollider(GroundHits[i].collider))
                    continue;
                if (GroundHits[i].point.y >= bestY)
                    continue;
                bestY = GroundHits[i].point.y;
                best = GroundHits[i].point;
                found = true;
            }

            return found ? best : worldPos;
        }

        // ─── Render-scale / FOV ────────────────────────────────────────────────────

        void ApplyRenderScale(bool walking)
        {
            // 弹窗 / 原生半层 / 闲置沉浸同档；数值见 IslandCameraSrpSetup（手机 / 闺蜜机分档）。
            // 定时窗只抬 BottomIconsHidden、不抬 OverlayPresent，保持场景正常分辨率。
            float scale = HudOverlayGate.HasPopup() || NativeOverlayGate.OverlayPresent
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

            Camera cam = GetComponent<Camera>();
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

        // ─── Math helpers ──────────────────────────────────────────────────────────

        /// <summary>水平朝向（度）。Unity yaw：forward = (sin(yaw), 0, cos(yaw))。</summary>
        static float HorizontalYaw(Vector3 from, Vector3 to)
        {
            Vector3 flat = to - from;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f)
                return 0f;
            return Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }
    }
}