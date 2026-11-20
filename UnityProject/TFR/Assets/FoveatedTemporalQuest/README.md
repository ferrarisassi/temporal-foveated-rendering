# Foveated Temporal Rendering no Quest 2 (Unity 6 + URP)

Este projeto renderiza o **centro da visão sempre na taxa máxima do display**. A **periferia é atualizada com menos frequência**, e a frequência depende de **quão rápido o usuário está se movendo**: com o usuário parado, a periferia atualiza poucas vezes por segundo; com movimento rápido, volta à taxa máxima.

O projeto também **anota o que o usuário está fazendo** em cada quadro e **mede quanto se economiza**, com um protocolo de experimento e um script de análise.

> **Estado do código:** foi escrito para Unity 6 LTS + URP 17 (Render Graph) e passou por verificação de sintaxe. **Ainda não foi compilado no Unity nem rodado no Quest.** Na primeira importação, pode ser preciso corrigir alguma assinatura de API. O pipeline de análise foi testado com logs sintéticos.

---

## 1. Como funciona

```
            ┌──────────────────────────────────────┐
            │  periferia distante (zona 1)          │  atualiza 1 a cada N_far quadros
            │    ┌──────────────────────────┐       │
            │    │ periferia média (zona 0)  │       │  atualiza 1 a cada N_mid quadros
            │    │     ┌──────────────┐      │       │
            │    │     │   fóvea      │      │       │  todo quadro
            │    │     │  (< 15°)     │      │       │
            │    │     └──────────────┘      │       │
            │    │        (15°–30°)          │       │
            │    └──────────────────────────┘       │
            │              (> 30°)                  │
            └──────────────────────────────────────┘
   Zonas por excentricidade (graus) a partir do eixo óptico de cada olho.
   O Quest 2 não tem eye tracking, então a fóvea é fixa no centro da lente.
```

Etapas em cada quadro:

1. **`MotionTracker`** mede a velocidade de rotação (°/s) e de deslocamento (m/s) da cabeça. Ele suaviza o sinal e classifica a atividade: `parado`, `olhando_devagar`, `girando_cabeca`, `andando` ou `movimento_rapido`.
2. **`TemporalFoveationController`** converte a velocidade em um divisor de frame rate para cada zona e decide quais zonas são renderizadas neste quadro.
3. **`TemporalFoveationFeature`** (URP) executa três passes:
   - **Máscara early-Z** (antes dos objetos opacos): grava profundidade no plano *near* nas zonas que não serão atualizadas. Assim, toda a cena falha no teste de profundidade nessas áreas e **não é sombreada**. É daqui que vem a economia.
   - **Composição** (antes do pós-processamento): preenche essas zonas com a última imagem gravada da zona, **reprojetada pela rotação da cabeça** desde aquele quadro. A faixa de transição entre zonas é suave (`bandDeg`) para não aparecer uma borda.
   - **Cópia**: nas zonas atualizadas no quadro, a imagem é copiada para o histórico daquela zona.
4. **`SessionLogger`** grava uma linha por quadro com a atividade, a velocidade, os divisores, o frame rate efetivo de cada zona, o tempo de GPU e CPU, os quadros perdidos e a potência. Eventos vão para um arquivo separado.

Quando a periferia inteira está na taxa máxima (movimento rápido), o feature **não insere nenhum passe**, então o custo extra é zero.

### Velocidade → frame rate (padrões, display a 90 Hz)

| Velocidade normalizada | Exemplo | Periferia média | Periferia distante |
|---|---|---|---|
| 0 | parado | ~18 Hz (1/5) | ~6 Hz (1/15) |
| 0,25 | olhando devagar (~30°/s) | 45 Hz (1/2) | ~22 Hz (1/4) |
| 0,5 | girando (~60°/s) ou andando (~0,6 m/s) | 45 Hz (1/2) | 30 Hz (1/3) |
| ≥ 0,75 | ≥ 90°/s ou ≥ 0,9 m/s | 90 Hz | 90 Hz |

A velocidade normalizada é `max(rotação / 120°/s, deslocamento / 1,2 m/s)`. Os valores são aproximados porque as curvas (`midCurve`, `farCurve`) são suaves e editáveis no Inspector. A taxa **sobe na hora** quando o usuário começa a se mover e **desce depois de 0,3 s** de velocidade estável (histerese), para evitar oscilação.

---

## 2. Montagem do projeto

### Requisitos
- Unity **6 LTS** (6000.0.x) com **Android Build Support**
- Template **Universal 3D (URP)**
- **Meta XR Core SDK** (`com.meta.xr.sdk.core`), recomendado. Também funciona só com o plugin OpenXR + feature "Meta Quest Support", mas aí as configurações do Quest (FFR, frequência, clocks) precisam ser feitas à mão.
- Quest 2 em modo desenvolvedor

