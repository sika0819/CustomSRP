using UnityEngine;
using UnityEngine.Rendering;

using UnityGameFramework.Runtime;
namespace CustomSRP
{
    public class Shadows
    {
        public const int MaxTilesPerLight = 6;

        static readonly GlobalKeyword[] FilterQualityKeywords =
        {
            GlobalKeyword.Create("_SHADOW_FILTER_MEDIUM"),
            GlobalKeyword.Create("_SHADOW_FILTER_HIGH")
        };

        static readonly GlobalKeyword[] ShadowMaskKeywords =
        {
            GlobalKeyword.Create("_SHADOW_MASK_ALWAYS"),
            GlobalKeyword.Create("_SHADOW_MASK_DISTANCE")
        };

        static readonly int
            ShadowAtlasSizeId = Shader.PropertyToID("_ShadowAtlasSize"),
            ShadowDistanceFadeId = Shader.PropertyToID("_ShadowDistanceFade");

        public readonly DirectionalShadows directionalShadows = new();
        public readonly OtherShadows otherShadows = new();

        ShadowSettings _settings;

        static int _setupLogCounter;

        public void Setup(ShadowSettings settings)
        {
            _settings = settings;
            directionalShadows.Setup(settings);
            otherShadows.Setup(settings);

            // [SRP][Shadow.Setup] 每 60 帧打一次,打印当前配置 + 平台信息,便于真机排查"为啥没阴影"
            if ((_setupLogCounter++ & 63) == 0)
            {
                Log.Info(string.Format(
                    "[SRP][Shadow.Setup] maxDistance={0:F2} distFade={1:F3} dirCascades={2} dirAtlas={3} otherAtlas={4} softCascade={5} reversedZ={6} device={7} supportsShadows={8}",
                    _settings.maxDistance,
                    _settings.distanceFade,
                    _settings.directional.cascadeCount,
                    (int)_settings.directional.atlasSize,
                    (int)_settings.other.atlasSize,
                    _settings.directional.softCascadeBlend,
                    SystemInfo.usesReversedZBuffer,
                    SystemInfo.graphicsDeviceType,
                    SystemInfo.supportsShadows));
            }
        }

        public void Render(UnsafeCommandBuffer buffer)
        {
            SetKeywords(
                buffer, FilterQualityKeywords, (int)_settings.filterQuality - 1);
            SetKeywords(
                buffer, ShadowMaskKeywords,
                directionalShadows.UsesShadowMask || otherShadows.UsesShadowMask
                    ? QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask
                        ? 0
                        : 1
                    : -1);
            float f = 1f - _settings.directional.cascadeFade;
            buffer.SetGlobalVector(ShadowDistanceFadeId, new Vector4(
                1f / _settings.maxDistance, 1f / _settings.distanceFade,
                1f / (1f - f * f)));
            int directionalAtlasSize = (int)_settings.directional.atlasSize;
            int otherAtlasSize = (int)_settings.other.atlasSize;
            buffer.SetGlobalVector(ShadowAtlasSizeId, new Vector4(
                directionalAtlasSize, 1f / directionalAtlasSize,
                otherAtlasSize, 1f / otherAtlasSize));

            // [SRP][Shadow.Render] 每 60 帧打一次,看最终有多少 directional/other 影子光被纳入绘制
            if ((_setupLogCounter & 63) == 0)
            {
                Log.Info(string.Format(
                    "[SRP][Shadow.Render] filterQuality={0} filterSize={1:F1} dirLightCount={2} otherLightCount={3} usesMask={4}",
                    _settings.filterQuality,
                    _settings.DirectionalFilterSize,
                    directionalShadows.LightCount,
                    otherShadows.LightCount,
                    directionalShadows.UsesShadowMask || otherShadows.UsesShadowMask));
            }
        }

        public static Matrix4x4 ConvertToAtlasMatrix(
            Matrix4x4 m, Vector2 offset, float scale)
        {
            if (SystemInfo.usesReversedZBuffer)
            {
                m.m20 = -m.m20;
                m.m21 = -m.m21;
                m.m22 = -m.m22;
                m.m23 = -m.m23;
            }

            m.m00 = (0.5f * (m.m00 + m.m30) + offset.x * m.m30) * scale;
            m.m01 = (0.5f * (m.m01 + m.m31) + offset.x * m.m31) * scale;
            m.m02 = (0.5f * (m.m02 + m.m32) + offset.x * m.m32) * scale;
            m.m03 = (0.5f * (m.m03 + m.m33) + offset.x * m.m33) * scale;
            m.m10 = (0.5f * (m.m10 + m.m30) + offset.y * m.m30) * scale;
            m.m11 = (0.5f * (m.m11 + m.m31) + offset.y * m.m31) * scale;
            m.m12 = (0.5f * (m.m12 + m.m32) + offset.y * m.m32) * scale;
            m.m13 = (0.5f * (m.m13 + m.m33) + offset.y * m.m33) * scale;
            m.m20 = 0.5f * (m.m20 + m.m30);
            m.m21 = 0.5f * (m.m21 + m.m31);
            m.m22 = 0.5f * (m.m22 + m.m32);
            m.m23 = 0.5f * (m.m23 + m.m33);
            return m;
        }

        public static Vector2 SetTileViewport(
            UnsafeCommandBuffer buffer, int index, int split, float tileSize)
        {
            // Guard against divide-by-zero: callers occasionally pass split=0
            // when the directional light count is 0 (e.g., a scene with no main light).
            if (split < 1) split = 1;
            var offset = new Vector2(index % split, index / split);
            buffer.SetViewport(new Rect(
                offset.x * tileSize, offset.y * tileSize, tileSize, tileSize));
            return offset;
        }

        static void SetKeywords(
            UnsafeCommandBuffer buffer, GlobalKeyword[] keywords, int enabledIndex)
        {
            for (int i = 0; i < keywords.Length; i++)
            {
                buffer.SetKeyword(keywords[i], i == enabledIndex);
            }
        }
    }
}
