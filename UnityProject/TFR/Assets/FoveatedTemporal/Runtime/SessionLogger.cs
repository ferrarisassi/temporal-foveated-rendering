using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace FoveatedTemporal
{
    /// <summary>Contexto atual do experimento (escrito pelo ExperimentRunner, lido pelo logger).</summary>
    public static class ExperimentContext
    {
        public static string Participant = "P00";
        public static string Condition = "-";
        public static string Task = "-";
        public static string Phase = "idle";   // idle | instrucao | aquecimento | medicao | pergunta | pausa
        public static int Block = 0;
    }

    /// <summary>
    /// Grava dois CSVs em Application.persistentDataPath/FTR_Logs:
    ///   frames_*.csv — uma linha por quadro renderizado (métricas + o que o usuário estava fazendo)
    ///   events_*.csv — eventos: mudanças de tarefa/condição/atividade, relatos de artefato, respostas
    /// Para baixar: adb pull /sdcard/Android/data/&lt;pacote&gt;/files/FTR_Logs
    /// </summary>
    [DefaultExecutionOrder(2000)]
    public class SessionLogger : MonoBehaviour
    {
        public TemporalFoveationController controller;
        public MotionTracker motion;
        public bool logEveryFrame = true;

        static SessionLogger s_Instance;

        StreamWriter m_Frames, m_Events;
        readonly StringBuilder m_Sb = new StringBuilder(512);
        readonly PerfProbe m_Probe = new PerfProbe();
        readonly Queue<float> m_UpdMid = new Queue<float>(), m_UpdFar = new Queue<float>();
        float m_NextFlush;
        int m_LastLoggedFrame = -1;
        string m_PendingEvents = "";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string FolderPath { get; private set; }

        const string FrameHeader =
            "time_s,frame,participant,block,condition,task,phase,activity," +
            "ang_dps,ang_raw_dps,lin_mps,hand_mps,speed_norm," +
            "display_hz,div_mid,div_far,target_hz_mid,target_hz_far,fresh_mid,fresh_far,eff_hz_mid,eff_hz_far," +
            "area_fovea,area_mid,area_far,skip_frac_pred," +
            "ftm_cpu_ms,ftm_gpu_ms,xr_app_gpu_raw,xr_dropped_total," +
            "meta_app_gpu_ms,meta_app_cpu_ms,meta_gpu_util,meta_cpu_util,meta_gpu_mhz,meta_cpu_mhz," +
            "battery_temp_c,battery_current_ma,battery_voltage_v,power_w,backbuffer_warning,events";

        void Awake()
        {
            s_Instance = this;
            if (controller == null) controller = FindFirstObjectByType<TemporalFoveationController>();
            if (motion == null) motion = FindFirstObjectByType<MotionTracker>();
        }

        public void BeginSession(string participant)
        {
            EndSession();
            FolderPath = Path.Combine(Application.persistentDataPath, "FTR_Logs");
            Directory.CreateDirectory(FolderPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
            m_Frames = new StreamWriter(Path.Combine(FolderPath, $"frames_{participant}_{stamp}.csv"), false, new UTF8Encoding(false));
            m_Events = new StreamWriter(Path.Combine(FolderPath, $"events_{participant}_{stamp}.csv"), false, new UTF8Encoding(false));
            m_Frames.WriteLine(FrameHeader);
            m_Events.WriteLine("time_s,frame,participant,block,condition,task,phase,type,detail");
            LogEvent("sessao_inicio", $"device={SystemInfo.deviceModel};unity={Application.unityVersion};gfx={SystemInfo.graphicsDeviceType}");
            Debug.Log("[FTR] Log em " + FolderPath);
        }

        public void EndSession()
        {
            if (m_Frames == null) return;
            LogEvent("sessao_fim", "");
            m_Frames.Flush(); m_Frames.Dispose(); m_Frames = null;
            m_Events.Flush(); m_Events.Dispose(); m_Events = null;
        }

        /// <summary>Registra um evento (também aparece na coluna "events" da linha do quadro).</summary>
        public static void LogEvent(string type, string detail)
        {
            var s = s_Instance;
            if (s == null || s.m_Events == null) return;
            string clean = (detail ?? "").Replace(',', ';').Replace('\n', ' ');
            s.m_Events.WriteLine(string.Join(",",
                Time.unscaledTime.ToString("F4", Inv), Time.frameCount.ToString(Inv),
                ExperimentContext.Participant, ExperimentContext.Block.ToString(Inv), ExperimentContext.Condition,
                ExperimentContext.Task, ExperimentContext.Phase, type, clean));
            s.m_PendingEvents += (s.m_PendingEvents.Length > 0 ? "|" : "") + type;
        }

        void OnEnable()
        {
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            if (motion != null) motion.ActivityChanged += OnActivityChanged;
        }

        void OnDisable()
        {
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
            if (motion != null) motion.ActivityChanged -= OnActivityChanged;
        }

        void OnApplicationPause(bool paused) { if (paused) Flush(); }
        void OnApplicationQuit() { EndSession(); }
        void OnDestroy() { EndSession(); }

        void OnActivityChanged(Activity from, Activity to)
        {
            LogEvent("atividade", MotionTracker.Label(from) + "->" + MotionTracker.Label(to));
        }

        void Flush()
        {
            m_Frames?.Flush();
            m_Events?.Flush();
        }

        static string F(float v, string fmt = "F3") => float.IsNaN(v) ? "" : v.ToString(fmt, Inv);

        static float RollingHz(Queue<float> q, bool fresh, float now)
        {
            if (fresh) q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > 1f) q.Dequeue();
            return q.Count;
        }

        // Chamado depois que todas as câmeras do quadro foram gravadas: a decisão do quadro já é final.
        void OnEndContextRendering(ScriptableRenderContext ctx, List<Camera> cams)
        {
            if (m_Frames == null || !logEveryFrame) return;
            int frame = Time.frameCount;
            if (frame == m_LastLoggedFrame) return;
            m_LastLoggedFrame = frame;

            m_Probe.Sample();

            float now = Time.unscaledTime;
            bool enabled = TemporalFoveationState.FeatureEnabled;
            bool freshMid = !enabled || !TemporalFoveationState.Stale[0];
            bool freshFar = !enabled || !TemporalFoveationState.Stale[1];
            float effMid = RollingHz(m_UpdMid, freshMid, now);
            float effFar = RollingHz(m_UpdFar, freshFar, now);

            var c = controller;
            var m = motion;
            var sb = m_Sb;
            sb.Clear();
            sb.Append(now.ToString("F4", Inv)).Append(',')
              .Append(frame.ToString(Inv)).Append(',')
              .Append(ExperimentContext.Participant).Append(',')
              .Append(ExperimentContext.Block.ToString(Inv)).Append(',')
              .Append(ExperimentContext.Condition).Append(',')
              .Append(ExperimentContext.Task).Append(',')
              .Append(ExperimentContext.Phase).Append(',')
              .Append(m != null ? MotionTracker.Label(m.CurrentActivity) : "").Append(',')
              .Append(m != null ? F(m.AngularDps, "F2") : "").Append(',')
              .Append(m != null ? F(m.RawAngularDps, "F2") : "").Append(',')
              .Append(m != null ? F(m.LinearMps) : "").Append(',')
              .Append(m != null ? F(m.HandMps) : "").Append(',')
              .Append(m != null ? F(m.NormalizedSpeed) : "").Append(',')
              .Append(c != null ? F(c.DisplayHz, "F1") : "").Append(',')
              .Append(c != null ? c.DivMid.ToString(Inv) : "").Append(',')
              .Append(c != null ? c.DivFar.ToString(Inv) : "").Append(',')
              .Append(c != null ? F(c.TargetHzMid, "F2") : "").Append(',')
              .Append(c != null ? F(c.TargetHzFar, "F2") : "").Append(',')
              .Append(freshMid ? '1' : '0').Append(',')
              .Append(freshFar ? '1' : '0').Append(',')
              .Append(F(effMid, "F0")).Append(',')
              .Append(F(effFar, "F0")).Append(',')
              .Append(c != null ? F(c.AreaFovea, "F4") : "").Append(',')
              .Append(c != null ? F(c.AreaMid, "F4") : "").Append(',')
              .Append(c != null ? F(c.AreaFar, "F4") : "").Append(',')
              .Append(c != null && enabled ? F(c.PredictedSkipFraction, "F4") : "0").Append(',')
              .Append(F(m_Probe.FtmCpuMs)).Append(',')
              .Append(F(m_Probe.FtmGpuMs)).Append(',')
              .Append(F(m_Probe.XrAppGpuRaw, "F6")).Append(',')
              .Append(m_Probe.XrDroppedFrames >= 0 ? m_Probe.XrDroppedFrames.ToString(Inv) : "").Append(',')
              .Append(F(m_Probe.MetaAppGpuMs)).Append(',')
              .Append(F(m_Probe.MetaAppCpuMs)).Append(',')
              .Append(F(m_Probe.MetaGpuUtil, "F1")).Append(',')
              .Append(F(m_Probe.MetaCpuUtil, "F1")).Append(',')
              .Append(F(m_Probe.MetaGpuMHz, "F0")).Append(',')
              .Append(F(m_Probe.MetaCpuMHz, "F0")).Append(',')
              .Append(F(m_Probe.BatteryTempC, "F1")).Append(',')
              .Append(F(m_Probe.BatteryCurrentMa, "F0")).Append(',')
              .Append(F(m_Probe.BatteryVoltageV, "F3")).Append(',')
              .Append(F(m_Probe.PowerW, "F3")).Append(',')
              .Append(TemporalFoveationState.BackbufferWarning ? '1' : '0').Append(',')
              .Append(m_PendingEvents);
            m_Frames.WriteLine(sb.ToString());
            m_PendingEvents = "";

            if (now >= m_NextFlush) { m_NextFlush = now + 1f; Flush(); }
        }
    }
}
