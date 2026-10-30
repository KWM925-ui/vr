using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Opt-in process-lifecycle diagnosis; not a desktop-input acceptance test.</summary>
    public sealed class LifecycleShutdownProbe : MonoBehaviour
    {
        string _directory;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-lifecycleShutdownProbe");
            if (index < 0 || index + 1 >= args.Length) return;
            var probe = new GameObject("Opt-in lifecycle shutdown diagnosis").AddComponent<LifecycleShutdownProbe>();
            probe._directory = Path.GetFullPath(args[index + 1]);
            DontDestroyOnLoad(probe.gameObject);
        }

        IEnumerator Start()
        {
            Directory.CreateDirectory(_directory);
            Debug.Log("[LIFECYCLE_PROBE] begin 180 seconds of ordinary simulation");
            yield return new WaitForSecondsRealtime(180f);
            var session = FindObjectOfType<SimulationSession>();
            if (session == null || !session.RepositionExperience())
            {
                Debug.LogError("[LIFECYCLE_PROBE] reposition failed");
                Application.Quit(2);
                yield break;
            }
            Debug.Log("[LIFECYCLE_PROBE] actual session reposition complete");
            yield return new WaitForSecondsRealtime(1f);
            File.WriteAllText(Path.Combine(_directory, "quit_requested.txt"), DateTime.UtcNow.ToString("O"));
            Debug.Log("[LIFECYCLE_PROBE] Application.Quit requested");
            Application.Quit();
        }
    }
}
