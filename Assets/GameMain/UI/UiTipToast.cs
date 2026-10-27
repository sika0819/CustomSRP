using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// UI Toolkit toast matching Boluo <c>UICanvasTip</c> / TipWindows:
    /// tip.png 9-slice bubble, white 36px text, pad 60/36, min 288×138, max width 666.
    /// </summary>
    public static class UiTipToast
    {
        public const string ToastClass = "game-tip-toast";
        public const string LabelClass = "game-tip-toast__label";

        public const float PadHorizontal = 60f;
        public const float PadVertical = 36f;
        public const float MinWidth = 288f;
        public const float MinHeight = 138f;
        public const float MaxWidth = 666f;

        /// <summary>TipWindows prefab default anchored Y (above stage center).</summary>
        public const float DefaultAnchoredY = 472f;

        public struct Request
        {
            public string Message;
            public float Seconds;
            public float AnchoredY;
            public bool Error;
            /// <summary>加载罩深度高于 Tip，启动期失败必须抬到罩子上面才看得见。</summary>
            public bool AboveLoading;
        }

        static bool _hasPending;
        static Request _pending;

        /// <summary>
        /// Show the global tip bubble. It is not parented to a sliding sheet.
        /// </summary>
        public static void ShowMessage(
            string message,
            float seconds = 1.6f,
            float anchoredY = DefaultAnchoredY,
            bool error = false,
            bool aboveLoading = false)
        {
            _pending = new Request
            {
                Message = message ?? string.Empty,
                Seconds = seconds,
                AnchoredY = anchoredY,
                Error = error,
                AboveLoading = aboveLoading,
            };
            _hasPending = true;

            if (TipToastForm.Current != null)
            {
                TipToastForm.Current.FlushPending();
                return;
            }

            EnsureHost();
        }

        /// <summary>Open the tip layer once and keep it for the island session.</summary>
        public static void EnsureHost()
        {
            if (TipToastForm.Current != null)
                return;

            if (GameEntry.UI == null)
            {
                UnityEngine.Debug.LogWarning("[UiTipToast] UI is not ready.");
                return;
            }

            GameEntry.EnsureUiGroups();
            if (GameEntry.UI.HasUIForm(UiAssets.TipToastForm))
                return;

            // pauseCoveredUIForm: false — the bubble must not pause the sheet under it.
            GameEntry.UI.OpenUIForm(UiAssets.TipToastForm, UiAssets.TipGroup, false);
        }

        public static void ResetSession()
        {
            _hasPending = false;
            _pending = default;
            TipToastForm.ClearCurrent();
        }

        internal static void NotifyHostClosed()
        {
            _hasPending = false;
        }

        internal static bool TryConsume(out Request request)
        {
            request = _pending;
            if (!_hasPending)
                return false;
            _hasPending = false;
            return true;
        }

        public static VisualElement Ensure(VisualElement host, string name)
        {
            if (host == null)
                return null;

            VisualElement toast = host.Q(name);
            if (toast != null)
            {
                if (toast.Q<Label>(LabelName(name)) == null && toast is Label legacy)
                    return MigrateLegacyLabel(host, legacy, name);
                return toast;
            }

            toast = Create(name);
            host.Add(toast);
            return toast;
        }

        public static Label GetLabel(VisualElement toast)
        {
            if (toast == null)
                return null;
            return toast.Q<Label>(className: LabelClass) ?? toast.Q<Label>();
        }

        public static void Show(VisualElement toast, string message, float anchoredY = DefaultAnchoredY)
        {
            if (toast == null)
                return;

            Label label = GetLabel(toast);
            if (label != null)
                label.text = message ?? string.Empty;

            FitToMessage(toast, label);
            Place(toast, anchoredY);
            toast.style.display = DisplayStyle.Flex;
            toast.BringToFront();
        }

        public static void Hide(VisualElement toast)
        {
            if (toast != null)
                toast.style.display = DisplayStyle.None;
        }

        static VisualElement Create(string name)
        {
            var toast = new VisualElement { name = name };
            toast.AddToClassList(ToastClass);
            toast.pickingMode = PickingMode.Ignore;
            toast.style.display = DisplayStyle.None;

            var label = new Label { name = LabelName(name) };
            label.AddToClassList(LabelClass);
            label.pickingMode = PickingMode.Ignore;
            toast.Add(label);
            return toast;
        }

        static string LabelName(string toastName) => toastName + "__label";

        static VisualElement MigrateLegacyLabel(VisualElement host, Label legacy, string name)
        {
            string text = legacy.text;
            host.Remove(legacy);
            VisualElement toast = Create(name);
            Label label = GetLabel(toast);
            if (label != null)
                label.text = text;
            host.Add(toast);
            return toast;
        }

        static void FitToMessage(VisualElement toast, Label label)
        {
            if (toast == null || label == null)
                return;

            // Preferred single-line width (avoid VisualElement.MeasureMode — Unity 6 nested enum
            // sometimes fails CS0103 in this asmdef). Approximate then clamp like TipWindow.ShowTip.
            label.style.whiteSpace = WhiteSpace.NoWrap;
            float fontSize = label.resolvedStyle.fontSize;
            if (fontSize < 1f)
                fontSize = 36f;
            float textW = (label.text != null ? label.text.Length : 0) * fontSize * 0.62f;

            float width = Mathf.Clamp(textW + PadHorizontal * 2f, MinWidth, MaxWidth);
            toast.style.width = width;
            toast.style.minHeight = MinHeight;
            toast.style.height = StyleKeyword.Auto;
            toast.style.marginLeft = -width * 0.5f;

            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.maxWidth = width - PadHorizontal * 2f;
        }

        static void Place(VisualElement toast, float anchoredY)
        {
            if (toast == null)
                return;

            // Stage center + TipWindows pivot (0.5, 0.5): marginTop shifts the top edge,
            // so subtract half min-height so the bubble center sits near anchoredY.
            toast.style.left = Length.Percent(50);
            toast.style.top = Length.Percent(50);
            toast.style.marginTop = -(anchoredY + MinHeight * 0.5f);
            toast.style.right = StyleKeyword.Auto;
            toast.style.bottom = StyleKeyword.Auto;
        }

        // Error toast — uses a distinct tint on the same tip.png bubble.
        // Calling code manages the auto-hide timer.
        public static void ApplyErrorStyle(VisualElement toast)
        {
            if (toast == null)
                return;
            toast.style.unityBackgroundImageTintColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        }

        public static void ResetToNormalStyle(VisualElement toast)
        {
            if (toast == null)
                return;
            toast.style.unityBackgroundImageTintColor = new Color(1f, 1f, 1f, 0.902f);
        }
    }
}
