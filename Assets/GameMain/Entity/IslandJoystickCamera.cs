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
        public static IslandJoystickCamera Current { get; private set; }

        public float MoveSpeed = 18f;
        public float StickYawSpeed = 120f;
        public float Pitch = 12f;
        public float EyeHeight = 4f;
        public float Gravity = 28f;
        public float TerrainProbeUp = 80f;
        public float TerrainProbeDown = 200f;
        public LayerMask GroundMask = ~0;

        [Header("Body collision")]
        [Tooltip("Capsule radius for rock/cliff blocking. 0 disables CharacterController collision.")]
        public float BodyRadius = 0.45f;
        [Tooltip("Extra capsule height above the eye point.")]
        public float HeadClearance = 0.5f;
        public float SlopeLimit = 45f;
        public float StepOffset = 0.4f;

        float _yaw;
        Vector2 _smoothedMove;
        CharacterController _cc;
        float _verticalSpeed;
        float _appliedEyeHeight = -1f;
        float _appliedRadius = -1f;
        float _appliedRenderScale = -1f;
        const float InputSmooth = 10f;
        /// <summary>Stick magnitude above this counts as walking for render-scale drop.</summary>
        const float WalkRenderScaleThreshold = 0.05f;
        /// <summary>Small downward speed while grounded so CC keeps contact.</summary>
        const float GroundStickSpeed = 2f;

        void OnEnable()
        {
            Current = this;
            EnsureBody();
            ApplyRenderScale(false);
        }

        void OnDisable()
        {
            ApplyRenderScale(false);
            if (Current == this)
                Current = null;
        }

        public void ApplySpawnPose()
        {
            Teleport(IslandSpawnPose.DefaultPos, IslandSpawnPose.DefaultYawDegrees);
        }

        public void Teleport(Vector3 worldPos, float yawDegrees)
        {
            _yaw = yawDegrees;
            _verticalSpeed = 0f;
            EnsureBody();
            Vector3 ground = SampleGround(worldPos);
            SetPose(ground + Vector3.up * EyeHeight, Quaternion.Euler(Pitch, _yaw, 0f));
            // 对齐 Boluo 切落点：立刻打开落点附近 MeshCollider，避免走路穿大石。
            float colRadius = Mathf.Max(48f, EnvStreamingConfig.MeshColliderEnableRadius);
            EnvMeshColliderLod lod = FindAnyObjectByType<EnvMeshColliderLod>();
            lod?.ForceRefreshNear(transform.position, colRadius);
        }

        public void ApplyPreset(IslandPresetAsset preset)
        {
            if (preset == null)
                return;
            if (preset.CameraToTargetDis > 0.1f)
                EyeHeight = Mathf.Max(1f, preset.PlayerHeight * 0.5f);
            EnsureBody();
            Teleport(preset.TargetPos, preset.TargetRotateY);
            if (preset.CameraOffset.sqrMagnitude > 0.01f)
                Pitch = preset.CameraOffset.x;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            EnsureBody();

            if (LocomotionInput.ConsumeLook(out Vector2 look))
                _yaw += look.x * 0.18f;

            Vector2 want = Vector2.zero;
            float speedRatio = 0f;
            if (LocomotionInput.Consume(out Vector2 dir, out float speed))
            {
                want = dir;
                speedRatio = Mathf.Clamp01(speed);
            }

            _smoothedMove = Vector2.MoveTowards(_smoothedMove, want, InputSmooth * dt);
            float moveMag = _smoothedMove.magnitude;
            // Forward stick = walking; in-place yaw (stick X) keeps full scale.
            bool walking = Mathf.Abs(_smoothedMove.y) > WalkRenderScaleThreshold;
            ApplyRenderScale(walking);

            Vector3 motion = Vector3.zero;
            if (moveMag > 1e-4f)
            {
                float rate = Mathf.Max(speedRatio, moveMag);
                _yaw += _smoothedMove.x * StickYawSpeed * rate * dt;

                Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
                motion = forward * (_smoothedMove.y * MoveSpeed * rate * dt);
            }

            motion.y = IntegrateVertical(dt);
            MoveBody(motion);
            transform.rotation = Quaternion.Euler(Pitch, _yaw, 0f);
        }

        void ApplyRenderScale(bool walking)
        {
            float scale = walking
                ? IslandCameraSrpSetup.WalkingRenderScale
                : IslandCameraSrpSetup.DefaultRenderScale;
            if (Mathf.Approximately(scale, _appliedRenderScale))
                return;

            IslandCameraSrpSetup.SetRenderScale(gameObject, scale);
            _appliedRenderScale = scale;
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
            float radius = Mathf.Min(BodyRadius, height * 0.45f);
            // Transform is at the eye; capsule feet sit on the ground under the eye.
            _cc.height = height;
            _cc.radius = radius;
            _cc.center = new Vector3(0f, -EyeHeight + height * 0.5f, 0f);
            _cc.slopeLimit = SlopeLimit;
            _cc.stepOffset = Mathf.Min(StepOffset, height * 0.4f);
            _cc.skinWidth = 0.08f;
            _cc.minMoveDistance = 0f;
            _appliedEyeHeight = EyeHeight;
            _appliedRadius = BodyRadius;
        }

        void MoveBody(Vector3 motion)
        {
            if (_cc != null && _cc.enabled)
            {
                CollisionFlags flags = _cc.Move(motion);
                if ((flags & CollisionFlags.Below) != 0 && _verticalSpeed < 0f)
                    _verticalSpeed = -GroundStickSpeed;
                return;
            }

            // No CC: keep old free-cam terrain snap for horizontal moves only.
            Vector3 pos = transform.position + motion;
            if (Mathf.Abs(motion.x) + Mathf.Abs(motion.z) > 1e-4f)
            {
                Vector3 ground = SampleGround(pos);
                pos.y = ground.y + EyeHeight;
            }

            transform.position = pos;
        }

        void SetPose(Vector3 pos, Quaternion rot)
        {
            _verticalSpeed = 0f;
            if (_cc != null && _cc.enabled)
            {
                _cc.enabled = false;
                transform.SetPositionAndRotation(pos, rot);
                _cc.enabled = true;
                return;
            }

            transform.SetPositionAndRotation(pos, rot);
        }

        Vector3 SampleGround(Vector3 worldPos)
        {
            Vector3 origin = worldPos + Vector3.up * TerrainProbeUp;
            float dist = TerrainProbeUp + TerrainProbeDown;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, dist, GroundMask,
                    QueryTriggerInteraction.Ignore))
                return hit.point;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain != null)
            {
                float y = terrain.SampleHeight(worldPos) + terrain.transform.position.y;
                return new Vector3(worldPos.x, y, worldPos.z);
            }

            return worldPos;
        }
    }
}
