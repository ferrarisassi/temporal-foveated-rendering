using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FoveatedTemporal.Editor
{
    public static class FTRSetupMenu
    {
        const string MatPath = "Assets/FoveatedTemporal/Materials/HeavyProcedural.mat";

        [MenuItem("Tools/Foveated Temporal/Criar sistema na cena")]
        public static void CreateSystem()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                EditorUtility.DisplayDialog("Foveated Temporal",
                    "Nenhuma câmera com a tag MainCamera na cena.\n\nAdicione primeiro o rig XR " +
                    "(Meta > Building Blocks > Camera Rig, ou XR Origin) e rode este menu de novo.", "OK");
                return;
            }

            var root = new GameObject("FTR_System");
            Undo.RegisterCreatedObjectUndo(root, "Criar FTR_System");

            var motion = root.AddComponent<MotionTracker>();
            motion.head = cam.transform;

            var ctrl = root.AddComponent<TemporalFoveationController>();
            ctrl.xrCamera = cam;
            ctrl.motion = motion;

            var logger = root.AddComponent<SessionLogger>();
            logger.controller = ctrl;
            logger.motion = motion;

            root.AddComponent<QuestPerfSettings>();
            var dbg = root.AddComponent<DebugToggles>();
            dbg.controller = ctrl;

            // HUD
            var hudGo = new GameObject("FTR_HUD");
            Undo.RegisterCreatedObjectUndo(hudGo, "Criar HUD");
            var hud = hudGo.AddComponent<HudText>();
            hud.head = cam.transform;

            // Alvo
            var targetGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Undo.RegisterCreatedObjectUndo(targetGo, "Criar alvo");
            targetGo.name = "FTR_Target";
            Object.DestroyImmediate(targetGo.GetComponent<Collider>());
            targetGo.transform.localScale = Vector3.one * 0.12f;
            var target = targetGo.AddComponent<FollowTarget>();

            // Protocolo
            var runner = root.AddComponent<ExperimentRunner>();
            runner.controller = ctrl;
            runner.logger = logger;
            runner.hud = hud;
            runner.target = target;
            runner.head = cam.transform;

            // Ambiente de carga
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                var shader = Shader.Find("FoveatedTemporal/HeavyProcedural");
                if (shader != null)
                {
                    mat = new Material(shader) { enableInstancing = true };
                    AssetDatabase.CreateAsset(mat, MatPath);
                    AssetDatabase.SaveAssets();
                }
            }
            var env = new GameObject("FTR_Environment");
            Undo.RegisterCreatedObjectUndo(env, "Criar ambiente");
            var load = env.AddComponent<LoadEnvironment>();
            load.heavyMaterial = mat;

            Selection.activeGameObject = root;
            Debug.Log("[FTR] Sistema criado. Agora rode Tools > Foveated Temporal > Verificar configurações.");
        }

        [MenuItem("Tools/Foveated Temporal/Verificar configurações")]
        public static void CheckSettings()
        {
            int problems = 0;

            if (!PlayerSettings.enableFrameTimingStats)
            {
                PlayerSettings.enableFrameTimingStats = true;
                Debug.Log("[FTR] Ativei Player Settings > Frame Timing Stats (necessário para o FrameTimingManager).");
            }

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null)
            {
                Debug.LogError("[FTR] O pipeline ativo não é URP.");
                problems++;
            }
            else
            {
                if (urp.msaaSampleCount > 1)
                    Debug.Log($"[FTR] MSAA = {urp.msaaSampleCount}x. Funciona, mas a cópia do histórico precisa resolver o MSAA (custo de banda). Registre esse valor no estudo.");
            }

            bool found = false, shaderOk = false;
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data == null) continue;
                foreach (var f in data.rendererFeatures)
                {
                    if (f is TemporalFoveationFeature tf)
                    {
                        found = true;
                        if (tf.settings.shader != null) shaderOk = true;
                        if (!tf.isActive) Debug.LogWarning("[FTR] O TemporalFoveationFeature está desativado em " + data.name);
                    }
                }
                if (data.depthPrimingMode != DepthPrimingMode.Disabled)
                    Debug.LogWarning($"[FTR] {data.name}: Depth Priming ligado. Desligue para a máscara early-Z cobrir toda a cena.");
            }
            if (!found)
            {
                Debug.LogError("[FTR] Adicione o 'Temporal Foveation Feature' ao Universal Renderer Data usado no Android " +
                               "(selecione o asset do renderer > Add Renderer Feature).");
                problems++;
            }
            else if (!shaderOk)
            {
                Debug.LogError("[FTR] Atribua o shader Hidden/FoveatedTemporal/FTR no campo Shader do feature (senão ele fica fora do build).");
                problems++;
            }

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
                Debug.LogWarning("[FTR] Color space não é Linear (recomendado pela Meta).");

            Debug.Log(problems == 0 ? "[FTR] Configurações OK." : $"[FTR] {problems} problema(s) encontrado(s). Veja acima.");
        }
    }
}
