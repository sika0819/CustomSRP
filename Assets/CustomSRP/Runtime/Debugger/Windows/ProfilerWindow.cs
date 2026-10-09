using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;

namespace CustomSRP.Debugger
{
    sealed class ProfilerWindow : PerfDebuggerWindowBase
    {
        Label _supported;
        Label _enabled;
        Label _monoUsed;
        Label _monoHeap;
        Label _allocated;
        Label _reserved;
        Label _unused;
        Label _gfxDriver;
        Label _temp;
        Label _targetFps;
        Label _vSync;

        public override string Title => "Profiler";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("Profiler Information");
            box.Add(Row("Supported", out _supported));
            box.Add(Row("Enabled", out _enabled));
            box.Add(Row("Mono Used", out _monoUsed));
            box.Add(Row("Mono Heap", out _monoHeap));
            box.Add(Row("Total Allocated", out _allocated));
            box.Add(Row("Total Reserved", out _reserved));
            box.Add(Row("Unused Reserved", out _unused));
            box.Add(Row("Graphics Driver", out _gfxDriver));
            box.Add(Row("Temp Allocator", out _temp));
            box.Add(Row("Target Frame Rate", out _targetFps));
            box.Add(Row("vSync Count", out _vSync));
            root.Add(box);
        }

        public override void Refresh()
        {
            Set(_supported, Profiler.supported.ToString());
            Set(_enabled, Profiler.enabled.ToString());
            Set(_monoUsed, Bytes(Profiler.GetMonoUsedSizeLong()));
            Set(_monoHeap, Bytes(Profiler.GetMonoHeapSizeLong()));
            Set(_allocated, Bytes(Profiler.GetTotalAllocatedMemoryLong()));
            Set(_reserved, Bytes(Profiler.GetTotalReservedMemoryLong()));
            Set(_unused, Bytes(Profiler.GetTotalUnusedReservedMemoryLong()));
            Set(_gfxDriver, Bytes(Profiler.GetAllocatedMemoryForGraphicsDriver()));
            Set(_temp, Bytes(Profiler.GetTempAllocatorSize()));
            Set(_targetFps, Application.targetFrameRate.ToString());
            Set(_vSync, QualitySettings.vSyncCount.ToString());
        }
    }
}
