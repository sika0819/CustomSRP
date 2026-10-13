// 自动给所有 BuildTargetGroup 注入 UGF 日志所需的 Scripting Define Symbols。
// UGF 的 Log.Info/Warning/Error 全部带 [Conditional(ENABLE_*_LOG)]，不定义这些宏
// 会让所有 Log.* 调用在编译期被剔除，等于实际不生效。
// 缺哪个就补哪个；不重复、不抹掉用户已存在的宏。
//
// 触发时机：
//   - [InitializeOnLoadMethod] Unity 打开工程 / 重新加载 AppDomain 时跑一次
//   - 提供一个菜单 Tools/Custom SRP/Force Reapply UGF Log Defines 手动强制再跑一次
//
// 注：Build Player 时不执行（包了 #if UNITY_EDITOR）。

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    [InitializeOnLoad]
    public static class UGFLogMacrosBootstrap
    {
        // 想要强制打开的日志宏集合
        static readonly string[] RequiredDefines =
        {
            "ENABLE_LOG",                  // 总闸门
            "ENABLE_DEBUG_LOG",
            "ENABLE_INFO_LOG",
            "ENABLE_WARNING_LOG",
            "ENABLE_ERROR_LOG",
            "ENABLE_FATAL_LOG",
        };

        // 兼容 Unity 6 的所有活跃 BuildTargetGroup。
        // 包含全部的目标组，方便第一次开工程时一次性铺好；
        // 后续如果用户/项目要求某个目标关掉，自行再编辑就 OK。
        static readonly BuildTargetGroup[] TargetGroups =
        {
            BuildTargetGroup.Standalone,
            BuildTargetGroup.Android,
            BuildTargetGroup.iOS,
            BuildTargetGroup.WebGL,
            BuildTargetGroup.tvOS,
        };

        const string PrefKey = "CustomSRP.UGFLogMacrosBootstrap.Applied";

        static UGFLogMacrosBootstrap()
        {
            // 让 ApplyAndReport() 在 Editor 完成加载后再跑，避免一些初始化竞态。
            EditorApplication.delayCall += () =>
            {
                try { ApplyAndReport(force: false); }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            };
        }

        [MenuItem("Tools/Custom SRP/Force Reapply UGF Log Defines")]
        public static void ForceApplyFromMenu()
        {
            ApplyAndReport(force: true);
        }

        static void ApplyAndReport(bool force)
        {
            // 触发后清掉 flag，这样下一次变更后才能再被自动触发一次。
            if (force) EditorPrefs.DeleteKey(PrefKey);

            if (!force && EditorPrefs.GetBool(PrefKey, false))
                return;

            bool anyChanged = false;
            var report = new List<string>();

            foreach (var group in DistinctGroups())
            {
                string current = PlayerSettings.GetScriptingDefineSymbolsForGroup(group) ?? string.Empty;
                var set = new HashSet<string>(
                    current.Split(';', ',')
                           .Select(s => s.Trim())
                           .Where(s => s.Length > 0));

                int added = 0;
                foreach (var d in RequiredDefines)
                {
                    if (set.Add(d)) added++;
                }

                if (added > 0)
                {
                    string merged = string.Join(";", set);
                    PlayerSettings.SetScriptingDefineSymbolsForGroup(group, merged);
                    AssetDatabase.SaveAssets();
                    anyChanged = true;
                    report.Add($"  +{group} : +{added} symbols -> {merged}");
                }
                else
                {
                    report.Add($"  ={group} : unchanged ({current})");
                }
            }

            EditorPrefs.SetBool(PrefKey, true);

            // 注意：这里故意用 UnityEngine.Debug.*，不用 UGF Log.*。
            // 因为本脚本的目标就是给工程补 ENABLE_*_LOG，未补上之前，
            // UGF Log.* 会被 [Conditional] 编译剔除，提示信息看不到。
            if (anyChanged)
            {
                Debug.LogWarning("[UGFLogMacrosBootstrap] Patched Scripting Define Symbols (recompile required):\n"
                                 + string.Join("\n", report));
            }
            else
            {
                Debug.Log("[UGFLogMacrosBootstrap] All required ENABLE_*_LOG macros already present.\n"
                          + string.Join("\n", report));
            }
        }

        // 去重，防止未来扩展列表时塞到重复枚举值。
        static IEnumerable<BuildTargetGroup> DistinctGroups()
        {
            var seen = new HashSet<string>();
            foreach (var g in TargetGroups)
            {
                string key = g.ToString();
                if (seen.Add(key)) yield return g;
            }
        }
    }
}
#endif
