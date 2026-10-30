using UnityEngine;

namespace Game
{
    /// <summary>
    /// 一扇隐形区域门：实心盒子挡住走路，外层 Trigger 用来弹下载窗。
    /// </summary>
    public sealed class SceneBankGate : MonoBehaviour
    {
        public int MapItemIndex { get; private set; }

        /// <summary>目录里这一扇门自己的 bank（区域环境音）。就绪即可开门。</summary>
        public string[] ZoneBanks { get; private set; } = System.Array.Empty<string>();

        /// <summary>与 <see cref="ZoneBanks"/> 相同（兼容旧调用）；不再 Expand 预设整包。</summary>
        public string[] BankNames { get; private set; } = System.Array.Empty<string>();
        public bool IsBlocked { get; private set; }
        public bool PlayerInside { get; private set; }
        public bool PromptLatched;

        BoxCollider _solid;
        BoxCollider _trigger;
        SphereCollider _solidSphere;
        SphereCollider _triggerSphere;
        int _overlaps;

        public void Setup(
            int mapItemIndex,
            string objectName,
            string[] bankNames,
            Vector3 boxSize,
            float radius,
            SceneBankGateShape shape,
            bool blocked)
        {
            MapItemIndex = mapItemIndex;
            ZoneBanks = bankNames ?? System.Array.Empty<string>();
            BankNames = ZoneBanks;
            name = "Gate " + mapItemIndex + " " + (objectName ?? "");

            var solidGo = new GameObject("Solid");
            solidGo.transform.SetParent(transform, false);
            var sensorGo = new GameObject("Sensor");
            sensorGo.transform.SetParent(transform, false);

            if (shape == SceneBankGateShape.Sphere)
            {
                _solidSphere = solidGo.AddComponent<SphereCollider>();
                _solidSphere.radius = radius;
                _triggerSphere = sensorGo.AddComponent<SphereCollider>();
                _triggerSphere.isTrigger = true;
                _triggerSphere.radius = radius + SceneBankGateCatalog.TriggerPaddingMeters;
            }
            else
            {
                _solid = solidGo.AddComponent<BoxCollider>();
                _solid.size = boxSize;
                _trigger = sensorGo.AddComponent<BoxCollider>();
                _trigger.isTrigger = true;
                float pad = SceneBankGateCatalog.TriggerPaddingMeters * 2f;
                _trigger.size = boxSize + new Vector3(pad, pad, pad);
            }

            Rigidbody body = sensorGo.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezeAll;

            SceneBankGateSensor sensor = sensorGo.AddComponent<SceneBankGateSensor>();
            sensor.Bind(this);

            SetBlocked(blocked);
        }

        public void SetGuardedBanks(string[] banks)
        {
            BankNames = banks ?? System.Array.Empty<string>();
        }

        public void SetBlocked(bool blocked)
        {
            IsBlocked = blocked;
            if (_solid != null)
                _solid.enabled = blocked;
            if (_trigger != null)
                _trigger.enabled = blocked;
            if (_solidSphere != null)
                _solidSphere.enabled = blocked;
            if (_triggerSphere != null)
                _triggerSphere.enabled = blocked;
            if (blocked)
                return;

            _overlaps = 0;
            PlayerInside = false;
            PromptLatched = false;
        }

        public void NotifyEnter(Collider other)
        {
            if (!IsBlocked || !IsPlayer(other))
                return;
            _overlaps++;
            PlayerInside = true;
        }

        public void NotifyExit(Collider other)
        {
            if (!IsPlayer(other))
                return;
            _overlaps = Mathf.Max(0, _overlaps - 1);
            if (_overlaps > 0)
                return;
            PlayerInside = false;
            // 锁只由采样在「人已出门且没有弹窗」时清掉。物理 Exit 提前清锁会让关窗瞬间又弹。
        }

