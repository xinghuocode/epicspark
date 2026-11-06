Shader "SparkCode/Water"
{
    Properties
    {
        _ShallowColor ("Shallow Color", Color) = (0.2, 0.46, 0.47, 1)
        _DeepColor ("Deep Color", Color) = (0.035, 0.13, 0.15, 1)
        _FoamColor ("Foam Color", Color) = (0.74, 0.94, 0.92, 1)
        _NormalMap ("Ripple Normal", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 0.6
        _FlowSpeed ("Flow Speed", Float) = 0.055
        _Smoothness ("Smoothness", Range(0, 1)) = 0.93
        _FresnelColor ("Sky Reflection Color", Color) = (0.68, 0.8, 0.86, 1)
        _Alpha ("Opacity", Range(0, 1)) = 0.9
        _Glitter ("Glitter", Range(0, 2)) = 0.6
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
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float4 color : COLOR;
            };

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _FoamColor;
                float4 _NormalMap_ST;
                float _NormalStrength;
                float _FlowSpeed;
                float _Smoothness;
                half4 _FresnelColor;
                float _Alpha;
                float _Glitter;
            CBUFFER_END

            Varyings vert (Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                float2 uv1 = uv * float2(3.0, 3.0) + _Time.y * _FlowSpeed * float2(1.0, 0.35);
                float2 uv2 = uv * float2(5.3, 4.1) - _Time.y * _FlowSpeed * float2(0.45, 1.0);
                float2 uv3 = uv * float2(8.7, 7.9) + _Time.y * _FlowSpeed * float2(-0.7, 0.6);

                float3 n1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv1), _NormalStrength);
                float3 n2 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv2), _NormalStrength);
                float3 n3 = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv3), _NormalStrength * 0.5);
                float3 nTS = normalize(float3(n1.xy + n2.xy + n3.xy * 0.6, 1.0));

                float3 up = normalize(i.normalWS);
                float3 worldN = normalize(float3(nTS.x, up.y, nTS.y));

                float3 viewDir = normalize(_WorldSpaceCameraPos - i.positionWS);
                half fresnel = pow(saturate(1.0 - saturate(dot(worldN, viewDir))), 3.2);

                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);
                float3 halfDir = normalize(lightDir + viewDir);
                half spec = pow(saturate(dot(worldN, halfDir)), 140.0) * (0.6 + _Smoothness * 2.2);

                half3 shallow = _ShallowColor.rgb;
                half3 deep = _DeepColor.rgb;
                half3 water = lerp(deep, shallow, saturate(fresnel * 1.25 + 0.18));
                water = lerp(water, _FresnelColor.rgb, fresnel * 0.55);
                water += spec * mainLight.color * 1.15;
                water += _FresnelColor.rgb * 0.05;

                float2 glitUV = uv * 38.0 + _Time.y * _FlowSpeed * 30.0;
                half glit = pow(saturate(sin(glitUV.x + sin(glitUV.y * 1.7)) * 0.5 + 0.5), 26.0);
                glit *= saturate(fresnel * 1.6 + 0.15) * _Glitter;
                water += _FoamColor.rgb * glit;

                half edgeFoam = saturate(i.color.r); // optional vertex-painted foam mask
                water = lerp(water, _FoamColor.rgb, edgeFoam * 0.5);
                half alpha = saturate(_Alpha * (0.62 + fresnel * 0.5) + edgeFoam * 0.25);
                return half4(water, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
