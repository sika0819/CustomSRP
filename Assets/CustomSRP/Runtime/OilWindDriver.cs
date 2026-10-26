using Game;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// 独立风力推送器：确保风力全局变量（_OilGrassWindStrength, _OilGrassWindDir）
    /// 在所有天气下都被设置，不依赖草地是否存在。
    /// </summary>
    [DisallowMultipleComponent]
    public class OilWindDriver : MonoBehaviour
    {
        [Tooltip("默认风力强度（WeatherService.Level = 3 时达到此值）")]
        public float defaultWindStrength = 0.55f;

        [Tooltip("风力方向（归一化）")]
        public Vector3 windDirection = new Vector3(1f, 0f, 0.35f);

        static readonly int WindDirId = Shader.PropertyToID("_OilGrassWindDir");
        static readonly int WindStrengthId = Shader.PropertyToID("_OilGrassWindStrength");

        float _lastWindStrength = float.NaN;
        Vector3 _lastWindDir;
        bool _initialized;

        void Update()
        {
            PushWindGlobals();
        }

        void PushWindGlobals()
        {
            if (!Application.isPlaying)
                return;

            Vector3 dir = windDirection.sqrMagnitude > 1e-6f ? windDirection.normalized : Vector3.right;
            float effectiveWind = GetWeatherWindStrength();

            // 只在值变化时设置
            if (_initialized
                && (dir - _lastWindDir).sqrMagnitude < 1e-8f
                && Mathf.Abs(effectiveWind - _lastWindStrength) < 1e-5f)
            {
                return;
            }

            Shader.SetGlobalVector(WindDirId, dir);
            Shader.SetGlobalFloat(WindStrengthId, effectiveWind);

            _lastWindDir = dir;
            _lastWindStrength = effectiveWind;
            _initialized = true;
        }

        /// <summary>
        /// 从天气系统获取风力强度。风天气时返回 0-1 的强度值，其他天气返回 0。
        /// Safe for editor - only queries WeatherService in play mode.
        /// </summary>
        float GetWeatherWindStrength()
        {
            if (!Application.isPlaying)
                return 0f;

            var weather = WeatherService.Ensure();
            // VisualWind01 lerps during weather transitions (Kind snaps immediately).
            return Mathf.Lerp(0f, defaultWindStrength, weather.VisualWind01);
        }
    }
}
