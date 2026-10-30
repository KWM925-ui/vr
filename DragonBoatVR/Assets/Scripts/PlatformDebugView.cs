using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Keep the original scene script identity as the external-device
    /// visibility guard. The user removed the F3/third diagnostic view on
    /// 2026-10-29; device simulation continues behind the two presentation views.</summary>
    [DefaultExecutionOrder(1200)]
    public class PlatformDebugView : MonoBehaviour
    {
        void Start() => HideDevices();

        public void HideDevices()
        {
            foreach (string name in new[] { "CableRig", "StewartPlatformRig" })
            {
                var rig = GameObject.Find(name);
                if (rig == null) continue;
                foreach (var renderer in rig.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.gameObject.layer = 2;
                    renderer.enabled = false;
                }
            }
            foreach (var camera in FindObjectsOfType<Camera>(true))
                camera.cullingMask &= ~(1 << 2);
        }
    }
}
