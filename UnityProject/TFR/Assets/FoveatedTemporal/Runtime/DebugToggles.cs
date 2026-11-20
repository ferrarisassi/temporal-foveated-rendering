using UnityEngine;

namespace FoveatedTemporal
{
    /// <summary>
    /// Atalhos no headset para validar a técnica (segure o grip esquerdo):
    ///   grip esq. + X : liga/desliga o overlay colorido das zonas
    ///   grip esq. + Y : congela a periferia (teste de reprojeção: gire a cabeça; a periferia deve ficar "presa" ao mundo)
    ///   grip esq. + A : alterna o flip Y da reprojeção (se no teste a periferia andar para o lado errado no eixo vertical)
    /// </summary>
    public class DebugToggles : MonoBehaviour
    {
        public TemporalFoveationController controller;

        void Update()
        {
            if (controller == null) return;
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            bool grip = dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool g) && g;
            if (!grip) return;
            if (XRButtons.Down(XRButtons.Button.X))
            {
                controller.debugOverlay = !controller.debugOverlay;
                SessionLogger.LogEvent("debug_overlay", controller.debugOverlay ? "on" : "off");
            }
            if (XRButtons.Down(XRButtons.Button.Y))
            {
                controller.freezePeripheryTest = !controller.freezePeripheryTest;
                TemporalFoveationState.InvalidateAll();
                SessionLogger.LogEvent("freeze_test", controller.freezePeripheryTest ? "on" : "off");
            }
            if (XRButtons.Down(XRButtons.Button.A))
            {
                controller.flipYReprojection = !controller.flipYReprojection;
                SessionLogger.LogEvent("flip_y", controller.flipYReprojection ? "on" : "off");
            }
        }
    }
}
