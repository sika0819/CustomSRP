using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Island-wide water probe. Four static cubemaps for sky period anchors.
    /// </summary>
    public sealed class OilWaterReflectionSet : ScriptableObject
    {
        public Vector3 position;
        public Vector3 size;
        public Cubemap dawn;
        public Cubemap day;
        public Cubemap dusk;
        public Cubemap night;

        public Cubemap CubemapForPeriod(float period)
        {
            int slot = PeriodSlot(period);
            Cubemap cube = CubemapForSlot(slot);
            if (cube != null)
            {
                return cube;
            }

            // Fall back to any available slot.
            for (int i = 0; i < 4; i++)
            {
                Cubemap other = CubemapForSlot(i);
                if (other != null)
                {
                    return other;
                }
            }

            return null;
        }

        public Cubemap CubemapForSlot(int slot)
        {
            switch (slot)
            {
                case 0: return dawn;
                case 1: return day;
                case 2: return dusk;
                default: return night;
            }
        }

        public static int PeriodSlot(float period)
        {
            if (float.IsNaN(period) || float.IsInfinity(period))
            {
                return 1;
            }

            period = period - Mathf.Floor(period * 0.25f) * 4f;
            if (period < 0f)
            {
                period += 4f;
            }

            // Nearest of 0/1/2/3 (period wraps at 4 → dawn).
            int nearest = Mathf.RoundToInt(period) % 4;
            if (nearest < 0)
            {
                nearest += 4;
            }

            return nearest;
        }
    }
}
