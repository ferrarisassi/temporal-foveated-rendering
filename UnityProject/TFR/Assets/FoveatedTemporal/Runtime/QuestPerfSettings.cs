using UnityEngine;

namespace FoveatedTemporal
{
    /// <summary>
    /// Fixa as condições do aparelho para que só a foveação temporal varie entre as condições:
    ///  - desliga o Fixed Foveated Rendering espacial da Meta (senão mistura as duas técnicas);
    ///  - fixa a frequência do display;
    ///  - pede níveis de CPU/GPU sustentados (reduz a variação de clock entre condições).
    /// Requer o Meta XR Core SDK (define META_XR_CORE criado pelo .asmdef). Sem ele, só avisa.
    /// </summary>
    public class QuestPerfSettings : MonoBehaviour
    {
        [Tooltip("72, 80, 90 ou 120 no Quest 2.")]
        public float displayFrequency = 90f;
        public bool disableFixedFoveatedRendering = true;
        public bool sustainedHighClocks = true;

        void Start()
        {
#if META_XR_CORE
            try
            {
                if (disableFixedFoveatedRendering)
                {
                    OVRManager.useDynamicFoveatedRendering = false;
                    OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.Off;
                }
                if (sustainedHighClocks)
                {
                    OVRManager.suggestedCpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
                    OVRManager.suggestedGpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
                }
                if (displayFrequency > 0f && OVRManager.display != null)
                    OVRManager.display.displayFrequency = displayFrequency;
                Debug.Log($"[FTR] Quest: FFR={OVRManager.foveatedRenderingLevel}, freq={displayFrequency} Hz");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[FTR] Não foi possível aplicar as configurações do Quest: " + e.Message);
            }
#else
            Debug.LogWarning("[FTR] Meta XR Core SDK não encontrado: FFR, frequência e clocks não foram fixados. " +
                             "Desligue o Foveated Rendering nas configurações do OpenXR / Meta Quest manualmente.");
#endif
        }
    }
}
