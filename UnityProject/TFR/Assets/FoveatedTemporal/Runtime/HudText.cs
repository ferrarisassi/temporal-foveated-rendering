using UnityEngine;

namespace FoveatedTemporal
{
    /// <summary>Texto de instruções que segue a cabeça com atraso (fica na região foveal).</summary>
    public class HudText : MonoBehaviour
    {
        public Transform head;
        public float distance = 1.6f;
        public float followSpeed = 4f;
        public float characterSize = 0.012f;

        TextMesh m_Text;

        void Awake()
        {
            m_Text = GetComponent<TextMesh>();
            if (m_Text == null) m_Text = gameObject.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_Text.font = font;
            m_Text.fontSize = 64;
            m_Text.characterSize = characterSize;
            m_Text.anchor = TextAnchor.MiddleCenter;
            m_Text.alignment = TextAlignment.Center;
            m_Text.color = Color.white;
            var mr = GetComponent<MeshRenderer>();
            if (mr != null && font != null) mr.sharedMaterial = font.material;
        }

        public void Show(string text)
        {
            if (m_Text == null) return;
            m_Text.text = text;
            gameObject.SetActive(!string.IsNullOrEmpty(text));
        }

        void LateUpdate()
        {
            if (head == null) { if (Camera.main != null) head = Camera.main.transform; else return; }
            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            Vector3 target = head.position + fwd.normalized * distance;
            float a = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, target, a);
            transform.rotation = Quaternion.LookRotation(transform.position - head.position, Vector3.up);
        }
    }
}
