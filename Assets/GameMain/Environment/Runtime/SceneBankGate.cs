using UnityEngine;

namespace Game
{
    /// <summary>
    /// 一扇隐形区域门：实心盒子挡住走路，外层 Trigger 用来弹下载窗。
    /// </summary>
    public sealed class SceneBankGate : MonoBehaviour
    {
        public int MapItemIndex { get; private set; }
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
            BankNames = bankNames ?? System.Array.Empty<string>();
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
            PromptLatched = false;
        }

        /// <summary>身体上的点是否落在外层 Trigger 里。CharacterController 经常收不到 Trigger 消息。</summary>
        public bool ContainsBody(Vector3 eye, Vector3 foot)
        {
            if (!IsBlocked)
                return false;
            Vector3 mid = (eye + foot) * 0.5f;
            if (_trigger != null)
                return Contains(_trigger, eye) || Contains(_trigger, foot) || Contains(_trigger, mid);
            if (_triggerSphere != null)
                return Contains(_triggerSphere, eye) || Contains(_triggerSphere, foot) || Contains(_triggerSphere, mid);
            return false;
        }

        /// <summary>按身体采样刷新是否在门内。物理 Trigger 仍算在里面时不提前清掉。</summary>
        public void SetSampledInside(bool inside)
        {
            if (inside)
            {
                PlayerInside = true;
                return;
            }

            if (_overlaps > 0)
                return;
            if (!PlayerInside)
                return;
            PlayerInside = false;
            PromptLatched = false;
        }

        static bool Contains(BoxCollider box, Vector3 world)
        {
            Vector3 local = box.transform.InverseTransformPoint(world) - box.center;
            Vector3 half = box.size * 0.5f;
            return Mathf.Abs(local.x) <= half.x
                && Mathf.Abs(local.y) <= half.y
                && Mathf.Abs(local.z) <= half.z;
        }

        static bool Contains(SphereCollider sphere, Vector3 world)
        {
            Vector3 center = sphere.transform.TransformPoint(sphere.center);
            float scale = Mathf.Max(
                Mathf.Abs(sphere.transform.lossyScale.x),
                Mathf.Abs(sphere.transform.lossyScale.y),
                Mathf.Abs(sphere.transform.lossyScale.z));
            float radius = sphere.radius * scale;
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
