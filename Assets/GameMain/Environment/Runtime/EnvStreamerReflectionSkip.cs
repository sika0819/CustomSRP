using UnityEngine;

namespace Game
{
    /// <summary>
    /// Mirror-friendly helper used by the env streamer. We avoid referencing
    /// <c>CustomSRP.OilWaterLayers</c> directly because <c>Game.asmdef</c> has
    /// no compile-time link to the SRP assembly — this keeps the GameMain /
    /// SRP boundary clean. Behaviour mirrors <c>ApplyReflectionSkip</c> from
    /// <c>OilWaterLayers</c>.
    /// </summary>
    public static class EnvStreamerReflectionSkip
    {
        const string ReflectionSkipLayerName = "ReflectionSkip";

        public static int ReflectionSkipLayerIndex =>
            LayerMask.NameToLayer(ReflectionSkipLayerName);

        public static bool ReflectionSkipLayerReady => ReflectionSkipLayerIndex >= 0;

        public static void Apply(GameObject go)
        {
            if (go == null || !ReflectionSkipLayerReady)
            {
                return;
            }
            SetRecursive(go.transform, ReflectionSkipLayerIndex);
        }

        /// <summary>Same layer as <see cref="Apply"/>, but only this subtree
        /// (LOD_Billboard on a full tree). The rest of the prefab still reflects.</summary>
        public static void ApplySubtree(Transform root)
        {
            if (root == null || !ReflectionSkipLayerReady)
            {
                return;
            }
            SetRecursive(root, ReflectionSkipLayerIndex);
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
