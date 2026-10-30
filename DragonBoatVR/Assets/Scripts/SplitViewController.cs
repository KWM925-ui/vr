using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR
{
    /// <summary>Desktop presentation has exactly two panes: the cabin view and
    /// the outside follow view. Input belongs to each view, not to layout hotkeys.</summary>
    public class SplitViewController : MonoBehaviour
    {
        public Camera mainCamera;
        public Camera twinCamera;
        [Tooltip("双视窗时主视窗占屏宽比例")]
        [Range(0.4f, 0.9f)] public float mainWidthRatio = 0.68f;

        // Preserve the serialized default and existing setup-wizard API.
        public enum Layout { Split }
        [HideInInspector] public Layout layout = Layout.Split;
        bool _xrActive;

        void Start() => Apply();
        void Update() { if (_xrActive != UnityEngine.XR.XRSettings.enabled) Apply(); }

        public void Apply()
        {
            Camera[] cameras = FindObjectsOfType<Camera>(true);
            foreach (var camera in cameras)
            {
                if (mainCamera == null && camera.name == "MainCamera") mainCamera = camera;
                if (twinCamera == null && camera.name == "TwinCamera") twinCamera = camera;
                // Old saved scenes may still contain this camera. It is never
                // enabled or recreated, including after an obsolete layout value.
                if (camera.name == "RigCamera") camera.enabled = false;
            }
            if (mainCamera == null || twinCamera == null) return;
            _xrActive = UnityEngine.XR.XRSettings.enabled;
            layout = Layout.Split;
            float width = Mathf.Clamp(mainWidthRatio, 0.4f, 0.9f);
            mainCamera.rect = _xrActive ? new Rect(0f, 0f, 1f, 1f) : new Rect(0f, 0f, width, 1f);
            twinCamera.rect = new Rect(width, 0f, 1f - width, 1f);
            mainCamera.depth = 0f;
            twinCamera.depth = 1f;
            mainCamera.stereoTargetEye = StereoTargetEyeMask.Both;
            twinCamera.stereoTargetEye = StereoTargetEyeMask.None;
            mainCamera.GetUniversalAdditionalCameraData().allowXRRendering = true;
            twinCamera.GetUniversalAdditionalCameraData().allowXRRendering = false;
            mainCamera.cullingMask &= ~(1 << 2);
            twinCamera.cullingMask &= ~(1 << 2);
            mainCamera.enabled = twinCamera.enabled = true;
        }
    }
}
