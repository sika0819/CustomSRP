using System;
using UnityEngine;
using UnityEngine.UIElements;

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
                CurrentSize(out int width, out int height);
                if (height > 0 && width >= height)
                    return true;

                ScreenOrientation orientation = Screen.orientation;
                return orientation == ScreenOrientation.LandscapeLeft
                    || orientation == ScreenOrientation.LandscapeRight;
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

            PanelSettings settings = IsPad ? Instance.PadPanelSettings : Instance.PhonePanelSettings;
            if (settings != null && document.panelSettings != settings)
                document.panelSettings = settings;
        }

        void ApplyAll()
        {
            CurrentSize(out _width, out _height);
            _orientation = Screen.orientation;
            UIDocument[] documents = FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < documents.Length; i++)
                Apply(documents[i]);
            Changed?.Invoke();
        }
    }
}
