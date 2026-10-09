using UnityEngine;
using UnityEngine.UIElements;

namespace CustomSRP.Debugger
{
    /// <summary>
    /// Corner RenderTexture preview via UI Toolkit (replaces uGUI RawImage).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RtPreviewOverlay : MonoBehaviour
    {
        [SerializeField]
        RenderTexture texture;

        [SerializeField]
        Vector2 size = new Vector2(320f, 180f);

        [SerializeField]
        Vector2 margin = new Vector2(16f, 16f);

        UIDocument _document;
        PanelSettings _panelSettings;
        VisualElement _image;

        public void SetTexture(RenderTexture rt)
        {
            texture = rt;
            ApplyTexture();
        }

        void OnEnable()
        {
            EnsureUi();
            ApplyTexture();
        }

        void OnDisable()
        {
            if (_document != null)
            {
                _document.rootVisualElement?.Clear();
            }
        }

        void OnDestroy()
        {
            if (_panelSettings != null)
            {
                Destroy(_panelSettings);
            }
        }

        void EnsureUi()
        {
            _document = GetComponent<UIDocument>();
            if (_document == null)
            {
                _document = gameObject.AddComponent<UIDocument>();
            }

            if (_panelSettings == null)
            {
                _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                _panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
                _panelSettings.sortingOrder = 100;
            }

            _document.panelSettings = _panelSettings;
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Ignore;

            _image = new VisualElement();
            _image.pickingMode = PickingMode.Ignore;
            _image.style.position = Position.Absolute;
            _image.style.left = margin.x;
            _image.style.bottom = margin.y;
            _image.style.width = size.x;
            _image.style.height = size.y;
            _image.style.borderTopWidth = 1;
            _image.style.borderBottomWidth = 1;
            _image.style.borderLeftWidth = 1;
            _image.style.borderRightWidth = 1;
            _image.style.borderTopColor = new Color(1f, 1f, 1f, 0.35f);
            _image.style.borderBottomColor = new Color(1f, 1f, 1f, 0.35f);
            _image.style.borderLeftColor = new Color(1f, 1f, 1f, 0.35f);
            _image.style.borderRightColor = new Color(1f, 1f, 1f, 0.35f);
            root.Add(_image);
        }

        void ApplyTexture()
        {
            if (_image == null)
            {
                return;
            }

            _image.style.backgroundImage = texture != null
                ? Background.FromRenderTexture(texture)
                : StyleKeyword.None;
        }
    }
}
