using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace FoveatedTemporal
{
    /// <summary>
    /// Coleta métricas de desempenho de várias fontes, para uma confirmar a outra:
    ///  - FrameTimingManager (Unity): CPU e GPU por quadro. Exige "Frame Timing Stats" nas Player Settings.
    ///  - XRDisplaySubsystem: tempo de GPU do app e quadros perdidos, informados pelo runtime XR.
    ///  - Meta XR Core SDK (opcional, símbolo FTR_META_PERF): GPU/CPU do app, utilização e clocks.
    ///  - Bateria Android: corrente, tensão e temperatura -> potência aproximada (W). Desconecte o cabo!
    /// Valores indisponíveis ficam NaN (célula vazia no CSV).
    /// </summary>
    public class PerfProbe
    {
        public float FtmCpuMs = float.NaN, FtmGpuMs = float.NaN;
        public float XrAppGpuRaw = float.NaN;   // unidade depende do runtime (s ou ms); a análise detecta
        public int XrDroppedFrames = -1;        // contador acumulado
        public float MetaAppGpuMs = float.NaN, MetaAppCpuMs = float.NaN;
        public float MetaGpuUtil = float.NaN, MetaCpuUtil = float.NaN;
        public float MetaGpuMHz = float.NaN, MetaCpuMHz = float.NaN;
        public float BatteryTempC = float.NaN, BatteryCurrentMa = float.NaN, BatteryVoltageV = float.NaN, PowerW = float.NaN;

        readonly FrameTiming[] m_Timings = new FrameTiming[1];
        readonly List<XRDisplaySubsystem> m_Displays = new List<XRDisplaySubsystem>();
        float m_NextBattery;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Activity, m_BatteryManager, m_Filter;
#endif

        public void Sample()
        {
            // ---- Unity FrameTimingManager ----
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0)
            {
                FtmCpuMs = (float)m_Timings[0].cpuFrameTime;
                FtmGpuMs = (float)m_Timings[0].gpuFrameTime;
            }

            // ---- XR display subsystem ----
            m_Displays.Clear();
            SubsystemManager.GetSubsystems(m_Displays);
            XrAppGpuRaw = float.NaN;
            foreach (var d in m_Displays)
            {
                if (!d.running) continue;
                if (d.TryGetAppGPUTimeLastFrame(out float g)) XrAppGpuRaw = g;
                if (d.TryGetDroppedFrameCount(out int dropped)) XrDroppedFrames = dropped;
                break;
            }

            // ---- Meta XR Core SDK (opcional) ----
            // Ative definindo o símbolo FTR_META_PERF em Player Settings > Scripting Define Symbols.
            // Se não compilar na sua versão do SDK, remova o símbolo: as outras fontes continuam funcionando,
            // e o OVR Metrics Tool fornece as mesmas métricas por fora.
#if META_XR_CORE && FTR_META_PERF
            try
            {
                float? v;
                v = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.App_GpuTime_Float);
                MetaAppGpuMs = v.HasValue ? v.Value * 1000f : float.NaN;
                v = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.App_CpuTime_Float);
                MetaAppCpuMs = v.HasValue ? v.Value * 1000f : float.NaN;
                v = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.System_GpuUtilPercentage_Float);
                MetaGpuUtil = v.HasValue ? v.Value : float.NaN;
                v = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.System_CpuUtilAveragePercentage_Float);
                MetaCpuUtil = v.HasValue ? v.Value : float.NaN;
                v = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.Device_GpuClockFrequencyInMHz_Float);
                MetaGpuMHz = v.HasValue ? v.Value : float.NaN;
                v = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.Device_CpuClockFrequencyInMHz_Float);
                MetaCpuMHz = v.HasValue ? v.Value : float.NaN;
            }
            catch (System.Exception) { /* métrica não suportada neste runtime */ }
#endif

            // ---- Bateria (a cada 0,5 s; chamadas JNI são caras) ----
            if (Time.unscaledTime >= m_NextBattery)
            {
                m_NextBattery = Time.unscaledTime + 0.5f;
                SampleBattery();
            }
        }

        void SampleBattery()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (m_Activity == null)
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                        m_Activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    m_BatteryManager = m_Activity.Call<AndroidJavaObject>("getSystemService", "batterymanager");
                    m_Filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED");
                }
                const int BATTERY_PROPERTY_CURRENT_NOW = 2; // microampères
                int currentUa = m_BatteryManager.Call<int>("getIntProperty", BATTERY_PROPERTY_CURRENT_NOW);
                using (var intent = m_Activity.Call<AndroidJavaObject>("registerReceiver", null, m_Filter))
                {
                    if (intent != null)
                    {
                        BatteryVoltageV = intent.Call<int>("getIntExtra", "voltage", 0) / 1000f;
                        BatteryTempC = intent.Call<int>("getIntExtra", "temperature", 0) / 10f;
                    }
                }
                BatteryCurrentMa = Mathf.Abs(currentUa) / 1000f;
                PowerW = BatteryCurrentMa / 1000f * BatteryVoltageV;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[FTR] Falha ao ler a bateria: " + e.Message);
                m_NextBattery = Time.unscaledTime + 10f;
            }
#endif
        }
    }
}
