using UnityEngine;
using UnityEngine.UIElements;

namespace CustomSRP.Debugger
{
    public abstract class PerfDebuggerWindowBase : IPerfDebuggerWindow
    {
        protected VisualElement Root { get; private set; }

        public abstract string Title { get; }

        public virtual void Initialize()
        {
        }

        public virtual void Shutdown()
        {
            Root = null;
        }

        public virtual void OnEnter()
        {
        }

        public virtual void OnLeave()
        {
        }

        public virtual void OnUpdate(float elapseSeconds, float realElapseSeconds)
        {
        }

        public void Build(VisualElement root)
        {
            Root = root;
            root.Clear();
            OnBuild(root);
            Refresh();
        }

        public virtual void Refresh()
        {
        }

        protected abstract void OnBuild(VisualElement root);

        protected static VisualElement Row(string title, out Label valueLabel)
        {
            var row = new VisualElement();
            row.AddToClassList("perf-row");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("perf-row-title");
            valueLabel = new Label("-");
            valueLabel.AddToClassList("perf-row-value");
            Label copied = valueLabel;
            copied.RegisterCallback<ClickEvent>(_ =>
            {
                GUIUtility.systemCopyBuffer = copied.text;
            });
            row.Add(titleLabel);
            row.Add(valueLabel);
            return row;
        }

        protected static void Set(Label label, string value)
        {
            if (label != null)
            {
                label.text = value ?? "-";
            }
        }

        protected static string Bytes(long byteLength)
        {
            if (byteLength < 1024L)
            {
                return $"{byteLength} B";
            }

            if (byteLength < 1048576L)
            {
                return $"{byteLength / 1024f:F2} KB";
            }

            if (byteLength < 1073741824L)
            {
                return $"{byteLength / 1048576f:F2} MB";
            }

            return $"{byteLength / 1073741824f:F2} GB";
        }

        protected static VisualElement Box(string heading)
        {
            var box = new VisualElement();
            box.AddToClassList("perf-box");
            if (!string.IsNullOrEmpty(heading))
            {
                var h = new Label(heading);
                h.AddToClassList("perf-heading");
                box.Add(h);
            }

            return box;
        }
    }
}
