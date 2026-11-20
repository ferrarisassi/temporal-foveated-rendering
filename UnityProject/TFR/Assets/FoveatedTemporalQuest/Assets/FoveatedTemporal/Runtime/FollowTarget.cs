using UnityEngine;

namespace FoveatedTemporal
{
    /// <summary>
    /// Esfera-alvo que o participante acompanha com a cabeça. Torna a velocidade da cabeça
    /// reproduzível entre condições e participantes.
    /// Movimento: yaw(t) = A · sen(2π f t), com f escolhido para atingir a velocidade de pico desejada.
    /// </summary>
    public class FollowTarget : MonoBehaviour
    {
        public Transform origin;          // geralmente a posição inicial da cabeça
        public float distance = 2.5f;
        public float height = 0f;         // relativo à cabeça

        float m_AmplitudeDeg, m_PeakDps, m_T;
        float m_BaseYaw;
        bool m_Moving;

        public void SetStatic(float baseYawDeg)
        {
            m_Moving = false; m_BaseYaw = baseYawDeg; m_T = 0f;
            Place(0f);
        }

        /// <summary>Oscila ±amplitude em torno de baseYaw com velocidade angular de pico peakDps.</summary>
        public void SetSweep(float baseYawDeg, float amplitudeDeg, float peakDps)
        {
            m_Moving = true; m_BaseYaw = baseYawDeg; m_AmplitudeDeg = amplitudeDeg; m_PeakDps = peakDps; m_T = 0f;
        }

        void Update()
        {
            if (!m_Moving) { Place(0f); return; }
            m_T += Time.deltaTime;
            float f = m_PeakDps / (2f * Mathf.PI * Mathf.Max(1f, m_AmplitudeDeg));
            Place(m_AmplitudeDeg * Mathf.Sin(2f * Mathf.PI * f * m_T));
        }

        void Place(float yawOffsetDeg)
        {
            if (origin == null) return;
            Quaternion q = Quaternion.Euler(0f, m_BaseYaw + yawOffsetDeg, 0f);
            transform.position = origin.position + q * Vector3.forward * distance + Vector3.up * height;
        }
    }
}
