// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "Mine/ReflectFloor" {
    Properties {
        _WaveScale ("Wave scale", Range (0.02,0.15)) = 0.063
        _ReflDistort ("Reflection distort", Range (0,1.5)) = 0.44
        _RefrDistort ("Refraction distort", Range (0,1.5)) = 0.40
        _RefrColor ("Refraction color", COLOR)  = ( .34, .85, .92, 1)
        [NoScaleOffset] _Fresnel ("Fresnel (A) ", 2D) = "gray" {}
        [NoScaleOffset] _BumpMap ("Normalmap ", 2D) = "bump" {}
        WaveSpeed ("Wave speed (map1 x,y; map2 x,y)", Vector) = (19,9,-16,-7)
        [NoScaleOffset] _ReflectiveColor ("Reflective color (RGB) fresnel (A) ", 2D) = "" {}
        _HorizonColor ("Simple water horizon color", COLOR)  = ( .172, .463, .435, 1)
        [HideInInspector] _ReflectionTex ("Internal Reflection", 2D) = "" {}
        [HideInInspector] _RefractionTex ("Internal Refraction", 2D) = "" {}
        _ReflectionScale ("Reflection Scale", Range(0.0, 1.0)) = 0.18
        
        // 点光源着色器
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Colro", COLOR) = (0, 0, 0, 0)
        _Diffuse ("Diffuse", Color) = (1, 1, 1, 1)
        _Specular ("Specular", Color) = (1, 1, 1, 1)
        _Gloss ("Gloss", Range(8.0, 256)) = 20
        _Size ("图片大小", Float) = 20
        _PointLightSize ("点光源着色范围", Float) = 20
    }
    Subshader {
        Tags { "WaterMode"="Refractive" "RenderType"="Opaque" }
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile WATER_REFRACTIVE WATER_REFLECTIVE WATER_SIMPLE
            
            #if defined (WATER_REFLECTIVE) || defined (WATER_REFRACTIVE)
                #define HAS_REFLECTION 1
            #endif
            #if defined (WATER_REFRACTIVE)
                #define HAS_REFRACTION 1
            #endif
            
            #include "UnityCG.cginc"
            
            uniform float4 _WaveScale4;
            uniform float4 _WaveOffset;
            float _ReflectionScale;
            
            #if HAS_REFLECTION
                uniform float _ReflDistort;
            #endif
            #if HAS_REFRACTION
                uniform float _RefrDistort;
            #endif
            
            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };
            
            struct v2f {
                float4 pos : SV_POSITION;
                #if defined(HAS_REFLECTION) || defined(HAS_REFRACTION)
                    float4 ref : TEXCOORD0;
                    float2 bumpuv0 : TEXCOORD1;
                    float2 bumpuv1 : TEXCOORD2;
                    float3 viewDir : TEXCOORD3;
                #else
                    float2 bumpuv0 : TEXCOORD0;
                    float2 bumpuv1 : TEXCOORD1;
                    float3 viewDir : TEXCOORD2;
                #endif
                UNITY_FOG_COORDS(4)
            };
            
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                
                // scroll bump waves
                float4 temp;
                float4 wpos = mul (unity_ObjectToWorld, v.vertex);
                temp.xyzw = wpos.xzxz * _WaveScale4 + _WaveOffset;
                o.bumpuv0 = temp.xy;
                o.bumpuv1 = temp.wz;
                
                // object space view direction (will normalize per pixel)
                o.viewDir.xzy = WorldSpaceViewDir(v.vertex);
                
                #if defined(HAS_REFLECTION) || defined(HAS_REFRACTION)
                o.ref = ComputeNonStereoScreenPos(o.pos);
                #endif
            
                UNITY_TRANSFER_FOG(o,o.pos);
                return o;
            }
            
            #if defined (WATER_REFLECTIVE) || defined (WATER_REFRACTIVE)
                sampler2D _ReflectionTex;
            #endif
            #if defined (WATER_REFLECTIVE) || defined (WATER_SIMPLE)
                sampler2D _ReflectiveColor;
            #endif
            #if defined (WATER_REFRACTIVE)
                sampler2D _Fresnel;
                sampler2D _RefractionTex;
                uniform float4 _RefrColor;
            #endif
            #if defined (WATER_SIMPLE)
                uniform float4 _HorizonColor;
            #endif
            sampler2D _BumpMap;
            
            half4 frag( v2f i ) : SV_Target
            {
                i.viewDir = normalize(i.viewDir);
                
                // combine two scrolling bumpmaps into one
                half3 bump1 = UnpackNormal(tex2D( _BumpMap, i.bumpuv0 )).rgb;
                half3 bump2 = UnpackNormal(tex2D( _BumpMap, i.bumpuv1 )).rgb;
                half3 bump = (bump1 + bump2) * 0.5;
                
                // fresnel factor
                half fresnelFac = dot( i.viewDir, bump );
                
                // perturb reflection/refraction UVs by bumpmap, and lookup colors
                #if HAS_REFLECTION
                    float4 uv1 = i.ref; uv1.xy += bump * _ReflDistort;
                    half4 refl = tex2Dproj( _ReflectionTex, UNITY_PROJ_COORD(uv1) );
                #endif
                #if HAS_REFRACTION
                    float4 uv2 = i.ref; uv2.xy -= bump * _RefrDistort;
                    half4 refr = tex2Dproj( _RefractionTex, UNITY_PROJ_COORD(uv2) ) * _RefrColor;
                #endif
                
                // final color is between refracted and reflected based on fresnel
                half4 color;
                
                #if defined(WATER_REFRACTIVE)
                    half fresnel = UNITY_SAMPLE_1CHANNEL( _Fresnel, float2(fresnelFac,fresnelFac) );
                    color = lerp( refr, refl * _ReflectionScale, fresnel );
                #endif
                
                #if defined(WATER_REFLECTIVE)
                    half4 water = tex2D( _ReflectiveColor, float2(fresnelFac,fresnelFac) );
                    color.rgb = lerp( water.rgb, refl.rgb, water.a );
                    color.a = refl.a * water.a;
                #endif
                
                #if defined(WATER_SIMPLE)
                    half4 water = tex2D( _ReflectiveColor, float2(fresnelFac,fresnelFac) );
                    color.rgb = lerp( water.rgb, _HorizonColor.rgb, water.a );
                    color.a = _HorizonColor.a;
                #endif
            
                UNITY_APPLY_FOG(i.fogCoord, color);
                return color;
            }
            ENDCG
        }
        
        Pass {
            // Pass for other pixel lights
            Tags { "LightMode" = "ForwardAdd" }
            
            Blend One One
            
            CGPROGRAM
            
            // Apparently need to add this declaration
            #pragma multi_compile_fwdadd
            
            #pragma vertex vert
            #pragma fragment frag
            
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            
            fixed4 _Diffuse;
            fixed4 _Specular;
            
            sampler2D _BumpMap;
            float4 _BumpMap_ST;
            
            float _Gloss;
            float _Size;
            
            float _PointLightSize;
            
            fixed4 _Color;
            
            struct a2v {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };
            
            struct v2f {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                
                float3 currPos : TEXCOORD2;
            };
            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert(a2v v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                
                float4 currPos = v.vertex;
                currPos.x = currPos.x - fmod(currPos.x, _Size) + _Size / 2;
                currPos.z = currPos.z - fmod(currPos.z, _Size) + _Size / 2;
                o.currPos = mul(unity_ObjectToWorld, currPos).xyz;
                
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                
                return o;
            }
            
            fixed4 frag(v2f i) : SV_Target {
                fixed3 worldNormal = normalize(i.worldNormal);
                #ifdef USING_DIRECTIONAL_LIGHT
                    fixed3 worldLightDir = normalize(_WorldSpaceLightPos0.xyz);
                #else
                    fixed3 worldLightDir = normalize(_WorldSpaceLightPos0.xyz - i.worldPos.xyz);
                #endif
                
                fixed3 diffuse = _LightColor0.rgb * _Diffuse.rgb * max(0, dot(worldNormal, worldLightDir));
                				
				fixed3 viewDir = normalize(_WorldSpaceCameraPos.xyz - i.worldPos.xyz);
				fixed3 halfDir = normalize(worldLightDir + viewDir);
				fixed3 specular = _LightColor0.rgb * _Specular.rgb * pow(max(0, dot(worldNormal, halfDir)), _Gloss);
				
                #ifdef USING_DIRECTIONAL_LIGHT
                    fixed atten = 1.0;
                #else
                #if defined (POINT)
                    float3 lightCoord = mul(unity_WorldToLight, float4(i.worldPos, 1)).xyz;
                    fixed atten = tex2D(_LightTexture0, dot(lightCoord, lightCoord).rr).UNITY_ATTEN_CHANNEL;
                    
                    #elif defined (SPOT)
                        float4 lightCoord = mul(unity_WorldToLight, float4(i.worldPos, 1));
                        fixed atten = (lightCoord.z > 0) * tex2D(_LightTexture0, lightCoord.xy / lightCoord.w + 0.5).w * tex2D(_LightTextureB0, dot(lightCoord, lightCoord).rr).UNITY_ATTEN_CHANNEL;
                    #else
                        fixed atten = 1.0;
                    #endif
                    
                #endif
                
                return fixed4((specular) * atten, 1.0);
            }
            ENDCG
        }
    }
}
