using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;

namespace CustomSRP.Debugger
{
    sealed class MemorySummaryWindow : PerfDebuggerWindowBase
    {
        readonly List<Record> _records = new();
        Label _summary;
        VisualElement _list;
        DateTime _sampleTime = DateTime.MinValue;
        int _sampleCount;
        long _sampleSize;

        public override string Title => "Memory";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("Runtime Memory Summary");
            var sample = new Button(TakeSample) { text = "Take Sample" };
            sample.AddToClassList("perf-button");
            box.Add(sample);
            _summary = new Label("Please take sample first.");
            _summary.AddToClassList("perf-note");
            box.Add(_summary);
            _list = new VisualElement();
            _list.AddToClassList("perf-mem-list");
            box.Add(_list);
            root.Add(box);
        }

        public override void Refresh()
        {
            if (_sampleTime <= DateTime.MinValue || _summary == null)
            {
                return;
            }

            _summary.text =
                $"{_sampleCount} Objects ({Bytes(_sampleSize)}) @ {_sampleTime.ToLocalTime():HH:mm:ss}";
            if (_list == null)
            {
                return;
            }

            _list.Clear();
            int n = Mathf.Min(_records.Count, 40);
            for (int i = 0; i < n; i++)
            {
                Record r = _records[i];
                var row = new Label($"{r.Name}  ×{r.Count}  {Bytes(r.Size)}");
                row.AddToClassList("perf-mem-row");
                _list.Add(row);
            }
        }

        void TakeSample()
        {
            _records.Clear();
            _sampleTime = DateTime.UtcNow;
            _sampleCount = 0;
            _sampleSize = 0L;

            UnityEngine.Object[] samples = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            var map = new Dictionary<string, Record>(256);
            for (int i = 0; i < samples.Length; i++)
            {
                long size = Profiler.GetRuntimeMemorySizeLong(samples[i]);
                string name = samples[i].GetType().Name;
                _sampleCount++;
                _sampleSize += size;
                if (!map.TryGetValue(name, out Record record))
                {
                    record = new Record(name);
                    map.Add(name, record);
                    _records.Add(record);
                }

                record.Count++;
                record.Size += size;
            }

            _records.Sort((a, b) =>
            {
                int c = b.Size.CompareTo(a.Size);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            Refresh();
        }

        sealed class Record
        {
            public readonly string Name;
            public int Count;
            public long Size;

            public Record(string name) => Name = name;
        }
    }
}
