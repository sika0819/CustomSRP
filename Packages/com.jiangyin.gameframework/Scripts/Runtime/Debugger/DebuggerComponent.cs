//------------------------------------------------------------
// Game Framework
// Copyright © 2013-2021 Jiang Yin. All rights reserved.
// Homepage: https://gameframework.cn/
// Feedback: mailto:ellan@gameframework.cn
//------------------------------------------------------------

using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    /// <summary>
    /// 调试器组件。界面是 UI Toolkit，挂在本组件所在物体上。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Framework/Debugger")]
    public sealed class DebuggerComponent : GameFrameworkComponent
    {
        const string PrefScale = "Debugger.UiScale";
        const string PrefOpen = "Debugger.ShowFull";
        const float PanelMargin = 8f;
        const float PanelMaxWidth = 720f;

        static StyleSheet s_CachedStyle = null;
        static MethodInfo s_PanelUpdate = null;

        [SerializeField]
        private DebuggerActiveWindowType m_ActiveWindow = DebuggerActiveWindowType.AlwaysOpen;

        [SerializeField]
        private float m_UiScale = 1.25f;

        [SerializeField]
        private int m_TargetFrameRate = 60;

        private UIDocument m_Document = null;
        private PanelSettings m_PanelSettings = null;
        private VisualElement m_Root = null;
        private Button m_Icon = null;
        private VisualElement m_Panel = null;
        private VisualElement m_Tabs = null;
        private VisualElement m_Content = null;
        private readonly List<IDebuggerPage> m_Windows = new List<IDebuggerPage>();
        private readonly List<Button> m_TabButtons = new List<Button>();
        private ConsoleWindow m_Console = null;
        private DebuggerFpsCounter m_Fps = null;
        private IDebuggerPage m_Current = null;
        private bool m_ShowFull = false;
        private float m_RefreshTimer = 0f;
        private bool m_WindowActive = false;
        private bool m_WindowsReady = false;
        private bool m_Rebuilding = false;
        private bool m_Ready = false;
        private bool m_SyncedEditorScreen = false;

        /// <summary>
        /// 获取或设置调试器窗口是否激活。
        /// </summary>
        public bool ActiveWindow
        {
            get
            {
                return m_WindowActive;
            }
            set
            {
                m_WindowActive = value;
                if (enabled != value)
                {
                    enabled = value;
                }

                if (m_Root != null)
                {
                    m_Root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
        }

        /// <summary>
        /// 获取或设置界面缩放。
        /// </summary>
        public float UiScale
        {
            get
            {
                return m_UiScale;
            }
            set
            {
                m_UiScale = Mathf.Clamp(value, 0.75f, 2.5f);
                PlayerPrefs.SetFloat(PrefScale, m_UiScale);
                ApplyScale();
            }
        }

        /// <summary>
        /// 游戏框架组件初始化。
        /// </summary>
        protected override void Awake()
        {
            base.Awake();

            m_Ready = true;
            m_UiScale = PlayerPrefs.GetFloat(PrefScale, m_UiScale);
            m_ShowFull = PlayerPrefs.GetInt(PrefOpen, 0) == 1;
            m_Fps = new DebuggerFpsCounter(0.5f);
            ApplyActiveWindow();
            if (!isActiveAndEnabled)
            {
                return;
            }

            EnsureSession();
        }

        private void Start()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            QualitySettings.vSyncCount = 0;
            if (m_TargetFrameRate > 0)
            {
                Application.targetFrameRate = m_TargetFrameRate;
            }
        }

        private void OnEnable()
        {
            if (!m_Ready || !Application.isPlaying)
            {
                return;
            }

            m_WindowActive = true;
            EnsureSession();
            if (m_Root != null)
            {
                m_Root.style.display = DisplayStyle.Flex;
            }
        }

        private void OnDisable()
        {
            if (m_Root != null)
            {
                m_Root.style.display = DisplayStyle.None;
            }
        }

        private void OnDestroy()
        {
            TeardownUi();
        }

        private void Update()
        {
            if (!m_WindowActive)
            {
                return;
            }

            if (m_Fps == null)
            {
                m_Fps = new DebuggerFpsCounter(0.5f);
            }

            m_Fps.Update(Time.unscaledDeltaTime);
            TickRuntimePanel();
            SyncEditorScreenToCamera();
            FitToCamera();
            if (m_Current != null)
            {
                m_Current.OnUpdate(Time.deltaTime, Time.unscaledDeltaTime);
            }

            if (m_Icon != null && !m_ShowFull)
            {
                Color32 color = m_Console != null ? m_Console.BadgeColor() : new Color32(180, 255, 180, 255);
                m_Icon.style.color = new StyleColor((Color)color);
                m_Icon.text = string.Format("{0:0.0} FPS\n{1:0.0} ms", m_Fps.CurrentFps, m_Fps.CurrentMs);
            }

            m_RefreshTimer += Time.unscaledDeltaTime;
            if (m_ShowFull && m_RefreshTimer >= 0.5f)
            {
                m_RefreshTimer = 0f;
                if (m_Current != null)
                {
                    m_Current.Refresh();
                }
            }
        }

        /// <summary>
        /// 还原调试器界面缩放，并收起完整窗口。
        /// </summary>
        public void ResetLayout()
        {
            UiScale = 1.25f;
            m_ShowFull = false;
            PlayerPrefs.SetInt(PrefOpen, 0);
            ShowIconOrPanel();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void EditorPlayHook()
        {
            UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayMode;
            UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayMode;
        }

        private static void OnEditorPlayMode(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
            {
                DebuggerComponent[] all = Resources.FindObjectsOfTypeAll<DebuggerComponent>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null)
                    {
                        all[i].TeardownUi();
                    }
                }

                return;
            }

            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode)
            {
                return;
            }

            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (!Application.isPlaying)
                {
                    return;
                }

                DebuggerComponent debugger = Object.FindAnyObjectByType<DebuggerComponent>(FindObjectsInactive.Exclude);
                if (debugger != null)
                {
                    debugger.EnsureSession();
                }
            };
        }
