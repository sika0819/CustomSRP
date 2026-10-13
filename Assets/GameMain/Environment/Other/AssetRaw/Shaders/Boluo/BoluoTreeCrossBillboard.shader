// 族: 植被 | 光: BoluoLitAlbedoObject | 雾: BoluoFinishLitStamp | 语言: docs/油画世界.md
Shader "Boluo/Mobile/TreeCrossBillboard"
{
    Properties
    {
        _MainTex ("Albedo Atlas", 2D) = "white" {}
        _BumpMap ("Normal Atlas", 2D) = "bump" {}
        _FoliageMask ("Foliage Mask", 2D) = "white" {}
        _LeafTex ("Near Leaf Texture", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.35
        _EdgeFade ("Edge Fade", Range(0.05, 0.45)) = 0.18
        _NormalStrength ("Normal Strength", Range(0, 1)) = 0.85
        _LeafTexBlend ("Near Leaf Texture Blend", Range(0, 1)) = 0
        _BoluoHasLeafTex ("Has near leaf texture", Range(0, 1)) = 0
        _BoluoBillboardBakeRevision ("Bake revision", Float) = 0
        _BoluoSpringRole ("Spring Role", Range(0, 1)) = 0
        // bake/copy only；frag 不读。不要当第二套季节盘。
        _BoluoNativeSeason ("Native Season Mat", Range(0, 1)) = 0
        _BoluoFoliageSnowScale ("Foliage Snow Scale", Range(0, 1)) = 1
        _BoluoConiferSnow ("Evergreen / Conifer", Range(0, 1)) = 0
        _BoluoHasFoliageMask ("Has baked foliage mask", Range(0, 1)) = 0
        _BoluoLeafCard ("Leaf card (hide mesh in winter)", Range(0, 1)) = 0
        [HDR] _TopColor ("Season GPU stamp", Color) = (1, 1, 1, 1)
        [HDR] _BottomColor ("Season GPU stamp", Color) = (1, 1, 1, 1)
        _BarkColor ("Season GPU bark stamp", Color) = (0.5451, 0.4902, 0.3922, 1)
        // 朝向在 Common.hlsl：选正面/侧面图后只亮一张正对镜头的卡。
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            AlphaToMask Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:BoluoGpuInstancingSetup
            #pragma multi_compile _ BOLUO_GPU_INDIRECT
            #pragma multi_compile _ _BOLUO_SKS_CHEAP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "BoluoTreeCrossBillboardCommon.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_FoliageMask);
            SAMPLER(sampler_FoliageMask);
            TEXTURE2D(_LeafTex);
            SAMPLER(sampler_LeafTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _BumpMap_ST;
                float4 _FoliageMask_ST;
                float4 _LeafTex_ST;
                float _Cutoff;
                float _EdgeFade;
                float _NormalStrength;
                float _LeafTexBlend;
                float _BoluoHasLeafTex;
                float _BoluoBillboardBakeRevision;
                float _BoluoSpringRole;
                float _BoluoNativeSeason;
                float _BoluoFoliageSnowScale;
                float _BoluoConiferSnow;
                float _BoluoHasFoliageMask;
                float _BoluoLeafCard;
                half4 _TopColor;
                half4 _BottomColor;
                half4 _BarkColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float keep : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 nWS;
                float keep;
                float3 posOS = BoluoTreeCrossBillboardRotateYaw(input.positionOS.xyz, input.normalOS, nWS, keep);
                if (keep > 0.5)
                    posOS = BoluoTreeCrossBillboardApplyWind(posOS);
                VertexPositionInputs posInputs = GetVertexPositionInputs(posOS);
                output.positionWS = posInputs.positionWS;
                // 叶/干共面。后画的赢；叶卡略拉近相机，即使提交顺序乱了叶也赢 Z。4cm 在远树上看不见。
                if (_BoluoLeafCard > 0.5)
                {
                    float3 toCam = GetCameraPositionWS() - posInputs.positionWS;
                    float distSq = max(dot(toCam, toCam), 1e-8);
                    output.positionWS += toCam * rsqrt(distSq) * 0.04;
                    output.positionCS = TransformWorldToHClip(output.positionWS);
                }
                else
                    output.positionCS = posInputs.positionCS;
                output.normalWS = nWS;
                output.keep = keep;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                clip(input.keep - 0.5);
                // 阔叶叶卡整卡关掉，干 submesh 仍画。不是在混合图集上打洞。
                if (_BoluoLeafCard > 0.5h && _BoluoConiferSnow < 0.5h)
                    clip(0.5h - BoluoSeasonWinterAmount());
                // 负 mip bias：远树 atlas 默认会把叶纹平均成一团灰。
                half4 col = SAMPLE_TEXTURE2D_BIAS(_MainTex, sampler_MainTex, input.uv, -0.65);
                half uvY = saturate(input.uv.y);
                half bakedFoliage = SAMPLE_TEXTURE2D(_FoliageMask, sampler_FoliageMask, input.uv).r;
                half foliage = _BoluoLeafCard > 0.5h
                    ? 1.0h
                    : (_BoluoHasFoliageMask > 0.5h ? saturate(bakedFoliage) : 0.0h);
                // 近叶 _Texture 是单簇/混贴，不能铺到十字片 UV 上（整冠会变成几片巨叶）。
                // 冠层 RGB 只认烘焙 atlas；季色走 BoluoTreeCrossBillboardLeafAlbedo。
                // 网格已沿外轮廓裁实，Queue=Geometry Opaque；不做 col.a clip（避免 mip 平均打洞）。
                // _Cutoff 仍留给 baker / 贴图 alphaTestReferenceValue 对齐，frag 不读。
                half3 albedo = BoluoTreeCrossBillboardLeafAlbedo(
                    col.rgb, uvY, _BoluoSpringRole, _TopColor.rgb, _BottomColor.rgb,
                    _BarkColor.rgb, foliage, 1.0h, _BoluoConiferSnow);
#if defined(_BOLUO_SKS_CHEAP)
                // 廉价只砍法线/湿边/油画，季色仍走 TintLeaf。
                half ndotl = saturate(dot(normalize(input.normalWS), _MainLightPosition.xyz) * 0.5h + 0.5h);
                return half4(albedo * (0.4h + 0.6h * ndotl), 1);
#else
                half4 nrm = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv);
                // 冬天阔叶卡 clip 掉以后只剩干卡：用针叶档 UV 顶缘，才能在光枝上看到雪。
                albedo = BoluoSeasonApplyBillboardCapSnow(
                    albedo, uvY, _BoluoFoliageSnowScale,
                    foliage < 0.5h ? 1.0h : _BoluoConiferSnow);
                half3 rgb = BoluoTreeCrossBillboardShade(
                    albedo, nrm.rgb, nrm.a, _NormalStrength, input.positionWS);
                if (foliage < 0.5h)
                    rgb = BoluoNightKeepWood(rgb, albedo, input.normalWS);
                else
                    rgb = BoluoNightKeepFoliage(rgb, albedo, input.normalWS);
                if (foliage > 0.5h)
                    rgb = BoluoPainterlyContourWash(rgb, input.positionWS, col.a);
                if (foliage > 0.5h)
                    rgb = BoluoFinishLitFoliage(rgb, input.positionWS, input.normalWS, input.uv);
                else
                {
                    rgb = ApplyBoluoPainterly(rgb, input.positionWS, input.normalWS);
                    rgb = BoluoMixFoliageFog(rgb, input.positionWS);
                }
                return half4(rgb, 1);
#endif
            }
            ENDHLSL
        }
    }
    FallBack Off
}
