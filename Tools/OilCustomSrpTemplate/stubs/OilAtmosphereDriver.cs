using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Template stub replacing the GameMain WeatherService bridge.
    /// Real projects should swap this for the full OilAtmosphereDriver from the source repo.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OilAtmosphereDriver : MonoBehaviour
    {
        public static OilAtmosphereDriver EnsureOnSkybox()
        {
            var sky = Object.FindAnyObjectByType<OilSkyboxTime>();
            if (sky == null)
                return Object.FindAnyObjectByType<OilAtmosphereDriver>();

            var existing = sky.GetComponent<OilAtmosphereDriver>();
            if (existing != null)
                return existing;

            return sky.gameObject.AddComponent<OilAtmosphereDriver>();
        }
    }
}