#endif

        private void ApplyActiveWindow()
        {
            bool open;
            switch (m_ActiveWindow)
            {
                case DebuggerActiveWindowType.AlwaysOpen:
                    open = true;
                    break;

                case DebuggerActiveWindowType.OnlyOpenWhenDevelopment:
                    open = Debug.isDebugBuild;
                    break;

                case DebuggerActiveWindowType.OnlyOpenInEditor:
                    open = Application.isEditor;
                    break;

                default:
                    open = false;
                    break;
            }

            ActiveWindow = open;
        }

        private void EnsureSession()
        {
            if (!m_Ready || !isActiveAndEnabled || !Application.isPlaying)
            {
                return;
            }

            bool treeAlive = m_Document != null
                && m_Root != null
                && m_Root.parent == m_Document.rootVisualElement;
            if (!treeAlive)
            {
                RebuildChrome();
                return;
            }

            ScheduleFit();
        }

        private void RebuildChrome()
        {
            if (m_Rebuilding)
            {
                return;
            }

            m_Rebuilding = true;
            EnsureUi();
            if (!m_WindowsReady)
            {
                CreateWindows();
                m_WindowsReady = true;
            }

            BindTabs();
            if (m_WindowActive)
            {
                ShowIconOrPanel();
                ScheduleFit();
            }

            m_Rebuilding = false;
        }

        private void TeardownUi()
        {
            for (int i = 0; i < m_Windows.Count; i++)
            {
                m_Windows[i].Shutdown();
            }

            m_Windows.Clear();
            m_TabButtons.Clear();
            m_WindowsReady = false;
            m_Current = null;
            m_Console = null;
            m_Icon = null;
            m_Panel = null;
            m_Tabs = null;
            m_Content = null;
            m_Root = null;
            m_Document = null;
            if (m_PanelSettings != null)
            {
                Destroy(m_PanelSettings);
                m_PanelSettings = null;
            }
        }

        private void EnsureUi()
        {
            m_Document = gameObject.GetComponent<UIDocument>();
            if (m_Document == null)
            {
                m_Document = gameObject.AddComponent<UIDocument>();
            }

            if (m_PanelSettings == null)
            {
                m_PanelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                m_PanelSettings.hideFlags = HideFlags.DontSave;
                m_PanelSettings.name = "DebuggerPanelSettings";
                m_PanelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
                m_PanelSettings.sortingOrder = 32000;
            }

            m_PanelSettings.scale = m_UiScale;
#if UNITY_EDITOR
            if (m_PanelSettings.themeStyleSheet == null)
            {
                m_PanelSettings.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                    "Packages/com.unity.render-pipelines.core/Runtime/Debugging/Runtime UI Resources/RuntimeDebugWindow.tss");
            }
