using System.Runtime.InteropServices;
using UnityEngine;

namespace WanosAudio
{
    public enum WanosResult
    {
        Success = 0,
        InvalidEngine = 0x0001,
        BankNotLoaded = 0x0003,
        BankNotDecoded = 0x0004,
        CreateEngineFailed = 0x0006,
        InvalidFilePath = 0x000a,
        BankOpenFailed = 0x0130,
        InitDspFailed = 0x0071,
        InitAndroidDeviceFailed = 0x00e0,
    }

    public enum WanosPlaybackState
    {
        Uninitialized = 0,
        Stopped = 1,
        Started = 2,
        Starting = 3,
        Stopping = 4,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WanosVector3
    {
        public float X;
        public float Y;
        public float Z;

        public WanosVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>
        /// Unity → Wanos 轴：X 不变，Y↔Z（与 Boluo <c>Wanos6DoFEntity</c> / <c>WanosVector3</c> 一致）。
        /// 不做此交换时，引擎把 Unity 高度当水平轴，岛上走动几乎无水平距离变化 → 无 3D 衰减/定位。
        /// </summary>
        public static WanosVector3 FromUnity(Vector3 v) => new WanosVector3(v.x, v.z, v.y);

        /// <summary>已是 Wanos 轴序的分量（不再交换）。</summary>
        public static WanosVector3 FromWanos(float x, float y, float z) => new WanosVector3(x, y, z);
    }

    /// <summary>Native Wanos6DoFEntity: position, forward, up (meters, Wanos axes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WanosPose6Dof
    {
        public WanosVector3 Position;
        public WanosVector3 Forward;
        public WanosVector3 Up;

        /// <summary>Wanos 轴下的单位姿态：forward=+Y，up=+Z（对应 Unity forward/up 交换后）。</summary>
        public static WanosPose6Dof Identity => new WanosPose6Dof
        {
            Position = WanosVector3.FromWanos(0f, 0f, 0f),
            Forward = WanosVector3.FromWanos(0f, 1f, 0f),
            Up = WanosVector3.FromWanos(0f, 0f, 1f),
        };

        public static WanosPose6Dof FromTransform(Transform t)
        {
            if (t == null)
                return Identity;
            return new WanosPose6Dof
            {
                Position = WanosVector3.FromUnity(t.position),
                Forward = WanosVector3.FromUnity(t.forward),
                Up = WanosVector3.FromUnity(t.up),
            };
        }

        public static WanosPose6Dof FromPosition(Vector3 position)
        {
            // 与 Boluo Wanos6DoFEntity(Vector3) 一致：只写位置；方位用 Identity 的 forward/up。
            return new WanosPose6Dof
            {
                Position = WanosVector3.FromUnity(position),
                Forward = Identity.Forward,
                Up = Identity.Up,
            };
        }
    }
}
