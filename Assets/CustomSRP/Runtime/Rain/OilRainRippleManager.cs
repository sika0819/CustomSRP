using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Facade for rain impact effects. Contains no pools, no GameObjects and no
    /// ParticleSystems.
    ///
    /// Ripples: entirely shader-driven.
    ///   - Water normal perturbation: OilRainRippleFlipbook.hlsl, fed by
    ///     OilRainFlipbookDriver.PushRippleGlobals() (normal flipbook atlas).
    ///   - Screen-space rings on water: OilRainPostPass, fed by
    ///     OilRainRenderHook.OnRainHit -> OilRainPostPass.EnqueueRipple.
    /// Splashes: GPU-instanced flipbook quads via OilRainFlipbookDriver.DrawSplashes().
    ///
    /// This class only gates the hit event and keeps the existing call sites
    /// (OilRainManager / OilRainRenderHook) compiling.
    /// </summary>
    [DisallowMultipleComponent]
    public class OilRainRippleManager : MonoBehaviour
    {
        [SerializeField] OilRainSettings _settings;

        void Awake()
        {
            if (_settings == null)
                _settings = OilRainSettings.Current ?? GetComponent<OilRainSettings>();
        }

        /// <summary>
        /// Rain impact entry point. Rejects non-water surfaces. Everything past
        /// this point is handled on the GPU: no object is created, pooled or
        /// destroyed here.
        /// </summary>
        public void ProcessRainHit(Vector3 worldPos, Vector3 normal)
        {
            if (_settings == null) return;

            // Only near-horizontal surfaces (water). Matches the flipbook's
            // assumption that the shading normal can be perturbed in place on XZ.
            if (normal.y < 0.9f) return;

            // Flipbook normals are a continuous field, not per-impact events:
            // OilRainFlipbookDriver.PushRippleGlobals already scales the ripple
            // strength by rain intensity, so nothing to do for the ripple path.

            // Splashes are GPU-instanced quads spawned by
            // OilRainFlipbookDriver.SpawnSplashes, which follows the camera on its
            // own schedule. This hook only needs to enqueue the screen-space
            // ripple ring, which is the only per-impact visual.
            if (_settings.postRippleEnabled)
                OilRainPostPass.EnqueueRipple(worldPos);
        }

        /// <summary>
        /// Kept for call-site compatibility. Splashes are flipbook quads now, so
        /// this is a no-op; the GPU path spawns them around the camera target.
        /// </summary>
        public void SpawnSplash(Vector3 worldPos) { }
    }
}