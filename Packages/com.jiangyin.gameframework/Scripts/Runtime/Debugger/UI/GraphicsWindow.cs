using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    sealed class GraphicsWindow : DebuggerPage
    {
        Label _name;
        Label _vendor;
        Label _type;
        Label _version;
        Label _mem;
        Label _mt;
        Label _shader;
        Label _maxTex;
        Label _rtCount;
        Label _api;
        Label _pipeline;

        public override string Title => "Graphics";

        protected override void OnBuild(VisualElement root)
        {
            var box = Box("Graphics Information");
            box.Add(Row("Device Name", out _name));
            box.Add(Row("Vendor", out _vendor));
            box.Add(Row("Device Type", out _type));
            box.Add(Row("Version", out _version));
            box.Add(Row("GPU Memory", out _mem));
            box.Add(Row("Multi Threaded", out _mt));
            box.Add(Row("Shader Level", out _shader));
            box.Add(Row("Max Texture Size", out _maxTex));
            box.Add(Row("Render Targets", out _rtCount));
            box.Add(Row("Graphics API", out _api));
            box.Add(Row("Render Pipeline", out _pipeline));
            root.Add(box);
        }

        public override void Refresh()
        {
            Set(_name, SystemInfo.graphicsDeviceName);
            Set(_vendor, SystemInfo.graphicsDeviceVendor);
            Set(_type, SystemInfo.graphicsDeviceType.ToString());
            Set(_version, SystemInfo.graphicsDeviceVersion);
            Set(_mem, $"{SystemInfo.graphicsMemorySize} MB");
            Set(_mt, SystemInfo.graphicsMultiThreaded.ToString());
            Set(_shader, SystemInfo.graphicsShaderLevel.ToString());
            Set(_maxTex, SystemInfo.maxTextureSize.ToString());
            Set(_rtCount, SystemInfo.supportedRenderTargetCount.ToString());
            Set(_api, SystemInfo.graphicsDeviceType.ToString());
            RenderPipelineAsset rp = GraphicsSettings.currentRenderPipeline;
            Set(_pipeline, rp != null ? rp.name : "(none)");
        }
    }
}