        /// <summary>
        /// 身体上的点是否落在外层 Trigger 里。CharacterController 经常收不到 Trigger 消息。
        /// <paramref name="inflateMeters"/> 把检测体积再撑开一圈，贴墙时胶囊中心在壳外也算进门。
        /// </summary>
        public bool ContainsBody(Vector3 eye, Vector3 foot, float inflateMeters = 0f)
        {
            if (!IsBlocked)
                return false;
            Vector3 mid = (eye + foot) * 0.5f;
            return Hits(eye, inflateMeters) || Hits(foot, inflateMeters) || Hits(mid, inflateMeters);
        }

        bool Hits(Vector3 world, float inflateMeters)
        {
            if (_trigger != null)
                return Contains(_trigger, world, inflateMeters);
            if (_triggerSphere != null)
                return Contains(_triggerSphere, world, inflateMeters);
            return false;
        }

        /// <summary>按身体采样刷新是否在门内。</summary>
        /// <param name="clearLatch">人已在门外时是否清掉下载窗锁。弹窗还开着时保持锁，避免关窗瞬间再弹。</param>
        public void SetSampledInside(bool inside, bool clearLatch = true)
        {
            if (inside)
            {
                PlayerInside = true;
                return;
            }

            // CharacterController 经常只发 Enter、不发 Exit，重叠计数会一直大于 0。
            // 身体采样已经在门外时仍信这个计数，弹窗锁就永远清不掉，没下完再走进去不会再出进度条。
            _overlaps = 0;
            PlayerInside = false;
            if (clearLatch)
                PromptLatched = false;
        }

        /// <summary>
        /// 把眼、脚、身体中点一起退出 Trigger 所需的水平位移。
        /// 方向是从门心指向身体。已经在外面时返回 zero。
        /// </summary>
        public Vector3 HorizontalExitPush(Vector3 eye, Vector3 foot, float inflateMeters)
        {
            if (!IsBlocked || !ContainsBody(eye, foot, inflateMeters))
                return Vector3.zero;

            Vector3 mid = (eye + foot) * 0.5f;
            Vector3 away = mid - TriggerCenter();
            away.y = 0f;
            if (away.sqrMagnitude < 1e-6f)
                away = Vector3.forward;
            else
                away.Normalize();

            float need = 0f;
            need = Mathf.Max(need, DistanceToExit(eye, away, inflateMeters));
            need = Mathf.Max(need, DistanceToExit(foot, away, inflateMeters));
            need = Mathf.Max(need, DistanceToExit(mid, away, inflateMeters));
            if (need <= 0f || float.IsPositiveInfinity(need))
                return Vector3.zero;
            return away * (need + 0.05f);
        }

        Vector3 TriggerCenter()
        {
            if (_trigger != null)
                return _trigger.transform.TransformPoint(_trigger.center);
            if (_triggerSphere != null)
                return _triggerSphere.transform.TransformPoint(_triggerSphere.center);
            return transform.position;
        }

        float DistanceToExit(Vector3 world, Vector3 worldDir, float inflateMeters)
        {
            if (!Hits(world, inflateMeters))
                return 0f;
            if (_trigger != null)
                return DistanceToExitBox(_trigger, world, worldDir, inflateMeters);
            if (_triggerSphere != null)
                return DistanceToExitSphere(_triggerSphere, world, worldDir, inflateMeters);
            return 0f;
        }

        static float DistanceToExitBox(BoxCollider box, Vector3 world, Vector3 worldDir, float inflateMeters)
        {
            Transform tf = box.transform;
            Vector3 localP = tf.InverseTransformPoint(world) - box.center;
            Vector3 localD = tf.InverseTransformVector(worldDir);
            Vector3 lossy = tf.lossyScale;
            Vector3 half = box.size * 0.5f;
            half.x += inflateMeters / Mathf.Max(0.001f, Mathf.Abs(lossy.x));
            half.y += inflateMeters / Mathf.Max(0.001f, Mathf.Abs(lossy.y));
            half.z += inflateMeters / Mathf.Max(0.001f, Mathf.Abs(lossy.z));

            float tExit = float.PositiveInfinity;
            tExit = AxisExit(localP.x, localD.x, half.x, tExit);
            tExit = AxisExit(localP.y, localD.y, half.y, tExit);
            tExit = AxisExit(localP.z, localD.z, half.z, tExit);
            if (float.IsPositiveInfinity(tExit))
                return 0f;
            return Mathf.Max(0f, tExit);
        }

