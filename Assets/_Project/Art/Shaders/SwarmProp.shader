// 资源实体等实例化道具：_BaseColor + 主光源 Lambert + 阴影 + 环境光，支持 GPU Instancing。
// _Fadeable = 1 的材质（巨型植物）在全局 _SwarmPropFade (xyz 中心, w 强度) / _SwarmPropFadeRadius 圆柱内抖动镂空。
Shader "Swarm/Prop"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Fadeable ("Fadeable", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Fadeable;
            CBUFFER_END

            float4 _SwarmPropFade;
            float _SwarmPropFadeRadius;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fog : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float Bayer4(float2 pixel)
            {
                uint2 p = uint2(pixel) & 3;
                static const float m[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
                return (m[p.y * 4 + p.x] + 0.5) / 16.0;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half fade = 0;
                if (_Fadeable > 0.5 && _SwarmPropFade.w > 0.001)
                {
                    float d = distance(i.positionWS.xz, _SwarmPropFade.xz);
                    fade = _SwarmPropFade.w * (1.0 - smoothstep(_SwarmPropFadeRadius * 0.85, _SwarmPropFadeRadius * 1.1, d));
                    clip(Bayer4(i.positionCS.xy) - fade * 0.8);
                }

                float3 n = normalize(i.normalWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half ndl = saturate(dot(n, light.direction));
                half3 ambient = max(SampleSH(n), half3(0.24, 0.25, 0.27));
                half3 c = _BaseColor.rgb * (light.color * ndl * light.shadowAttenuation + ambient);
                c = lerp(c, c * 0.6 + half3(0.08, 0.1, 0.12), fade);
                c = MixFog(c, i.fog);
                return half4(c, 1);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
    }
    FallBack Off
}
