using System;
using System.Diagnostics;
using System.Threading;

namespace DragonBoatVR
{
    /// <summary>Opt-in shutdown diagnostics; disabled during normal operation.</summary>
    internal static class LifecycleTrace
    {
        static readonly bool Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "-lifecycleTrace") >= 0;

        internal static void Mark(string stage)
        {
            if (!Enabled) return;
            UnityEngine.Debug.Log("[LIFECYCLE] ticks=" + Stopwatch.GetTimestamp()
                + " thread=" + Thread.CurrentThread.ManagedThreadId + " " + stage);
        }
    }
}
