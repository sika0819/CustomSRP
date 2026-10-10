using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    sealed class ConsoleWindow : DebuggerPage
    {
        const int MaxLines = 120;

        readonly Queue<LogNode> _logs = new();

        Toggle _lockScroll;
        Toggle _info;
        Toggle _warning;
        Toggle _error;
        Toggle _fatal;
        ScrollView _logView;
        Label _stack;
        LogNode _selected;
        bool _hasSelected;
        bool _dirty = true;

        public override string Title => "Console";

        public int InfoCount { get; private set; }
        public int WarningCount { get; private set; }
        public int ErrorCount { get; private set; }
        public int FatalCount { get; private set; }

        public override void Initialize()
        {
            Application.logMessageReceived += OnLog;
        }

        public override void Shutdown()
        {
            Application.logMessageReceived -= OnLog;
            _logs.Clear();
            base.Shutdown();
        }

        protected override void OnBuild(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.flexShrink = 1;
            root.style.flexBasis = 0;
            root.style.flexDirection = FlexDirection.Column;
            root.style.minHeight = 0;

            var toolbar = new VisualElement();
            toolbar.AddToClassList("perf-toolbar");
            toolbar.style.flexShrink = 0;
            var clear = new Button(Clear) { text = "Clear" };
            clear.AddToClassList("perf-button");
            toolbar.Add(clear);
            _lockScroll = new Toggle("Lock") { value = true };
            _info = new Toggle("Info") { value = true };
            _warning = new Toggle("Warn") { value = true };
            _error = new Toggle("Error") { value = true };
            _fatal = new Toggle("Fatal") { value = true };
            foreach (var t in new[] { _lockScroll, _info, _warning, _error, _fatal })
            {
                t.AddToClassList("perf-toggle");
                t.RegisterValueChangedCallback(_ => { _dirty = true; Refresh(); });
                toolbar.Add(t);
            }

            root.Add(toolbar);

            _logView = new ScrollView(ScrollViewMode.Vertical);
            _logView.AddToClassList("perf-log-list");
            _logView.style.flexGrow = 1;
            _logView.style.flexShrink = 1;
            _logView.style.flexBasis = 0;
            _logView.style.minHeight = 0;
            root.Add(_logView);

            _stack = new Label();
            _stack.AddToClassList("perf-stack");
            _stack.style.whiteSpace = WhiteSpace.Normal;
            _stack.style.height = 72;
            _stack.style.flexShrink = 0;
            _stack.style.color = new Color(0.82f, 0.86f, 0.9f);
            root.Add(_stack);
            _dirty = true;
        }

        public override void OnUpdate(float elapseSeconds, float realElapseSeconds)
        {
            if (_dirty)
            {
                Refresh();
            }
        }

        public override void Refresh()
        {
            if (_logView == null)
            {
                return;
            }

            RefreshCount();
            _info.text = $"Info ({InfoCount})";
            _warning.text = $"Warn ({WarningCount})";
            _error.text = $"Error ({ErrorCount})";
            _fatal.text = $"Fatal ({FatalCount})";

            _logView.Clear();
            int shown = 0;
            foreach (LogNode node in _logs)
            {
                if (!PassFilter(node))
                {
                    continue;
                }

                shown++;
                LogNode captured = node;
                var line = new Label(FormatLine(captured));
                line.AddToClassList("perf-log-line");
                line.AddToClassList(ClassFor(captured.Type));
                line.style.color = ColorFor(captured.Type);
                line.style.whiteSpace = WhiteSpace.Normal;
                line.style.fontSize = 12;
                line.style.marginBottom = 2;
                line.style.paddingLeft = 4;
                line.style.paddingRight = 4;
                line.pickingMode = PickingMode.Position;
                line.RegisterCallback<ClickEvent>(_ => Select(captured));
                _logView.Add(line);
            }

            if (shown == 0)
            {
                var empty = new Label("No logs yet.");
                empty.style.color = new Color(0.75f, 0.8f, 0.86f);
                empty.style.fontSize = 13;
                empty.style.paddingTop = 8;
                empty.style.paddingLeft = 4;
                _logView.Add(empty);
            }

            if (_lockScroll.value)
            {
                _logView.scrollOffset = new Vector2(0f, float.MaxValue);
            }

            _stack.text = _hasSelected
                ? $"{_selected.Message}\n\n{_selected.Stack}"
                : string.Empty;
            _dirty = false;
        }

        public Color32 BadgeColor()
        {
            if (FatalCount > 0)
            {
                return new Color32(180, 50, 50, 255);
            }

            if (ErrorCount > 0)
            {
                return new Color32(255, 80, 80, 255);
            }

            if (WarningCount > 0)
            {
                return new Color32(255, 210, 60, 255);
            }

            return new Color32(180, 255, 180, 255);
        }

        void Select(LogNode node)
        {
            _selected = node;
            _hasSelected = true;
            _stack.text = $"{node.Message}\n\n{node.Stack}";
            GUIUtility.systemCopyBuffer = $"{node.Message}\n\n{node.Stack}";
        }

        void Clear()
        {
            _logs.Clear();
            _hasSelected = false;
            _dirty = true;
            Refresh();
        }

        void OnLog(string condition, string stackTrace, LogType type)
        {
            _logs.Enqueue(new LogNode(type, condition, stackTrace));
            while (_logs.Count > MaxLines)
            {
                _logs.Dequeue();
            }

            _dirty = true;
        }

        void RefreshCount()
        {
            InfoCount = WarningCount = ErrorCount = FatalCount = 0;
            foreach (LogNode n in _logs)
            {
                switch (n.Type)
                {
                    case LogType.Log:
                    case LogType.Assert:
                        InfoCount++;
                        break;
                    case LogType.Warning:
                        WarningCount++;
                        break;
                    case LogType.Error:
                        ErrorCount++;
                        break;
                    case LogType.Exception:
                        FatalCount++;
                        break;
                }
            }
        }

        bool PassFilter(LogNode node) =>
            node.Type switch
            {
                LogType.Warning => _warning.value,
                LogType.Error => _error.value,
                LogType.Exception => _fatal.value,
                _ => _info.value,
            };

        static string FormatLine(LogNode node) =>
            $"[{node.Type}] {Truncate(node.Message, 160)}";

        static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        static Color ColorFor(LogType type) =>
            type switch
            {
                LogType.Warning => new Color(1f, 0.82f, 0.31f),
                LogType.Error => new Color(1f, 0.43f, 0.43f),
                LogType.Exception => new Color(1f, 0.28f, 0.28f),
                _ => new Color(0.9f, 0.92f, 0.94f),
            };

        static string ClassFor(LogType type) =>
            type switch
            {
                LogType.Warning => "log-warn",
                LogType.Error => "log-error",
                LogType.Exception => "log-fatal",
                _ => "log-info",
            };

        readonly struct LogNode
        {
            public readonly LogType Type;
            public readonly string Message;
            public readonly string Stack;

            public LogNode(LogType type, string message, string stack)
            {
                Type = type;
                Message = message ?? string.Empty;
                Stack = stack ?? string.Empty;
            }
        }
    }
}
