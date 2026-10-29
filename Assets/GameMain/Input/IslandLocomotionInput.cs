using UnityEngine;

namespace Game
{
    /// <summary>
    /// Joystick / WASD → LocomotionInput mailbox.
    /// 移动和转向只认一根手指。第二根手指不转视角，避免两指同时拧、松手后方向粘住。
    /// </summary>
    public sealed class IslandLocomotionInput
    {
        IslandVirtualJoystick _joystick;

        public bool IsMoveInputActive { get; private set; }
        public bool IsJoystickActive => _joystick != null && _joystick.IsActive;

        public void Bind(IslandVirtualJoystick joystick)
        {
            _joystick = joystick;
        }

        public void ForceEnd()
        {
            _joystick?.ForceEnd();
            LocomotionInput.ClearAll();
            IsMoveInputActive = false;
        }

        public void ApplyLayoutScale() => _joystick?.ApplyLayoutScale();

        public void Tick()
        {
            bool canMove = !HudOverlayGate.BlocksLocomotion()
                && (GameEntry.Presets == null || !GameEntry.Presets.IsTransitioning);
            _joystick?.TickFrame(canMove);

            if (!canMove)
            {
                LocomotionInput.ClearAll();
                IsMoveInputActive = false;
                return;
            }

            // 清掉上一帧没被相机吃掉的视角增量，避免停手后还在转。
            LocomotionInput.ClearLook();

            if (_joystick != null
                && _joystick.IsActive
                && _joystick.TryGetMove(out Vector2 dir, out float speed))
            {
                IsMoveInputActive = true;
                LocomotionInput.Set(dir, speed);
                return;
            }

#if UNITY_EDITOR || UNITY_STANDALONE
            float hx = Input.GetAxisRaw("Horizontal");
            float hy = Input.GetAxisRaw("Vertical");
            var key = new Vector2(hx, hy);
            if (key.sqrMagnitude > 0.01f)
            {
                IsMoveInputActive = true;
                LocomotionInput.Set(key.normalized, 1f);
                return;
            }
#endif

            IsMoveInputActive = false;
        }
    }
}
