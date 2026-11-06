Shader "SparkCode/SparkCore"
{
    Properties
    {
        _MainTex ("Gold Albedo", 2D) = "white" {}
        _EmissiveTex ("Emissive Mottle", 2D) = "white" {}
        _Color ("Base Color", Color) = (1, 0.72, 0.28, 1)
        _EmissionColor ("Emission Color", Color) = (1, 0.74, 0.32, 1)
        _EmissionIntensity ("Emission Intensity", Float) = 5
        _FlowSpeed ("Flow Speed", Float) = 0.12
        _FresnelColor ("Fresnel Color", Color) = (1, 0.92, 0.6, 1)
        _FresnelPower ("Fresnel Power", Float) = 2.4
        _SparkleScale ("Sparkle Scale", Float) = 26
        _SparkleIntensity ("Sparkle Intensity", Float) = 2
        _Pulse ("Pulse", Range(0,1)) = 0.15
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_EmissiveTex);
            SAMPLER(sampler_EmissiveTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _EmissiveTex_ST;
                half4 _Color;
                half4 _EmissionColor;
                float _EmissionIntensity;
                float _FlowSpeed;
                half4 _FresnelColor;
                float _FresnelPower;
                float _SparkleScale;
                float _SparkleIntensity;
                float _Pulse;
            CBUFFER_END

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            Varyings vert (Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float2 flow = float2(_Time.y * _FlowSpeed, _Time.y * _FlowSpeed * 0.63);
                half3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv + flow * 0.3).rgb * _Color.rgb;
                half emissiveTex = SAMPLE_TEXTURE2D(_EmissiveTex, sampler_EmissiveTex, i.uv * 1.6 - flow).r;

                half3 baseEmis = _EmissionColor.rgb * _EmissionIntensity * (0.55 + emissiveTex * 0.9);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(_WorldSpaceCameraPos - i.positionWS);
                half fres = pow(saturate(1.0h - saturate(dot(n, v))), _FresnelPower);

                float2 sp = i.uv * _SparkleScale + floor(_Time.y * 3.0) * 0.371;
                float sn = hash21(floor(sp));
                half sparkle = pow(saturate(sn), 16.0) * _SparkleIntensity * (0.45 + 0.55 * sin(_Time.y * 4.0 + sn * 21.0));

                half3 col = albedo * 0.35h;
                col += baseEmis * (0.75h + fres * 0.9h);
                col += _FresnelColor.rgb * fres * 1.15h;
                col += _EmissionColor.rgb * sparkle;
                col *= 1.0h + _Pulse * (0.5h + 0.5h * sin(_Time.y * 2.1));
                return half4(col, 1.0h);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