### Passo a passo
1. Crie o projeto (Universal 3D) e mude a plataforma para Android.
2. Instale o Meta XR Core SDK e rode o **Project Setup Tool** da Meta (corrige a maior parte das configurações).
3. Copie a pasta `Assets/FoveatedTemporal` deste pacote para o `Assets/` do projeto.
4. Confira estas configurações:
   - **XR Plug-in Management > Android**: Oculus ou OpenXR ligado. Stereo Rendering Mode: **Multiview** (Single Pass Instanced). O código **não** funciona em Multi Pass.
   - **Foveated Rendering do sistema desligado**: o `QuestPerfSettings` desliga isso via SDK. Sem o SDK, desligue na feature do OpenXR. Senão a foveação espacial se mistura com a temporal.
   - **Player Settings**: Graphics API **Vulkan**, Color Space **Linear**, **Frame Timing Stats** ligado (o menu de verificação liga).
   - **Project Settings > Graphics > Render Graph**: *Compatibility Mode* **desligado** (é o padrão no Unity 6).
   - No **Universal Renderer Data** usado no Android: *Depth Priming* **Disabled**. Anote o valor de **MSAA** e mantenha o mesmo em todas as condições.
5. No asset do **Universal Renderer Data**, clique em **Add Renderer Feature > Temporal Foveation Feature** e **atribua o shader `Hidden/FoveatedTemporal/FTR`** no campo *Shader*. Sem isso, o shader fica fora do build.
6. Adicione o rig XR à cena (Meta > Building Blocks > **Camera Rig**). A câmera do centro precisa ter a tag **MainCamera**.
7. Menu **Tools > Foveated Temporal > Criar sistema na cena**. Ele cria o `FTR_System` (tracker, controller, logger, protocolo), o HUD, a esfera-alvo e o ambiente de carga.
8. Menu **Tools > Foveated Temporal > Verificar configurações** e corrija o que aparecer no Console.
9. Faça o build e rode no Quest.

---

## 3. Validação técnica (antes de chamar participantes)

Todos os atalhos abaixo funcionam no headset **segurando o grip esquerdo**:

| Atalho | Teste | O que deve acontecer |
|---|---|---|
| grip + **X** | Overlay das zonas | Verde = fóvea. Azul/ciano = zona atualizada. Amarelo/vermelho = zona reaproveitada. As cores devem piscar mais quando você está parado e ficar fixas em azul/ciano quando gira a cabeça rápido. |
| grip + **Y** | Congelar a periferia | A periferia para de atualizar. **Gire a cabeça devagar**: a periferia deve ficar "presa" ao mundo, como se fosse real. Se ela andar junto com a cabeça, a reprojeção está errada. |
| grip + **A** | Inverter o Y da reprojeção | Use se, no teste anterior, a rotação horizontal estiver certa mas a vertical estiver invertida (olhar para cima faz a periferia subir). |

Confira também:
- **Coluna `backbuffer_warning` do log sempre 0.** Se aparecer 1, a câmera está escrevendo direto na tela e o efeito fica desligado (Render Graph em modo de compatibilidade ou configuração do renderer).
- **`eff_hz_mid` e `eff_hz_far`** batem com o esperado (por exemplo, ~18 e ~6 parado a 90 Hz).
- **Calibre a carga**: no `FTR_Environment`, ajuste **`iterations`** até que, no modo **Baseline**, o tempo de GPU fique entre 70% e 90% do orçamento (90 Hz = 11,1 ms; 72 Hz = 13,9 ms). Com a GPU ociosa não há o que economizar. Com ela acima do orçamento, o baseline perde quadros e a comparação fica confusa. Use o mesmo valor para todos os participantes.

---

## 4. O que é anotado (o que o usuário está fazendo)

Cada linha do `frames_*.csv` tem três camadas de anotação:

1. **Atividade detectada automaticamente** (`activity`), pela velocidade com histerese de 0,25 s: `parado`, `olhando_devagar`, `girando_cabeca`, `andando`, `movimento_rapido`. As mudanças também vão para os eventos (`atividade,parado->girando_cabeca`).
2. **Tarefa do protocolo** (`task`, `phase`): o que foi pedido ao participante e em que fase ele está (`instrucao`, `aquecimento`, `medicao`, `pergunta`, `pausa`).
3. **Eventos manuais**: o participante aperta o **gatilho direito** quando percebe tremulação ou travamento na periferia (`artefato_reportado`). Ao fim de cada condição, ele responde se percebeu algo e quanto incomodou.

