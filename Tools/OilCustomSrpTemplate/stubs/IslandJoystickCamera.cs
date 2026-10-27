using UnityEngine;

namespace Game
{
    /// <summary>Template stub — optional walk-camera hook for OilRainStreakQuad.</summary>
    public sealed class IslandJoystickCamera : MonoBehaviour
    {
        public static IslandJoystickCamera Current { get; set; }

        void OnEnable() => Current = this;
        void OnDisable()
        {
            if (Current == this)
                Current = null;
        }
    }
}
