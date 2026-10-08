using UnityEngine;
using UnityEngine.UI;

namespace CustomSRP.Examples
{
    /// <summary>
    /// On-device FPS HUD via uGUI (avoids IMGUI OnGUI cost).
    /// Unlocks Unity's default mobile 30 FPS cap for perf testing.
    /// </summary>
    public sealed class MobilePerfHud : MonoBehaviour
    {
        [SerializeField] float _updateInterval = 0.5f;
        [SerializeField] int _targetFrameRate = 60;

        float _accum;
        int _frames;
        float _timer;
        float _fps;
        float _ms;
        Text _label;

        void Awake()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = _targetFrameRate;
            EnsureLabel();
        }

        void EnsureLabel()
        {
            if (_label != null)
            {
                return;
            }

            var canvasGo = new GameObject("MobilePerfHudCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            canvasGo.AddComponent<GraphicRaycaster>().enabled = false;

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(canvasGo.transform, false);
            _label = textGo.AddComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_label.font == null)
            {
                _label.font = Font.CreateDynamicFontFromOSFont("sans-serif", 36);
            }

            _label.fontSize = 36;
            _label.alignment = TextAnchor.UpperLeft;
            _label.color = Color.white;
            _label.raycastTarget = false;

            var rt = _label.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(24f, -24f);
            rt.sizeDelta = new Vector2(520f, 220f);
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _accum += dt;
            _frames++;
            _timer += dt;
            if (_timer < _updateInterval)
            {
                return;
            }

            _fps = _frames / _accum;
            _ms = 1000f * _accum / Mathf.Max(1, _frames);
            _accum = 0f;
            _frames = 0;
            _timer = 0f;

            if (_label != null)
            {
                _label.text =
                    $"Oil NPR Empty\n{_fps:0.0} FPS\n{_ms:0.00} ms\n" +
                    $"{Screen.width}x{Screen.height}\n" +
                    $"cap {_targetFrameRate}";
            }
        }
    }
}
