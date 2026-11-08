// 地形：顶点色 + 主光源 Lambert + 阴影 + 环境光。
// 全局 _SwarmSnow：朝上表面积雪（顶点色 alpha = 可积雪）。
// 全局 _SwarmFade (xyz = 世界坐标中心, w = 强度 0..1) + _SwarmFadeRadius：
//   UV0.x = 1 的高山顶点在该圆内用抖动镂空（不透明管线中的“虚化”），露出山后的东西。
Shader "Swarm/Terrain"
{
    Properties { }
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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float _SwarmSnow;
            float4 _SwarmFade;
            float _SwarmFadeRadius;

            // 热力图叠加（GDD 3.2 / 4）：A = 采集/集结/回避/路径，B = 警报/三种气味，C = 危险/覆盖/归巢
            TEXTURE2D(_SwarmHeatA); SAMPLER(sampler_SwarmHeatA);
            TEXTURE2D(_SwarmHeatB);
            TEXTURE2D(_SwarmHeatC);
            float4 _SwarmHeatMaskA, _SwarmHeatMaskB, _SwarmHeatMaskC;
            float4 _SwarmHeatColors[12];
            float _SwarmMapSize;
            float _SwarmShowCoverage;

            half3 ApplyHeat(half3 albedo, float3 positionWS)
            {
                if (_SwarmMapSize <= 0) return albedo;
                float any = dot(_SwarmHeatMaskA, 1) + dot(_SwarmHeatMaskB, 1) + dot(_SwarmHeatMaskC, 1) + _SwarmShowCoverage;
                if (any <= 0) return albedo;

                float2 uv = positionWS.xz / _SwarmMapSize;
                float4 a = SAMPLE_TEXTURE2D(_SwarmHeatA, sampler_SwarmHeatA, uv);
                float4 b = SAMPLE_TEXTURE2D(_SwarmHeatB, sampler_SwarmHeatA, uv);
                float4 c = SAMPLE_TEXTURE2D(_SwarmHeatC, sampler_SwarmHeatA, uv);

                if (_SwarmShowCoverage > 0.5)
                {
                    float cov = saturate(c.y);
                    albedo *= lerp(0.45, 1.0, cov);                          // 覆盖外压暗
                    float edge = 1.0 - abs(cov * 2.0 - 1.0);                 // 覆盖边界一圈高亮
                    albedo = lerp(albedo, _SwarmHeatColors[10].rgb, saturate(edge * 1.6) * 0.8);
                }

                float v[10] = {
                    a.x * _SwarmHeatMaskA.x, a.y * _SwarmHeatMaskA.y, a.z * _SwarmHeatMaskA.z, a.w * _SwarmHeatMaskA.w,
                    b.x * _SwarmHeatMaskB.x, b.y * _SwarmHeatMaskB.y, b.z * _SwarmHeatMaskB.z, b.w * _SwarmHeatMaskB.w,
                    c.x * _SwarmHeatMaskC.x, c.z * _SwarmHeatMaskC.z };
                [unroll] for (int k = 0; k < 10; k++)
                {
                    float w = saturate(v[k]);
                    albedo = lerp(albedo, _SwarmHeatColors[k].rgb, w * 0.75);
                }
                return albedo;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 color : COLOR;
                float fog : TEXCOORD2;
                float mountain : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color;
                o.fog = ComputeFogFactor(p.positionCS.z);
                o.mountain = v.uv.x;
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
                if (i.mountain > 0.5 && _SwarmFade.w > 0.001)
                {
                    float d = distance(i.positionWS.xz, _SwarmFade.xz);
                    fade = _SwarmFade.w * (1.0 - smoothstep(_SwarmFadeRadius * 0.55, _SwarmFadeRadius, d));
                    clip(Bayer4(i.positionCS.xy) - fade * 0.8);
                }

                float3 n = normalize(i.normalWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half3 albedo = i.color.rgb;
                half snow = saturate(_SwarmSnow) * saturate(n.y * 2.0 - 1.0) * i.color.a;
                albedo = lerp(albedo, half3(0.86, 0.88, 0.92), snow);
                if (i.mountain < 0.5) albedo = ApplyHeat(albedo, i.positionWS);
                half ndl = saturate(dot(n, light.direction));
                half3 ambient = max(SampleSH(n), half3(0.24, 0.25, 0.27));
                half3 c = albedo * (light.color * ndl * light.shadowAttenuation + ambient);
                c = lerp(c, c * 0.6 + half3(0.08, 0.1, 0.12), fade); // 虚化区域稍微发灰
                c = MixFog(c, i.fog);
                return half4(c, 1);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
    }
    FallBack Off
}
