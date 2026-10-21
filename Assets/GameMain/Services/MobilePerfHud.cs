using UnityEngine;

namespace Game
{
    /// <summary>
    /// Lightweight on-device frame budget overlay (Release-safe).
    /// 3-finger tap toggles visibility. Shows FPS, CPU/GPU frame ms, CRP buffer, mirror flag.
    /// </summary>
    public sealed class MobilePerfHud : MonoBehaviour
    {
        const string PrefsVisibleKey = "Game.MobilePerfHud.Visible";
        const float SampleInterval = 0.5f;

        static MobilePerfHud _instance;

        readonly FrameTiming[] _timings = new FrameTiming[1];
        float _accumDt;
        int _accumFrames;
        float _fps;
        float _cpuMs;
        float _gpuMs;
        bool _visible;
        GUIStyle _style;
        Rect _box = new Rect(8f, 8f, 280f, 110f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
#if UNITY_EDITOR
            // Editor Play Mode: optional; default off so SceneView stays clean.
            if (PlayerPrefs.GetInt(PrefsVisibleKey, 0) == 0)
                return;
#endif
            Ensure();
        }

        public static MobilePerfHud Ensure()
        {
            if (_instance != null)
                return _instance;

            var go = new GameObject("[MobilePerfHud]")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MobilePerfHud>();
            return _instance;
        }

        public static void SetVisible(bool visible)
        {
            Ensure();
            _instance._visible = visible;
            PlayerPrefs.SetInt(PrefsVisibleKey, visible ? 1 : 0);
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            // Default on so a fresh Release APK can measure without adb; 3-finger tap hides.
            _visible = PlayerPrefs.GetInt(PrefsVisibleKey, 1) != 0;
            FrameTimingManager.CaptureFrameTimings();
        }

        void Update()
        {
            if (Input.touchCount >= 3 && Input.GetTouch(0).phase == TouchPhase.Began)
            {
                _visible = !_visible;
                PlayerPrefs.SetInt(PrefsVisibleKey, _visible ? 1 : 0);
            }

            float dt = Time.unscaledDeltaTime;
            _accumDt += dt;
            _accumFrames++;
            if (_accumDt < SampleInterval)
                return;

            _fps = _accumFrames / _accumDt;
            _accumDt = 0f;
            _accumFrames = 0;

            FrameTimingManager.CaptureFrameTimings();
            uint n = FrameTimingManager.GetLatestTimings(1, _timings);
            if (n > 0)
            {
                // cpuFrameTime / gpuFrameTime are seconds.
                _cpuMs = (float)(_timings[0].cpuFrameTime * 1000.0);
                _gpuMs = (float)(_timings[0].gpuFrameTime * 1000.0);
            }
            else
            {
                _cpuMs = dt * 1000f;
                _gpuMs = 0f;
            }
        }

        void OnGUI()
        {
            if (!_visible)
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 22,
                    richText = false,
                    normal = { textColor = Color.white }
                };
                _style.normal.background = Texture2D.whiteTexture;
            }

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.Box(_box, GUIContent.none);
            GUI.color = Color.white;

            string bound = _gpuMs > 0.01f && _gpuMs >= _cpuMs * 0.9f
                ? "GPU?"
                : "CPU?";
            string text =
                $"FPS {_fps:0.0}  ({bound})\n" +
                $"CPU {_cpuMs:0.0} ms  GPU {_gpuMs:0.0} ms\n" +
                $"buf {IslandLocomotionState.BufferWidth}x{IslandLocomotionState.BufferHeight}\n" +
                $"walk {(IslandLocomotionState.IsWalking ? "Y" : "n")}  " +
                $"mirror {(IslandLocomotionState.MirrorRenderedThisFrame ? "DRAW" : "reuse")}\n" +
                $"(3-finger tap hide)";

            GUI.Label(new Rect(_box.x + 8f, _box.y + 6f, _box.width - 12f, _box.height - 8f),
                text, _style);
            GUI.color = prev;
        }
    }
}
