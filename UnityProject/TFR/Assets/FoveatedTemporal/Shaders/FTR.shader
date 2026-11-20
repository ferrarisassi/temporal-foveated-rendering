// Temporal Foveated Rendering — passes de tela cheia (URP, XR Single Pass Instanced / Multiview).
// Pass 0: Mask      — escreve profundidade no plano near onde a zona está "stale" (early-Z rejeita a cena).
// Pass 1: Composite — preenche as zonas stale com o histórico reprojetado (rotação da cabeça).
// Pass 2: Debug     — pinta as zonas (verde = fóvea, azul/ciano = atualizada, amarelo/vermelho = stale).
Shader "Hidden/FoveatedTemporal/FTR"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        float4x4 _FTR_InvProj[2];   // inversa da projeção, por olho
        float4x4 _FTR_Reproj[4];    // [zona * 2 + olho]: NDC atual -> clip no quadro em que a zona foi gravada
        float4 _FTR_Zones;          // x = raio da fóvea (graus), y = início da periferia distante, z = faixa de transição, w = flipY
        float4 _FTR_Stale;          // x = periferia média stale (0/1), y = periferia distante stale (0/1)

        TEXTURE2D_X(_FTR_History0);
        TEXTURE2D_X(_FTR_History1);
        SAMPLER(sampler_ftr_linear_clamp);

        struct Attributes
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings VertFTR(Attributes input)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            // z = UNITY_NEAR_CLIP_VALUE por padrão: usado pelo pass de máscara
            o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
            o.uv = GetFullScreenTriangleTexCoord(input.vertexID);
            return o;
        }

        uint FTR_Eye()
        {
        #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
            return unity_StereoEyeIndex;
        #else
            return 0;
        #endif
        }

        float2 UvToNdc(float2 uv)
        {
            float2 n = uv * 2.0 - 1.0;
            if (_FTR_Zones.w > 0.5) n.y = -n.y;
            return n;
        }

        float2 NdcToUv(float2 n)
        {
            if (_FTR_Zones.w > 0.5) n.y = -n.y;
            return n * 0.5 + 0.5;
        }

        // Excentricidade (graus) do pixel em relação ao eixo óptico do olho.
        // Usa a projeção real do olho: o centro da lente do Quest não fica no centro da imagem.
        float EccentricityDeg(float2 uv, uint eye)
        {
            float4 v = mul(_FTR_InvProj[eye], float4(UvToNdc(uv), 0.0, 1.0));
            float3 d = normalize(v.xyz / v.w);
            return degrees(acos(saturate(-d.z))); // a câmera olha para -Z no espaço de view
        }

        void ZoneWeights(float ecc, out float wFovea, out float wMid, out float wFar)
        {
            float t1 = smoothstep(_FTR_Zones.x, _FTR_Zones.x + _FTR_Zones.z, ecc);
            float t2 = smoothstep(_FTR_Zones.y, _FTR_Zones.y + _FTR_Zones.z, ecc);
            wFovea = 1.0 - t1;
            wMid = t1 * (1.0 - t2);
            wFar = t1 * t2;
        }

        float2 ReprojectUV(float2 uv, uint index)
        {
            float4 c = mul(_FTR_Reproj[index], float4(UvToNdc(uv), 0.0, 1.0));
            return NdcToUv(c.xy / max(c.w, 1e-5));
        }
        ENDHLSL

        // ---------------------------------------------------------------- Pass 0: Mask
        Pass
        {
            Name "FTR Mask"
            ZWrite On
            ZTest Always
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex VertFTR
            #pragma fragment FragMask

            half4 FragMask(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                uint eye = FTR_Eye();
                float wF, wM, wD;
                ZoneWeights(EccentricityDeg(i.uv, eye), wF, wM, wD);
                // Quanto deste pixel precisa de conteúdo novo neste quadro?
                float fresh = wF + wM * (1.0 - _FTR_Stale.x) + wD * (1.0 - _FTR_Stale.y);
                if (fresh > 0.001) discard;   // precisa ser renderizado: não mascara
                return 0;                      // mascara: profundidade near -> early-Z rejeita a cena
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------- Pass 1: Composite
        Pass
        {
            Name "FTR Composite"
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex VertFTR
            #pragma fragment FragComposite

            half4 FragComposite(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                uint eye = FTR_Eye();
                float wF, wM, wD;
                ZoneWeights(EccentricityDeg(i.uv, eye), wF, wM, wD);

                float aM = wM * _FTR_Stale.x;
                float aD = wD * _FTR_Stale.y;
                float alpha = aM + aD;
                if (alpha <= 0.001) discard;

                float3 col = 0;
                if (aM > 0.0)
                    col += aM * SAMPLE_TEXTURE2D_X(_FTR_History0, sampler_ftr_linear_clamp, ReprojectUV(i.uv, 0 * 2 + eye)).rgb;
                if (aD > 0.0)
                    col += aD * SAMPLE_TEXTURE2D_X(_FTR_History1, sampler_ftr_linear_clamp, ReprojectUV(i.uv, 1 * 2 + eye)).rgb;

                return half4(col / alpha, alpha);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------- Pass 2: Debug overlay
        Pass
        {
            Name "FTR Debug"
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex VertFTR
            #pragma fragment FragDebug

            half4 FragDebug(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                uint eye = FTR_Eye();
                float wF, wM, wD;
                ZoneWeights(EccentricityDeg(i.uv, eye), wF, wM, wD);

                float3 cF = float3(0.1, 1.0, 0.1);
                float3 cM = _FTR_Stale.x > 0.5 ? float3(1.0, 0.9, 0.0) : float3(0.1, 0.4, 1.0);
                float3 cD = _FTR_Stale.y > 0.5 ? float3(1.0, 0.1, 0.1) : float3(0.0, 0.9, 0.9);
                return half4(wF * cF + wM * cM + wD * cD, 0.25);
            }
            ENDHLSL
        }
    }
}
