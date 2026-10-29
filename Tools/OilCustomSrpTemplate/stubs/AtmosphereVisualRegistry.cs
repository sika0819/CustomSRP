using UnityEngine;

namespace Game
{
    /// <summary>
    /// Template stand-in for the GameMain registry. OilSkyboxTime registers here on enable.
    /// </summary>
    public static class AtmosphereVisualRegistry
    {
        static Component _skybox;

        public static Component Skybox
        {
            get
            {
                if (_skybox == null)
                    _skybox = null;
                return _skybox;
            }
        }

        public static void RegisterSkybox(Component skybox)
        {
            if (skybox != null)
                _skybox = skybox;
        }

        public static void UnregisterSkybox(Component skybox)
        {
            if (_skybox == skybox)
                _skybox = null;
        }
    }
}
