using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Template stub replacing the GameMain WeatherService bridge.
    /// Real projects should swap this for the full OilAtmosphereDriver from the source repo.
    /// The skybox passes itself in; this stub does not search the scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OilAtmosphereDriver : MonoBehaviour
    {
        public static OilAtmosphereDriver EnsureOnSkybox(OilSkyboxTime sky = null)
        {
            if (sky == null)
                sky = Game.AtmosphereVisualRegistry.Skybox as OilSkyboxTime;
            if (sky == null)
                return null;

            OilAtmosphereDriver existing = sky.GetComponent<OilAtmosphereDriver>();
            if (existing != null)
                return existing;

            return sky.gameObject.AddComponent<OilAtmosphereDriver>();
        }
    }
}
