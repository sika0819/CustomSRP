using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YooAsset;

using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 河岸 / 海边石头换独立材质 <c>ShiTou_Shore</c>（Inspector 调 <c>_Wetness</c>）；
    /// 内陆仍用预制体 <c>ShiTou</c>（Wetness=0）。水位 Y 固定，不做运行时扫水。
    /// </summary>
    public static class EnvRockShoreWet
    {
        const float ShoreBand = 5f;

        /// <summary>海面 + 固定内陆河 Y（与岛内容对齐）。</summary>
        static readonly float[] WaterYs = { 5.7f, 22f, 64f, 101.5f };

        static Material _shoreMat;
        static bool _bindAttempted;

        /// <summary>Boot 时预加载岸边石头材质（真机走 Yoo；编辑器可 AssetDatabase）。</summary>
        public static IEnumerator PreloadCoroutine(List<AssetHandle> ownedHandles)
        {
            if (_shoreMat != null)
                yield break;

            if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.ShiTouShoreLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<Material>(
                    SceneAssets.ShiTouShoreLocation,
                    (asset, handle) =>
                    {
                        _shoreMat = asset;
                        if (handle != null)
                            ownedHandles?.Add(handle);
                    });
            }

#if UNITY_EDITOR
            if (_shoreMat == null)
                _shoreMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                    SceneAssets.ShiTouShoreMatPath);
#endif

            _bindAttempted = true;
            if (_shoreMat == null)
                Log.Warning("[RockShoreWet] ShiTou_Shore missing — shore rocks stay on inland mat.");
        }

        /// <summary>已绑定的岸边材质（Inspector 调 _Wetness）；未加载则为 null。</summary>
        public static Material ShoreMaterial => _shoreMat;

        /// <summary>若石头竖向范围与水位带相交，则把 ShiTou 换成 ShiTou_Shore。</summary>
        public static void ApplyIfNearShore(GameObject go)
        {
            if (go == null)
                return;

            Material shore = ResolveShoreMaterial();
            if (shore == null)
                return;

            if (!TryGetWorldYRange(go, out float yMin, out float yMax))
                return;

            if (!OverlapsWaterBand(yMin, yMax))
                return;

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;

                Material[] mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0)
                    continue;

                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (!IsInlandShiTou(mats[i]))
                        continue;
                    mats[i] = shore;
                    changed = true;
                }

                if (changed)
                    renderer.sharedMaterials = mats;
            }
        }

        static Material ResolveShoreMaterial()
        {
            if (_shoreMat != null)
                return _shoreMat;

            if (_bindAttempted)
                return null;

            _bindAttempted = true;
#if UNITY_EDITOR
            _shoreMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                SceneAssets.ShiTouShoreMatPath);
#endif
            return _shoreMat;
        }

        static bool IsInlandShiTou(Material material)
        {
            if (material == null)
                return false;

            string name = material.name;
            int paren = name.IndexOf(" (", System.StringComparison.Ordinal);
            if (paren > 0)
                name = name.Substring(0, paren);

            return name == "ShiTou";
        }

        static bool TryGetWorldYRange(GameObject go, out float yMin, out float yMax)
        {
            yMin = float.PositiveInfinity;
            yMax = float.NegativeInfinity;

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                Bounds b = renderer.bounds;
                if (b.min.y < yMin)
                    yMin = b.min.y;
                if (b.max.y > yMax)
                    yMax = b.max.y;
            }

            if (yMin <= yMax)
                return true;

            float y = go.transform.position.y;
            yMin = yMax = y;
            return true;
        }

        /// <summary>竖向区间与任一固定水位 ±band 相交则视为岸边石。</summary>
        public static bool OverlapsWaterBand(float yMin, float yMax)
        {
            for (int i = 0; i < WaterYs.Length; i++)
            {
                if (RangeHitsY(yMin, yMax, WaterYs[i], ShoreBand))
                    return true;
            }

            return false;
        }

        static bool RangeHitsY(float yMin, float yMax, float waterY, float band)
        {
            float lo = waterY - band;
            float hi = waterY + band;
            return yMax >= lo && yMin <= hi;
        }
    }
}
