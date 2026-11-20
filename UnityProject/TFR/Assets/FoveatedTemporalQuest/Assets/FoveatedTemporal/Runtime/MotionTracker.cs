using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace FoveatedTemporal
{
    /// <summary>O que o usuário está fazendo (anotado automaticamente em cada quadro do log).</summary>
    public enum Activity
    {
        Parado,          // cabeça praticamente imóvel
        OlhandoDevagar,  // rotação lenta da cabeça
        GirandoCabeca,   // rotação moderada/rápida
        Andando,         // deslocamento do corpo
        MovimentoRapido  // rotação muito rápida ou deslocamento rápido
    }

    /// <summary>
    /// Mede a velocidade do usuário (rotação e translação da cabeça e, opcionalmente, das mãos),
    /// suaviza o sinal e classifica a atividade.
    ///
    /// A suavização é assimétrica: sobe rápido (attack) e desce devagar (release).
    /// Assim, quando o usuário começa a se mover, o frame rate da periferia sobe na hora;
    /// quando para, ele cai aos poucos.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class MotionTracker : MonoBehaviour
    {
        [Tooltip("Transform da câmera XR (cabeça).")]
        public Transform head;

        [Header("Suavização (segundos)")]
        public float attackTime = 0.03f;
        public float releaseTime = 0.40f;

        [Header("Normalização: velocidade que vale 1.0 (taxa máxima)")]
        public float angularFullDps = 120f;
        public float linearFullMps = 1.2f;
        public float handFullMps = 2.0f;
        [Tooltip("Inclui a velocidade dos controles no fator de velocidade.")]
        public bool includeHands = false;
        [Tooltip("Abaixo disso a rotação é considerada ruído de tracking.")]
        public float angularDeadZoneDps = 2f;
        public float linearDeadZoneMps = 0.02f;

        [Header("Limiares de atividade")]
        public float stillMaxDps = 4f;
        public float stillMaxMps = 0.05f;
        public float slowLookMaxDps = 35f;
        public float walkingMinMps = 0.35f;
        public float fastMinDps = 120f;
        public float fastMinMps = 1.2f;
        [Tooltip("Tempo mínimo numa nova atividade antes de trocar o rótulo (evita oscilação).")]
        public float activityDwell = 0.25f;

        // ---- Saídas ----
        public float RawAngularDps { get; private set; }
        public float RawLinearMps { get; private set; }
        public float AngularDps { get; private set; }
        public float LinearMps { get; private set; }
        public float HandMps { get; private set; }
        /// <summary>0 = parado, 1 = rápido (ou acima). Usado pelo controller para escolher o frame rate.</summary>
        public float NormalizedSpeed { get; private set; }
        public Activity CurrentActivity { get; private set; } = Activity.Parado;

        public event System.Action<Activity, Activity> ActivityChanged;

        Quaternion m_PrevRot;
        Vector3 m_PrevPos;
        bool m_HasPrev;
        Activity m_Candidate;
        float m_CandidateTime;
        readonly List<InputDevice> m_Devices = new List<InputDevice>();

        void OnEnable()
        {
            m_HasPrev = false;
        }

        void Update()
        {
            if (head == null)
            {
                if (Camera.main != null) head = Camera.main.transform;
                else return;
            }

            float dt = Time.unscaledDeltaTime;
            if (dt <= 1e-5f) return;

            Quaternion rot = head.rotation;
            Vector3 pos = head.position;

            if (!m_HasPrev)
            {
                m_PrevRot = rot; m_PrevPos = pos; m_HasPrev = true;
                return;
            }

            RawAngularDps = Quaternion.Angle(m_PrevRot, rot) / dt;
            Vector3 d = pos - m_PrevPos;
            d.y *= 0.5f; // movimentos verticais (agachar) pesam menos
            RawLinearMps = d.magnitude / dt;
            m_PrevRot = rot; m_PrevPos = pos;

            float ang = RawAngularDps < angularDeadZoneDps ? 0f : RawAngularDps;
            float lin = RawLinearMps < linearDeadZoneMps ? 0f : RawLinearMps;
            AngularDps = Smooth(AngularDps, ang, dt);
            LinearMps = Smooth(LinearMps, lin, dt);

            float hand = includeHands ? ReadHandSpeed() : 0f;
            HandMps = Smooth(HandMps, hand, dt);

            float s = Mathf.Max(AngularDps / angularFullDps, LinearMps / linearFullMps);
            if (includeHands) s = Mathf.Max(s, HandMps / handFullMps);
            NormalizedSpeed = Mathf.Clamp01(s);

            UpdateActivity(Time.unscaledDeltaTime);
        }

        float Smooth(float current, float target, float dt)
        {
            float tau = target > current ? attackTime : releaseTime;
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, tau));
            return current + (target - current) * a;
        }

        float ReadHandSpeed()
        {
            float best = 0f;
            m_Devices.Clear();
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, m_Devices);
            foreach (var dev in m_Devices)
                if (dev.TryGetFeatureValue(CommonUsages.deviceVelocity, out Vector3 v))
                    best = Mathf.Max(best, v.magnitude);
            return best;
        }

        Activity Classify()
        {
            if (AngularDps >= fastMinDps || LinearMps >= fastMinMps) return Activity.MovimentoRapido;
            if (LinearMps >= walkingMinMps) return Activity.Andando;
            if (AngularDps < stillMaxDps && LinearMps < stillMaxMps) return Activity.Parado;
            if (AngularDps < slowLookMaxDps) return Activity.OlhandoDevagar;
            return Activity.GirandoCabeca;
        }

        void UpdateActivity(float dt)
        {
            Activity a = Classify();
            if (a == CurrentActivity) { m_CandidateTime = 0f; return; }
            if (a != m_Candidate) { m_Candidate = a; m_CandidateTime = 0f; }
            m_CandidateTime += dt;
            if (m_CandidateTime >= activityDwell)
            {
                var old = CurrentActivity;
                CurrentActivity = a;
                m_CandidateTime = 0f;
                ActivityChanged?.Invoke(old, a);
            }
        }

        public static string Label(Activity a)
        {
            switch (a)
            {
                case Activity.Parado: return "parado";
                case Activity.OlhandoDevagar: return "olhando_devagar";
                case Activity.GirandoCabeca: return "girando_cabeca";
                case Activity.Andando: return "andando";
                default: return "movimento_rapido";
            }
        }
    }
}
