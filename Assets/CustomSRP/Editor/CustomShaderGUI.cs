using System;
using System.Collections.Generic;
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

        static readonly HashSet<string> BillboardHiddenProps = new HashSet<string>
        {
            "_InternalEdge",
            "_EdgeStrength",
            "_EdgeColor"
        };

        MaterialEditor editor;
        UnityEngine.Object[] materials;
        MaterialProperty[] properties;
        bool showPresets;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            editor = materialEditor;
            materials = materialEditor.targets;
            this.properties = properties;

            EditorGUI.BeginChangeCheck();
            // Billboard mats don't use internal crease ink; hide those OilNPR props in Inspector.
            // (Removing them from .mat YAML is not enough — Shader Properties still draw.)
            if (AllTargetsAreBillboard())
            {
                EnsureBillboardEdgeOff();
                materialEditor.PropertiesDefaultGUI(FilterProperties(properties, BillboardHiddenProps));
            }
            else
            {
                base.OnGUI(materialEditor, properties);
            }

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
            }
        }

        static MaterialProperty[] FilterProperties(
            MaterialProperty[] source, HashSet<string> hide)
        {
            var list = new List<MaterialProperty>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                if (hide.Contains(source[i].name))
                    continue;
                list.Add(source[i]);
            }

            return list.ToArray();
        }

        bool AllTargetsAreBillboard()
        {
            if (materials == null || materials.Length == 0)
                return false;
            for (int i = 0; i < materials.Length; i++)
            {
                var m = materials[i] as Material;
                if (m == null
                    || m.name.IndexOf("Billboard", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        void EnsureBillboardEdgeOff()
        {
            foreach (Material m in materials)
            {
                if (m == null)
                    continue;
                m.DisableKeyword("_INTERNAL_EDGE_ON");
                if (m.HasProperty("_InternalEdge"))
                    m.SetFloat("_InternalEdge", 0f);
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
