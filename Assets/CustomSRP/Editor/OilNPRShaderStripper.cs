using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Mobile：剥离 Oil NPR 用不到的软阴影变体以减包体。
    /// OIL_MOBILE_PERF 不采 other-shadow；OIL_NPR_HARD_DIRECTIONAL 忽略 filter keyword。
    /// 保留 INSTANCING_ON — EnvNativeInstancing / DrawMeshInstanced 需要。
    /// </summary>
    public class OilNPRShaderStripper : IPreprocessShaders
    {
        public int callbackOrder => 0;

        public void OnProcessShader(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            if (shader == null)
            {
                return;
            }

            if (shader.name != "CustomSRP/OilNPR"
                && shader.name != "CustomSRP/TerrainOilNPR")
            {
                return;
            }

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.Android && target != BuildTarget.iOS)
            {
                return;
            }

            var softCascade = new ShaderKeyword("_SOFT_CASCADE_BLEND");
            var filterMed = new ShaderKeyword("_SHADOW_FILTER_MEDIUM");
            var filterHigh = new ShaderKeyword("_SHADOW_FILTER_HIGH");
            for (int i = data.Count - 1; i >= 0; i--)
            {
                var set = data[i].shaderKeywordSet;
                if (set.IsEnabled(softCascade)
                    || set.IsEnabled(filterMed)
                    || set.IsEnabled(filterHigh))
                {
                    data.RemoveAt(i);
                }
            }
        }
    }
}
