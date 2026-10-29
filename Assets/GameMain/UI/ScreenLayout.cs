using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Phone panels match height. Pad panels use Expand.
    /// A pad is a screen whose long edge is at most 1.6 times the short edge.
    /// </summary>
    public class ScreenLayout : MonoBehaviour
    {
        public static ScreenLayout Instance { get; private set; }
        public static event Action Changed;

        public PanelSettings PhonePanelSettings;
        public PanelSettings PadPanelSettings;

        int _width;
        int _height;
        ScreenOrientation _orientation;

        public static bool IsPad
        {
            get
            {
                CurrentSize(out int width, out int height);
                float longEdge = Mathf.Max(width, height);
                float shortEdge = Mathf.Min(width, height);
                return shortEdge > 0f && longEdge / shortEdge <= 1.6f;
            }
        }

        public static bool IsLandscapeForAssets
        {
            get
            {
                ScreenOrientation orientation = Screen.orientation;
                if (orientation == ScreenOrientation.LandscapeLeft
                    || orientation == ScreenOrientation.LandscapeRight)
                    return true;
                if (orientation == ScreenOrientation.Portrait
                    || orientation == ScreenOrientation.PortraitUpsideDown)
                    return false;

                CurrentSize(out int width, out int height);
                return height > 0 && width >= height;
            }
        }

        static void CurrentSize(out int width, out int height)
        {
            Camera cam = Camera.main;
            if (cam != null && cam.pixelWidth > 0 && cam.pixelHeight > 0)
            {
                width = cam.pixelWidth;
                height = cam.pixelHeight;
                return;
            }

            width = Screen.width;
            height = Screen.height;
        }

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            ApplyAll();
        }

        void Update()
        {
            CurrentSize(out int width, out int height);
            if (width == _width && height == _height && Screen.orientation == _orientation)
                return;
            ApplyAll();
        }

        public static void Apply(UIDocument document)
        {
            if (document == null || Instance == null)
                return;

            PanelSettings phone = Instance.PhonePanelSettings;
            PanelSettings pad = Instance.PadPanelSettings;
            // Only retarget game forms already on phone/pad (or unset).
            // Debugger / overlays keep their own PanelSettings (e.g. sortingOrder 32000).
            PanelSettings current = document.panelSettings;
            if (current != null && current != phone && current != pad)
                return;

            PanelSettings settings = IsPad ? pad : phone;
            if (settings != null && current != settings)
                document.panelSettings = settings;
        }

        void ApplyAll()
        {
            CurrentSize(out _width, out _height);
            _orientation = Screen.orientation;
            if (GameEntry.UI != null)
            {
                UIForm[] forms = GameEntry.UI.GetAllLoadedUIForms();
                for (int i = 0; i < forms.Length; i++)
                {
                    UIDocument document = forms[i] != null
                        ? forms[i].GetComponent<UIDocument>()
                        : null;
                    Apply(document);
                }
            }

            BootEarlyCover cover = BootEarlyCover.Instance;
            if (cover != null)
                Apply(cover.GetComponent<UIDocument>());
            Changed?.Invoke();
        }
    }
}
