using System;
using UnityEngine;

namespace CustomSRP
{
    [CreateAssetMenu(menuName = "Rendering/Custom Post FX Settings")]
    public class PostFXSettings : ScriptableObject
    {
        const string ColorLUTComputePath = "Assets/CustomSRP/Shaders/ColorLUT.compute";

        [SerializeField] Shader shader = default;

        [SerializeField] ComputeShader colorLUTComputeShader = default;

        public ComputeShader ColorLUTComputeShader => colorLUTComputeShader;

#if UNITY_EDITOR
        void OnValidate()
        {
            if (colorLUTComputeShader == null)
            {
                colorLUTComputeShader =
                    UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        ColorLUTComputePath);
            }

            if (shader == null)
            {
                shader = Shader.Find("Hidden/CustomSRP/Post FX Stack");
            }
        }
#endif

        public static bool AreApplicableTo(Camera camera)
        {
#if UNITY_EDITOR
            if (camera.cameraType == CameraType.SceneView &&
                !UnityEditor.SceneView.currentDrawingSceneView.sceneViewState.showImageEffects)
            {
                return false;
            }
#endif
            return camera.cameraType <= CameraType.SceneView;
        }

        [Serializable]
        public struct BloomSettings
        {
            public bool enabled;

            public bool ignoreRenderScale;

            [Range(0f, 16f)] public int maxIterations;
            [Min(1)] public int downscaleLimit;
            public bool bicubicUpsampling;
            [Min(0f)] public float threshold;
            [Range(0f, 1f)] public float thresholdKnee;
            [Min(0f)] public float intensity;
            public bool fadeFireflies;

            public enum Mode
            {
                Additive,
                Scattering
            }

            public Mode mode;

            [Range(0.05f, 0.95f)] public float scatter;

            /// <summary>
            /// Mobile-friendly clamp: limits iterations and enforces a downscale floor
            /// so the bloom mip chain stays shallow on tile-based GPUs.
            /// </summary>
            public void ClampForMobile()
            {
                // Mobile: cap at 2 mip levels (saves ~12 ROP passes vs 4-level chain).
                if (maxIterations > 2) maxIterations = 2;
                // Require a minimum downscale of 4x so the pyramid is never too deep.
                if (downscaleLimit < 4) downscaleLimit = 4;
            }
        }

        [SerializeField]
        BloomSettings bloom = new BloomSettings
        {
            enabled = false,
            downscaleLimit = 2,
            maxIterations = 4,
            threshold = 1f,
            thresholdKnee = 0.5f,
            intensity = 0.2f,
            fadeFireflies = true,
            mode = BloomSettings.Mode.Scattering,
            scatter = 0.7f
        };

        public BloomSettings Bloom => bloom;

        [Serializable]
        public struct ColorAdjustmentsSettings
        {
            public bool enabled;

            public float postExposure;

            [Range(-100f, 100f)]
            public float contrast;

            [ColorUsage(false, true)]
            public Color colorFilter;

            [Range(-180f, 180f)]
            public float hueShift;

            [Range(-100f, 100f)]
            public float saturation;
        }

        [SerializeField]
        ColorAdjustmentsSettings colorAdjustments = new ColorAdjustmentsSettings
        {
            enabled = false,
            colorFilter = Color.white
        };

        public ColorAdjustmentsSettings ColorAdjustments => colorAdjustments;

        [Serializable]
        public struct WhiteBalanceSettings
        {
            public bool enabled;

            [Range(-100f, 100f)]
            public float temperature, tint;
        }

        [SerializeField]
        WhiteBalanceSettings whiteBalance = new WhiteBalanceSettings
        {
            enabled = false
        };

        public WhiteBalanceSettings WhiteBalance => whiteBalance;

        [Serializable]
        public struct SplitToningSettings
        {
            public bool enabled;

            [ColorUsage(false)]
            public Color shadows, highlights;

            [Range(-100f, 100f)]
            public float balance;
        }

