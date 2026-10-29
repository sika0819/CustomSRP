using UnityEngine;

namespace Game
{
    /// <summary>
    /// Move/look mailbox: HUD writes, player consumes each frame.
    /// </summary>
    public static class LocomotionInput
    {
        static Vector2 _dir;
        static float _speed;
        static bool _pending;
        static Vector2 _look;
        static bool _lookPending;

        public static void Set(Vector2 dir, float speedRatio)
        {
            _dir = dir;
            _speed = speedRatio;
            _pending = true;
        }

        public static void SetLook(Vector2 pixelDelta)
        {
            _look += pixelDelta;
            _lookPending = true;
        }

        public static void Clear()
        {
            _dir = default;
            _speed = 0f;
            _pending = false;
        }

        public static void ClearLook()
        {
            _look = default;
            _lookPending = false;
        }

        public static void ClearAll()
        {
            Clear();
            ClearLook();
        }

        public static bool ConsumeLook(out Vector2 pixelDelta)
        {
            pixelDelta = _look;
            bool had = _lookPending;
            _look = default;
            _lookPending = false;
            return had;
        }

        public static bool Consume(out Vector2 dir, out float speedRatio)
        {
            dir = _dir;
            speedRatio = _speed;
            bool had = _pending;
            Clear();
            return had;
        }
    }
}
