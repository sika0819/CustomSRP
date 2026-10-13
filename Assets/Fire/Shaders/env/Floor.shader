Shader "Unlit/Floor"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Colro", COLOR) = (0, 0, 0, 0)
        
        _Diffuse ("Diffuse", Color) = (1, 1, 1, 1)
        _Specular ("Specular", Color) = (1, 1, 1, 1)
        _Gloss ("Gloss", Range(8.0, 256)) = 20
        _BumpMap ("发线纹理", 2D) = "bump" {}
        _Size ("图片大小", Float) = 20
        
        _PointLightSize ("点光源着色范围", Float) = 20
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            
            CGPROGRAM
            // Apparently need to add this declaration
            #pragma multi_compile_fwdbase
            
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            
            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // sample the texture
                fixed4 col = tex2D(_MainTex, i.uv);
                
                return _Color;
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
