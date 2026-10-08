using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Mobile builds: strip GPU Instancing variants from OilNPR (SRP Batcher path).
    /// </summary>
    public class OilNPRShaderStripper : IPreprocessShaders
    {
        public int callbackOrder => 0;

        public void OnProcessShader(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            if (shader == null || shader.name != "CustomSRP/OilNPR")
            {
                return;
            }

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.Android && target != BuildTarget.iOS)
            {
                return;
            }

            var instancing = new ShaderKeyword("INSTANCING_ON");
            var softCascade = new ShaderKeyword("_SOFT_CASCADE_BLEND");
            var filterMed = new ShaderKeyword("_SHADOW_FILTER_MEDIUM");
            var filterHigh = new ShaderKeyword("_SHADOW_FILTER_HIGH");
            for (int i = data.Count - 1; i >= 0; i--)
            {
                var set = data[i].shaderKeywordSet;
                if (set.IsEnabled(instancing) ||
                    set.IsEnabled(softCascade) ||
                    set.IsEnabled(filterMed) ||
                    set.IsEnabled(filterHigh))
                {
                    data.RemoveAt(i);
                }
            }
        }
    }
}
