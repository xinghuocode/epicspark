Shader "SparkCode/SkyGradient"
{
    Properties
    {
        _ZenithColor ("Zenith", Color) = (0.16, 0.32, 0.55, 1)
        _HorizonColor ("Horizon", Color) = (0.72, 0.78, 0.8, 1)
        _GroundColor ("Below Horizon", Color) = (0.34, 0.33, 0.3, 1)
        _SunColor ("Sun Color", Color) = (1, 0.88, 0.7, 1)
        _SunDirection ("Sun Direction", Vector) = (0.35, 0.7, 0.45, 0)
        _SunSize ("Sun Size", Range(0.2, 8)) = 1.6
        _HazeColor ("Haze Color", Color) = (0.9, 0.86, 0.78, 1)
        _HazeStrength ("Haze Strength", Range(0, 1)) = 0.25
        _CloudStrength ("Cloud Strength", Range(0, 1)) = 0.18
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor;
                half4 _HorizonColor;
                half4 _GroundColor;
                half4 _SunColor;
                float4 _SunDirection;
                float _SunSize;
                half4 _HazeColor;
                float _HazeStrength;
                float _CloudStrength;
            CBUFFER_END

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float noise2(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1.0, 0.0));
                float c = hash21(i + float2(0.0, 1.0));
                float d = hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float fbm(float2 p)
            {
                float value = 0.0;
                float amp = 0.5;
                for (int k = 0; k < 4; k++)
                {
                    value += amp * noise2(p);
                    p *= 2.03;
                    amp *= 0.5;
                }
                return value;
            }

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float up = saturate(dir.y);
                float3 sky = lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(up, 0.55));
                sky = lerp(sky, _GroundColor.rgb, saturate(-dir.y * 2.5));

                float3 sunDir = normalize(_SunDirection.xyz);
                float sunDot = saturate(dot(dir, sunDir));
                float cosSize = cos(radians(_SunSize));
                float disc = smoothstep(cosSize, cosSize * 0.9993, sunDot);
                float glow = pow(sunDot, 90.0) * 0.6 + pow(sunDot, 12.0) * 0.22;
                sky += _SunColor.rgb * (disc * 3.2 + glow);

                // thin high clouds near the horizon, kept subtle so the sky stays clean through the oculus
                float2 cuv = dir.xz / (abs(dir.y) + 0.18) * 0.6;
                float cloud = fbm(cuv + float2(3.1, 7.7));
                cloud = smoothstep(0.52, 0.78, cloud) * smoothstep(0.02, 0.35, up) * _CloudStrength;
                sky = lerp(sky, _HazeColor.rgb * 1.02, cloud);

                float haze = pow(1.0 - up, 5.0) * _HazeStrength;
                sky += _HazeColor.rgb * haze * 0.35;

                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
