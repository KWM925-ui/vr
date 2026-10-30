using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// Hydrodynamic force-coverage sweep over arbitrary blade states.
    ///
    /// The product claim under test: as long as the blade is in the water and moving
    /// relative to it — any direction, any immersion fraction, any attack angle, any
    /// speed, including pure rotation — the paddle must produce a finite, physically
    /// directed force/torque. Force is exactly zero only when the wetted area is zero
    /// or the blade is at rest relative to the water; nothing in between may silently
    /// drop the feedback.
    ///
    /// Flat analytical water only (isolated zero-intensity WaveField, no scene assets);
    /// wave-induced entry is covered by the runtime checks. No sockets, no scene edits.
    /// </summary>
    public static class HydroForceCoverageSelfTest
    {
        const float Dt = 0.02f;
        const int RandomTrials = 512;
        static int passed, failed;

        [MenuItem("DragonBoatVR/水动力全空间覆盖自检")]
        public static void Run()
        {
            passed = failed = 0;
            Check("dry blade produces exactly zero force at any speed", TestDryZeroForce);
            Check("resting wet blade: zero force, full wetted area reported", TestWetRestZeroForce);
            Check("random poses: finite force that always opposes motion, area in range", TestRandomSweep);
            Check("quadratic speed scaling with strict monotonicity", TestSpeedScaling);
            Check("attack-angle ordering with analytic Cd ratio", TestAttackAngle);
            Check("half immersion halves both area and force", TestPartialImmersion);
            Check("pure rotation: zero net force, torque about shaft resists rotation", TestPureRotation);
            Check("feedback output respects motor force/torque caps", TestFeedbackCaps);
            Check("final grip wrench stays capped after shaft reorientation", TestFeedbackCapsAfterRotation);
            Check("filtered drag opposes a reversed stroke in still water", TestFeedbackReversal);
            Check("filtered mixed-motion wrench dissipates relative-flow energy", TestFeedbackRelativePower);
            Check("continuity: millimetre pose change bounds force change", TestContinuity);
            Debug.Log($"[HYDROSWEEP] passed={passed} failed={failed}");
            Debug.Log(failed == 0
                ? "[HYDROSWEEP] RESULT=PASS hydrodynamic force coverage over arbitrary blade states"
                : "[HYDROSWEEP] RESULT=FAIL see individual checks");
            if (failed != 0)
                throw new InvalidOperationException($"Hydro force coverage self-test failed: {failed} groups.");
        }

        static void Check(string name, Action test)
        {
            try { test(); passed++; Debug.Log($"[HYDROSWEEP] {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"[HYDROSWEEP] {name}: {error}"); }
        }

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        sealed class Fixture : IDisposable
        {
            readonly GameObject root = new GameObject("HydroForceCoverageSelfTest (software fixture)");
            public readonly PaddleHydrodynamics hydro;

            public Fixture()
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                var water = new GameObject("flat analytical water").AddComponent<WaveField>();
                water.transform.SetParent(root.transform, false);
                water.intensity = 0f;
                water.backgroundCurrentMps = Vector2.zero;
                var go = new GameObject("paddle");
                go.transform.SetParent(root.transform, false);
                hydro = go.AddComponent<PaddleHydrodynamics>();
                hydro.externallyStepped = true;
                hydro.requireValidInput = false;   // velocity derived from injected pose deltas
                hydro.applyPropulsion = false;
                hydro.applyForceToRigidbody = false;
                typeof(PaddleHydrodynamics).GetField("_wave", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(hydro, water);
            }

            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>Advance one step at the given pose. Velocity comes from the delta
        /// to the previous step's pose, so callers must step twice (prime, then measure).</summary>
        static void Step(Fixture fx, Vector3 position, Quaternion rotation)
        {
            fx.hydro.transform.SetPositionAndRotation(position, rotation);
            fx.hydro.Step(Dt);
        }

        static float ForceAt(Fixture fx, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            Step(fx, position, rotation);                                   // prime _lastBladePos
            Step(fx, position + velocity * Dt, rotation);                   // measure step: v = Δp/dt
            return fx.hydro.RawWaterForceN.magnitude;
        }

        // Blade rectangle: local XY plane, width = area/length along X, length along Y,
        // face normal local +Z (see PaddleHydrodynamics header). Identity rotation puts
        // the face square to +Z motion (attack 90°); yaw 90° puts it edge-on (0°).
        static Vector3 FullyWetCentre => new Vector3(0f, -1f, 0f);

        static void TestDryZeroForce()
        {
            using (var fx = new Fixture())
            {
                Random.InitState(20261027);
                for (int i = 0; i < 64; i++)
                {
                    var dir = Random.insideUnitSphere.normalized;
                    float speed = 0.2f + 4.8f * Random.value;
                    var rot = Quaternion.Euler(360f * Random.value, 360f * Random.value, 360f * Random.value);
                    float lift = 0.6f + Random.value;   // centre at least 0.3+margin above flat water
                    Step(fx, new Vector3(0f, lift, 0f), rot);
                    Step(fx, new Vector3(0f, lift, 0f) + dir * (speed * Dt), rot);
                    Require(fx.hydro.RawWaterForceN == Vector3.zero,
                        $"dry blade must not produce force (trial {i}, got {fx.hydro.RawWaterForceN})");
                    Require(fx.hydro.WettedAreaM2 == 0f && !fx.hydro.BladeInWater,
                        $"dry blade must report zero wetted area (trial {i})");
                    Require(fx.hydro.WaterForceN == Vector3.zero && fx.hydro.CurrentDrag == 0f,
                        $"dry blade must not leave a smoothed feedback tail (trial {i})");
                    Require(fx.hydro.FeedbackRequest.dissipativePowerW == 0f,
                        "dry blade retained a drag-dissipation reference");
                }
            }
        }

        static void TestWetRestZeroForce()
        {
            using (var fx = new Fixture())
            {
                Step(fx, FullyWetCentre, Quaternion.identity);
                Step(fx, FullyWetCentre, Quaternion.identity);   // zero delta → zero velocity
                Require(fx.hydro.BladeInWater, "fully submerged stationary blade must count as in water");
                Require(Mathf.Abs(fx.hydro.WettedAreaM2 - fx.hydro.bladeArea) <= 1e-4f * fx.hydro.bladeArea,
                    $"full immersion area expected {fx.hydro.bladeArea}, got {fx.hydro.WettedAreaM2}");
                Require(fx.hydro.RawWaterForceN == Vector3.zero && fx.hydro.RawTorqueAtBladeNm == Vector3.zero,
                    "blade at rest relative to still water must produce zero force/torque");
                Require(fx.hydro.WaterForceN == Vector3.zero,
                    "resting wet blade must not leak a smoothed force into the feedback request");
            }
        }

        static void TestRandomSweep()
        {
            using (var fx = new Fixture())
            {
                Random.InitState(20261027);
                int wetMoving = 0, dry = 0;
                for (int i = 0; i < RandomTrials; i++)
                {
                    // y in ±0.6: guaranteed fully dry above +0.3, fully wet below −0.3,
                    // everything in between exercises partial immersion at random tilt.
                    var p0 = new Vector3(0f, -1.2f + 2.4f * Random.value, 0f);
                    var rot = Quaternion.Euler(360f * Random.value, 360f * Random.value, 360f * Random.value);
                    var dir = Random.insideUnitSphere.normalized;
                    float speed = 0.2f + 3.8f * Random.value;
                    var v = dir * speed;
                    Step(fx, p0, rot);
                    Step(fx, p0 + v * Dt, rot);
                    var f = fx.hydro.RawWaterForceN;
                    Require(SimulationValue.Finite(f) && SimulationValue.Finite(fx.hydro.RawTorqueAtBladeNm)
                        && SimulationValue.Finite(fx.hydro.FeedbackRequest.forceWorldN),
                        $"nonfinite feedback at trial {i}");
                    Require(fx.hydro.WettedAreaM2 >= 0f && fx.hydro.WettedAreaM2 <= fx.hydro.bladeArea * 1.0001f,
                        $"wetted area out of range at trial {i}: {fx.hydro.WettedAreaM2}");
                    if (fx.hydro.WettedAreaM2 <= 1e-9f)
                    {
                        dry++;
                        Require(f == Vector3.zero, $"zero area but nonzero force at trial {i}: {f}");
                        continue;
                    }
                    wetMoving++;
                    Require(f.magnitude > 0f, $"wet and moving but zero force at trial {i}");
                    // No angular velocity in this sweep and still water → every wet point
                    // moves with v, so the total drag must strictly oppose the motion.
                    Require(Vector3.Dot(f, v) < 0f,
                        $"force must oppose motion at trial {i}: F={f} v={v}");
                    Require(Mathf.Abs(fx.hydro.RelativeSpeed - speed) <= 2e-3f * speed,
                        $"relative speed should equal blade speed (trial {i}: {fx.hydro.RelativeSpeed} vs {speed})");
                }
                Require(wetMoving > RandomTrials / 2 && dry > 16,
                    $"sweep should mix wet ({wetMoving}) and dry ({dry}) states meaningfully");
            }
        }

        static void TestSpeedScaling()
        {
            using (var fx = new Fixture())
            {
                float previous = 0f;
                float fHalf = ForceAt(fx, FullyWetCentre, Quaternion.identity, Vector3.forward * 0.5f);
                previous = fHalf;
                foreach (float speed in new[] { 1f, 2f, 4f })
                {
                    float f = ForceAt(fx, FullyWetCentre, Quaternion.identity, Vector3.forward * speed);
                    Require(f > previous, $"force must grow with speed at {speed} m/s");
                    float expected = fHalf * (speed * speed) / (0.5f * 0.5f);
                    Require(Mathf.Abs(f - expected) <= 1e-3f * expected,
                        $"quadratic scaling broken at {speed} m/s: {f} vs {expected}");
                    previous = f;
                }
            }
        }

        static void TestAttackAngle()
        {
            using (var fx = new Fixture())
            {
                const float speed = 1.5f;
                float f0 = ForceAt(fx, FullyWetCentre, Quaternion.Euler(0f, 90f, 0f), Vector3.forward * speed);
                float f45 = ForceAt(fx, FullyWetCentre, Quaternion.Euler(0f, 45f, 0f), Vector3.forward * speed);
                float f90 = ForceAt(fx, FullyWetCentre, Quaternion.identity, Vector3.forward * speed);
                Require(f90 > f45 && f45 > f0, $"attack ordering broken: 0°={f0} 45°={f45} 90°={f90}");
                float ratio = fx.hydro.cdFlat / fx.hydro.cdEdge;
                Require(Mathf.Abs(f90 / f0 - ratio) <= 1e-2f * ratio,
                    $"Cd ratio expected {ratio}, measured {f90 / f0}");
                float cd45 = fx.hydro.cdFlat * 0.5f + fx.hydro.cdEdge * 0.5f;
                Require(Mathf.Abs(f45 / f90 - cd45 / fx.hydro.cdFlat) <= 1e-2f,
                    $"45° Cd mix expected {cd45 / fx.hydro.cdFlat}, measured {f45 / f90}");
            }
        }

        static void TestPartialImmersion()
        {
            using (var fx = new Fixture())
            {
                const float speed = 1.5f;
                // Identity rotation, centre at the waterline: exactly half the blade rows
                // are below the flat surface → half the reference area, half the force.
                float fHalf = ForceAt(fx, Vector3.zero, Quaternion.identity, Vector3.forward * speed);
                float areaHalf = fx.hydro.WettedAreaM2;
                Require(Mathf.Abs(areaHalf - fx.hydro.bladeArea * 0.5f) <= 1e-3f * fx.hydro.bladeArea,
                    $"half immersion area expected {fx.hydro.bladeArea * 0.5f}, got {areaHalf}");
                float fFull = ForceAt(fx, new Vector3(0f, -0.5f, 0f), Quaternion.identity, Vector3.forward * speed);
                Require(Mathf.Abs(fHalf / fFull - 0.5f) <= 2e-3f,
                    $"half immersion should halve the force, ratio {fHalf / fFull}");
            }
        }

        static void TestPureRotation()
        {
            using (var fx = new Fixture())
            {
                const float omega = 3f;   // rad/s about world +Y (the shaft axis at identity)
                var r0 = Quaternion.identity;
                var r1 = Quaternion.Euler(0f, omega * Dt * Mathf.Rad2Deg, 0f);
                Step(fx, FullyWetCentre, r0);
                Step(fx, FullyWetCentre, r1);
                Require(fx.hydro.BladeAngularVelocity.magnitude > 1f,
                    "rotation delta must register as blade angular velocity");
                Require(fx.hydro.RawWaterForceN.magnitude < 1e-4f,
                    $"symmetric pure rotation must cancel net force, got {fx.hydro.RawWaterForceN}");
                float shaftTorque = Vector3.Dot(fx.hydro.RawTorqueAtBladeNm, Vector3.up);
                Require(shaftTorque < -1e-4f,
                    $"drag torque about the shaft must resist the rotation, got {shaftTorque}");
                Require(fx.hydro.FeedbackRequest.valid,
                    "pure rotation must still produce a valid feedback request (torque channel)");
            }
        }

        static void TestFeedbackCapsAfterRotation()
        {
            using (var fx = new Fixture())
            {
                var p = FullyWetCentre;
                var rotation = Quaternion.Euler(0f, 0f, 90f);
                Step(fx, p, rotation);
                // Build a filtered force along the shaft with little grip torque.
                // Reorienting the shaft changes the current lever arm while that
                // force is still in the smoothing history.
                for (int i = 0; i < 60; i++)
                {
                    p += rotation * Vector3.down * (30f * Dt);
                    Step(fx, p, rotation);
                }
                Require(fx.hydro.FeedbackRequest.forceWorldN.magnitude > 250f,
                    "test did not establish the loaded feedback history");
                foreach (float angle in new[] { 0f, -90f, 0f, 90f })
                {
                    rotation = Quaternion.Euler(0f, 0f, angle);
                    p += rotation * Vector3.down * (30f * Dt);
                    Step(fx, p, rotation);
                    var request = fx.hydro.FeedbackRequest;
                    Require(request.valid && fx.hydro.BladeInWater,
                        "test lost valid wet input during reorientation");
                    Require(request.forceWorldN.magnitude <= fx.hydro.maxFeedbackForceN * 1.001f
                        && request.torqueAtGripWorldNm.magnitude <= fx.hydro.maxFeedbackTorqueNm * 1.001f,
                        $"final grip wrench exceeded caps after {angle} deg: force={request.forceWorldN.magnitude} N, torque={request.torqueAtGripWorldNm.magnitude} Nm");
                    Vector3 reconstructed = fx.hydro.TorqueAtBladeNm
                        + Vector3.Cross(p - request.gripWorldPosition, fx.hydro.WaterForceN);
                    Require(Vector3.Distance(reconstructed, request.torqueAtGripWorldNm) < 0.001f
                        && Vector3.Distance(fx.hydro.WaterForceN, request.forceWorldN) < 0.001f,
                        "limited grip output no longer represents the reported blade wrench");
                }
            }
        }

        static void TestFeedbackCaps()
        {
            using (var fx = new Fixture())
            {
                var p = FullyWetCentre;
                var v = Vector3.forward * 30f;   // raw force far above the motor limits
                Step(fx, p, Quaternion.identity);
                for (int i = 0; i < 41; i++)
                {
                    p += v * Dt;
                    Step(fx, p, Quaternion.identity);
                }
                Require(fx.hydro.RawWaterForceN.magnitude > 10000f,
                    "uncapped raw telemetry force expected at 30 m/s");
                Require(fx.hydro.WaterForceN.magnitude <= fx.hydro.maxFeedbackForceN * 1.001f,
                    $"smoothed feedback force must respect the {fx.hydro.maxFeedbackForceN} N cap, got {fx.hydro.WaterForceN.magnitude}");
                Require(fx.hydro.FeedbackRequest.torqueAtGripWorldNm.magnitude <= fx.hydro.maxFeedbackTorqueNm * 1.001f,
                    $"feedback torque at the grip must respect the {fx.hydro.maxFeedbackTorqueNm} Nm cap, got {fx.hydro.FeedbackRequest.torqueAtGripWorldNm.magnitude}");
            }
        }

        static void TestFeedbackReversal()
        {
            using (var fx = new Fixture())
            {
                var position = FullyWetCentre;
                Step(fx, position, Quaternion.identity);
                for (int i = 0; i < 60; i++)
                {
                    position += Vector3.forward * Dt;
                    Step(fx, position, Quaternion.identity);
                }
                position -= Vector3.forward * Dt;
                Step(fx, position, Quaternion.identity);
                float rawPower = Vector3.Dot(fx.hydro.RawWaterForceN, fx.hydro.BladeVelocity);
                float feedbackPower = Vector3.Dot(fx.hydro.WaterForceN, fx.hydro.BladeVelocity);
                Require(rawPower < -1f, "reversal fixture did not establish resisting raw drag");
                Require(feedbackPower < 0f,
                    $"memoryless still-water drag accelerated the reversed stroke: raw={rawPower} W, feedback={feedbackPower} W");
                Require(fx.hydro.FeedbackRequest.valid && fx.hydro.WaterForceN.sqrMagnitude > 1e-6f,
                    "reversing a wet moving blade silently dropped feedback");
            }
        }

        static void TestFeedbackRelativePower()
        {
            using (var fx = new Fixture())
            {
                var water = (WaveField)typeof(PaddleHydrodynamics).GetField("_wave",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fx.hydro);
                var flow = new Vector3(0.3f, 0f, -0.2f);
                water.backgroundCurrentMps = new Vector2(flow.x, flow.z);
                Step(fx, FullyWetCentre, Quaternion.identity);
                // A steady current may do positive work on a stationary/slow
                // paddle in world space. The dissipative quantity is work
                // relative to the water, including the distributed moment.
                for (int i = 1; i <= 300; i++)
                {
                    float t = i * Dt;
                    var position = FullyWetCentre + new Vector3(0.12f * Mathf.Sin(9f * t),
                        0.05f * Mathf.Sin(4f * t), 0.16f * Mathf.Sin(13f * t));
                    var rotation = Quaternion.Euler(20f * Mathf.Sin(6f * t),
                        35f * Mathf.Sin(8f * t), 15f * Mathf.Sin(5f * t));
                    Step(fx, position, rotation);
                    var velocity = fx.hydro.BladeVelocity - flow;
                    float rawPower = Vector3.Dot(fx.hydro.RawWaterForceN, velocity)
                        + Vector3.Dot(fx.hydro.RawTorqueAtBladeNm, fx.hydro.BladeAngularVelocity);
                    float feedbackPower = Vector3.Dot(fx.hydro.WaterForceN, velocity)
                        + Vector3.Dot(fx.hydro.TorqueAtBladeNm, fx.hydro.BladeAngularVelocity);
                    Require(rawPower < 0f, "mixed-motion fixture lost raw dissipation");
                    Require(feedbackPower < 0f,
                        $"filtered wrench added relative-flow energy at sample {i}: raw={rawPower} W, feedback={feedbackPower} W");
                    Require(Mathf.Abs(rawPower + fx.hydro.RawDissipativePowerW) <= 0.001f + Mathf.Abs(rawPower) * 0.0001f
                        && Mathf.Abs(feedbackPower + fx.hydro.FeedbackRequest.dissipativePowerW)
                            <= 0.001f + Mathf.Abs(feedbackPower) * 0.0001f,
                        "distributed relative-flow work did not reach the scaled feedback request");
                    Require(fx.hydro.FeedbackRequest.valid, "mixed-motion feedback became invalid");
                }
            }
        }

        static void TestContinuity()
        {
            using (var fx = new Fixture())
            {
                var rot = Quaternion.Euler(23f, 47f, 11f);
                var v = new Vector3(0.3f, 0.1f, 0.93f).normalized * 1.2f;
                var centre = new Vector3(0f, -0.3f, 0f);
                float fA = ForceAt(fx, centre, rot, v);
                float fB = ForceAt(fx, centre + v.normalized * 0.001f,
                    rot * Quaternion.Euler(0.5f, 0f, 0f), v);
                Require(Mathf.Abs(fB - fA) <= 0.05f * fA + 0.5f,
                    $"millimetre pose change must not jump the force: {fA} -> {fB}");
            }
        }
    }
}
