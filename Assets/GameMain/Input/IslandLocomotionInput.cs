using UnityEngine;

namespace Game
{
    /// <summary>
    /// Joystick / WASD → LocomotionInput mailbox.
    /// Rotation is stick X (and A/D); no right-click look required.
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
            _lookPointerId = -1;
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

            // Mobile: second finger can still nudge look; mouse right-click is not required.
            TickSecondFingerLook();

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

        int _lookPointerId = -1;
        Vector2 _lastLookPos;

        void TickSecondFingerLook()
        {
            if (Input.touchCount < 2)
            {
                _lookPointerId = -1;
                return;
            }

            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (_joystick != null && _joystick.OwnsPointer(touch.fingerId))
                    continue;

                if (touch.phase == TouchPhase.Began)
                {
                    if (_lookPointerId < 0)
                    {
                        _lookPointerId = touch.fingerId;
                        _lastLookPos = touch.position;
                    }
                    continue;
                }

                if (touch.fingerId != _lookPointerId)
                    continue;

                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    _lookPointerId = -1;
                    continue;
                }

                Vector2 delta = touch.position - _lastLookPos;
                _lastLookPos = touch.position;
                if (delta.sqrMagnitude > 1e-4f)
                    LocomotionInput.SetLook(delta);
            }
        }
    }
}