Limiares padrão de atividade (editáveis no `MotionTracker`):

| Atividade | Regra |
|---|---|
| parado | rotação < 4°/s **e** deslocamento < 0,05 m/s |
| olhando_devagar | rotação < 35°/s |
| girando_cabeca | rotação ≥ 35°/s |
| andando | deslocamento ≥ 0,35 m/s |
| movimento_rapido | rotação ≥ 120°/s **ou** deslocamento ≥ 1,2 m/s |

---

## 5. Como comprovar e medir quanto se economiza

### Condições (intra-sujeitos, ordem por quadrado latino balanceado)
| Condição | O que isola |
|---|---|
| `baseline` | Efeito desligado. É a referência de custo. |
| `overhead` | Efeito ligado com tudo a 90 Hz, mas copiando o histórico todo quadro. Mede **o custo fixo da técnica**. |
| `fixo` | Periferia sempre a 1/2 da taxa, como o TFR da Meta (Padebettu et al., 2023). |
| `adaptativo` | Taxa definida pela velocidade (a proposta deste projeto). |

### Tarefas (em cada condição)
A esfera-alvo torna a velocidade da cabeça reproduzível: o participante a acompanha com a cabeça.

| Tarefa | Velocidade de pico |
|---|---|
| `parado` | 0°/s |
| `olhar_devagar` | 20°/s |
| `girar_moderado` | 70°/s |
| `girar_rapido` | 160°/s |
| `andar` | caminhar na área livre |
| `livre` | exploração livre |

Cada tarefa tem 4 s de instrução, 5 s de aquecimento (descartados) e 40 s de medição. Com 4 condições, 2 repetições e as pausas, a sessão dura cerca de 35 a 40 minutos. Reduza `repetitions` ou `measureSeconds` se precisar.

### Métricas registradas
| Métrica | Fonte | Uso |
|---|---|---|
| Tempo de GPU por quadro | `FrameTimingManager`, `XRDisplaySubsystem` e, opcionalmente, Meta SDK (`FTR_META_PERF`) | **Principal.** Economia = 1 − média(condição) / média(baseline) |
| Quadros perdidos | `XRDisplaySubsystem.TryGetDroppedFrameCount` | Se o pior caso estoura o orçamento |
| Potência | Corrente × tensão da bateria (Android) | Secundária e ruidosa. **Rode sem o cabo USB.** |
| Temperatura e clocks | Bateria / Meta SDK | Controle: as condições precisam ter clocks iguais |
| Frame rate efetivo por zona | Contagem de atualizações no último segundo | Confirma que a regra de velocidade funcionou |
| Fração pulada prevista | Área da zona × se foi pulada | Compara a economia teórica com a medida |

**Para confirmar por fora**, rode o **OVR Metrics Tool** com gravação em CSV durante a sessão e compare o *App GPU time* com o log.

**Cuidados para medidas limpas:**
- Bateria acima de 50%, cabo desconectado e headset frio no início. Faça 2 minutos de aquecimento antes do primeiro bloco: a temperatura muda os clocks.
- Clocks fixos (`QuestPerfSettings.sustainedHighClocks`). Confira `meta_gpu_mhz` se ativar `FTR_META_PERF`.
- O mesmo build, `iterations`, MSAA e frequência para todos.

### Coleta e análise
```bash
adb pull /sdcard/Android/data/<seu.pacote>/files/FTR_Logs ./logs
pip install pandas numpy matplotlib scipy
python Analysis/analyze.py ./logs
```
O script gera em `logs/resultados/`:
- `relatorio.md`: resumo com tabelas e gráficos
- economia por tarefa e **por atividade detectada**, com teste de Wilcoxon pareado (a partir de 5 participantes)
- curva **velocidade × GPU** e **velocidade × frame rate efetivo** da periferia
- custo fixo (`overhead`) e eficiência (economia medida ÷ fração de pixels pulada)
- relatos de artefato por minuto e respostas por condição

Para testar o script antes de ter dados reais:
```bash
python Analysis/make_synthetic_logs.py /tmp/synth
python Analysis/analyze.py /tmp/synth
```
(os números sintéticos são inventados e só servem para checar o pipeline).

### Como interpretar
- **Use a média, não a mediana.** O custo por quadro é bimodal: quadros de atualização custam como o baseline, e quadros reaproveitados custam menos. A média é o que importa para consumo, temperatura e folga de GPU.
- **O pior quadro não diminui.** Num quadro em que todas as zonas atualizam, o custo é igual ao do baseline mais as cópias. Por isso a técnica economiza energia e folga média, mas não garante que um cenário acima do orçamento passe a caber. Veja `gpu_p95_ms` e os quadros perdidos.
- **Eficiência < 1** significa que parte do custo não depende de pixels: vértices (continuam sendo processados), banda das cópias e resolução de MSAA.
- A economia só "vale" se `adaptativo` for melhor que o baseline **depois** de pagar o `overhead`, e com taxa de artefatos percebidos aceitável.

