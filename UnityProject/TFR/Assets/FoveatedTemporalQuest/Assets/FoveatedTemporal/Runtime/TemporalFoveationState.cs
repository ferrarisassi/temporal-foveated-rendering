using UnityEngine;

namespace FoveatedTemporal
{
    /// <summary>
    /// Estado compartilhado entre o controller (lógica de jogo) e o renderer feature (URP).
    /// Zona 0 = periferia média, zona 1 = periferia distante. A fóvea é sempre renderizada a cada quadro.
    ///
    /// A cada quadro o controller chama <see cref="DecideFrame"/> (em LateUpdate, antes da renderização).
    /// Para cada zona, decide-se:
    ///   Stale[k] = true  -> a zona NÃO é renderizada neste quadro (máscara early-Z) e é preenchida
    ///                       com o histórico reprojetado.
    ///   Copy[k]  = true  -> a zona é renderizada e o quadro é copiado para o histórico da zona.
    /// </summary>
    public static class TemporalFoveationState
    {
        public const int ZoneCount = 2;

        // ---- Configuração (escrita pelo controller) ----
        public static bool FeatureEnabled;          // false = Baseline (nenhum passe é inserido)
        public static Camera TargetCamera;          // câmera XR principal (null = qualquer câmera Game)
        public static readonly int[] Divisor = { 1, 1 }; // 1 = taxa máxima; N = atualiza 1 a cada N quadros
        public static bool ForceCopies;             // modo OverheadOnly: copia histórico todo quadro sem economizar
        public static bool DebugOverlay;
        public static bool FlipY;

        public static float FoveaDeg = 15f;         // raio da fóvea (graus de excentricidade)
        public static float MidDeg = 30f;           // início da periferia distante
        public static float BandDeg = 3f;           // faixa de transição suave entre zonas

        // ---- Decisão do quadro ----
        public static readonly bool[] Stale = new bool[ZoneCount];
        public static readonly bool[] Copy = new bool[ZoneCount];
        public static readonly bool[] HistoryValid = new bool[ZoneCount];
        static readonly int[] s_FramesSinceUpdate = new int[ZoneCount];

        public static int DecidedFrame { get; private set; } = -1;
        public static bool AnyStale => Stale[0] || Stale[1];

        /// <summary>Último quadro em que o renderer feature realmente processou a câmera.</summary>
        public static int LastRenderedFrame = -1;
        /// <summary>Verdadeiro se, no último quadro renderizado, a câmera escrevia direto no backbuffer (efeito inativo).</summary>
        public static bool BackbufferWarning;

        public static void DecideFrame(int frame)
        {
            DecidedFrame = frame;
            for (int k = 0; k < ZoneCount; k++)
            {
                if (!FeatureEnabled)
                {
                    Stale[k] = false; Copy[k] = false; HistoryValid[k] = false; s_FramesSinceUpdate[k] = 0;
                    continue;
                }

                int n = Mathf.Max(1, Divisor[k]);
                if (n == 1)
                {
                    Stale[k] = false;
                    Copy[k] = ForceCopies;
                    if (!ForceCopies) HistoryValid[k] = false; // histórico vai ficar velho; força cópia quando voltar a reduzir
                    s_FramesSinceUpdate[k] = 0;
                    continue;
                }

                s_FramesSinceUpdate[k]++;
                if (!HistoryValid[k] || s_FramesSinceUpdate[k] >= n)
                {
                    Stale[k] = false; Copy[k] = true; s_FramesSinceUpdate[k] = 0;
                }
                else
                {
                    Stale[k] = true; Copy[k] = false;
                }
            }
        }

        /// <summary>Chamado pelo feature quando o histórico não pode ser usado neste quadro (realocação etc.).</summary>
        public static void ForceFresh(int zone)
        {
            Stale[zone] = false;
            Copy[zone] = true;
            HistoryValid[zone] = false;
            s_FramesSinceUpdate[zone] = 0;
        }

        public static void InvalidateAll()
        {
            for (int k = 0; k < ZoneCount; k++)
            {
                HistoryValid[k] = false;
                s_FramesSinceUpdate[k] = 0;
            }
        }
    }
}
