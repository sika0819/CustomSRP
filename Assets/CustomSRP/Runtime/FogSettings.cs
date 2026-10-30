using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace CustomSRP
{
    [Serializable]
    public class FogSettings
    {
        public enum Source
        {
            Off,
            Environment,
            Custom
        }

        [Tooltip("Off: no fog. Environment: Lighting window Fog. Custom: the fields below.")]
        public Source source = Source.Environment;

        [Tooltip("Fog color follows OilSkyboxTime TOD fog table (night = #0A1828, not main light).")]
        [FormerlySerializedAs("matchSkyHorizon")]
        public bool matchMainLight = true;

        [Tooltip("Used when Source is Custom.")]
        public FogMode mode = FogMode.ExponentialSquared;

        [Tooltip("Used when Source is Custom and the sky is not driving the color.")]
        [ColorUsage(false, true)]
        public Color color = new Color(0.67f, 0.76f, 0.82f, 1f);

        [Tooltip("Exponential and Exponential Squared density. Used when Source is Custom.")]
        [Min(0f)]
        public float density = 0.003f;

        [Tooltip("Linear fog start. Used when Source is Custom.")]
        public float startDistance;

        [Tooltip("Linear fog end. Used when Source is Custom.")]
        public float endDistance = 300f;

        [Tooltip("Sky/cloud fog top feather. Density sets how high fog climbs; higher skyPower sharpens the top edge.")]
        [Range(0.5f, 8f)]
        public float skyPower = 4.5f;

        public struct ShaderData
        {
            public float enabled;
            public float mode;
            public Vector4 color;
            public Vector4 parameters;
            public float skyPower;
        }

        public static ShaderData Evaluate(FogSettings settings, bool ignoreFog)
        {
            Source source = settings == null ? Source.Environment : settings.source;
            if (ignoreFog || source == Source.Off)
            {
                return default;
            }

            bool environment = source == Source.Environment;
            if (environment && !RenderSettings.fog)
            {
                return default;
            }

            FogMode mode = environment ? RenderSettings.fogMode : settings.mode;
            float density = environment ? RenderSettings.fogDensity : settings.density;
            float start = environment ? RenderSettings.fogStartDistance : settings.startDistance;
            float end = environment ? RenderSettings.fogEndDistance : settings.endDistance;
            bool matchTodFog = settings == null || settings.matchMainLight;
            float skyPower = settings == null || settings.skyPower < 0.5f ? 4.5f : settings.skyPower;

            Vector4 color;
            if (matchTodFog && TryTodFogColor(out Color fogColor))
            {
                // OilSkyboxTime authors sRGB anchors (#0A1828 night); upload linear for the fog pass.
                Color linear = fogColor.linear;
                color = new Vector4(linear.r, linear.g, linear.b, 1f);
            }
            else
            {
                Color picked = (environment ? RenderSettings.fogColor : settings.color).linear;
                color = new Vector4(picked.r, picked.g, picked.b, 1f);
            }

            return new ShaderData
            {
                enabled = 1f,
                mode = ModeToShader(mode),
                color = color,
                parameters = Pack(mode, density, start, end),
                skyPower = skyPower
            };
        }

        static bool TryTodFogColor(out Color fogColor)
        {
            fogColor = default;

            // Cheap path: skybox _Period is synced by OilSkyboxTime (no per-frame scene search).
            Material sky = RenderSettings.skybox;
            if (sky != null && sky.HasProperty("_Period"))
            {
                OilSkyboxTime.EvaluateFog(sky.GetFloat("_Period"), out fogColor);
                return true;
            }

            // ApplyFogColor keeps Environment fog on the same TOD table (night = #0A1828).
            if (RenderSettings.fog)
            {
                fogColor = RenderSettings.fogColor;
                return true;
            }

            return false;
        }

        static float ModeToShader(FogMode mode)
        {
            switch (mode)
            {
                case FogMode.Linear:
                    return 0f;
                case FogMode.Exponential:
                    return 1f;
                default:
                    return 2f;
            }
        }

        static Vector4 Pack(FogMode mode, float density, float start, float end)
        {
            // exp2(-density^2 * z^2) and exp(-density * z), matching Unity fog.
            const float invLn2 = 1.44269504089f;
            const float invSqrtLn2 = 1.20112240879f;
            var parameters = new Vector4();
            switch (mode)
            {
                case FogMode.Linear:
                    float span = Mathf.Max(0.01f, end - start);
                    parameters.z = -1f / span;
                    parameters.w = end / span;
                    break;
                case FogMode.Exponential:
                    parameters.y = Mathf.Max(0f, density) * invLn2;
                    break;
                default:
                    parameters.x = Mathf.Max(0f, density) * invSqrtLn2;
                    break;
            }

            return parameters;
        }
    }
}
