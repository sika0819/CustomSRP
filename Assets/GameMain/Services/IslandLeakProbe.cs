using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 泄漏对照。默认每个模块都关着，运行时用命令一个一个打开。
    /// 正常游玩把 <see cref="Enabled"/> 改回 false。
    /// </summary>
    public static class IslandLeakProbe
    {
        public enum Module
        {
            Terrain = 0,
            Ocean,
            Inland,
            Grass,
            Songshu,
            Lvshu,
            Yeshu,
            Guanmu,
            ShiTou,
            JianZhu,
            Qiao,
            ZaWu,
            Hua,
            Zacao,
            Zhibei,
            Other,
            Common,
            Texiao,
            FxDay,
            FxNight,
            Reflect,
            Cloud,
            Rain,
            Sky,
            Shadow,
            PostFx,
            Warp,
            Lut,
            Audio,
            Hud,
            Count,
        }

        public const string FlagFileName = "boluo_leak_probe";

        /// <summary>
        /// true：进岛时下面的模块全部关闭，等命令再打开。
        /// 真机：<c>persistentDataPath/boluo_leak_probe</c>。
        /// </summary>
#if BOLUO_LEAK_PROBE
        static bool _enabledDefault = true;
#else
        static bool _enabledDefault = false;
#endif
        static bool? _enabledCache;

        public static bool Enabled
        {
            get
            {
                if (_enabledCache.HasValue)
                    return _enabledCache.Value;
                bool on = _enabledDefault || FlagExists();
                _enabledCache = on;
                if (on)
                    Log.Info("[LeakProbe] enabled via flag/default");
                return on;
            }
            set => _enabledCache = value;
        }

        public static event Action<Module, bool> Changed;

        static readonly bool[] On = new bool[(int)Module.Count];
        static readonly HashSet<string> UnknownCategories = new HashSet<string>();
        static Dictionary<string, Module> _alias;

        static readonly string[] Names =
        {
            "地形", "海", "河", "草",
            "松", "绿树", "椰树", "灌木",
            "石头", "建筑", "桥", "杂物",
            "花", "杂草", "植被", "其他",
            "公共", "特效", "白天特效", "夜晚特效",
            "倒影", "云", "雨", "天空",
            "阴影", "后处理", "扭曲", "LUT",
            "音频", "界面",
        };

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            Array.Clear(On, 0, On.Length);
            UnknownCategories.Clear();
            _enabledCache = null;
        }

        static bool FlagExists()
        {
            try
            {
                string persistent = Path.Combine(Application.persistentDataPath, FlagFileName);
                if (File.Exists(persistent))
                    return true;
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var env = new AndroidJavaClass("android.os.Environment"))
                using (var dir = env.CallStatic<AndroidJavaObject>("getExternalStorageDirectory"))
                {
                    string root = dir != null ? dir.Call<string>("getAbsolutePath") : null;
                    if (!string.IsNullOrEmpty(root))
                    {
                        string external = Path.Combine(
                            root, "Android/data", Application.identifier, "files", FlagFileName);
                        if (File.Exists(external))
                            return true;
                    }
                }
#endif
            }
            catch (Exception)
            {
                // ignore
            }

            return false;
        }

        public static bool Allow(Module module)
        {
            if (!Enabled)
                return true;
            int index = (int)module;
            return (uint)index < On.Length && On[index];
        }

        public static bool AllowCategory(string category)
        {
            if (!Enabled)
                return true;
            if (!TryCategory(category, out Module module))
            {
                if (!string.IsNullOrEmpty(category) && UnknownCategories.Add(category))
                    Log.Warning("[LeakProbe] 未登记分类，保持关闭: " + category);
                return false;
            }

            return Allow(module);
        }

        public static void Set(Module module, bool on)
        {
            if (!Enabled)
            {
                Log.Warning("[LeakProbe] Enabled=false，命令不起作用");
                return;
            }

            int index = (int)module;
            if ((uint)index >= On.Length || module == Module.Count)
                return;

            if (On[index] == on)
            {
                Log.Info("[LeakProbe] " + Display(module) + (on ? " 已经开着" : " 已经关着"));
                LogMemory(Display(module));
                return;
            }

            On[index] = on;
            Log.Info("[LeakProbe] " + Display(module) + (on ? " 打开" : " 关闭"));
            LogMemory(Display(module));
            Changed?.Invoke(module, on);
        }

        public static void Execute(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            line = line.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal))
                return;

            string[] parts = line.Split(
                new[] { ' ', '\t', ',', '，', '、' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string verb = parts[0];
            if (Is(verb, "list", "ls", "列表", "状态"))
            {
                LogList();
                return;
            }

            if (Is(verb, "mem", "memory", "内存"))
            {
                LogMemory("snapshot");
                return;
            }

            if (Is(verb, "help", "帮助", "?"))
            {
                LogHelp();
                return;
            }

            bool turnOn;
            string name;
            if (Is(verb, "on", "开", "打开", "1"))
            {
                if (parts.Length < 2)
                {
                    Log.Warning("[LeakProbe] 用法: on 海");
                    return;
                }

                turnOn = true;
                name = parts[1];
            }
            else if (Is(verb, "off", "关", "关闭", "0"))
            {
                if (parts.Length < 2)
                {
                    Log.Warning("[LeakProbe] 用法: off 海");
                    return;
                }

                turnOn = false;
                name = parts[1];
            }
            else if (TryParse(verb, out Module single))
            {
                Set(single, true);
                return;
            }
            else
            {
                Log.Warning("[LeakProbe] 无法识别: " + line);
                return;
            }

            if (Is(name, "all", "全部", "所有"))
            {
                if (turnOn)
                {
                    Log.Warning("[LeakProbe] 不要一次全开。一个一个打开才能对上内存。");
                    return;
                }

                for (int i = 0; i < (int)Module.Count; i++)
                    Set((Module)i, false);
                return;
            }

            if (!TryParse(name, out Module module))
            {
                Log.Warning("[LeakProbe] 没有这个模块: " + name);
                return;
            }

            Set(module, turnOn);
        }

        public static void LogMemory(string reason)
        {
            long managed = GC.GetTotalMemory(false);
            long unity = Profiler.GetTotalAllocatedMemoryLong();
            long reserved = Profiler.GetTotalReservedMemoryLong();
            long gfx = 0;
            try
            {
                gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
            }
            catch (Exception)
            {
                gfx = 0;
            }

            Log.Info(
                "[LeakProbe] mem " + reason
                + " managed=" + Mb(managed)
                + " unity=" + Mb(unity)
                + " gfx=" + Mb(gfx)
                + " reserved=" + Mb(reserved));
        }

        public static void LogHelp()
        {
            Log.Info(
                "[LeakProbe] 命令: on 地形 / on 海 / on 河 / on 草 / on 松 / on 绿树 / on 椰树 / on 灌木 / "
                + "on 石头 / on 建筑 / on 桥 / on 杂物 / on 花 / on 杂草 / on 植被 / "
                + "on 倒影 / on 云 / on 雨 / on 天空 / on 阴影 / on 后处理 / on 扭曲 / on LUT / "
                + "on 音频 / on 界面 / list / mem 。关已加载的地形、海、河、草不会把对象拆掉。");
        }

        public static string Display(Module module)
        {
            int index = (int)module;
            if ((uint)index < Names.Length)
                return Names[index];
            return module.ToString();
        }

        static void LogList()
        {
            var sb = new StringBuilder(256);
            sb.Append("[LeakProbe]");
            for (int i = 0; i < (int)Module.Count; i++)
            {
                sb.Append(' ');
                sb.Append(Names[i]);
                sb.Append(On[i] ? "=开" : "=关");
            }

            Log.Info(sb.ToString());
            LogMemory("list");
        }

        static bool TryCategory(string category, out Module module)
        {
            module = default;
            if (string.IsNullOrEmpty(category))
                return false;
            if (Eq(category, "Songshu"))
                module = Module.Songshu;
            else if (Eq(category, "Lvshu"))
                module = Module.Lvshu;
            else if (Eq(category, "Yeshu"))
                module = Module.Yeshu;
            else if (Eq(category, "Guanmu"))
                module = Module.Guanmu;
            else if (Eq(category, "ShiTou"))
                module = Module.ShiTou;
            else if (Eq(category, "JianZhu"))
                module = Module.JianZhu;
            else if (Eq(category, "Qiao"))
                module = Module.Qiao;
            else if (Eq(category, "ZaWu"))
                module = Module.ZaWu;
            else if (Eq(category, "Hua"))
                module = Module.Hua;
            else if (Eq(category, "Zacao"))
                module = Module.Zacao;
            else if (Eq(category, "Zhibei"))
                module = Module.Zhibei;
            else if (Eq(category, "Other"))
                module = Module.Other;
            else if (Eq(category, "Common"))
                module = Module.Common;
            else if (Eq(category, "texiao"))
                module = Module.Texiao;
            else if (Eq(category, "FX_Day"))
                module = Module.FxDay;
            else if (Eq(category, "FX_Night"))
                module = Module.FxNight;
            else
                return false;
            return true;
        }

        static bool TryParse(string token, out Module module)
        {
            if (string.IsNullOrEmpty(token))
            {
                module = default;
                return false;
            }

            return Alias.TryGetValue(token.Trim(), out module);
        }

        static Dictionary<string, Module> Alias
        {
            get
            {
                if (_alias != null)
                    return _alias;

                _alias = new Dictionary<string, Module>(96, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < (int)Module.Count; i++)
                {
                    var module = (Module)i;
                    _alias[module.ToString()] = module;
                    _alias[Names[i]] = module;
                }

                Add(Module.Terrain, "地");
                Add(Module.Ocean, "海洋", "海水");
                Add(Module.Inland, "内河", "瀑布", "河流");
                Add(Module.Grass, "草地");
                Add(Module.Songshu, "松树");
                Add(Module.Yeshu, "椰");
                Add(Module.ShiTou, "石");
                Add(Module.JianZhu, "房子", "建筑群");
                Add(Module.Reflect, "反射", "倒影面");
                Add(Module.Sky, "天空盒");
                Add(Module.Shadow, "影子");
                Add(Module.PostFx, "后效", "post");
                Add(Module.Warp, "warp");
                Add(Module.Lut, "校色", "lut");
                Add(Module.Audio, "声音", "音乐");
                Add(Module.Hud, "ui", "主界面");
                Add(Module.Texiao, "特效层");
                Add(Module.Common, "common");
                Add(Module.Other, "other");
                return _alias;
            }
        }

        static void Add(Module module, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                _alias[keys[i]] = module;
        }

        static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static bool Is(string token, params string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                if (string.Equals(token, words[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static string Mb(long bytes) => (bytes / (1024f * 1024f)).ToString("F1") + "MB";
    }
}
