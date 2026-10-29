using System.IO;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 二次进岛阶梯：LeakProbe 下插一档「连 MapItem 都不建」。
    /// 文件 <c>boluo_skip_mapitem</c> 存在且 LeakProbe 开着时，跳过 SpawnMapItems。
    /// </summary>
    public static class IslandStageGate
    {
        public const string SkipMapItemFlag = "boluo_skip_mapitem";

        static bool? _skipMapCache;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => _skipMapCache = null;

        public static bool SkipMapItems
        {
            get
            {
                if (_skipMapCache.HasValue)
                    return _skipMapCache.Value;
                bool on = IslandLeakProbe.Enabled && FlagExists(SkipMapItemFlag);
                _skipMapCache = on;
                if (on)
                    Log.Info("[IslandStage] skip MapItem/Prefill");
                return on;
            }
        }

        static bool FlagExists(string name)
        {
            try
            {
                if (File.Exists(Path.Combine(Application.persistentDataPath, name)))
                    return true;
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var env = new AndroidJavaClass("android.os.Environment"))
                using (var dir = env.CallStatic<AndroidJavaObject>("getExternalStorageDirectory"))
                {
                    string root = dir != null ? dir.Call<string>("getAbsolutePath") : null;
                    if (!string.IsNullOrEmpty(root)
                        && File.Exists(Path.Combine(
                            root, "Android/data", Application.identifier, "files", name)))
                        return true;
                }
#endif
            }
            catch (System.Exception)
            {
                // ignore
            }

            return false;
        }
    }
}
