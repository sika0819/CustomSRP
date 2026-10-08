using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    public class CustomShaderGUI : ShaderGUI
    {
        enum ShadowMode
        {
            On,
            Clip,
            Dither,
            Off
        }

        MaterialEditor editor;
        Object[] materials;
        MaterialProperty[] properties;
        bool showPresets;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            EditorGUI.BeginChangeCheck();
            base.OnGUI(materialEditor, properties);
            editor = materialEditor;
            materials = materialEditor.targets;
            this.properties = properties;

            EditorGUILayout.Space();
            showPresets = EditorGUILayout.Foldout(showPresets, "Presets", true);
            if (showPresets)
            {
                OpaquePreset();
                ClipPreset();
                FadePreset();
                TransparentPreset();
            }

            if (EditorGUI.EndChangeCheck())
            {
                SetShadowCasterPass();
                SetOutlinePass();
            }
        }

        bool HasProperty(string name) =>
            FindProperty(name, properties, false) != null;

        bool HasPremultiplyAlpha => HasProperty("_PremulAlpha");

        bool SetProperty(string name, float value)
        {
            MaterialProperty property = FindProperty(name, properties, false);
            if (property != null)
            {
                property.floatValue = value;
                return true;
            }

            return false;
        }

        void SetProperty(string name, string keyword, bool value)
        {
            if (SetProperty(name, value ? 1f : 0f))
            {
                SetKeyword(keyword, value);
            }
        }

        void SetKeyword(string keyword, bool enabled)
        {
            if (enabled)
            {
                foreach (Material m in materials)
                {
                    m.EnableKeyword(keyword);
                }
            }
            else
            {
                foreach (Material m in materials)
                {
                    m.DisableKeyword(keyword);
                }
            }
        }

        bool Clipping
        {
            set => SetProperty("_Clipping", "_CLIPPING", value);
        }

        bool PremultiplyAlpha
        {
            set => SetProperty("_PremulAlpha", "_PREMULTIPLY_ALPHA", value);
        }

        ShadowMode Shadows
        {
            set
            {
                if (SetProperty("_Shadows", (float)value))
                {
                    SetKeyword("_SHADOWS_CLIP", value == ShadowMode.Clip);
                    SetKeyword("_SHADOWS_DITHER", value == ShadowMode.Dither);
                }
            }
        }

        BlendMode SrcBlend
        {
            set => SetProperty("_SrcBlend", (float)value);
        }

        BlendMode DstBlend
        {
            set => SetProperty("_DstBlend", (float)value);
        }

        bool ZWrite
        {
            set => SetProperty("_ZWrite", value ? 1f : 0f);
        }

        RenderQueue Queue
        {
            set
            {
                foreach (Material m in materials)
                {
                    m.renderQueue = (int)value;
                }
            }
        }

        void SetShadowCasterPass()
        {
            MaterialProperty shadows = FindProperty("_Shadows", properties, false);
            if (shadows == null || shadows.hasMixedValue)
            {
                return;
            }

            bool enabled = shadows.floatValue < (float)ShadowMode.Off;
            foreach (Material m in materials)
            {
                m.SetShaderPassEnabled("ShadowCaster", enabled);
            }
        }

        void SetOutlinePass()
        {
            MaterialProperty outline = FindProperty("_Outline", properties, false);
            if (outline == null || outline.hasMixedValue)
            {
                return;
            }

            bool enabled = outline.floatValue > 0.5f;
            foreach (Material m in materials)
            {
                m.SetShaderPassEnabled("Outline", enabled);
                if (enabled)
                {
                    m.EnableKeyword("_OUTLINE_ON");
                }
                else
                {
                    m.DisableKeyword("_OUTLINE_ON");
                }
            }
        }

        bool PresetButton(string name)
        {
            if (GUILayout.Button(name))
            {
                editor.RegisterPropertyChangeUndo(name);
                return true;
            }

            return false;
        }

        void OpaquePreset()
        {
            if (PresetButton("Opaque"))
            {
                Clipping = false;
                PremultiplyAlpha = false;
                Shadows = ShadowMode.On;
                SrcBlend = BlendMode.One;
                DstBlend = BlendMode.Zero;
                ZWrite = true;
                Queue = RenderQueue.Geometry;
            }
        }

        void ClipPreset()
        {
            if (PresetButton("Clip"))
            {
                Clipping = true;
                PremultiplyAlpha = false;
                Shadows = ShadowMode.Clip;
                SrcBlend = BlendMode.One;
                DstBlend = BlendMode.Zero;
                ZWrite = true;
                Queue = RenderQueue.AlphaTest;
            }
        }

        void FadePreset()
        {
            if (PresetButton("Fade"))
            {
                Clipping = false;
                PremultiplyAlpha = false;
                Shadows = ShadowMode.Dither;
                SrcBlend = BlendMode.SrcAlpha;
                DstBlend = BlendMode.OneMinusSrcAlpha;
                ZWrite = false;
                Queue = RenderQueue.Transparent;
            }
        }

        void TransparentPreset()
        {
            if (HasPremultiplyAlpha && PresetButton("Transparent"))
            {
                Clipping = false;
                PremultiplyAlpha = true;
                Shadows = ShadowMode.Dither;
                SrcBlend = BlendMode.One;
                DstBlend = BlendMode.OneMinusSrcAlpha;
                ZWrite = false;
                Queue = RenderQueue.Transparent;
            }
        }
    }
}
