using UnityEngine;

namespace FoveatedTemporal
{
    /// <summary>
    /// Gera uma sala de painéis ao redor do usuário com o material de carga (HeavyProcedural).
    /// Use <see cref="iterations"/> para calibrar o custo de GPU: no Baseline, a GPU deve ficar
    /// ocupada (~70–90% do orçamento do quadro), senão não há o que economizar.
    /// Orçamento: 72 Hz = 13,9 ms · 90 Hz = 11,1 ms · 120 Hz = 8,3 ms.
    /// </summary>
    public class LoadEnvironment : MonoBehaviour
    {
        public Material heavyMaterial;
        [Range(1, 256)] public int iterations = 48;
        public float radius = 4f;
        public int columns = 24;
        public int rows = 6;
        public float panelHeight = 0.9f;
        public bool buildFloorAndCeiling = true;

        Material m_Instance;
        int m_LastIterations = -1;

        void Start()
        {
            if (heavyMaterial == null)
            {
                Debug.LogError("[FTR] LoadEnvironment sem material. Use Tools > Foveated Temporal > Criar sistema na cena.");
                return;
            }
            m_Instance = new Material(heavyMaterial) { enableInstancing = true };
            Build();
            Apply();
        }

        void Update()
        {
            if (m_Instance != null && iterations != m_LastIterations) Apply();
        }

        void Apply()
        {
            m_Instance.SetFloat("_Iterations", iterations);
            m_LastIterations = iterations;
        }

        void Build()
        {
            float circumference = 2f * Mathf.PI * radius;
            float width = circumference / columns * 1.02f;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    float ang = c * 360f / columns;
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(go.GetComponent<Collider>());
                    go.name = $"Panel_{r}_{c}";
                    go.transform.SetParent(transform, false);
                    Quaternion q = Quaternion.Euler(0f, ang, 0f);
                    go.transform.localPosition = q * Vector3.forward * radius + Vector3.up * (r * panelHeight);
                    go.transform.localRotation = q;
                    go.transform.localScale = new Vector3(width, panelHeight * 0.98f, 0.05f);
                    go.GetComponent<MeshRenderer>().sharedMaterial = m_Instance;
                }
            }
            if (buildFloorAndCeiling)
            {
                MakeSlab("Floor", -0.05f);
                MakeSlab("Ceiling", rows * panelHeight);
            }
        }

        void MakeSlab(string name, float y)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * y;
            go.transform.localScale = new Vector3(radius * 2f, 0.02f, radius * 2f);
            go.GetComponent<MeshRenderer>().sharedMaterial = m_Instance;
        }

        void OnDestroy()
        {
            if (m_Instance != null) Destroy(m_Instance);
        }
    }
}
