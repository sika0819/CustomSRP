using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Layer helpers shared by the planar mirror probe, the env streamer, and any
    /// editor menu that needs to flag reflect-culled instances.
    /// <list type="bullet">
    /// <item><c>Water</c> (built-in) — ocean / river renderers; the mirror
    /// camera excludes them so the sea doesn't reflect inside itself.</item>
    /// <item><c>ReflectionSkip</c> (index 3 in TagManager) — grass / clutter /
    /// small rocks; keep them out of the mirror culling mask to save CPU
    /// (per-instance fog + oblique clip-plane) and pixels (they blur into a
    /// muddy haze along the surface anyway).</item>
    /// </list>
    /// </summary>
    public static class OilWaterLayers
    {
        public const string LayerName = "Water";

        /// <summary>Layer index 3 (TagManager). Items flagged at spawn time
        /// land on this layer; <see cref="PlanarReflectionCullingMask"/> drops
        /// them from the mirror camera.</summary>
        public const string ReflectionSkipLayerName = "ReflectionSkip";

        public static int LayerIndex => LayerMask.NameToLayer(LayerName);

        public static int ReflectionSkipLayerIndex =>
            LayerMask.NameToLayer(ReflectionSkipLayerName);

        /// <summary>Camera mask that excludes only <c>Water</c>. Retained for callers
        /// that specifically want "everything but the sea" without also dropping
        /// <c>ReflectionSkip</c> items; the planar mirror uses
        /// <see cref="PlanarReflectionCullingMask"/> instead.</summary>
        public static int EverythingExceptWater
        {
            get
            {
                int water = LayerIndex;
                return water < 0 ? ~0 : ~(1 << water);
            }
        }

        /// <summary>
        /// Mirror camera culling mask. Excludes <c>Water</c> (no sea-in-its-own-reflection)
        /// and <c>ReflectionSkip</c> (grass / clutter / small rocks stay out of the mirror).
        /// Returns <c>~0</c> when the project hasn't added the layers yet so we don't
        /// silently cull everything.
        /// </summary>
        public static int PlanarReflectionCullingMask
        {
            get
            {
                int mask = ~0;
                int water = LayerIndex;
                int skip = ReflectionSkipLayerIndex;
                if (water >= 0)
                    mask &= ~(1 << water);
                if (skip >= 0)
                    mask &= ~(1 << skip);
                return mask;
            }
        }

        /// <summary>True when the layer exists AND is enabled for this project.</summary>
        public static bool ReflectionSkipLayerReady => ReflectionSkipLayerIndex >= 0;

        public static void ApplyTo(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            int water = LayerIndex;
            if (water < 0)
            {
                return;
            }

            SetRecursive(go.transform, water);
        }

        public static void ApplyTo(Renderer renderer)
        {
            if (renderer == null)
            {
                return;
            }

            ApplyTo(renderer.gameObject);
        }

        /// <summary>
        /// Put non-reflection-prone instances (grass, clutter, small rocks) on the
        /// ReflectionSkip layer. Tree / building / landmark instances are skipped
        /// because they SHOULD appear in the mirror.
        /// </summary>
        public static void ApplyReflectionSkip(GameObject go)
        {
            if (go == null || !ReflectionSkipLayerReady)
            {
                return;
            }

            SetRecursive(go.transform, ReflectionSkipLayerIndex);
        }

        /// <summary>Put ocean / river renderers on the Water layer so the mirror
        /// camera culls them via <see cref="PlanarReflectionCullingMask"/>.</summary>
        public static void BindWaters()
        {
            int water = LayerIndex;
            if (water < 0)
            {
                return;
            }

            Renderer[] renderers = Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !UsesOilWater(renderer))
                {
                    continue;
                }

                ApplyTo(renderer);
            }
        }

        static bool UsesOilWater(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || material.shader == null)
                {
                    continue;
                }

                string name = material.shader.name;
                if (name == "CustomSRP/OilOceanNPR" || name == "CustomSRP/OilRiverNPR")
                {
                    return true;
                }
            }

            return false;
        }

        static void SetRecursive(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
            {
                SetRecursive(root.GetChild(i), layer);
            }
        }
    }
}

