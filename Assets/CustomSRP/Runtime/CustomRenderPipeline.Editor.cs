#if UNITY_EDITOR
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;
using LightType = UnityEngine.LightType;

namespace CustomSRP
{
    public partial class CustomRenderPipeline
    {
        partial void InitializeForEditor()
        {
            Lightmapping.SetDelegate(lightsDelegate);
            // The engine overlay pass does not run for this pipeline in the editor.
            // DrawUIOverlay in CameraRenderer paints UI Toolkit into the Game view.
            UnityEngine.Rendering.SupportedRenderingFeatures.active.rendersUIOverlay = true;
        }

        partial void DisposeForEditor()
        {
            Lightmapping.ResetDelegate();
        }

        static readonly Lightmapping.RequestLightsDelegate lightsDelegate =
            (Light[] lights, NativeArray<LightDataGI> output) =>
            {
                var lightData = new LightDataGI();
                for (int i = 0; i < lights.Length; i++)
                {
                    Light light = lights[i];
                    switch (light.type)
                    {
                        case LightType.Directional:
                            var directionalLight = new DirectionalLight();
                            LightmapperUtils.Extract(light, ref directionalLight);
                            lightData.Init(ref directionalLight);
                            break;
                        case LightType.Point:
                            var pointLight = new PointLight();
                            LightmapperUtils.Extract(light, ref pointLight);
                            lightData.Init(ref pointLight);
                            break;
                        case LightType.Spot:
                            var spotLight = new SpotLight();
                            LightmapperUtils.Extract(light, ref spotLight);
                            spotLight.innerConeAngle = light.innerSpotAngle * Mathf.Deg2Rad;
                            spotLight.angularFalloff = AngularFalloffType.AnalyticAndInnerAngle;
                            lightData.Init(ref spotLight);
                            break;
                        case LightType.Rectangle:
                            var rectangleLight = new RectangleLight();
                            LightmapperUtils.Extract(light, ref rectangleLight);
                            rectangleLight.mode = LightMode.Baked;
                            lightData.Init(ref rectangleLight);
                            break;
                        default:
                            lightData.InitNoBake(light.GetEntityId());
                            break;
                    }

                    lightData.falloff = FalloffType.InverseSquared;
                    output[i] = lightData;
                }
            };
    }
}
#endif
