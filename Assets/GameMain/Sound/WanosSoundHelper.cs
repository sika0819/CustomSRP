using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>GF SoundHelper — Wanos assets are not Unity Object refs; release is a no-op.</summary>
    public sealed class WanosSoundHelper : SoundHelperBase
    {
        public override void ReleaseSoundAsset(object soundAsset)
        {
            // WanosSoundAsset is a lightweight managed object; nothing to unload from Resource.
        }
    }
}
