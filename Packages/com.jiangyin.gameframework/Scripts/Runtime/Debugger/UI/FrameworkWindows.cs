using System.Collections.Generic;
using GameFramework;
using GameFramework.Network;
using GameFramework.ObjectPool;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    /// <summary>
    /// Framework pages that sit beside the console, profiler, and device pages.
    /// </summary>
    static class FrameworkWindows
    {
        public static void AddTo(List<IDebuggerPage> windows)
        {
            windows.Add(new EnvironmentWindow());
            windows.Add(new SceneWindow());
            windows.Add(new PathWindow());
            windows.Add(new InputWindow());
            windows.Add(new ObjectPoolWindow());
            windows.Add(new ReferencePoolWindow());
            windows.Add(new NetworkWindow());
            windows.Add(new OperationsWindow());
        }
    }

    sealed class EnvironmentWindow : DebuggerPage
    {
        Label _product;
        Label _company;
        Label _identifier;
        Label _framework;
        Label _game;
        Label _resource;
        Label _application;
        Label _targetFps;
        Label _network;
        Label _playing;
        Label _editor;
        Label _debug;

        public override string Title => "Environment";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            var box = Box("Environment");
            box.Add(Row("Product Name", out _product));
            box.Add(Row("Company Name", out _company));
            box.Add(Row("Game Identifier", out _identifier));
            box.Add(Row("Game Framework Version", out _framework));
            box.Add(Row("Game Version", out _game));
            box.Add(Row("Resource Version", out _resource));
            box.Add(Row("Application Version", out _application));
            box.Add(Row("Target Frame Rate", out _targetFps));
            box.Add(Row("Internet Reachability", out _network));
            box.Add(Row("Is Playing", out _playing));
            box.Add(Row("Is Editor", out _editor));
            box.Add(Row("Is Debug Build", out _debug));
            scroll.Add(box);
        }

        public override void Refresh()
        {
            BaseComponent basis = GameEntry.GetComponent<BaseComponent>();
            ResourceComponent resource = GameEntry.GetComponent<ResourceComponent>();
            Set(_product, Application.productName);
            Set(_company, Application.companyName);
            Set(_identifier, Application.identifier);
            Set(_framework, GameFramework.Version.GameFrameworkVersion);
            Set(_game, $"{GameFramework.Version.GameVersion} ({GameFramework.Version.InternalGameVersion})");
            if (basis != null && basis.EditorResourceMode)
            {
                Set(_resource, "Unavailable in editor resource mode");
            }
            else if (resource == null || string.IsNullOrEmpty(resource.ApplicableGameVersion))
            {
                Set(_resource, "Unknown");
            }
            else
            {
                Set(_resource, $"{resource.ApplicableGameVersion} ({resource.InternalResourceVersion})");
            }

            Set(_application, Application.version);
            Set(_targetFps, Application.targetFrameRate.ToString());
            Set(_network, Application.internetReachability.ToString());
            Set(_playing, Application.isPlaying.ToString());
            Set(_editor, Application.isEditor.ToString());
            Set(_debug, Debug.isDebugBuild.ToString());
        }
    }

    sealed class SceneWindow : DebuggerPage
    {
        Label _count;
        Label _buildCount;
        Label _name;
        Label _path;
        Label _index;
        Label _loaded;
        Label _roots;

        public override string Title => "Scene";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            var box = Box("Scene");
            box.Add(Row("Scene Count", out _count));
            box.Add(Row("Scenes In Build", out _buildCount));
            box.Add(Row("Active Scene Name", out _name));
            box.Add(Row("Active Scene Path", out _path));
            box.Add(Row("Build Index", out _index));
            box.Add(Row("Is Loaded", out _loaded));
            box.Add(Row("Root Count", out _roots));
            scroll.Add(box);
        }

        public override void Refresh()
        {
            Scene active = SceneManager.GetActiveScene();
            Set(_count, SceneManager.sceneCount.ToString());
            Set(_buildCount, SceneManager.sceneCountInBuildSettings.ToString());
            Set(_name, active.name);
            Set(_path, active.path);
            Set(_index, active.buildIndex.ToString());
            Set(_loaded, active.isLoaded.ToString());
            Set(_roots, active.rootCount.ToString());
        }
    }

    sealed class PathWindow : DebuggerPage
    {
        Label _current;
        Label _data;
        Label _persistent;
        Label _streaming;
        Label _temp;
        Label _log;

        public override string Title => "Path";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            var box = Box("Path");
            box.Add(Row("Current Directory", out _current));
            box.Add(Row("Data Path", out _data));
            box.Add(Row("Persistent Data Path", out _persistent));
            box.Add(Row("Streaming Assets Path", out _streaming));
            box.Add(Row("Temporary Cache Path", out _temp));
            box.Add(Row("Console Log Path", out _log));
            scroll.Add(box);
        }

        public override void Refresh()
        {
            Set(_current, Utility.Path.GetRegularPath(System.Environment.CurrentDirectory));
            Set(_data, Utility.Path.GetRegularPath(Application.dataPath));
            Set(_persistent, Utility.Path.GetRegularPath(Application.persistentDataPath));
            Set(_streaming, Utility.Path.GetRegularPath(Application.streamingAssetsPath));
            Set(_temp, Utility.Path.GetRegularPath(Application.temporaryCachePath));
            Set(_log, Utility.Path.GetRegularPath(Application.consoleLogPath));
        }
    }

    sealed class InputWindow : DebuggerPage
    {
        Label _orientation;
        Label _mouse;
        Label _position;
        Label _anyKey;
        Label _input;
        Label _touch;

        public override string Title => "Input";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            var box = Box("Input");
            box.Add(Row("Device Orientation", out _orientation));
            box.Add(Row("Mouse Present", out _mouse));
            box.Add(Row("Mouse Position", out _position));
            box.Add(Row("Any Key", out _anyKey));
            box.Add(Row("Input String", out _input));
            box.Add(Row("Touch Count", out _touch));
            scroll.Add(box);
        }

        public override void Refresh()
        {
            Set(_orientation, Input.deviceOrientation.ToString());
            Set(_mouse, Input.mousePresent.ToString());
            Set(_position, Input.mousePosition.ToString());
            Set(_anyKey, Input.anyKey.ToString());
            Set(_input, string.IsNullOrEmpty(Input.inputString) ? "-" : Input.inputString);
            Set(_touch, Input.touchCount.ToString());
        }
    }

    sealed class ObjectPoolWindow : DebuggerPage
    {
        VisualElement _body;

        public override string Title => "Object Pool";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            _body = new VisualElement();
            scroll.Add(_body);
        }

        public override void Refresh()
        {
            if (_body == null)
            {
                return;
            }

            _body.Clear();
            ObjectPoolComponent pools = GameEntry.GetComponent<ObjectPoolComponent>();
            if (pools == null)
            {
                _body.Add(new Label("Object pool component is not in the scene."));
                return;
            }

            var summary = Box("Object Pool");
            summary.Add(new Label($"Count {pools.Count}"));
            _body.Add(summary);
            ObjectPoolBase[] all = pools.GetAllObjectPools(true);
            for (int i = 0; i < all.Length; i++)
            {
                ObjectPoolBase pool = all[i];
                var box = Box(string.IsNullOrEmpty(pool.Name) ? pool.FullName : pool.Name);
                box.Add(new Label($"{pool.ObjectType.Name}  used {pool.Count}  free {pool.CanReleaseCount}  cap {pool.Capacity}"));
                ObjectInfo[] infos = pool.GetAllObjectInfos();
                int shown = Mathf.Min(infos.Length, 12);
                for (int n = 0; n < shown; n++)
                {
                    ObjectInfo info = infos[n];
                    string name = string.IsNullOrEmpty(info.Name) ? "<None>" : info.Name;
                    box.Add(new Label($"{name}  inUse {info.IsInUse}  spawn {info.SpawnCount}"));
                }

                if (infos.Length > shown)
                {
                    box.Add(new Label($"… {infos.Length - shown} more"));
                }

                _body.Add(box);
            }
        }
    }

    sealed class ReferencePoolWindow : DebuggerPage
    {
        VisualElement _body;

        public override string Title => "Reference Pool";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            _body = new VisualElement();
            scroll.Add(_body);
        }

        public override void Refresh()
        {
            if (_body == null)
            {
                return;
            }

            _body.Clear();
            var summary = Box("Reference Pool");
            summary.Add(new Label($"Count {ReferencePool.Count}  strict {ReferencePool.EnableStrictCheck}"));
            _body.Add(summary);
            ReferencePoolInfo[] infos = ReferencePool.GetAllReferencePoolInfos();
            int shown = Mathf.Min(infos.Length, 40);
            for (int i = 0; i < shown; i++)
            {
                ReferencePoolInfo info = infos[i];
                _body.Add(new Label(
                    $"{info.Type.Name}  unused {info.UnusedReferenceCount}  using {info.UsingReferenceCount}  acquire {info.AcquireReferenceCount}  release {info.ReleaseReferenceCount}"));
            }

            if (infos.Length > shown)
            {
                _body.Add(new Label($"… {infos.Length - shown} more"));
            }
        }
    }

    sealed class NetworkWindow : DebuggerPage
    {
        VisualElement _body;

        public override string Title => "Network";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            _body = new VisualElement();
            scroll.Add(_body);
        }

        public override void Refresh()
        {
            if (_body == null)
            {
                return;
            }

            _body.Clear();
            NetworkComponent network = GameEntry.GetComponent<NetworkComponent>();
            if (network == null)
            {
                _body.Add(new Label("Network component is not in the scene."));
                return;
            }

            var summary = Box("Network");
            summary.Add(new Label($"Channels {network.NetworkChannelCount}"));
            _body.Add(summary);
            INetworkChannel[] channels = network.GetAllNetworkChannels();
            for (int i = 0; i < channels.Length; i++)
            {
                INetworkChannel channel = channels[i];
                var box = Box($"{channel.Name} ({(channel.Connected ? "Connected" : "Disconnected")})");
                box.Add(new Label($"Service {channel.ServiceType}  family {channel.AddressFamily}"));
                box.Add(new Label($"Send {channel.SendPacketCount} / {channel.SentPacketCount}"));
                box.Add(new Label($"Receive {channel.ReceivePacketCount} / {channel.ReceivedPacketCount}"));
                if (channel.Connected)
                {
                    INetworkChannel captured = channel;
                    var disconnect = new Button(() => captured.Close()) { text = "Disconnect" };
                    disconnect.AddToClassList("perf-button");
                    box.Add(disconnect);
                }

                _body.Add(box);
            }
        }
    }

    sealed class OperationsWindow : DebuggerPage
    {
        public override string Title => "Operations";

        protected override void OnBuild(VisualElement root)
        {
            ScrollView scroll = ScrollHost(root);
            var box = Box("Operations");
            box.Add(Action("Object Pool Release", () => GameEntry.GetComponent<ObjectPoolComponent>()?.Release()));
            box.Add(Action("Object Pool Release Unused", () => GameEntry.GetComponent<ObjectPoolComponent>()?.ReleaseAllUnused()));
            box.Add(Action("Unload Unused Assets", () => GameEntry.GetComponent<ResourceComponent>()?.ForceUnloadUnusedAssets(false)));
            box.Add(Action("Unload Assets And Collect", () => GameEntry.GetComponent<ResourceComponent>()?.ForceUnloadUnusedAssets(true)));
            box.Add(Action("Shutdown", () => GameEntry.Shutdown(ShutdownType.None)));
            box.Add(Action("Restart", () => GameEntry.Shutdown(ShutdownType.Restart)));
            box.Add(Action("Quit", () => GameEntry.Shutdown(ShutdownType.Quit)));
            scroll.Add(box);
        }

        static Button Action(string title, System.Action action)
        {
            var button = new Button(() => action()) { text = title };
            button.AddToClassList("perf-button");
            button.style.marginBottom = 6;
            button.style.height = 32;
            return button;
        }
    }
}

