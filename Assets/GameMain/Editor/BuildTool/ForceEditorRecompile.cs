#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// One-shot domain reload kick so HostShell export picks up BoluoCI Vulkan-only.
    /// Safe to leave; RequestScriptCompilation is a no-op when already up to date.
    /// </summary>
    [InitializeOnLoad]
    static class ForceEditorRecompile
    {
        const string StampKey = "Boluo.ForceEditorRecompile.TerrainFix20261102";

        static ForceEditorRecompile()
        {
            // batchmode（含 -runTests）域重载后再 RequestScriptCompilation 会把测试跑丢。
            if (Application.isBatchMode)
                return;
            if (SessionState.GetBool(StampKey, false))
                return;
            SessionState.SetBool(StampKey, true);
            Debug.Log("[BoluoCI] ForceEditorRecompile → RequestScriptCompilation (TERRAIN_FIX_20261102)");
            CompilationPipeline.RequestScriptCompilation();
        }
    }
}
#endif
