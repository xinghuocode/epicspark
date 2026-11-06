Shader "SparkCode/AdditiveGlow"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Intensity ("Intensity", Float) = 1
        _SoftFade ("Radial Soft Fade", Range(0,1)) = 0
        _Scroll ("Scroll XY", Vector) = (0,0,0,0)
        _Pulse ("Pulse", Range(0,1)) = 0.1
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }
        Blend One One
        ZWrite Off
        Cull Off
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float _Intensity;
                float _SoftFade;
                float4 _Scroll;
                float _Pulse;
            CBUFFER_END

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex) + _Scroll.xy * _Time.y;
                o.color = v.color * _Color;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                half radial = saturate(1.0 - length(i.uv - 0.5) * 2.0);
                half soft = lerp(1.0, radial, saturate(_SoftFade));
                half a = tex.a * i.color.a * soft;
                half pulse = 1.0 + _Pulse * sin(_Time.y * 1.9 + i.uv.x * 5.0 + i.uv.y * 3.0) * 0.5;
                half3 c = tex.rgb * i.color.rgb * _Intensity * pulse;
                return half4(c * a, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
