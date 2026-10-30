using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DragonBoatVR
{
    internal static class EnvironmentProbeBootstrap
    {
        public static bool UpdateRequested { get; private set; }
        // A skybox material alone is insufficient when the shipped scene has
        // no baked ambient probe. Ask Unity's SkyManager to integrate that
        // actual sky once; do not invent a constant fill light or run per frame.
        // https://docs.unity3d.com/2022.3/Documentation/ScriptReference/DynamicGI.UpdateEnvironment.html
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            UpdateRequested = false;
            // RuntimeInitializeOnLoadMethod only runs at application startup.
            // A boat selection loads another scene with its own ambient probe.
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneLoaded += SceneLoaded;
        }

        static void SceneLoaded(Scene scene, LoadSceneMode mode) { RefreshEnvironment(); }

        static void RefreshEnvironment()
        {
            UpdateRequested = false;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                || RenderSettings.ambientMode != AmbientMode.Skybox || RenderSettings.skybox == null) return;
            DynamicGI.UpdateEnvironment();
            UpdateRequested = true;
        }
    }
}
