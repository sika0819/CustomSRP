#if UNITY_2019_4_OR_NEWER
using System;
using UnityEditor;
using UnityEngine;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace YooAsset.Editor
{
    /// <summary>
    /// 折叠开关
    /// </summary>
    #if UNITY_6000_6_OR_NEWER
    [UxmlElement]
    public partial class ToggleFoldout : Toggle
    #else
    public class ToggleFoldout : Toggle
    #endif
    {
        #if !UNITY_6000_6_OR_NEWER

        public new class UxmlFactory : UxmlFactory<ToggleFoldout, UxmlTraits> { }

        #endif

        private readonly VisualElement _checkbox;

        public ToggleFoldout()
        {
            _checkbox = this.Q<VisualElement>("unity-checkmark");
            RefreshIcon();
        }
        public override void SetValueWithoutNotify(bool newValue)
        {
            base.SetValueWithoutNotify(newValue);
            RefreshIcon();
        }

#if UNITY_2021_3_OR_NEWER
        protected override void ToggleValue()
        {
            base.ToggleValue();
            RefreshIcon();
        }
#endif

        public void RefreshIcon()
        {
            if (this.value)
            {
                var icon = EditorGUIUtility.IconContent(UIElementsIcon.FoldoutOn).image as Texture2D;
                _checkbox.style.backgroundImage = icon;
            }
            else
            {
                var icon = EditorGUIUtility.IconContent(UIElementsIcon.FoldoutOff).image as Texture2D;
                _checkbox.style.backgroundImage = icon;
            }
        }
    }
}
#endif