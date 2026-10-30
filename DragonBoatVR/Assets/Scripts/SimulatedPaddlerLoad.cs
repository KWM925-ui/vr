using System;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Software-only estimate of the existing occupant's changing first
    /// mass moment. It adds the weight-shift couple, never a second body weight.
    /// The session steps it once before PhysX; cameras and platform use that result.</summary>
    public sealed class SimulatedPaddlerLoad : MonoBehaviour
    {
        public string provenance = "UNMEASURED_SCRIPTED_OCCUPANT_FIRST_MOMENT";
        public bool measured;
        [Tooltip("Whole-system centre shift, not human travel or measured occupant mass. Calibrate against the final hull and hardware.")]
        [Range(0f, 0.03f)] public float maximumCenterShiftM = 0.012f;
        public Vector3 CenterShiftLocalM { get; private set; }
        public Vector3 TorqueWorldNm { get; private set; }

        public void Clear() { CenterShiftLocalM = TorqueWorldNm = Vector3.zero; }

        public bool ValidOffset(Vector3 value) => SimulationValue.Finite(maximumCenterShiftM)
            && maximumCenterShiftM >= 0f && maximumCenterShiftM <= 0.03f
            && SimulationValue.Finite(value) && Mathf.Abs(value.x) <= maximumCenterShiftM + 1e-7f
            && value.y == 0f && value.z == 0f;

        public void Step(BoatBuoyancy boat, PaddleDriver driver, double simulationTime)
        {
            Clear();
            if (!enabled || boat == null || driver == null || boat.BoatRb == null || boat.BoatRb.isKinematic) return;
            PaddleSample sample = driver.Sample;
            // A raw hardware/desktop camera movement is not a body measurement.
            // Only this explicitly selected virtual paddler supplies the estimate.
            if (driver.mode != PaddleDriver.Mode.VirtualPaddler || !sample.valid
                || !(sample.source == PaddleInputKind.Synthetic
                    || sample.source == PaddleInputKind.Encoder && sample.originalSource == PaddleInputKind.Synthetic)
                || double.IsNaN(sample.timeSeconds) || double.IsInfinity(sample.timeSeconds)
                || sample.timeSeconds > simulationTime + 1e-6
                || simulationTime - sample.timeSeconds > driver.inputTimeoutSeconds
                || !SimulationValue.Finite(sample.bladePosition)) return;
            var body = boat.BoatRb;
            Vector3 local = Quaternion.Inverse(body.rotation) * (sample.bladePosition - body.position);
            float low = driver.LowestHandheldBladeY(local.z - driver.pivotLocalPos.z);
            float range = driver.bladeAirHeight - low;
            if (!SimulationValue.Finite(range) || range <= 0f) return;
            // The lowering/recovery trajectory gives a continuous proxy for a
            // small lean toward the active side. It is not presented as measured
            // biomechanics, nor inferred from the HMD or the viewpoint position.
            float lowered = Mathf.Clamp01((driver.bladeAirHeight - local.y) / range);
            float lean = lowered * lowered * (3f - 2f * lowered);
            Apply(boat, new Vector3((driver.leftSide ? -1f : 1f) * maximumCenterShiftM * lean, 0f, 0f));
        }

        public void Apply(BoatBuoyancy boat, Vector3 centerShiftLocalM)
        {
            Clear();
            if (!ValidOffset(centerShiftLocalM)) throw new InvalidOperationException("Invalid simulated occupant mass moment.");
            if (!enabled || boat == null || boat.BoatRb == null || boat.BoatRb.isKinematic) return;
            var body = boat.BoatRb;
            CenterShiftLocalM = centerShiftLocalM;
            // Delta torque = delta(r_COM) cross total weight. The existing total
            // mass, net gravity, reference COM and inertia tensor stay untouched.
            TorqueWorldNm = Vector3.Cross(body.rotation * centerShiftLocalM, body.mass * Physics.gravity);
            body.AddTorque(TorqueWorldNm, ForceMode.Force);
        }

        void OnDisable() { Clear(); }
    }
}
