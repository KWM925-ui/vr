using UnityEngine;

namespace DragonBoatVR
{
    // Unity calls FixedUpdate before PhysX. Device output must consume the state from the
    // completed tick; WaitForFixedUpdate resumes after physics for every tick, even catch-up ticks.
    public class SimulationPostStep : MonoBehaviour
    {
        public SimulationSession session;
        System.Collections.IEnumerator Start()
        {
            while (true)
            {
                yield return new WaitForFixedUpdate();
                if (session != null && session.boat != null) session.StepDevices(Time.fixedDeltaTime);
            }
        }
    }
}
