using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Mobile：剥离软阴影与 Kuwahara 变体以减包体。
    /// 保留 INSTANCING_ON — EnvNativeInstancing / DrawMeshInstanced 需要；剥掉会导致关掉 MeshRenderer 后树/灌木全灭。
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

            var softCascade = new ShaderKeyword("_SOFT_CASCADE_BLEND");
            var filterMed = new ShaderKeyword("_SHADOW_FILTER_MEDIUM");
            var filterHigh = new ShaderKeyword("_SHADOW_FILTER_HIGH");
            var kuwahara = new ShaderKeyword("_KUWAHARA_ON");
            for (int i = data.Count - 1; i >= 0; i--)
            {
                var set = data[i].shaderKeywordSet;
                if (set.IsEnabled(softCascade) ||
                    set.IsEnabled(filterMed) ||
                    set.IsEnabled(filterHigh) ||
                    set.IsEnabled(kuwahara))
                {
                    data.RemoveAt(i);
                }
            }
        }
    }
}
