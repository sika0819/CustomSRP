using System;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 读命令文件，把 <see cref="IslandLeakProbe"/> 的模块一个一个打开。
    /// </summary>
    sealed class IslandLeakProbeDriver : MonoBehaviour
    {
        const string Header =
            "# 泄漏对照。一行一条，保存后约半秒被读走。\n"
            + "# on 地形\n"
            + "# on 海\n"
            + "# on 河\n"
            + "# on 草\n"
            + "# on 松\n"
            + "# list\n"
            + "# mem\n";

        float _nextPoll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!IslandLeakProbe.Enabled)
                return;
            if (FindAnyObjectByType<IslandLeakProbeDriver>() != null)
                return;

            var go = new GameObject(nameof(IslandLeakProbeDriver));
            DontDestroyOnLoad(go);
            go.AddComponent<IslandLeakProbeDriver>();
        }

        void OnEnable()
        {
            IslandLeakProbe.Changed += OnChanged;
        }

        void OnDisable()
        {
            IslandLeakProbe.Changed -= OnChanged;
        }

        void Start()
        {
            if (!IslandLeakProbe.Enabled)
            {
                enabled = false;
                return;
            }

            EnsureFile(ProjectCommandPath);
            EnsureFile(PersistentCommandPath);
            IslandLeakProbe.LogHelp();
            Log.Info("[LeakProbe] 编辑器命令文件 " + ProjectCommandPath);
            Log.Info("[LeakProbe] 真机命令文件 " + PersistentCommandPath);
            IslandLeakProbe.LogMemory("baseline");
        }

        void Update()
        {
            if (!IslandLeakProbe.Enabled)
                return;
            if (Time.unscaledTime < _nextPoll)
                return;
            _nextPoll = Time.unscaledTime + 0.4f;
            Poll(ProjectCommandPath);
            Poll(PersistentCommandPath);
        }

        static string ProjectCommandPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "LeakProbeCommand.txt"));

        static string PersistentCommandPath =>
            Path.Combine(Application.persistentDataPath, "LeakProbeCommand.txt");

        static void EnsureFile(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (!File.Exists(path))
                    File.WriteAllText(path, Header);
            }
            catch (Exception e)
            {
                Log.Warning("[LeakProbe] 命令文件不可写 " + path + " " + e.Message);
            }
        }

        static void Poll(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Log.Warning("[LeakProbe] 读命令失败 " + e.Message);
                return;
            }

            if (string.IsNullOrEmpty(text))
                return;

            string pending = string.Empty;
            if (!text.EndsWith("\n", StringComparison.Ordinal)
                && !text.EndsWith("\r", StringComparison.Ordinal))
            {
                int cut = text.LastIndexOf('\n');
                if (cut < 0)
                    return;
                pending = text.Substring(cut + 1);
                text = text.Substring(0, cut + 1);
            }

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool any = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                any = true;
                Log.Info("[LeakProbe] cmd " + line);
                IslandLeakProbe.Execute(line);
            }

            if (!any)
                return;

            try
            {
                var keep = new StringBuilder(Header.Length + pending.Length + 8);
                keep.Append(Header);
                if (pending.Length > 0)
                {
                    keep.Append(pending);
                    if (!pending.EndsWith("\n", StringComparison.Ordinal))
                        keep.Append('\n');
                }

                File.WriteAllText(path, keep.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[LeakProbe] 写回命令文件失败 " + e.Message);
            }
        }

        static void OnChanged(IslandLeakProbe.Module module, bool on)
        {
            if (!on)
            {
                if (module == IslandLeakProbe.Module.Rain)
                    SetRainBehaviours(false);
                if (module == IslandLeakProbe.Module.Terrain
                    || module == IslandLeakProbe.Module.Ocean
                    || module == IslandLeakProbe.Module.Inland
                    || module == IslandLeakProbe.Module.Grass)
                {
                    Log.Info(
                        "[LeakProbe] " + IslandLeakProbe.Display(module)
                        + " 已关闭后续加载。已经在内存里的实例不会拆。");
                }

                return;
            }

            switch (module)
            {
                case IslandLeakProbe.Module.Terrain:
                case IslandLeakProbe.Module.Ocean:
                case IslandLeakProbe.Module.Inland:
                case IslandLeakProbe.Module.Grass:
                    IslandWorldService world = GameEntry.World;
                    if (world != null)
                        world.StartCoroutine(world.ActivateModule(module));
                    else
                        Log.Info("[LeakProbe] 世界还没建，进岛时会带上 " + IslandLeakProbe.Display(module));
                    break;
                case IslandLeakProbe.Module.Rain:
                    SetRainBehaviours(true);
                    break;
                case IslandLeakProbe.Module.Audio:
                    StartAudio().Forget();
                    break;
                case IslandLeakProbe.Module.Hud:
                    OpenHud();
                    break;
            }
        }

        static void OpenHud()
        {
            if (GameEntry.UI == null)
            {
                Log.Info("[LeakProbe] UI 还没有。开关已记下，进岛到界面阶段会打开。");
                return;
            }

            if (!GameEntry.UI.HasUIForm(UiAssets.IslandMainForm))
                GameEntry.UI.OpenUIForm(UiAssets.IslandMainForm, "Main");
            UiTipToast.EnsureHost();
            IslandLeakProbe.LogMemory("hud");
        }

        static async UniTaskVoid StartAudio()
        {
            try
            {
                await BankUpgradeBootstrap.EnsureLocalBanksReadyAsync();
                WanosAudio.WanosEngine.EnablePlayback("LeakProbe");
                WanosAudio.WanosEngine.StartEngine("LeakProbe");
                WanosAudio.WanosEngine.Unmute("LeakProbe");
                GameEntry.Music?.ApplySavedVolumes();
                if (IslandAudioSession.Instance == null)
                    Log.Info("[LeakProbe] 音频会话还没有。开关已记下，揭罩时会再启动。");
                else
                    IslandAudioSession.Instance.StartAfterUncover();
                NowPlayingService.ArmSession();
                NowPlayingService.PublishPlayingForced();
                BankUpgradeBootstrap.WaitCriticalThenCueAsync(45f).Forget();
                IslandLeakProbe.LogMemory("audio");
            }
            catch (Exception e)
            {
                Log.Error("[LeakProbe] audio " + e.Message);
            }
        }

        static void SetRainBehaviours(bool on)
        {
            MonoBehaviour[] list = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            int n = 0;
            for (int i = 0; i < list.Length; i++)
            {
                MonoBehaviour behaviour = list[i];
                if (behaviour == null)
                    continue;
                if (!behaviour.GetType().Name.StartsWith("OilRain", StringComparison.Ordinal))
                    continue;
                if (behaviour.enabled == on)
                    continue;
                behaviour.enabled = on;
                n++;
            }

            Log.Info("[LeakProbe] rain behaviours " + (on ? "on " : "off ") + n);
        }
    }
}
