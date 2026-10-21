using UnityEngine;

namespace Game
{
    /// <summary>
    /// Coarse device tier for preset transition FX (walk during exit, black-hold length).
    /// </summary>
    public static class IslandGraphicsTier
    {
        /// <summary>
        /// Mobile with ≤4 GB system RAM — skip exit walk, shorten black hold.
        /// Editor / desktop always high tier.
        /// </summary>
        public static bool IsLowEnd
        {
            get
            {
#if UNITY_EDITOR
                return false;
#else
                if (!Application.isMobilePlatform)
                    return false;
                return SystemInfo.systemMemorySize > 0
                    && SystemInfo.systemMemorySize <= 4096;
#endif
            }
        }
    }
}
