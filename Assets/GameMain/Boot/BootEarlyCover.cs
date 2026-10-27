using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Launch-scene baked cover: still + tip + bar. No Yoo / GF dependency.
    /// Replaced by <see cref="LoadingForm"/> once Yoo opens it.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(UIDocument))]
    public sealed class BootEarlyCover : MonoBehaviour
    {
        public const int SortingOrder = 1000;

        [SerializeField] Texture2D _portraitStill;
        [SerializeField] Texture2D _landscapeStill;

        UIDocument _document;
        VisualElement _root;
        VisualElement _still;
        VisualElement _fill;
        Label _tip;
        bool _showing;
        float _progress = 0.02f;
        string _tipText = "正在进入世界...";

        public static BootEarlyCover Instance { get; private set; }

        public static bool IsShowing =>
            Instance != null && Instance._showing;

        void Awake()
        {
            Instance = this;
            _document = GetComponent<UIDocument>();
            if (_document != null)
                _document.sortingOrder = SortingOrder;

            // First rendered frame is this camera clear. Keep it the cover color
            // so the gap before the UITK overlay is never white.
            PaintCameraClear();
            ScreenLayout.Changed += OnScreenChanged;
            ScreenLayout.Apply(_document);
            Show("正在进入世界...", 0.02f);
        }

        void Start()
        {
            // UIDocument builds its tree in OnEnable (default order). This component
            // runs at -100, so Awake/OnEnable see a null root. Start is still before
            // the first camera render.
            if (_showing)
                Show(_tipText, _progress);
        }

        void LateUpdate()
        {
            if (!_showing || _still != null)
                return;
            Show(_tipText, _progress);
        }

        void OnEnable()
        {
            if (!_showing)
                return;
            CacheElements();
            ApplyStillForOrientation();
            SetProgress(_progress, _tipText);
        }

        void OnDestroy()
        {
            ScreenLayout.Changed -= OnScreenChanged;
            if (Instance == this)
                Instance = null;
        }

        public void Show(string tip = "正在进入世界...", float progress = 0.02f)
        {
            _tipText = string.IsNullOrEmpty(tip) ? _tipText : tip;
            _progress = Mathf.Clamp01(progress);
            CacheElements();
            if (_root != null)
                _root.style.display = DisplayStyle.Flex;
            if (_document != null)
            {
                _document.sortingOrder = SortingOrder;
                if (!_document.enabled)
                    _document.enabled = true;
            }

            ApplyStillForOrientation();
            SetProgress(_progress, _tipText);
            _showing = true;
        }

        public void SetProgress(float progress01, string tip = null)
        {
            progress01 = Mathf.Clamp01(progress01);
            _progress = progress01;
            if (!string.IsNullOrEmpty(tip))
                _tipText = tip;

            if (_fill != null)
            {
                float x = progress01;
                if (progress01 > 0.001f && x < 0.02f)
                    x = 0.02f;
                _fill.style.scale = new Scale(new Vector2(x, 1f));
            }

            if (_tip != null)
                _tip.text = _tipText;
        }

        public void Hide()
        {
            _showing = false;
            if (_root != null)
                _root.style.display = DisplayStyle.None;
            if (_document != null)
                _document.sortingOrder = -1;
        }

        public static void HideIfAny()
        {
            if (Instance != null)
                Instance.Hide();
        }

        void OnScreenChanged()
        {
            if (!_showing)
                return;
            ScreenLayout.Apply(_document);
            ApplyStillForOrientation();
        }

        void CacheElements()
        {
            if (_document == null)
                _document = GetComponent<UIDocument>();
            if (_document == null)
                return;

            VisualElement rootVe = _document.rootVisualElement;
            if (rootVe == null)
                return;

            _root = rootVe.Q("root") ?? rootVe;
            _still = rootVe.Q("still");
            _fill = rootVe.Q("fill");
            _tip = rootVe.Q<Label>("tip");
            if (_fill != null)
                _fill.style.transformOrigin = new TransformOrigin(Length.Percent(0), Length.Percent(50));
        }

        static void PaintCameraClear()
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;
            // rgb(12, 12, 14) — same as .boot-early-cover__root.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(12f / 255f, 12f / 255f, 14f / 255f, 1f);
        }

        void ApplyStillForOrientation()
        {
            bool landscape = ScreenLayout.IsLandscapeForAssets;
            if (_still == null)
                return;

            Texture2D tex = landscape ? _landscapeStill : _portraitStill;
            if (tex != null)
                _still.style.backgroundImage = new StyleBackground(tex);
            _still.style.display = DisplayStyle.Flex;
        }
    }
}
