using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Validates CustomSRP shaders against Unity's SRP Batcher compatibility checker.
    /// Menu: CustomSRP/Validate SRP Batcher Compatibility
    /// Batchmode: -executeMethod CustomSRP.Editor.ValidateSrpBatcherCompatibility.ValidateAndExit
    /// </summary>
    public static class ValidateSrpBatcherCompatibility
    {
        static readonly string[] ShaderPaths =
        {
            "Assets/CustomSRP/Shaders/Lit.shader",
            "Assets/CustomSRP/Shaders/Unlit.shader"
        };

        [MenuItem("CustomSRP/Validate SRP Batcher Compatibility")]
        public static void ValidateFromMenu()
        {
            var ok = Validate(logErrorsAsExceptions: false);
            EditorUtility.DisplayDialog(
                "SRP Batcher Compatibility",
                ok ? "All CustomSRP shaders are SRP Batcher compatible." : "One or more shaders are NOT compatible. See Console.",
                "OK");
        }

        public static void ValidateAndExit()
        {
            var ok = Validate(logErrorsAsExceptions: false);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool Validate(bool logErrorsAsExceptions)
        {
            var getCode = typeof(ShaderUtil).GetMethod(
                "GetSRPBatcherCompatibilityCode",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var getReason = typeof(ShaderUtil).GetMethod(
                "GetSRPBatcherCompatibilityIssueReason",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var getActiveSubshader = typeof(ShaderUtil).GetMethod(
                "GetShaderActiveSubshaderIndex",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (getCode == null || getReason == null || getActiveSubshader == null)
            {
                Debug.LogError(
                    "ValidateSrpBatcherCompatibility: ShaderUtil SRP Batcher APIs not found via reflection.");
                return false;
            }

            var allOk = true;
            foreach (var path in ShaderPaths)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null)
                {
                    Debug.LogError($"Missing shader: {path}");
                    allOk = false;
                    continue;
                }

                // Force compilation so compatibility code is up to date (matches ShaderInspector).
                var mat = new Material(shader);
                try
                {
                    mat.SetPass(0);
                    var subShader = (int)getActiveSubshader.Invoke(null, new object[] { shader });
                    var errCode = (int)getCode.Invoke(null, new object[] { shader, subShader });
                    if (errCode == 0)
                    {
                        Debug.Log($"[SRP Batcher] {shader.name}: compatible");
                    }
                    else
                    {
                        allOk = false;
                        var reason = (string)getReason.Invoke(
                            null, new object[] { shader, subShader, errCode });
                        var msg =
                            $"[SRP Batcher] {shader.name}: NOT compatible (code={errCode})\n{reason}";
                        if (logErrorsAsExceptions)
                        {
                            throw new InvalidOperationException(msg);
                        }

                        Debug.LogError(msg);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(mat);
                }
            }

            if (GraphicsSettings.currentRenderPipeline == null)
            {
                Debug.LogWarning(
                    "No SRP assigned; Unity Inspector only shows SRP Batcher status when an SRP is active. " +
                    "Validation still ran via ShaderUtil.");
            }

            return allOk;
        }
    }
}
