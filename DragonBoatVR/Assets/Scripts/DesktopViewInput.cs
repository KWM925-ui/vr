using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>IMGUI menus and the two camera panes own separate input regions.</summary>
    static class DesktopViewInput
    {
        public static bool PanelVisible(SimulationSession session)
            => session != null && session.riverEnvironment != null && session.riverEnvironment.PanelVisible;

        public static bool Available(Camera camera, SimulationSession session)
        {
            return Application.isPlaying && Application.isFocused && !UnityEngine.XR.XRSettings.enabled
                && camera != null && camera.isActiveAndEnabled && !RideHud.DiagnosticsVisible
                && !PanelVisible(session);
        }

        public static bool PointerInside(Camera camera)
            => camera != null && camera.pixelRect.Contains(Input.mousePosition);
    }
}
