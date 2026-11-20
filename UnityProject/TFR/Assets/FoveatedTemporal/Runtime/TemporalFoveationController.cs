using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace FoveatedTemporal
{
    public enum FoveationMode
    {
        Baseline,      // efeito desligado: referência de custo
        OverheadOnly,  // efeito ligado, mas tudo a taxa máxima + cópias de histórico: mede o custo fixo da técnica
        Fixed,         // divisores fixos (ex.: periferia a 1/2 da taxa, como no TFR da Meta)
        Adaptive       // divisores dependem da velocidade do usuário
    }

    /// <summary>
    /// Converte a velocidade do usuário em frame rate por zona e escreve a decisão em
    /// <see cref="TemporalFoveationState"/> a cada quadro.
    ///
    /// Modo Adaptive, por zona:
    ///   hz  = lerp(minHz, displayHz, curva(velocidadeNormalizada))
    ///   N   = round(displayHz / hz)       (a zona é renderizada 1 vez a cada N quadros)
    /// Quando a velocidade sobe, N diminui na hora; quando cai, N só aumenta depois de
    /// <see cref="slowDownHold"/> segundos estável (histerese).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class TemporalFoveationController : MonoBehaviour
    {
        public Camera xrCamera;
        public MotionTracker motion;
        public FoveationMode mode = FoveationMode.Adaptive;

        [Header("Zonas (graus de excentricidade a partir do centro da lente)")]
        [Range(5, 40)] public float foveaDeg = 15f;
        [Range(10, 60)] public float midDeg = 30f;
        [Range(0.5f, 10)] public float bandDeg = 3f;

        [Header("Modo Fixed")]
        [Min(1)] public int fixedDivMid = 2;
        [Min(1)] public int fixedDivFar = 2;

        [Header("Modo Adaptive")]
        [Tooltip("Frame rate mínimo da periferia média quando o usuário está parado.")]
        public float midMinHz = 18f;
        [Tooltip("Frame rate mínimo da periferia distante quando o usuário está parado.")]
        public float farMinHz = 6f;
        [Tooltip("x = velocidade normalizada (0..1), y = fração do caminho entre minHz e a taxa do display.")]
        public AnimationCurve midCurve = AnimationCurve.Linear(0, 0, 1, 1);
        public AnimationCurve farCurve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.5f, 0.35f), new Keyframe(1, 1));
        [Min(1)] public int maxDivisor = 30;
        [Tooltip("Tempo estável antes de reduzir o frame rate (subir é imediato).")]
        public float slowDownHold = 0.3f;

        [Header("Depuração")]
        public bool debugOverlay = false;
        [Tooltip("Inverte Y na reprojeção. Valide com o teste descrito no README.")]
        public bool flipYReprojection = false;
        [Tooltip("Congela a periferia (divisor enorme) para testar a reprojeção girando a cabeça.")]
        public bool freezePeripheryTest = false;

        // ---- Saídas para o log ----
        public float DisplayHz { get; private set; } = 72f;
        public int DivMid { get; private set; } = 1;
        public int DivFar { get; private set; } = 1;
        public float TargetHzMid { get; private set; }
        public float TargetHzFar { get; private set; }
        /// <summary>Fração da imagem (por olho) em cada zona; serve para prever a economia.</summary>
        public float AreaFovea { get; private set; }
        public float AreaMid { get; private set; }
        public float AreaFar { get; private set; }

        float m_HoldMid, m_HoldFar;
        float m_NextHzQuery, m_NextAreaQuery;
        FoveationMode m_LastMode;
        readonly List<XRDisplaySubsystem> m_Displays = new List<XRDisplaySubsystem>();

        void Start()
        {
            if (xrCamera == null) xrCamera = Camera.main;
            if (motion == null) motion = FindFirstObjectByType<MotionTracker>();
            m_LastMode = mode;
            TemporalFoveationState.InvalidateAll();
        }

        public void SetMode(FoveationMode m)
        {
            if (m == mode) return;
            mode = m;
            TemporalFoveationState.InvalidateAll();
            DivMid = DivFar = 1;
        }

        void LateUpdate()
        {
            if (mode != m_LastMode) { TemporalFoveationState.InvalidateAll(); m_LastMode = mode; }

            if (Time.unscaledTime >= m_NextHzQuery) { QueryDisplayHz(); m_NextHzQuery = Time.unscaledTime + 1f; }
            if (Time.unscaledTime >= m_NextAreaQuery) { ComputeZoneAreas(); m_NextAreaQuery = Time.unscaledTime + 2f; }

            int targetMid = 1, targetFar = 1;
            float dt = Time.unscaledDeltaTime;

            switch (mode)
            {
                case FoveationMode.Baseline:
                case FoveationMode.OverheadOnly:
                    targetMid = targetFar = 1;
                    TargetHzMid = TargetHzFar = DisplayHz;
                    DivMid = DivFar = 1;
                    break;

                case FoveationMode.Fixed:
                    DivMid = targetMid = Mathf.Max(1, fixedDivMid);
                    DivFar = targetFar = Mathf.Max(1, fixedDivFar);
                    TargetHzMid = DisplayHz / DivMid;
                    TargetHzFar = DisplayHz / DivFar;
                    break;

                case FoveationMode.Adaptive:
                    float s = motion != null ? motion.NormalizedSpeed : 1f;
                    TargetHzMid = Mathf.Lerp(Mathf.Min(midMinHz, DisplayHz), DisplayHz, Mathf.Clamp01(midCurve.Evaluate(s)));
                    TargetHzFar = Mathf.Lerp(Mathf.Min(farMinHz, DisplayHz), DisplayHz, Mathf.Clamp01(farCurve.Evaluate(s)));
                    targetMid = Mathf.Clamp(Mathf.RoundToInt(DisplayHz / Mathf.Max(0.5f, TargetHzMid)), 1, maxDivisor);
                    targetFar = Mathf.Clamp(Mathf.RoundToInt(DisplayHz / Mathf.Max(0.5f, TargetHzFar)), 1, maxDivisor);
                    // a periferia distante nunca atualiza mais que a média
                    targetFar = Mathf.Max(targetFar, targetMid);
                    DivMid = Hysteresis(DivMid, targetMid, ref m_HoldMid, dt);
                    DivFar = Hysteresis(DivFar, targetFar, ref m_HoldFar, dt);
                    break;
            }

            if (freezePeripheryTest && mode != FoveationMode.Baseline)
            {
                DivMid = DivFar = 100000;
            }

            TemporalFoveationState.FeatureEnabled = mode != FoveationMode.Baseline;
            TemporalFoveationState.ForceCopies = mode == FoveationMode.OverheadOnly;
            TemporalFoveationState.TargetCamera = xrCamera;
            TemporalFoveationState.Divisor[0] = DivMid;
            TemporalFoveationState.Divisor[1] = DivFar;
            TemporalFoveationState.FoveaDeg = foveaDeg;
            TemporalFoveationState.MidDeg = Mathf.Max(midDeg, foveaDeg + bandDeg);
            TemporalFoveationState.BandDeg = bandDeg;
            TemporalFoveationState.DebugOverlay = debugOverlay;
            TemporalFoveationState.FlipY = flipYReprojection;
            TemporalFoveationState.DecideFrame(Time.frameCount);
        }

        int Hysteresis(int current, int target, ref float hold, float dt)
        {
            if (target < current) { hold = 0f; return target; }      // mais rápido: imediato
            if (target > current)
            {
                hold += dt;
                if (hold >= slowDownHold) { hold = 0f; return target; }
                return current;
            }
            hold = 0f;
            return current;
        }

        void QueryDisplayHz()
        {
            m_Displays.Clear();
            SubsystemManager.GetSubsystems(m_Displays);
            foreach (var d in m_Displays)
            {
                if (d.running && d.TryGetDisplayRefreshRate(out float hz) && hz > 1f)
                {
                    DisplayHz = hz;
                    return;
                }
            }
            var rr = Screen.currentResolution.refreshRateRatio.value;
            if (rr > 1.0) DisplayHz = (float)rr;
        }

        /// <summary>Amostra a projeção do olho esquerdo numa grade e mede a fração de pixels de cada zona.</summary>
        void ComputeZoneAreas()
        {
            if (xrCamera == null) return;
            Matrix4x4 p = xrCamera.stereoEnabled
                ? xrCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left)
                : xrCamera.projectionMatrix;
            Matrix4x4 inv = p.inverse;
            const int N = 64;
            int f = 0, m = 0, d = 0;
            float e1 = foveaDeg, e2 = Mathf.Max(midDeg, foveaDeg + bandDeg);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Vector4 ndc = new Vector4((x + 0.5f) / N * 2f - 1f, (y + 0.5f) / N * 2f - 1f, 0f, 1f);
                    Vector4 v = inv * ndc;
                    Vector3 dir = new Vector3(v.x / v.w, v.y / v.w, v.z / v.w).normalized;
                    float ecc = Mathf.Acos(Mathf.Clamp01(-dir.z)) * Mathf.Rad2Deg;
                    if (ecc < e1) f++; else if (ecc < e2) m++; else d++;
                }
            float total = N * N;
            AreaFovea = f / total; AreaMid = m / total; AreaFar = d / total;
        }

        /// <summary>Fração prevista de pixels não sombreados neste quadro (ignora a faixa de transição).</summary>
        public float PredictedSkipFraction =>
            (TemporalFoveationState.Stale[0] ? AreaMid : 0f) + (TemporalFoveationState.Stale[1] ? AreaFar : 0f);
    }
}
