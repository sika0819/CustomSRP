#if UNITY_EDITOR
using System;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// 解析 Unity 命令行参数，与环境变量二选一（环境变量优先）。
    /// 例: -boluoVersion 1.1.6 -boluoEnv test
    /// </summary>
    internal static class BoluoCIArgParser
    {
        public static string Resolve(string envKey, params string[] cliKeys)
        {
            string fromEnv = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();

            string fromCli = TryGet(cliKeys);
            return string.IsNullOrWhiteSpace(fromCli) ? null : fromCli.Trim();
        }

        static string TryGet(string[] keys)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                foreach (string key in keys)
                {
                    if (TryMatchKeyValue(arg, key, out string inlineValue))
                        return inlineValue;

                    if (IsKeyToken(arg, key) && i + 1 < args.Length)
                        return args[i + 1];
                }
            }

            return null;
        }

        static bool IsKeyToken(string arg, string key)
        {
            return arg.Equals(key, StringComparison.OrdinalIgnoreCase)
                   || arg.Equals("-" + key, StringComparison.OrdinalIgnoreCase)
                   || arg.Equals("--" + key, StringComparison.OrdinalIgnoreCase);
        }

        static bool TryMatchKeyValue(string arg, string key, out string value)
        {
            value = null;
            string[] prefixes =
            {
                key + "=",
                "-" + key + "=",
                "--" + key + "="
            };

            foreach (string prefix in prefixes)
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = arg.Substring(prefix.Length);
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