        static float AxisExit(float p, float d, float half, float best)
        {
            if (Mathf.Abs(d) < 1e-8f)
                return best;
            float t1 = (half - p) / d;
            float t2 = (-half - p) / d;
            float t;
            if (t1 >= 0f && t2 < 0f)
                t = t1;
            else if (t2 >= 0f && t1 < 0f)
                t = t2;
            else if (t1 >= 0f && t2 >= 0f)
                t = Mathf.Min(t1, t2);
            else
                return best;
            return t < best ? t : best;
        }

        static float DistanceToExitSphere(SphereCollider sphere, Vector3 world, Vector3 worldDir, float inflateMeters)
        {
            Vector3 center = sphere.transform.TransformPoint(sphere.center);
            float scale = Mathf.Max(
                Mathf.Abs(sphere.transform.lossyScale.x),
                Mathf.Abs(sphere.transform.lossyScale.y),
                Mathf.Abs(sphere.transform.lossyScale.z));
            float radius = sphere.radius * scale + Mathf.Max(0f, inflateMeters);
            Vector3 oc = world - center;
            float b = Vector3.Dot(oc, worldDir);
            float c = oc.sqrMagnitude - radius * radius;
            float disc = b * b - c;
            if (disc < 0f)
                return 0f;
            return Mathf.Max(0f, -b + Mathf.Sqrt(disc));
        }

        static bool Contains(BoxCollider box, Vector3 world, float inflateMeters)
        {
            Vector3 local = box.transform.InverseTransformPoint(world) - box.center;
            Vector3 lossy = box.transform.lossyScale;
            Vector3 half = box.size * 0.5f;
            half.x += inflateMeters / Mathf.Max(0.001f, Mathf.Abs(lossy.x));
            half.y += inflateMeters / Mathf.Max(0.001f, Mathf.Abs(lossy.y));
            half.z += inflateMeters / Mathf.Max(0.001f, Mathf.Abs(lossy.z));
            return Mathf.Abs(local.x) <= half.x
                && Mathf.Abs(local.y) <= half.y
                && Mathf.Abs(local.z) <= half.z;
        }

        static bool Contains(SphereCollider sphere, Vector3 world, float inflateMeters)
        {
            Vector3 center = sphere.transform.TransformPoint(sphere.center);
            float scale = Mathf.Max(
                Mathf.Abs(sphere.transform.lossyScale.x),
                Mathf.Abs(sphere.transform.lossyScale.y),
                Mathf.Abs(sphere.transform.lossyScale.z));
            float radius = sphere.radius * scale + Mathf.Max(0f, inflateMeters);
            return (world - center).sqrMagnitude <= radius * radius;
        }

        static bool IsPlayer(Collider other)
        {
            return other != null && other.GetComponentInParent<IslandJoystickCamera>() != null;
        }
    }

    /// <summary>Trigger 必须和脚本在同一个物体上，消息再转给门。</summary>
    public sealed class SceneBankGateSensor : MonoBehaviour
    {
        SceneBankGate _gate;

        public void Bind(SceneBankGate gate)
        {
            _gate = gate;
        }

        void OnTriggerEnter(Collider other)
        {
            if (_gate != null)
                _gate.NotifyEnter(other);
        }

        void OnTriggerExit(Collider other)
        {
            if (_gate != null)
                _gate.NotifyExit(other);
        }
    }
}
