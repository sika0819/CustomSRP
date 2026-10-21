#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Editor
{
    /// <summary>
    /// 在 Window/AI/Navigation 窗口顶部挂 Island NavMesh 快捷烘焙按钮。
    /// NavigationWindow 为包内 internal，通过 rootVisualElement 注入。
    /// </summary>
    [InitializeOnLoad]
    static class IslandNavBakeNavigationWindowUI
    {
        const string HostName = "IslandNavBakeNavigationHost";
        const string NavWindowTypeName =
            "Unity.AI.Navigation.Editor.NavigationWindow, Unity.AI.Navigation.Editor";

        static readonly Type NavWindowType = Type.GetType(NavWindowTypeName);
        static double s_NextPoll;
        static Label s_Status;

        static IslandNavBakeNavigationWindowUI()
        {
            EditorApplication.update += Poll;
            EditorApplication.delayCall += HookAll;
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_NextPoll)
                return;
            s_NextPoll = EditorApplication.timeSinceStartup + 0.5;
            HookAll();
        }

        static void HookAll()
        {
            if (NavWindowType == null)
                return;

            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(NavWindowType);
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] is EditorWindow window)
                    EnsureHost(window);
            }
        }

        static void EnsureHost(EditorWindow window)
        {
            VisualElement root = window.rootVisualElement;
            if (root == null)
                return;
            if (root.Q(HostName) != null)
                return;

            var host = new VisualElement { name = HostName };
            host.style.paddingLeft = 6;
            host.style.paddingRight = 6;
            host.style.paddingTop = 6;
            host.style.paddingBottom = 4;
            host.style.borderBottomWidth = 1;
            host.style.borderBottomColor = new Color(0f, 0f, 0f, 0.35f);
            host.style.flexShrink = 0;

            var title = new Label("Island NavMesh");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 4;
            host.Add(title);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;

            var bakeBtn = new Button(BakeOnly) { text = "Bake" };
            bakeBtn.tooltip = "烘焙 Island NavMesh（不改 Surface / 水面配置）";
            bakeBtn.style.flexGrow = 1;
            bakeBtn.style.minWidth = 72;
            bakeBtn.style.height = 22;
            row.Add(bakeBtn);

            var rebuildBtn = new Button(RebuildAndBake) { text = "Rebuild + Bake" };
            rebuildBtn.tooltip = "重建 IslandNavBake 场景后烘焙（会重置水面挖洞标记）";
            rebuildBtn.style.flexGrow = 1;
            rebuildBtn.style.minWidth = 110;
            rebuildBtn.style.height = 22;
            row.Add(rebuildBtn);

            host.Add(row);

            s_Status = new Label("就绪");
            s_Status.style.marginTop = 4;
            s_Status.style.whiteSpace = WhiteSpace.Normal;
            s_Status.style.fontSize = 11;
            s_Status.style.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            host.Add(s_Status);

            root.Insert(0, host);
        }

        static void BakeOnly()
        {
            bool ok = IslandNavBakeSceneBuilder.BakeNavMesh(out string message);
            SetStatus(ok, message);
        }

        static void RebuildAndBake()
        {
            int n = IslandNavBakeSceneBuilder.BuildScene();
            bool ok = IslandNavBakeSceneBuilder.BakeNavMesh(out string message);
            SetStatus(ok, $"walkItems={n}\n{message}");
        }

        static void SetStatus(bool ok, string message)
        {
            if (s_Status == null)
                return;

            string oneLine = message != null
                ? message.Replace('\n', ' ').Trim()
                : "";
            if (oneLine.Length > 120)
                oneLine = oneLine.Substring(0, 117) + "…";

            s_Status.text = ok ? $"✓ {oneLine}" : $"✗ {oneLine}";
            s_Status.style.color = ok
                ? new Color(0.45f, 0.85f, 0.5f, 1f)
                : new Color(0.95f, 0.45f, 0.4f, 1f);

            if (!ok)
                EditorUtility.DisplayDialog("Island NavMesh", message ?? "Bake failed", "OK");
        }
    }
}
#endif
