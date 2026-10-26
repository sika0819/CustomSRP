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

            /// <summary>Gmj 4K 防御：资产若误开 bloom，最多 1 阶。</summary>
            public void ClampForGmj()
            {
                ClampForMobile();
                if (maxIterations > 1)
                    maxIterations = 1;
            }
        }

        [SerializeField]
        BloomSettings bloom = new BloomSettings
        {
            enabled = true,
            downscaleLimit = 2,
            maxIterations = 4,
            threshold = 1f,
            thresholdKnee = 0.5f,
            intensity = 0.3f,
            fadeFireflies = true,
            mode = BloomSettings.Mode.Scattering,
            scatter = 0.85f
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
            enabled = true,
            postExposure = 0.05f,
            contrast = 10f,
            colorFilter = Color.white,
            saturation = 8f
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
            enabled = true,
            temperature = 4f,
            tint = 2f
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
        // Off by default: strong split eats OilSkyLookTables day/night. Stored colors are mild if re-enabled.
        SplitToningSettings splitToning = new SplitToningSettings
        {
            enabled = false,
            shadows = new Color(0.45f, 0.47f, 0.52f, 1f),
            highlights = new Color(0.55f, 0.52f, 0.46f, 1f),
            balance = 0f
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
        // Off by default: navy/ochre multiply overrides sky look period. Near-white if re-enabled.
        ShadowsMidtonesHighlightsSettings shadowsMidtonesHighlights =
            new ShadowsMidtonesHighlightsSettings
            {
                enabled = false,
                shadows = new Color(0.96f, 0.97f, 1f, 1f),
                midtones = new Color(1f, 0.99f, 0.97f, 1f),
                highlights = new Color(1.02f, 1f, 0.96f, 1f),
                shadowsStart = 0.05f,
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
        public struct FXAASettings
        {
            [Tooltip("Apply FXAA in the final post pass. Also requires camera Allow FXAA.")]
            public bool enabled;

            [Range(0.0312f, 0.0833f)]
            public float fixedThreshold;

            [Range(0.063f, 0.333f)]
            public float relativeThreshold;

            [Range(0f, 1f)]
            public float subpixelBlending;

            public enum Quality
            {
                Low,
                Medium,
                High
            }

            public Quality quality;
        }

        [SerializeField]
        FXAASettings fxaa = new FXAASettings
        {
            enabled = false,
            fixedThreshold = 0.0833f,
            relativeThreshold = 0.166f,
            subpixelBlending = 0.75f,
            quality = FXAASettings.Quality.High
        };

        public FXAASettings FXAA => fxaa;

        [Serializable]
        public struct OilFilterSettings
        {
            [Tooltip("Edge wet-oil: fwidth edge + 4 diagonal taps (low cost).")]
            public bool enabled;

            [Tooltip("Multiplies wet-oil amount after edge/interior gates.")]
            [Range(0f, 1.5f)]
            public float strength;

            [Tooltip("Lite/HQ floor so flat areas keep a light stroke (0–0.25).")]
            [Range(0f, 0.25f)]
            public float interiorFloor;

            [Tooltip("Multiplies auto buffer-based radius scale.")]
            [Range(0.5f, 1.5f)]
            public float radiusScale;
        }

        [SerializeField]
        OilFilterSettings oilFilter = new OilFilterSettings
        {
            enabled = true,
            strength = 1.15f,
            interiorFloor = 0.12f,
            radiusScale = 1.08f
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

            [Range(0f, 0.12f)]
            public float grain;

            [Range(0f, 0.12f)]
            public float weave;
        }

        [SerializeField]
        CanvasSettings canvas = new CanvasSettings
        {
            enabled = true,
            // Keep a light tooth — heavy weave reads like a screen mesh.
            grain = 0.028f,
            weave = 0.022f
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
