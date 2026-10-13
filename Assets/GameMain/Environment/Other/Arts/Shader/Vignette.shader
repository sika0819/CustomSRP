Shader "Custom/Vignette"
{
    Properties
    {
        // UI Image / Canvas 渲染时会写入 _MainTex；缺失会触发 Console Error 并被 BLS 误报。
        _MainTex ("Texture", 2D) = "white" {}
        _Transition ("Transition Value", Range(0, 1)) = 0.5 // 0.5时中心透明，周围白色
        _Size ("Vignette Size", Range(0, 2)) = 0.5
        _Softness ("Vignette Softness", Range(0, 1)) = 0.2
        _Color ("Vignette Color", Color) = (1,1,1,1) // 默认白色
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha // 启用透明度混合
        ZWrite Off // 不写入深度缓冲

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

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
            float _Transition;
            float _Size;
            float _Softness;
            float4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 计算到屏幕中心的距离
                float2 center = float2(0.5, 0.5);
                float dis = distance(i.uv, center);
                
                // 计算当前过渡范围：Transition=0.5时，中心区域透明
                float currentRange = _Size * (1 - _Transition);
                
                // Softness=0 → edge0==edge1，smoothstep 未定义：
                // GLES/Vulkan 常得 1（全黑遮罩，IEMoveTo 峰值/瞬移靠它盖住旧场景）；
                // 若写成 alpha=0，安卓会在 Softness=0 保持阶段看穿黑场，露出旧预设再瞬切。
                // Softness>0：正常虹膜渐变。揭幕靠 _Color.a→0（ClearVignette），与 Softness=0 正交。
                float alpha;
                if (_Softness <= 1e-6)
                    alpha = 1.0;
                else
                    alpha = smoothstep(currentRange, currentRange + _Softness, dis);

                // 应用颜色和透明度
                fixed4 finalColor = _Color;
                finalColor.a = alpha * _Color.a; // 结合用户设置的透明度

                return finalColor;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
