using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CustomSRP.Debugger
{
    /// <summary>
    /// Runtime performance debugger (UI Toolkit). Ported from Boluo TEngine DebuggerModule
    /// performance windows; no IMGUI / uGUI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PerfDebugger : MonoBehaviour
    {
        const string PrefScale = "CustomSRP.PerfDebugger.UiScale";
        const string PrefOpen = "CustomSRP.PerfDebugger.ShowFull";

        static PerfDebugger _instance;
        static StyleSheet _cachedStyle;

        [SerializeField]
        PerfDebuggerActiveWindowType activeWindow = PerfDebuggerActiveWindowType.OnlyOpenWhenDevelopment;

        [SerializeField]
        float uiScale = 1.25f;

        [SerializeField]
        int targetFrameRate = 60;

        UIDocument _document;
        PanelSettings _panelSettings;
        VisualElement _root;
        Button _icon;
        VisualElement _panel;
        VisualElement _tabs;
        VisualElement _content;
        readonly List<IPerfDebuggerWindow> _windows = new();
        readonly List<Button> _tabButtons = new();
        ConsoleWindow _console;
        FpsCounter _fps;
        IPerfDebuggerWindow _current;
        bool _showFull;
        float _refreshTimer;
        bool _active;

        public static PerfDebugger Instance => _instance;

        public float UiScale
        {
            get => uiScale;
            set
            {
                uiScale = Mathf.Clamp(value, 0.75f, 2.5f);
                PlayerPrefs.SetFloat(PrefScale, uiScale);
                ApplyScale();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (_instance != null)
            {
                return;
            }

            // Default gate: Editor + Development players only.
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            return;
#endif

            var go = new GameObject("[PerfDebugger]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<PerfDebugger>();
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            uiScale = PlayerPrefs.GetFloat(PrefScale, uiScale);
            _showFull = PlayerPrefs.GetInt(PrefOpen, 0) == 1;
            _fps = new FpsCounter(0.5f);

            QualitySettings.vSyncCount = 0;
            if (targetFrameRate > 0)
            {
                Application.targetFrameRate = targetFrameRate;
            }

            EnsureUi();
            RegisterWindows();
            ResolveActive();
            if (_active)
            {
                ShowIconOrPanel();
            }
            else
            {
                enabled = false;
            }
        }

        void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            for (int i = 0; i < _windows.Count; i++)
            {
                _windows[i].Shutdown();
            }

            if (_panelSettings != null)
            {
                Destroy(_panelSettings);
            }
        }

        void Update()
        {
            if (!_active)
            {
                return;
            }

            _fps.Update(Time.unscaledDeltaTime);
            _current?.OnUpdate(Time.deltaTime, Time.unscaledDeltaTime);

            if (_icon != null && !_showFull)
            {
                Color32 c = _console != null ? _console.BadgeColor() : new Color32(180, 255, 180, 255);
                _icon.style.color = new StyleColor((Color)c);
                _icon.text = $"{_fps.CurrentFps:0.0} FPS\n{_fps.CurrentMs:0.0} ms";
            }

            _refreshTimer += Time.unscaledDeltaTime;
            if (_showFull && _refreshTimer >= 0.5f)
            {
                _refreshTimer = 0f;
                _current?.Refresh();
            }
        }

        public void ResetLayout()
        {
            UiScale = 1.25f;
            _showFull = false;
            PlayerPrefs.SetInt(PrefOpen, 0);
            ShowIconOrPanel();
        }

        void ResolveActive()
        {
            _active = activeWindow switch
            {
                PerfDebuggerActiveWindowType.AlwaysOpen => true,
                PerfDebuggerActiveWindowType.OnlyOpenWhenDevelopment => Debug.isDebugBuild,
                PerfDebuggerActiveWindowType.OnlyOpenInEditor => Application.isEditor,
                _ => false,
            };
        }

        void EnsureUi()
        {
            _document = gameObject.GetComponent<UIDocument>();
            if (_document == null)
            {
                _document = gameObject.AddComponent<UIDocument>();
            }

            _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _panelSettings.name = "PerfDebuggerPanelSettings";
            _panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            _panelSettings.scale = uiScale;
            _panelSettings.sortingOrder = 32000;
            _document.panelSettings = _panelSettings;

            _root = new VisualElement();
            _root.AddToClassList("perf-root");
            _root.pickingMode = PickingMode.Ignore;
            StyleSheet sheet = LoadStyle();
            if (sheet != null)
            {
                _root.styleSheets.Add(sheet);
            }

            _document.rootVisualElement.Clear();
            _document.rootVisualElement.Add(_root);

            _icon = new Button(OpenFull) { text = "FPS" };
            _icon.AddToClassList("perf-icon");
            _icon.pickingMode = PickingMode.Position;
            _root.Add(_icon);

            _panel = new VisualElement();
            _panel.AddToClassList("perf-panel");
            _panel.pickingMode = PickingMode.Position;
            _panel.style.display = DisplayStyle.None;

            var titlebar = new VisualElement();
            titlebar.AddToClassList("perf-titlebar");
            var title = new Label("PERF DEBUGGER");
            title.AddToClassList("perf-title");
            var close = new Button(CloseFull) { text = "Close" };
            close.AddToClassList("perf-close");
            titlebar.Add(title);
            titlebar.Add(close);
            _panel.Add(titlebar);

            _tabs = new VisualElement();
            _tabs.AddToClassList("perf-tabs");
            _panel.Add(_tabs);

            _content = new VisualElement();
            _content.AddToClassList("perf-content");
            _content.style.flexGrow = 1;
            _content.style.flexShrink = 1;
            _content.style.flexDirection = FlexDirection.Column;
            _content.style.minHeight = 0;
            _panel.Add(_content);
            _root.Add(_panel);

            ApplyScale();
        }

        void RegisterWindows()
        {
            _console = new ConsoleWindow();
            AddWindow(_console);
            AddWindow(new ProfilerWindow());
            AddWindow(new MemorySummaryWindow());
            AddWindow(new SystemWindow());
            AddWindow(new ScreenWindow());
            AddWindow(new GraphicsWindow());
            AddWindow(new QualityWindow());
            AddWindow(new TimeWindow());
            AddWindow(new SettingsWindow(this));

            _tabs.Clear();
            _tabButtons.Clear();
            for (int i = 0; i < _windows.Count; i++)
            {
                int index = i;
                IPerfDebuggerWindow window = _windows[i];
                window.Initialize();
                var tab = new Button(() => SelectWindow(index)) { text = window.Title };
                tab.AddToClassList("perf-tab");
                _tabs.Add(tab);
                _tabButtons.Add(tab);
            }

            if (_windows.Count > 0)
            {
                SelectWindow(0);
            }
        }

        void AddWindow(IPerfDebuggerWindow window) => _windows.Add(window);

        void SelectWindow(int index)
        {
            if (index < 0 || index >= _windows.Count)
            {
                return;
            }

            _current?.OnLeave();
            _current = _windows[index];
            _current.OnEnter();
            _content.Clear();
            var host = new VisualElement();
            host.style.flexGrow = 1;
            host.style.flexShrink = 1;
            host.style.flexDirection = FlexDirection.Column;
            host.style.minHeight = 0;
            _content.Add(host);
            _current.Build(host);

            for (int i = 0; i < _tabButtons.Count; i++)
            {
                if (i == index)
                {
                    _tabButtons[i].AddToClassList("perf-tab-active");
                }
                else
                {
                    _tabButtons[i].RemoveFromClassList("perf-tab-active");
                }
            }
        }

        void OpenFull()
        {
            _showFull = true;
            PlayerPrefs.SetInt(PrefOpen, 1);
            ShowIconOrPanel();
            _current?.Refresh();
        }

        void CloseFull()
        {
            _showFull = false;
            PlayerPrefs.SetInt(PrefOpen, 0);
            ShowIconOrPanel();
        }

        void ShowIconOrPanel()
        {
            if (_icon == null || _panel == null)
            {
                return;
            }

            _icon.style.display = _showFull ? DisplayStyle.None : DisplayStyle.Flex;
            _panel.style.display = _showFull ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ApplyScale()
        {
            if (_panelSettings != null)
            {
                _panelSettings.scale = uiScale;
            }
        }

        static StyleSheet LoadStyle()
        {
            if (_cachedStyle != null)
            {
                return _cachedStyle;
            }

            _cachedStyle = Resources.Load<StyleSheet>("PerfDebugger");
            return _cachedStyle;
        }
    }
}
