using UnityEngine;

namespace CustomSRP
{
    [System.Serializable]
    public class ShadowSettings
    {
        public enum MapSize
        {
            _256 = 256,
            _512 = 512,
            _1024 = 1024,
            _2048 = 2048,
            _4096 = 4096,
            _8192 = 8192
        }

        public enum FilterQuality
        {
            Low,
            Medium,
            High
        }

        public FilterQuality filterQuality = FilterQuality.Medium;

        public float DirectionalFilterSize => (float)filterQuality + 2f;

        public float OtherFilterSize => (float)filterQuality + 2f;

        [System.Serializable]
        public struct Directional
        {
            public MapSize atlasSize;

            [Range(1, 4)]
            public int cascadeCount;

            [Range(0f, 1f)]
            public float cascadeRatio1, cascadeRatio2, cascadeRatio3;

            [Range(0.001f, 1f)]
            public float cascadeFade;

            public bool softCascadeBlend;

            public Vector3 CascadeRatios =>
                new Vector3(cascadeRatio1, cascadeRatio2, cascadeRatio3);
        }

        [System.Serializable]
        public struct Other
        {
            public MapSize atlasSize;
        }

        [Min(0.001f)]
        public float maxDistance = 100f;

        [Range(0.001f, 1f)]
        public float distanceFade = 0.1f;

        public Directional directional = new Directional
        {
            atlasSize = MapSize._1024,
            cascadeCount = 4,
            cascadeRatio1 = 0.1f,
            cascadeRatio2 = 0.25f,
            cascadeRatio3 = 0.5f,
            cascadeFade = 0.1f
        };

        public Other other = new Other
        {
            atlasSize = MapSize._1024
        };

        /// <summary>
        /// Returns a mobile-friendly copy with reduced cascade count and atlas size.
        /// The original is left intact; callers must use the returned value.
        /// </summary>
        public ShadowSettings ClonedForMobile()
        {
            var c = (ShadowSettings)MemberwiseClone();
            // cascadeCount must be at least 1 or Shadows.SetTileViewport divides by zero.
            if (c.directional.cascadeCount < 1)
                c.directional.cascadeCount = 1;
            else if (c.directional.cascadeCount > 2)
                c.directional.cascadeCount = 2;
            if ((int)c.directional.atlasSize > 1024)
                c.directional.atlasSize = MapSize._1024;
            if ((int)c.other.atlasSize > 1024)
                c.other.atlasSize = MapSize._1024;
            return c;
        }
    }
}
