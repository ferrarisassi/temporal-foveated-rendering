// Shader de carga controlada para os testes de desempenho.
// O custo por pixel cresce com _Iterations: deixa a cena limitada por fragment shading,
// que é o custo que a foveação temporal economiza. O padrão é estático (não depende do tempo),
// então só o movimento da cabeça muda o que aparece na periferia.
Shader "FoveatedTemporal/HeavyProcedural"
{
    Properties
    {
        _Iterations ("Iterations (custo por pixel)", Range(1, 256)) = 48
        _Scale ("Scale", Float) = 6
        _ColorA ("Color A", Color) = (0.95, 0.55, 0.20, 1)
        _ColorB ("Color B", Color) = (0.10, 0.20, 0.45, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Iterations;
                float _Scale;
                float4 _ColorA;
                float4 _ColorB;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv * _Scale + i.positionWS.xz * 0.37;
                float acc = 0.0;
                int n = (int)_Iterations;
                [loop]
                for (int k = 0; k < n; k++)
                {
                    p = float2(p.x * 1.013 + sin(p.y + k * 0.37), p.y * 0.987 + cos(p.x - k * 0.21));
                    acc += sin(p.x * p.y * 0.1);
                }
                float t = saturate(0.5 + 0.5 * sin(acc * 0.15));
                // bordas de alto contraste deixam artefatos (judder) fáceis de notar
                float checker = step(0.5, frac(i.uv.x * 8.0)) == step(0.5, frac(i.uv.y * 8.0)) ? 1.0 : 0.6;
                return half4(lerp(_ColorA.rgb, _ColorB.rgb, t) * checker, 1.0);
            }
            ENDHLSL
        }
    }
}
