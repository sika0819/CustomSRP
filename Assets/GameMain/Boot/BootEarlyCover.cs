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
            // panelSettings 一换，旧 VisualElement 还非 null，但已经不在面板上。
            // 清数据冷启动会在 692×1523 → 840×1848 时换一次面板，进度和 Hide 打在旧树上，
            // 新树保持 UXML 默认（「正在进入世界...」、不满屏）。
            if (_showing)
            {
                if (_still != null && _still.panel != null)
                    return;
                Show(_tipText, _progress);
                return;
            }

            if (_document == null)
                _document = GetComponent<UIDocument>();
            VisualElement live = _document != null ? _document.rootVisualElement : null;
            if (live != null && live.style.display != DisplayStyle.None)
                Hide();
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
            if (_document == null)
                _document = GetComponent<UIDocument>();
            if (_document != null)
            {
                _document.sortingOrder = SortingOrder;
                if (!_document.enabled)
                    _document.enabled = true;
            }

            CacheElements();
            if (_root != null)
            {
                StretchFull(_root);
                _root.style.display = DisplayStyle.Flex;
                _root.style.opacity = 1f;
            }

            if (_still != null)
                StretchFull(_still);

            VisualElement panelRoot = _document != null ? _document.rootVisualElement : null;
            if (panelRoot != null && panelRoot != _root)
            {
                StretchFull(panelRoot);
                panelRoot.style.display = DisplayStyle.Flex;
                panelRoot.style.opacity = 1f;
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
            if (_fill == null || _fill.panel == null || _tip == null || _tip.panel == null)
                CacheElements();

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
            if (_document == null)
                _document = GetComponent<UIDocument>();
            CacheElements();
            if (_root != null)
            {
                _root.style.display = DisplayStyle.None;
                _root.style.opacity = 0f;
            }

            if (_document == null)
                return;

            VisualElement panelRoot = _document.rootVisualElement;
            if (panelRoot != null)
            {
                panelRoot.style.display = DisplayStyle.None;
                panelRoot.style.opacity = 0f;
                panelRoot.pickingMode = PickingMode.Ignore;
            }
        }

        public void Disarm()
        {
            // 不要关 Document。真机关掉 UIDocument 会把上一帧冻在 Surface 上，
            // 清数据后揭罩日志已经走完，屏幕仍停在加载封面。
            Hide();
        }

        public static void HideIfAny()
        {
            if (Instance != null)
                Instance.Hide();
        }

        void OnScreenChanged()
        {
            ScreenLayout.Apply(_document);
            if (_showing)
                Show(_tipText, _progress);
            else
                Hide();
        }

        static void StretchFull(VisualElement element)
        {
            element.style.position = Position.Absolute;
            element.style.left = 0f;
            element.style.right = 0f;
            element.style.top = 0f;
            element.style.bottom = 0f;
            element.style.marginLeft = 0f;
            element.style.marginRight = 0f;
            element.style.marginTop = 0f;
            element.style.marginBottom = 0f;
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