        [SerializeField]
        SplitToningSettings splitToning = new SplitToningSettings
        {
            enabled = false,
            shadows = Color.gray,
            highlights = Color.gray
        };

        public SplitToningSettings SplitToning => splitToning;

        [Serializable]
        public struct ChannelMixerSettings
        {
            public bool enabled;

            public Vector3 red, green, blue;
        }

        [SerializeField]
        ChannelMixerSettings channelMixer = new ChannelMixerSettings
        {
            enabled = false,
            red = Vector3.right,
            green = Vector3.up,
            blue = Vector3.forward
        };

        public ChannelMixerSettings ChannelMixer => channelMixer;

        [Serializable]
        public struct ShadowsMidtonesHighlightsSettings
        {
            public bool enabled;

            [ColorUsage(false, true)]
            public Color shadows, midtones, highlights;

            [Range(0f, 2f)]
            public float shadowsStart, shadowsEnd, highlightsStart, highLightsEnd;
        }

        [SerializeField]
        ShadowsMidtonesHighlightsSettings shadowsMidtonesHighlights =
            new ShadowsMidtonesHighlightsSettings
            {
                enabled = false,
                shadows = Color.white,
                midtones = Color.white,
                highlights = Color.white,
                shadowsEnd = 0.3f,
                highlightsStart = 0.55f,
                highLightsEnd = 1f
            };

        public ShadowsMidtonesHighlightsSettings ShadowsMidtonesHighlights =>
            shadowsMidtonesHighlights;

        [Serializable]
        public struct ToneMappingSettings
        {
            public bool enabled;

            public enum Mode
            {
                None,
                ACES,
                Neutral,
                Reinhard
            }

            public Mode mode;
        }

        [SerializeField]
        ToneMappingSettings toneMapping = new ToneMappingSettings
        {
            enabled = false,
            mode = ToneMappingSettings.Mode.None
        };

        public ToneMappingSettings ToneMapping => toneMapping;

        [Serializable]
        public struct OilFilterSettings
        {
            [Tooltip("Edge wet-oil: fwidth edge + 4 diagonal taps (low cost).")]
            public bool enabled;

            [Tooltip("Blend multiplier on top of resolution-based strength. Keep >0 so the dab stays visible.")]
            [Range(0.05f, 1f)]
            public float strength;
        }

        [SerializeField]
        OilFilterSettings oilFilter = new OilFilterSettings
        {
            enabled = true,
            strength = 0.45f
        };

        public OilFilterSettings OilFilter => oilFilter;

        [Serializable]
        public struct OilWarpSettings
        {
            [Tooltip("Immersive full-screen oil UV warp (breath-driven). Off when strength is 0.")]
            public bool enabled;

            [Tooltip("Caps immersive breath×weight strength.")]
            [Range(0f, 1f)]
            public float maxStrength;

            [Tooltip("Noise / flow spatial scale.")]
            [Range(0.4f, 2.5f)]
            public float scale;

            [Tooltip("Max UV offset in texels at full strength.")]
            [Range(3f, 14f)]
            public float maxTexels;
        }

        [SerializeField]
        OilWarpSettings oilWarp = new OilWarpSettings
        {
            enabled = true,
            maxStrength = 0.55f,
            scale = 1.05f,
            maxTexels = 5f
        };

        public OilWarpSettings OilWarp => oilWarp;

        [Serializable]
        public struct CanvasSettings
        {
            [Tooltip("Procedural canvas weave after tonemapping. Not film grain.")]
            public bool enabled;

            [Range(0f, 0.08f)]
            public float grain;

            [Range(0f, 0.08f)]
            public float weave;
        }

        [SerializeField]
        CanvasSettings canvas = new CanvasSettings
        {
            enabled = false,
            grain = 0.035f,
            weave = 0.02f
        };

        public CanvasSettings Canvas => canvas;

        [NonSerialized] Material material;

        public Material Material
        {
            get
            {
                if (material == null && shader != null)
                {
                    material = new Material(shader)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }

                return material;
            }
        }
    }
}

