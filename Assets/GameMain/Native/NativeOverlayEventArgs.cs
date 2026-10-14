using GameFramework;
using GameFramework.Event;

namespace Game.Native
{
    public sealed class NativeOverlayEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(NativeOverlayEventArgs).GetHashCode();
        public override int Id => EventId;

        public bool BottomIconsHidden { get; private set; }
        public bool BlocksLocomotion { get; private set; }
        public bool OverlayPresent { get; private set; }

        public static NativeOverlayEventArgs Create(bool bottomHidden, bool blocksLocomotion, bool overlayPresent)
        {
            var e = ReferencePool.Acquire<NativeOverlayEventArgs>();
            e.BottomIconsHidden = bottomHidden;
            e.BlocksLocomotion = blocksLocomotion;
            e.OverlayPresent = overlayPresent;
            return e;
        }

        public override void Clear()
        {
            BottomIconsHidden = false;
            BlocksLocomotion = false;
            OverlayPresent = false;
        }
    }
}
