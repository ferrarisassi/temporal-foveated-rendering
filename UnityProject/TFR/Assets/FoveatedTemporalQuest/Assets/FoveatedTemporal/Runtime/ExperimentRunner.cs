using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FoveatedTemporal
{
    public enum TargetMotion { Static, Sweep, None }

    [System.Serializable]
    public class ExperimentTask
    {
        public string id = "parado";
        [TextArea] public string instruction = "Fique parado olhando para a esfera.";
        public TargetMotion target = TargetMotion.Static;
        public float sweepAmplitudeDeg = 60f;
        public float sweepPeakDps = 20f;
        public float instructionSeconds = 4f;
        public float warmupSeconds = 5f;
        public float measureSeconds = 40f;
    }

    /// <summary>
    /// Roda o protocolo: para cada condição (ordem por quadrado latino balanceado, a partir do número
    /// do participante), executa as tarefas, registra o que o usuário faz e pergunta se ele percebeu artefatos.
    ///
    /// Controles do participante: gatilho direito = "percebi um artefato agora".
    /// Controles do pesquisador: antes de começar, X/Y mudam o número do participante e A inicia;
    /// durante a tarefa, B (segurar com o grip esquerdo) pula a tarefa.
    /// A condição atual NÃO aparece para o participante (estudo cego).
    /// </summary>
    public class ExperimentRunner : MonoBehaviour
    {
        public TemporalFoveationController controller;
        public SessionLogger logger;
        public HudText hud;
        public FollowTarget target;
        public Transform head;

        [Header("Participante")]
        public int participantNumber = 1;
        public bool autoStart = false;

        [Header("Desenho")]
        public List<FoveationMode> conditions = new List<FoveationMode>
        {
            FoveationMode.Baseline, FoveationMode.OverheadOnly, FoveationMode.Fixed, FoveationMode.Adaptive
        };
        [Min(1)] public int repetitions = 2;
        public float restSeconds = 8f;

        public List<ExperimentTask> tasks = new List<ExperimentTask>
        {
            new ExperimentTask { id = "parado", instruction = "Fique parado olhando para a esfera.", target = TargetMotion.Static },
            new ExperimentTask { id = "olhar_devagar", instruction = "Acompanhe a esfera com a cabeça.", target = TargetMotion.Sweep, sweepAmplitudeDeg = 60f, sweepPeakDps = 20f },
            new ExperimentTask { id = "girar_moderado", instruction = "Acompanhe a esfera com a cabeça.", target = TargetMotion.Sweep, sweepAmplitudeDeg = 60f, sweepPeakDps = 70f },
            new ExperimentTask { id = "girar_rapido", instruction = "Acompanhe a esfera com a cabeça.\nEla vai se mover rápido.", target = TargetMotion.Sweep, sweepAmplitudeDeg = 60f, sweepPeakDps = 160f },
            new ExperimentTask { id = "andar", instruction = "Caminhe devagar de um lado para o outro\nna sua área livre, olhando para a frente.", target = TargetMotion.None },
            new ExperimentTask { id = "livre", instruction = "Explore livremente, olhando ao redor.", target = TargetMotion.None, measureSeconds = 30f },
        };

        Transform m_Anchor;
        bool m_Skip;

        public static string ConditionName(FoveationMode m)
        {
            switch (m)
            {
                case FoveationMode.Baseline: return "baseline";
                case FoveationMode.OverheadOnly: return "overhead";
                case FoveationMode.Fixed: return "fixo";
                default: return "adaptativo";
            }
        }

        /// <summary>Quadrado latino balanceado (Williams) para n condições; linha = participante.</summary>
        public static List<int> LatinSquareRow(int n, int participant)
        {
            // primeira linha: 0, 1, n-1, 2, n-2, ...
            var first = new List<int> { 0 };
            int a = 1, b = n - 1;
            for (int i = 1; i < n; i++) first.Add(i % 2 == 1 ? a++ : b--);
            int row = ((participant % n) + n) % n;
            var order = new List<int>();
            foreach (int x in first) order.Add((x + row) % n);
            if (n % 2 == 1 && (participant / n) % 2 == 1) order.Reverse(); // n ímpar: alterna o reverso
            return order;
        }

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (controller == null) controller = FindFirstObjectByType<TemporalFoveationController>();
            if (logger == null) logger = FindFirstObjectByType<SessionLogger>();
            m_Anchor = new GameObject("FTR_TaskAnchor").transform;
            if (target != null) { target.origin = m_Anchor; target.gameObject.SetActive(false); }
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            controller.SetMode(FoveationMode.Baseline);
            ExperimentContext.Phase = "idle";

            if (!autoStart)
            {
                while (true)
                {
                    hud.Show($"Participante P{participantNumber:00}\n\nX: -1    Y: +1\nA: começar");
                    if (XRButtons.Down(XRButtons.Button.X)) participantNumber = Mathf.Max(1, participantNumber - 1);
                    if (XRButtons.Down(XRButtons.Button.Y)) participantNumber++;
                    if (XRButtons.Down(XRButtons.Button.A)) break;
                    yield return null;
                }
            }

            string pid = $"P{participantNumber:00}";
            ExperimentContext.Participant = pid;
            logger.BeginSession(pid);

            var order = LatinSquareRow(conditions.Count, participantNumber - 1);
            SessionLogger.LogEvent("ordem_condicoes", string.Join(">", order.ConvertAll(i => ConditionName(conditions[i]))));

            int block = 0;
            for (int rep = 0; rep < repetitions; rep++)
            {
                foreach (int ci in order)
                {
                    block++;
                    FoveationMode cond = conditions[ci];
                    ExperimentContext.Block = block;
                    ExperimentContext.Condition = ConditionName(cond);
                    controller.SetMode(cond);
                    SessionLogger.LogEvent("condicao_inicio", ExperimentContext.Condition + ";rep=" + rep);

                    foreach (var task in tasks)
                        yield return RunTask(task);

                    yield return AskPerception();

                    SessionLogger.LogEvent("condicao_fim", ExperimentContext.Condition);
                    ExperimentContext.Phase = "pausa";
                    ExperimentContext.Task = "-";
                    controller.SetMode(FoveationMode.Baseline);
                    float t = restSeconds;
                    while (t > 0f)
                    {
                        hud.Show($"Pausa\n{Mathf.CeilToInt(t)}");
                        t -= Time.unscaledDeltaTime;
                        yield return null;
                    }
                }
            }

            ExperimentContext.Phase = "fim";
            ExperimentContext.Condition = "-";
            hud.Show("Fim. Obrigado!");
            if (target != null) target.gameObject.SetActive(false);
            logger.EndSession();
        }

        IEnumerator RunTask(ExperimentTask task)
        {
            ExperimentContext.Task = task.id;
            m_Skip = false;

            // Âncora na posição/orientação atual da cabeça (só yaw)
            m_Anchor.position = head.position;
            float yaw = head.eulerAngles.y;

            if (target != null)
            {
                target.gameObject.SetActive(task.target != TargetMotion.None);
                if (task.target == TargetMotion.Static) target.SetStatic(yaw);
                else if (task.target == TargetMotion.Sweep) target.SetStatic(yaw); // começa parado durante a instrução
            }

            ExperimentContext.Phase = "instrucao";
            SessionLogger.LogEvent("tarefa_instrucao", task.id);
            yield return Wait(task.instructionSeconds, task.instruction, allowSkip: false);

            if (target != null && task.target == TargetMotion.Sweep)
                target.SetSweep(yaw, task.sweepAmplitudeDeg, task.sweepPeakDps);

            ExperimentContext.Phase = "aquecimento";
            yield return Wait(task.warmupSeconds, "", allowSkip: true);

            ExperimentContext.Phase = "medicao";
            SessionLogger.LogEvent("medicao_inicio", task.id + $";alvo_pico_dps={(task.target == TargetMotion.Sweep ? task.sweepPeakDps : 0f)}");
            yield return Wait(task.measureSeconds, "", allowSkip: true);
            SessionLogger.LogEvent(m_Skip ? "medicao_pulada" : "medicao_fim", task.id);
        }

        IEnumerator Wait(float seconds, string text, bool allowSkip)
        {
            hud.Show(text);
            float t = seconds;
            while (t > 0f && !m_Skip)
            {
                if (XRButtons.Down(XRButtons.Button.RightTrigger))
                    SessionLogger.LogEvent("artefato_reportado", "");
                if (allowSkip && XRButtons.Down(XRButtons.Button.B) && IsGripHeld())
                {
                    m_Skip = true;
                    SessionLogger.LogEvent("pesquisador_pulou", ExperimentContext.Task);
                }
                t -= Time.unscaledDeltaTime;
                yield return null;
            }
            hud.Show("");
        }

        static bool IsGripHeld()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            return dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool g) && g;
        }

        IEnumerator AskPerception()
        {
            ExperimentContext.Phase = "pergunta";
            ExperimentContext.Task = "-";
            if (target != null) target.gameObject.SetActive(false);

            yield return Ask("Na última sequência, você percebeu tremulação,\ntravamento ou rastros na visão periférica?\n\nA = Sim     B = Não",
                "resposta_percebeu", "sim", "nao");
            if (m_LastAnswerA)
                yield return Ask("O quanto isso incomodou?\n\nA = Nada / pouco     B = Bastante",
                    "resposta_incomodo", "pouco", "bastante");
        }

        bool m_LastAnswerA;

        IEnumerator Ask(string question, string eventType, string answerA, string answerB)
        {
            hud.Show(question);
            yield return null;
            while (true)
            {
                if (XRButtons.Down(XRButtons.Button.A)) { m_LastAnswerA = true; SessionLogger.LogEvent(eventType, answerA); break; }
                if (XRButtons.Down(XRButtons.Button.B)) { m_LastAnswerA = false; SessionLogger.LogEvent(eventType, answerB); break; }
                yield return null;
            }
            hud.Show("");
            yield return new WaitForSecondsRealtime(0.3f);
        }
    }
}