---

## 6. Limitações (vale citar no trabalho)
- **Fóvea fixa**: o Quest 2 não tem eye tracking. Se o usuário mover os olhos sem mover a cabeça, ele olha para a periferia reaproveitada. Isso é mais perceptível com a cabeça parada, justamente quando a taxa é menor.
- **Reprojeção só de rotação**: ao andar, a periferia reaproveitada não tem paralaxe correta (objetos próximos "escorregam"). Por isso o deslocamento entra no fator de velocidade.
- **Objetos animados** na periferia ficam travados na taxa da zona. A regra atual considera só o movimento do usuário, não o da cena.
- **Bordas do campo de visão**: ao girar, a reprojeção pode precisar de pixels fora da imagem antiga. Eles são preenchidos esticando a borda, numa área que quase não se vê na lente.
- A economia vem **só do fragment shading**. Uma cena limitada por vértices, CPU ou banda vai economizar pouco.
- A sensibilidade temporal da periferia é alta (flicker fusion aumenta com a excentricidade; Krajancich et al., 2021). Por isso os mínimos padrão não são muito baixos e é obrigatório medir a percepção junto com o desempenho.

---

## 7. Arquivos

```
Assets/FoveatedTemporal/
  Runtime/
    TemporalFoveationFeature.cs    Renderer feature URP (máscara, composição, cópia)
    TemporalFoveationState.cs      Estado compartilhado e decisão por quadro
    TemporalFoveationController.cs Velocidade -> divisores; modos baseline/overhead/fixo/adaptativo
    MotionTracker.cs               Velocidade da cabeça/mãos e classificação de atividade
    SessionLogger.cs               CSV por quadro + eventos
    PerfProbe.cs                   GPU/CPU, quadros perdidos, bateria/potência
    ExperimentRunner.cs            Protocolo: quadrado latino, tarefas, perguntas
    FollowTarget.cs, HudText.cs, XRButtons.cs, DebugToggles.cs
    LoadEnvironment.cs             Sala de carga com custo de GPU ajustável
    QuestPerfSettings.cs           Desliga o FFR espacial, fixa frequência e clocks
  Shaders/
    FTR.shader                     Passes de máscara, composição e debug
    HeavyProcedural.shader         Material de carga
  Editor/FTRSetupMenu.cs           Menus "Criar sistema" e "Verificar configurações"
Analysis/
  analyze.py                       Análise e relatório
  make_synthetic_logs.py           Logs falsos para testar a análise
```

### Parâmetros principais (Inspector do `FTR_System`)
| Componente | Campo | Padrão | Efeito |
|---|---|---|---|
| Controller | `foveaDeg` / `midDeg` / `bandDeg` | 15° / 30° / 3° | Tamanho das zonas e da transição |
| Controller | `midMinHz` / `farMinHz` | 18 / 6 Hz | Frame rate da periferia com o usuário parado |
| Controller | `midCurve` / `farCurve` | linear / suave | Formato da subida de frame rate com a velocidade |
| Controller | `fixedDivMid` / `fixedDivFar` | 2 / 2 | Condição `fixo` |
| Controller | `slowDownHold` | 0,3 s | Atraso para reduzir a taxa |
| MotionTracker | `angularFullDps` / `linearFullMps` | 120°/s / 1,2 m/s | Velocidade em que a periferia volta à taxa máxima |
| MotionTracker | `attackTime` / `releaseTime` | 0,03 / 0,4 s | Suavização (sobe rápido, desce devagar) |
| ExperimentRunner | `conditions`, `tasks`, `repetitions` | 4, 6, 2 | Desenho do experimento |
| LoadEnvironment | `iterations` | 48 | Custo de GPU por pixel (calibrar) |

## Referências
- Padebettu, Keeler & Lansel. *Temporal Foveated Rendering for VR on Modern Mobile Architecture.* VRST 2023.
- Flöter et al. *Evaluating Foveated Frame Rate Reduction in Virtual Reality for Head-Mounted Displays.* ETRA 2025.
- Krajancich, Kellnhofer & Wetzstein. *A Perceptual Model for Eccentricity-dependent Spatio-temporal Flicker Fusion and its Applications to Foveated Graphics.* SIGGRAPH 2021.
- Moon, Jeong & Lee. *Gaze-Contingent Foveated Frame-Rate Control for Low-Power XR Devices.* SID Digest 2026.
