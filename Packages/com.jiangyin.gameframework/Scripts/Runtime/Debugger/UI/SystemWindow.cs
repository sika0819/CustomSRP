using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    sealed class SystemWindow : DebuggerPage
    {
        Label _device;
        Label _os;
        Label _cpu;
        Label _cores;
        Label _freq;
        Label _ram;
        Label _battery;
        Label _unity;
        Label _platform;
        Label _lang;

        public override string Title => "System";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("System Information");
            box.Add(Row("Device Model", out _device));
            box.Add(Row("Operating System", out _os));
            box.Add(Row("Processor Type", out _cpu));
            box.Add(Row("Processor Count", out _cores));
            box.Add(Row("Processor Frequency", out _freq));
            box.Add(Row("System Memory", out _ram));
            box.Add(Row("Battery", out _battery));
            box.Add(Row("Unity Version", out _unity));
            box.Add(Row("Platform", out _platform));
            box.Add(Row("System Language", out _lang));
            root.Add(box);
        }

        public override void Refresh()
        {
            Set(_device, SystemInfo.deviceModel);
            Set(_os, SystemInfo.operatingSystem);
            Set(_cpu, SystemInfo.processorType);
            Set(_cores, SystemInfo.processorCount.ToString());
            Set(_freq, $"{SystemInfo.processorFrequency} MHz");
            Set(_ram, $"{SystemInfo.systemMemorySize} MB");
            Set(_battery, $"{SystemInfo.batteryStatus} {SystemInfo.batteryLevel:P0}");
            Set(_unity, Application.unityVersion);
            Set(_platform, Application.platform.ToString());
            Set(_lang, Application.systemLanguage.ToString());
        }
    }
}