#endif
            if (m_Document.panelSettings != m_PanelSettings)
            {
                m_Document.panelSettings = m_PanelSettings;
            }

            m_Root = new VisualElement();
            m_Root.AddToClassList("perf-root");
            m_Root.style.display = DisplayStyle.None;
            m_Root.pickingMode = PickingMode.Ignore;
            m_Root.style.position = Position.Absolute;
            m_Root.style.left = 0;
            m_Root.style.top = 0;
            m_Root.style.right = 0;
            m_Root.style.bottom = 0;
            StyleSheet sheet = LoadStyle();
            if (sheet != null)
            {
                m_Root.styleSheets.Add(sheet);
            }

            m_Document.rootVisualElement.Clear();
            m_Document.rootVisualElement.Add(m_Root);
            m_Document.rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnRootGeometry);
            m_Document.rootVisualElement.RegisterCallback<GeometryChangedEvent>(OnRootGeometry);

            m_Icon = new Button(OpenFull) { text = "FPS" };
            m_Icon.AddToClassList("perf-icon");
            m_Icon.pickingMode = PickingMode.Position;
            m_Root.Add(m_Icon);

            m_Panel = new VisualElement();
            m_Panel.AddToClassList("perf-panel");
            m_Panel.pickingMode = PickingMode.Position;
            m_Panel.style.position = Position.Absolute;
            m_Panel.style.left = PanelMargin;
            m_Panel.style.top = PanelMargin;
            m_Panel.style.width = 420;
            m_Panel.style.height = 280;
            m_Panel.style.display = DisplayStyle.None;
            m_Panel.style.flexDirection = FlexDirection.Column;

            VisualElement titlebar = new VisualElement();
            titlebar.AddToClassList("perf-titlebar");
            Label title = new Label("DEBUGGER");
            title.AddToClassList("perf-title");
            Button close = new Button(CloseFull) { text = "Close" };
            close.AddToClassList("perf-close");
            titlebar.Add(title);
            titlebar.Add(close);
            m_Panel.Add(titlebar);

            m_Tabs = new VisualElement();
            m_Tabs.AddToClassList("perf-tabs");
            m_Panel.Add(m_Tabs);

            m_Content = new VisualElement();
            m_Content.AddToClassList("perf-content");
            m_Content.style.flexGrow = 1;
            m_Content.style.flexShrink = 1;
            m_Content.style.flexBasis = 0;
            m_Content.style.minHeight = 0;
            m_Content.style.flexDirection = FlexDirection.Column;
            m_Panel.Add(m_Content);
            m_Root.Add(m_Panel);
            ApplyScale();
        }

        private void CreateWindows()
        {
            m_Windows.Clear();
            m_Console = new ConsoleWindow();
            m_Windows.Add(m_Console);
            m_Windows.Add(new ProfilerWindow());
            m_Windows.Add(new MemorySummaryWindow());
            m_Windows.Add(new SystemWindow());
            m_Windows.Add(new ScreenWindow());
            m_Windows.Add(new GraphicsWindow());
            m_Windows.Add(new QualityWindow());
            m_Windows.Add(new TimeWindow());
            m_Windows.Add(new SettingsWindow(this));
            FrameworkWindows.AddTo(m_Windows);
            for (int i = 0; i < m_Windows.Count; i++)
            {
                m_Windows[i].Initialize();
            }
        }

        private void BindTabs()
        {
            if (m_Tabs == null)
            {
                return;
            }

            m_Tabs.Clear();
            m_TabButtons.Clear();
            for (int i = 0; i < m_Windows.Count; i++)
            {
                int index = i;
                IDebuggerPage window = m_Windows[i];

                Button tab = new Button(() => SelectWindow(index)) { text = window.Title };
                tab.AddToClassList("perf-tab");
                m_Tabs.Add(tab);
                m_TabButtons.Add(tab);
            }

            int current = 0;
            if (m_Current != null)
            {
                int found = m_Windows.IndexOf(m_Current);
                if (found >= 0)
                {
                    current = found;
                }
            }

            if (m_Windows.Count > 0)
            {
                SelectWindow(current);
            }
        }

        private void SelectWindow(int index)
        {
            if (index < 0 || index >= m_Windows.Count || m_Content == null)
            {
                return;
            }

            if (m_Current != null)
            {
                m_Current.OnLeave();
            }

            m_Current = m_Windows[index];
            m_Current.OnEnter();
            m_Content.Clear();
            VisualElement host = new VisualElement();
            host.style.flexGrow = 1;
            host.style.flexShrink = 1;
            host.style.flexBasis = 0;
            host.style.minHeight = 0;
            host.style.flexDirection = FlexDirection.Column;
            m_Content.Add(host);
            m_Current.Build(host);

            for (int i = 0; i < m_TabButtons.Count; i++)
            {
                if (i == index)
                {
                    m_TabButtons[i].AddToClassList("perf-tab-active");
                }
                else
                {
                    m_TabButtons[i].RemoveFromClassList("perf-tab-active");
                }
            }
        }

        private void OpenFull()
        {
            m_ShowFull = true;
            PlayerPrefs.SetInt(PrefOpen, 1);
            ShowIconOrPanel();
            int index = m_Current == null ? -1 : m_Windows.IndexOf(m_Current);
            if (index >= 0)
            {
                SelectWindow(index);
            }
        }

        private void CloseFull()
        {
            m_ShowFull = false;
            PlayerPrefs.SetInt(PrefOpen, 0);
            ShowIconOrPanel();
        }

        private void ShowIconOrPanel()
        {
            if (m_Icon == null || m_Panel == null)
            {
                return;
            }

            m_Icon.style.display = m_ShowFull ? DisplayStyle.None : DisplayStyle.Flex;
            m_Panel.style.display = m_ShowFull ? DisplayStyle.Flex : DisplayStyle.None;
            if (m_Root != null && m_WindowActive)
            {
                m_Root.style.display = DisplayStyle.Flex;
            }
        }

        private void ApplyScale()
        {
            if (m_PanelSettings != null)
            {
                m_PanelSettings.scale = m_UiScale;
            }

            ScheduleFit();
        }

        private void ScheduleFit()
        {
            if (m_Document == null)
            {
                return;
            }

            VisualElement tree = m_Document.rootVisualElement;
            if (tree == null)
            {
                return;
            }

            FitPanel(tree.layout.width, tree.layout.height);
            tree.schedule.Execute(() =>
            {
                if (m_Document == null)
                {
                    return;
                }

                Rect layout = m_Document.rootVisualElement.layout;
                FitPanel(layout.width, layout.height);
            }).StartingIn(0);
        }

        private void OnRootGeometry(GeometryChangedEvent evt)
        {
            if (m_Rebuilding)
            {
                return;
            }

            FitToCamera();
        }

        private void TickRuntimePanel()
        {
#if UNITY_EDITOR
            if (m_Document == null)
            {
                return;
            }

            IPanel panel = m_Document.rootVisualElement != null ? m_Document.rootVisualElement.panel : null;
            if (panel == null)
            {
                return;
            }

            if (s_PanelUpdate == null)
            {
                s_PanelUpdate = panel.GetType().GetMethod(
                    "Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (s_PanelUpdate != null)
            {
                s_PanelUpdate.Invoke(panel, null);
            }

            Camera cam = Camera.main;
            VisualElement tree = m_Document.rootVisualElement;
            if (cam != null && tree != null && cam.pixelHeight > 16)
            {
                float scale = Mathf.Max(0.01f, m_UiScale);
                tree.style.width = cam.pixelWidth / scale;
                tree.style.height = cam.pixelHeight / scale;
            }
#endif
        }

        private void SyncEditorScreenToCamera()
        {
#if UNITY_EDITOR
            if (m_SyncedEditorScreen)
            {
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            int width = cam.pixelWidth;
            int height = cam.pixelHeight;
            if (width < 16 || height < 16)
            {
                return;
            }

            // Editor GUI can publish the toolbar size (about 72px tall) as Display.
            // UI Toolkit then lays the overlay out in that strip, so the console vanishes.
            if (Screen.height >= 128 || Screen.height >= height / 2)
            {
                m_SyncedEditorScreen = true;
                return;
            }

            Screen.SetResolution(width, height, FullScreenMode.Windowed);
            m_SyncedEditorScreen = true;
#endif
        }

        private void FitToCamera()
        {
            float width;
            float height;
            Camera cam = Camera.main;
            float scale = Mathf.Max(0.01f, m_UiScale);
            if (cam != null && cam.pixelWidth > 16 && cam.pixelHeight > 16)
            {
                width = cam.pixelWidth / scale;
                height = cam.pixelHeight / scale;
            }
            else if (m_Document != null && m_Document.rootVisualElement != null)
            {
                Rect layout = m_Document.rootVisualElement.layout;
                width = layout.width;
                height = layout.height;
            }
            else
            {
                return;
            }

            FitPanel(width, height);
        }

        private void FitPanel(float panelWidth, float panelHeight)
        {
            if (m_Panel == null || panelWidth < 32f || panelHeight < 32f)
            {
                return;
            }

            float width = panelWidth - PanelMargin * 2f;
            float height = panelHeight - PanelMargin * 2f;
            if (width > PanelMaxWidth)
            {
                width = PanelMaxWidth;
            }

            if (width < 160f)
            {
                width = panelWidth;
            }

            if (height < 80f)
            {
                height = panelHeight;
            }

            m_Panel.style.left = PanelMargin;
            m_Panel.style.top = PanelMargin;
            m_Panel.style.width = width;
            m_Panel.style.height = height;
        }

        private static StyleSheet LoadStyle()
        {
            if (s_CachedStyle != null)
            {
                return s_CachedStyle;
            }

            s_CachedStyle = Resources.Load<StyleSheet>("PerfDebugger");
            return s_CachedStyle;
        }
    }
}
