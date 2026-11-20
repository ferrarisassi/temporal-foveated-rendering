using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace FoveatedTemporal
{
    /// <summary>Leitura simples dos botões dos controles Touch, com detecção de borda (apertou neste quadro).</summary>
    public static class XRButtons
    {
        public enum Button { A, B, X, Y, RightTrigger, LeftTrigger, RightGrip, LeftGrip }

        static readonly Dictionary<Button, bool> s_Prev = new Dictionary<Button, bool>();
        static readonly Dictionary<Button, bool> s_Down = new Dictionary<Button, bool>();
        static int s_Frame = -1;

        static bool Read(Button b)
        {
            XRNode node = (b == Button.A || b == Button.B || b == Button.RightTrigger || b == Button.RightGrip)
                ? XRNode.RightHand : XRNode.LeftHand;
            var dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid) return false;
            InputFeatureUsage<bool> usage;
            switch (b)
            {
                case Button.A: case Button.X: usage = CommonUsages.primaryButton; break;
                case Button.B: case Button.Y: usage = CommonUsages.secondaryButton; break;
                case Button.RightTrigger: case Button.LeftTrigger: usage = CommonUsages.triggerButton; break;
                default: usage = CommonUsages.gripButton; break;
            }
            return dev.TryGetFeatureValue(usage, out bool v) && v;
        }

        static void Poll()
        {
            if (s_Frame == Time.frameCount) return;
            s_Frame = Time.frameCount;
            foreach (Button b in System.Enum.GetValues(typeof(Button)))
            {
                bool now = Read(b);
                s_Prev.TryGetValue(b, out bool prev);
                s_Down[b] = now && !prev;
                s_Prev[b] = now;
            }
        }

        /// <summary>Verdadeiro só no quadro em que o botão foi pressionado. Teclado no Editor: A=J, B=K, X=U, Y=I, gatilho dir.=Espaço.</summary>
        public static bool Down(Button b)
        {
            Poll();
            bool down = s_Down.TryGetValue(b, out bool d) && d;
#if UNITY_EDITOR && ENABLE_LEGACY_INPUT_MANAGER
            if (!down)
            {
                switch (b)
                {
                    case Button.A: down = Input.GetKeyDown(KeyCode.J); break;
                    case Button.B: down = Input.GetKeyDown(KeyCode.K); break;
                    case Button.X: down = Input.GetKeyDown(KeyCode.U); break;
                    case Button.Y: down = Input.GetKeyDown(KeyCode.I); break;
                    case Button.RightTrigger: down = Input.GetKeyDown(KeyCode.Space); break;
                }
            }
#endif
            return down;
        }
    }
}
