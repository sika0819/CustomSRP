//------------------------------------------------------------
// Game Framework
// Copyright © 2013-2021 Jiang Yin. All rights reserved.
// Homepage: https://gameframework.cn/
// Feedback: mailto:ellan@gameframework.cn
//------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace UnityGameFramework.Editor
{
    [CustomEditor(typeof(DebuggerComponent))]
    internal sealed class DebuggerComponentInspector : GameFrameworkInspector
    {
        private SerializedProperty m_ActiveWindow = null;
        private SerializedProperty m_UiScale = null;
        private SerializedProperty m_TargetFrameRate = null;

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            serializedObject.Update();

            DebuggerComponent t = (DebuggerComponent)target;

            if (EditorApplication.isPlaying && IsPrefabInHierarchy(t.gameObject))
            {
                bool activeWindow = EditorGUILayout.Toggle("Active Window", t.ActiveWindow);
                if (activeWindow != t.ActiveWindow)
                {
                    t.ActiveWindow = activeWindow;
                }

                if (GUILayout.Button("Reset Layout"))
                {
                    t.ResetLayout();
                }
            }
            else
            {
                EditorGUILayout.PropertyField(m_ActiveWindow);
            }

            float scaleBefore = t.UiScale;
            EditorGUILayout.PropertyField(m_UiScale);
            EditorGUILayout.PropertyField(m_TargetFrameRate);

            serializedObject.ApplyModifiedProperties();

            if (EditorApplication.isPlaying && IsPrefabInHierarchy(t.gameObject) && !Mathf.Approximately(scaleBefore, t.UiScale))
            {
                t.UiScale = t.UiScale;
            }
        }

        private void OnEnable()
        {
            m_ActiveWindow = serializedObject.FindProperty("m_ActiveWindow");
            m_UiScale = serializedObject.FindProperty("m_UiScale");
            m_TargetFrameRate = serializedObject.FindProperty("m_TargetFrameRate");
        }
    }
}
