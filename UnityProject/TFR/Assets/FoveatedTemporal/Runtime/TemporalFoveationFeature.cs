using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace FoveatedTemporal
{
    /// <summary>
    /// Temporal Foveated Rendering para URP (Unity 6, RenderGraph).
    ///
    /// Por quadro:
    ///  1. MaskPass (antes dos opacos): escreve profundidade no plano near nas zonas "stale".
    ///     Toda a geometria seguinte falha no teste de profundidade (early-Z) e não é sombreada.
    ///     É daqui que vem a economia de GPU.
    ///  2. CompositePass (antes do pós-processamento): preenche as zonas stale com o histórico
    ///     da zona, reprojetado pela rotação da cabeça desde a última atualização.
    ///  3. Copy: nas zonas atualizadas neste quadro, copia a imagem para o histórico da zona.
    ///
    /// Requisitos: Render Graph ativo (padrão no Unity 6), XR Single Pass Instanced / Multiview.
    /// Atribua o shader "Hidden/FoveatedTemporal/FTR" no campo Shader (para ele entrar no build).
    /// </summary>
    public class TemporalFoveationFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public class Settings
        {
            [Tooltip("Hidden/FoveatedTemporal/FTR. Precisa estar atribuído para ser incluído no build.")]
            public Shader shader;
            public RenderPassEvent maskEvent = RenderPassEvent.BeforeRenderingOpaques;
            public RenderPassEvent compositeEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public Settings settings = new Settings();

        internal Material Material { get; private set; }

        MaskPass m_MaskPass;
        CompositePass m_CompositePass;

        readonly RTHandle[] m_History = new RTHandle[TemporalFoveationState.ZoneCount];
        internal readonly TextureHandle[] HistoryHandles = new TextureHandle[TemporalFoveationState.ZoneCount];

        // Pose (rotação da view) de cada zona no momento em que o histórico foi gravado: [zona * 2 + olho]
        readonly Matrix4x4[] m_ZoneViewRot = new Matrix4x4[TemporalFoveationState.ZoneCount * 2];
        readonly Matrix4x4[] m_CurViewRot = new Matrix4x4[2];
        readonly Matrix4x4[] m_Proj = new Matrix4x4[2];
        readonly Matrix4x4[] m_InvProj = new Matrix4x4[2];
        readonly Matrix4x4[] m_Reproj = new Matrix4x4[TemporalFoveationState.ZoneCount * 2];

        static readonly int k_InvProj = Shader.PropertyToID("_FTR_InvProj");
        static readonly int k_Reproj = Shader.PropertyToID("_FTR_Reproj");
        static readonly int k_Zones = Shader.PropertyToID("_FTR_Zones");
        static readonly int k_Stale = Shader.PropertyToID("_FTR_Stale");
        static readonly int k_History0 = Shader.PropertyToID("_FTR_History0");
        static readonly int k_History1 = Shader.PropertyToID("_FTR_History1");

        bool m_WarnedBackbuffer;
        int m_PreparedFrame = -1;

        public override void Create()
        {
            if (settings.shader == null)
                settings.shader = Shader.Find("Hidden/FoveatedTemporal/FTR");

            if (Material != null) CoreUtils.Destroy(Material);
            Material = settings.shader != null ? CoreUtils.CreateEngineMaterial(settings.shader) : null;

            m_MaskPass = new MaskPass(this) { renderPassEvent = settings.maskEvent };
            m_CompositePass = new CompositePass(this) { renderPassEvent = settings.compositeEvent };
            // A composição e a cópia leem a cor da câmera: força textura intermediária.
            m_CompositePass.requiresIntermediateTexture = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (Material == null) return;
            if (!TemporalFoveationState.FeatureEnabled) return;
            if (renderingData.cameraData.cameraType != CameraType.Game) return;

            var target = TemporalFoveationState.TargetCamera;
            if (target != null && renderingData.cameraData.camera != target) return;

            // Periferia toda na taxa máxima e sem modo overhead: não insere nada (custo zero, sem textura intermediária).
            if (TemporalFoveationState.Divisor[0] <= 1 && TemporalFoveationState.Divisor[1] <= 1 &&
                !TemporalFoveationState.ForceCopies)
                return;

            renderer.EnqueuePass(m_MaskPass);
            renderer.EnqueuePass(m_CompositePass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(Material);
            Material = null;
            for (int k = 0; k < m_History.Length; k++)
            {
                m_History[k]?.Release();
                m_History[k] = null;
            }
        }

        static Matrix4x4 RotationOnly(Matrix4x4 view)
        {
            view.m03 = 0f; view.m13 = 0f; view.m23 = 0f;
            return view;
        }

        /// <summary>Chamado uma vez por quadro (no MaskPass, o primeiro a ser gravado).</summary>
        internal void PrepareFrame(RenderGraph renderGraph, UniversalCameraData cameraData)
        {
            int frame = Time.frameCount;
            m_PreparedFrame = frame;
            TemporalFoveationState.LastRenderedFrame = frame;

            // Se o controller não decidiu este quadro (ex.: desativado no meio), renderiza tudo.
            if (TemporalFoveationState.DecidedFrame != frame)
            {
                for (int k = 0; k < TemporalFoveationState.ZoneCount; k++)
                {
                    TemporalFoveationState.Stale[k] = false;
                    TemporalFoveationState.Copy[k] = false;
                }
            }

            // ---- Matrizes por olho ----
            int views = (cameraData.xr != null && cameraData.xr.enabled) ? cameraData.xr.viewCount : 1;
            for (int eye = 0; eye < 2; eye++)
            {
                int v = Mathf.Min(eye, views - 1);
                m_CurViewRot[eye] = RotationOnly(cameraData.GetViewMatrix(v));
                m_Proj[eye] = cameraData.GetProjectionMatrix(v);
                m_InvProj[eye] = m_Proj[eye].inverse;
            }

            // ---- Histórico (alocação / reimportação) ----
            var desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            for (int k = 0; k < TemporalFoveationState.ZoneCount; k++)
            {
                bool realloc = RenderingUtils.ReAllocateHandleIfNeeded(ref m_History[k], desc,
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: k == 0 ? "_FTR_HistoryMid" : "_FTR_HistoryFar");
                if (realloc && TemporalFoveationState.Stale[k])
                    TemporalFoveationState.ForceFresh(k);
                HistoryHandles[k] = renderGraph.ImportTexture(m_History[k]);
            }

            // ---- Reprojeção rotacional: NDC atual -> clip no quadro em que a zona foi gravada ----
            for (int k = 0; k < TemporalFoveationState.ZoneCount; k++)
                for (int eye = 0; eye < 2; eye++)
                {
                    int i = k * 2 + eye;
                    m_Reproj[i] = m_Proj[eye] * m_ZoneViewRot[i] * m_CurViewRot[eye].inverse * m_InvProj[eye];
                }

            float band = Mathf.Max(0.05f, TemporalFoveationState.BandDeg);
            float fovea = Mathf.Max(0f, TemporalFoveationState.FoveaDeg);
            float mid = Mathf.Max(fovea + band, TemporalFoveationState.MidDeg);

            Material.SetMatrixArray(k_InvProj, m_InvProj);
            Material.SetMatrixArray(k_Reproj, m_Reproj);
            Material.SetVector(k_Zones, new Vector4(fovea, mid, band, TemporalFoveationState.FlipY ? 1f : 0f));
            Material.SetVector(k_Stale, new Vector4(
                TemporalFoveationState.Stale[0] ? 1f : 0f,
                TemporalFoveationState.Stale[1] ? 1f : 0f, 0f, 0f));
            Material.SetTexture(k_History0, m_History[0]);
            Material.SetTexture(k_History1, m_History[1]);
        }

        internal bool IsPrepared => m_PreparedFrame == Time.frameCount;

        /// <summary>Atualiza o estado do material quando uma zona foi forçada a ser renderizada depois do PrepareFrame.</summary>
        internal void RefreshStaleVector()
        {
            Material.SetVector(k_Stale, new Vector4(
                TemporalFoveationState.Stale[0] ? 1f : 0f,
                TemporalFoveationState.Stale[1] ? 1f : 0f, 0f, 0f));
        }

        /// <summary>O histórico da zona foi gravado com a pose deste quadro.</summary>
        internal void OnHistoryCopied(int zone)
        {
            m_ZoneViewRot[zone * 2 + 0] = m_CurViewRot[0];
            m_ZoneViewRot[zone * 2 + 1] = m_CurViewRot[1];
            TemporalFoveationState.HistoryValid[zone] = true;
        }

        internal void WarnBackbuffer()
        {
            TemporalFoveationState.BackbufferWarning = true;
            if (m_WarnedBackbuffer) return;
            m_WarnedBackbuffer = true;
            Debug.LogWarning("[FTR] A câmera está renderizando direto no backbuffer; o efeito foi desativado neste quadro. " +
                             "Verifique se o Render Graph está ativo (Compatibility Mode desligado).");
        }

        // =====================================================================================
        class MaskPass : ScriptableRenderPass
        {
            readonly TemporalFoveationFeature m_Feature;
            class PassData { public Material material; }

            public MaskPass(TemporalFoveationFeature feature)
            {
                m_Feature = feature;
                profilingSampler = new ProfilingSampler("FTR Mask (early-Z)");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var resources = frameData.Get<UniversalResourceData>();

                m_Feature.PrepareFrame(renderGraph, cameraData);

                if (resources.isActiveTargetBackBuffer)
                {
                    m_Feature.WarnBackbuffer();
                    for (int k = 0; k < TemporalFoveationState.ZoneCount; k++)
                    {
                        TemporalFoveationState.Stale[k] = false;
                        TemporalFoveationState.Copy[k] = false;
                        TemporalFoveationState.HistoryValid[k] = false;
                    }
                    m_Feature.RefreshStaleVector();
                    return;
                }
                TemporalFoveationState.BackbufferWarning = false;

                if (!TemporalFoveationState.AnyStale) return;

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("FTR Mask (early-Z)", out var data, profilingSampler))
                {
                    data.material = m_Feature.Material;
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3, 1);
                    });
                }
            }
        }

        // =====================================================================================
        class CompositePass : ScriptableRenderPass
        {
            readonly TemporalFoveationFeature m_Feature;
            class CompositeData { public Material material; public int pass; }
            class CopyData { public TextureHandle source; }
            static readonly string[] k_CopyNames = { "FTR Copy History (mid)", "FTR Copy History (far)" };

            public CompositePass(TemporalFoveationFeature feature)
            {
                m_Feature = feature;
                profilingSampler = new ProfilingSampler("FTR Composite");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!m_Feature.IsPrepared) return;
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return; // já tratado no MaskPass

                TextureHandle color = resources.activeColorTexture;

                // 1) Composição das zonas stale com o histórico reprojetado
                if (TemporalFoveationState.AnyStale)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("FTR Composite", out var data, profilingSampler))
                    {
                        data.material = m_Feature.Material;
                        data.pass = 1;
                        builder.UseTexture(m_Feature.HistoryHandles[0], AccessFlags.Read);
                        builder.UseTexture(m_Feature.HistoryHandles[1], AccessFlags.Read);
                        builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
                        {
                            ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.pass, MeshTopology.Triangles, 3, 1);
                        });
                    }
                }

                // 2) Cópia para o histórico das zonas atualizadas neste quadro
                for (int k = 0; k < TemporalFoveationState.ZoneCount; k++)
                {
                    if (!TemporalFoveationState.Copy[k]) continue;
                    using (var builder = renderGraph.AddRasterRenderPass<CopyData>(k_CopyNames[k], out var data, profilingSampler))
                    {
                        data.source = color;
                        builder.UseTexture(color, AccessFlags.Read);
                        builder.SetRenderAttachment(m_Feature.HistoryHandles[k], 0, AccessFlags.WriteAll);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc((CopyData d, RasterGraphContext ctx) =>
                        {
                            Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), 0f, false);
                        });
                    }
                    m_Feature.OnHistoryCopied(k);
                }

                // 3) Overlay de depuração (zonas coloridas)
                if (TemporalFoveationState.DebugOverlay)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("FTR Debug Overlay", out var data, profilingSampler))
                    {
                        data.material = m_Feature.Material;
                        data.pass = 2;
                        builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
                        {
                            ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.pass, MeshTopology.Triangles, 3, 1);
                        });
                    }
                }
            }
        }
    }
}
