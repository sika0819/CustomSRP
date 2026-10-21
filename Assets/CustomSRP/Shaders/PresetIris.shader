Shader "Hidden/CustomSRP/Preset Iris"
{
    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off

        Pass
        {
            Name "Preset Iris Overlay"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            // Core Common only — no UnityInput / _ProjectionParams needed for this overlay.
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            float _PresetIrisSoftness;
            float _PresetIrisTransition;
            float _PresetIrisSize;
            float _PresetIrisAspect; // camera pixelWidth / pixelHeight (set by PresetIrisPass)
            float4 _PresetIrisColor;

            struct IrisVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 screenUV : TEXCOORD0;
            };

            IrisVaryings Vert(uint vertexID : SV_VertexID)
            {
                IrisVaryings output;
                // Fullscreen triangle; Metal/Vulkan UV origin matches this winding.
                output.positionCS = float4(
                    vertexID <= 1 ? -1.0 : 3.0,
                    vertexID == 1 ? 3.0 : -1.0,
                    0.0,
                    1.0);
                output.screenUV = float2(
                    vertexID <= 1 ? 0.0 : 2.0,
                    vertexID == 1 ? 2.0 : 0.0);
                return output;
            }

            float4 Frag(IrisVaryings input) : SV_TARGET
            {
                // Aspect-correct UV so the iris is circular in screen pixels
                // (UV-space distance alone stretches into an ellipse on phones).
                float2 d = input.screenUV - 0.5;
                float aspect = max(_PresetIrisAspect, 1e-4);
                d.x *= aspect;
                float dis = length(d);
                float currentRange = _PresetIrisSize * (1.0 - _PresetIrisTransition);

                // Softness≈0 → hard full black (mobile GLES / peak teleport).
                float alpha;
                if (_PresetIrisSoftness <= 1e-6)
                    alpha = 1.0;
                else
                    alpha = smoothstep(currentRange, currentRange + _PresetIrisSoftness, dis);

                float4 col = _PresetIrisColor;
                col.a = alpha * _PresetIrisColor.a;
                return col;
            }
            ENDHLSL
        }
    }
}
