using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// 安全欧拉角：先校验/归一化再转，避免
    /// "QuaternionToEuler: Input quaternion was not normalized"（常见于 Tools.handleRotation、
    /// 零 scale UI、Editor MCP 轮询场景）。
    /// </summary>
    public static class SafeQuaternionUtil
    {
        const float MagEpsilon = 1e-8f;
        const float UnitTolerance = 1e-4f;

        public static Vector3 ToEuler(Quaternion q)
        {
            if (!TryNormalize(ref q))
                return Vector3.zero;
            // 归一化后再走 Unity 原生转换，与 Inspector 一致且不再告警
            return q.eulerAngles;
        }

        public static Vector3 ToEuler(Transform t, bool local)
        {
            if (t == null)
                return Vector3.zero;
            return ToEuler(local ? t.localRotation : t.rotation);
        }

        public static bool TryNormalize(ref Quaternion q)
        {
            float magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (float.IsNaN(magSq) || float.IsInfinity(magSq) || magSq < MagEpsilon)
            {
                q = Quaternion.identity;
                return false;
            }

            if (Mathf.Abs(magSq - 1f) > UnitTolerance)
            {
                float inv = 1f / Mathf.Sqrt(magSq);
                q.x *= inv;
                q.y *= inv;
                q.z *= inv;
                q.w *= inv;
            }
            return true;
        }
    }
}
