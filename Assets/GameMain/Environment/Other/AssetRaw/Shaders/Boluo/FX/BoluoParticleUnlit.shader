// 族: 粒子 | Unlit | MeshUnlit / FlipbookUnlit 是 UsePass 别名
Shader "Boluo/FX/ParticleUnlit"
{
    // 粒子 / mesh 卡片 / 序列帧同一份。材质配 Queue / ZWrite / Blend；flipbook 有孔才开 keyword。
    Properties
    {
        [MainTexture] _MainTex ("Albedo", 2D) = "white" {}
        [HDR] [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        [HDR] _TintColor ("Tint (legacy)", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0
        [Toggle(_FLIPBOOK_ON)] _Flipbook ("Flipbook", Float) = 0
        _Columns ("Columns", Float) = 1
        _Rows ("Rows", Float) = 1
        _Fps ("FPS", Float) = 12
        _Phase ("Phase", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 0
        _BoluoWaterWrap ("水下染色", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _FLIPBOOK_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../BoluoWaterColors.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _BaseColor;
                half4 _TintColor;
                half _Cutoff;
                float _Columns;
                float _Rows;
                float _Fps;
                float _Phase;
                half _BoluoWaterWrap;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float2 BoluoFlipbookUv(float2 uv)
            {
                float cols = max(1.0, _Columns);
                float rows = max(1.0, _Rows);
                float frames = cols * rows;
                float t = frac(_Time.y * max(0.01, _Fps) / frames + _Phase);
                float frame = floor(t * frames);
                float col = fmod(frame, cols);
                float row = floor(frame / cols);
                float2 cell = float2(1.0 / cols, 1.0 / rows);
                return uv * cell + float2(col, (rows - 1.0) - row) * cell;
            }

            // Gamma 无 tonemap：HDR 叠色会爆白。染色只取 display-referred，亮部压到 0.68，翅纹/笔触还在。
            half3 BoluoParticleAlbedo(half3 texRgb, half3 tintRgb)
            {
                half3 rgb = texRgb * saturate(tintRgb);
                half peak = max(rgb.r, max(rgb.g, rgb.b));
                return peak > 0.68h ? rgb * (0.68h / peak) : rgb;
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
#if defined(_FLIPBOOK_ON)
                float2 uv = BoluoFlipbookUv(i.uv);
#else
                float2 uv = i.uv;
#endif
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                half4 tint = _BaseColor * _TintColor * i.color;
                half4 col = half4(BoluoParticleAlbedo(tex.rgb, tint.rgb), saturate(tex.a * tint.a));

#ifdef _ALPHATEST_ON
                // 镂空只看贴图。boluo-main 蟹/鱼 Color over Lifetime 顶点 a=0，
                // 旧 ShaderGraph 不拿它做 clip；乘进 cutoff 会整卡剪没。
                clip(tex.a - _Cutoff);
#endif
                half wrap = saturate(_BoluoWaterWrap);
                if (wrap > 1e-3h)
                {
                    half3 water = (half3)BoluoWaterShallow(float4(0.22, 0.50, 0.80, 1)).rgb;
                    half3 deep = (half3)BoluoWaterDeep(float4(0.07, 0.24, 0.58, 1)).rgb;
                    half3 wash = lerp(water, deep, 0.38h);
                    col.rgb = lerp(col.rgb, col.rgb * wash, wrap);
                }
                return col;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
