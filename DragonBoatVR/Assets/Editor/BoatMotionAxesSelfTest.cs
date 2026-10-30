using System;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class BoatMotionAxesSelfTest
    {
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("yaw resistance does not damp pitch or roll", YawOnly, ref passed, ref failed);
            Check("sway drag does not double the heave force", SwayOnly, ref passed, ref failed);
            Check("sway drag opposes sway and ignores forward speed", SwayCrossCoupling, ref passed, ref failed);
            Check("wave elevation and UDP readout are independent of hull immersion", SurfaceElevation, ref passed, ref failed);
            Debug.Log($"[BOAT_MOTION_AXES] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[BOAT_MOTION_AXES] RESULT=PASS" : "[BOAT_MOTION_AXES] RESULT=FAIL");
        }
        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try { test(); passed++; Debug.Log($"[BOAT_MOTION_AXES] {name}=True"); }
            catch (Exception e) { failed++; Debug.LogError($"[BOAT_MOTION_AXES] {name}: {e.Message}"); }
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        static Vector3 Step(Vector3 velocity, Vector3 angular, bool yaw)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("isolated hull axes");
            var boat = go.AddComponent<BoatBuoyancy>(); var body = go.GetComponent<Rigidbody>();
            typeof(BoatBuoyancy).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boat, null);
            body.useGravity = false; body.mass = 150f; body.drag = body.angularDrag = 0f;
            body.centerOfMass = Vector3.zero; body.inertiaTensor = Vector3.one * 10f;
            body.velocity = velocity; body.angularVelocity = angular;
            boat.points = new[] { new BoatBuoyancy.BuoyancyPoint { localOffset = Vector3.zero } };
            boat.fallbackWaterY = 1f; boat.totalBuoyancyMultiplier = boat.verticalWaterDamping = 0f;
            boat.forwardDragCoeff = boat.sidewaysDragCoeff = 0f;
            boat.yawAngularDragCoeff = yaw ? 20f : 0f; boat.applyWaveDrift = !yaw;
            bool automatic = Physics.autoSimulation;
            try
            {
                Physics.autoSimulation = false;
                typeof(BoatBuoyancy).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boat, null);
                Physics.Simulate(0.01f);
                return yaw ? body.angularVelocity : body.velocity;
            }
            finally { Physics.autoSimulation = automatic; UnityEngine.Object.DestroyImmediate(go); }
        }
        static void YawOnly()
        {
            var initial = new Vector3(0.3f, 0.4f, 0.5f);
            var actual = Step(Vector3.zero, initial, true);
            Require(Mathf.Abs(actual.x - initial.x) < 0.00001f && Mathf.Abs(actual.z - initial.z) < 0.00001f
                && actual.y > 0f && actual.y < initial.y, $"angular velocity before={initial}, after={actual}");
        }
        static void SwayOnly()
        {
            var initial = new Vector3(0f, 0.8f, 0.9f);
            var actual = Step(initial, Vector3.zero, false);
            Require(Vector3.Distance(actual, initial) < 0.00001f, $"zero sway still generated drag: delta={actual - initial}");
        }
        static void SwayCrossCoupling()
        {
            var still = Step(new Vector3(0.5f, 0f, 0f), Vector3.zero, false);
            var moving = Step(new Vector3(0.5f, 0.8f, 4f), Vector3.zero, false);
            Require(still.x > 0f && still.x < 0.5f && Mathf.Abs(still.x - moving.x) < 0.00001f,
                $"equal sway damped differently: slow={still.x:F7}, moving={moving.x:F7}");
        }

        static void SurfaceElevation()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var waveObject = new GameObject("fixed elevated water surface");
            var wave = waveObject.AddComponent<WaveField>();
            wave.transform.position = Vector3.up * 2f;
            wave.externallyStepped = true; wave.horizontalDisplacement = false;
            wave.waves = new[] { new WaveField.WaveComponent {
                direction = Vector2.right, wavelength = 10f, amplitude = 0.2f,
                phaseOffset = Mathf.PI * 0.5f, steepness = 0f } };
            wave.SetTime(0f);
            var hullObject = new GameObject("vertical immersion readout fixture");
            var boat = hullObject.AddComponent<BoatBuoyancy>();
            var body = hullObject.GetComponent<Rigidbody>();
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(BoatBuoyancy).GetMethod("Awake", hidden).Invoke(boat, null);
            typeof(BoatBuoyancy).GetField("_wave", hidden).SetValue(boat, wave);
            body.isKinematic = true; body.useGravity = false;
            boat.points = new[] {
                new BoatBuoyancy.BuoyancyPoint { localOffset = Vector3.down * 0.3f },
                new BoatBuoyancy.BuoyancyPoint { localOffset = Vector3.up * 0.3f } };
            boat.totalBuoyancyMultiplier = boat.verticalWaterDamping = 0f;
            boat.forwardDragCoeff = boat.sidewaysDragCoeff = boat.yawAngularDragCoeff = 0f;
            boat.applyWaveDrift = false;

            using (var receiver = new System.Net.Sockets.UdpClient(
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            {
                receiver.Client.ReceiveTimeout = 1000;
                var telemetry = hullObject.AddComponent<MotionTelemetry>();
                telemetry.boatTransform = hullObject.transform;
                telemetry.remoteHost = "127.0.0.1";
                telemetry.remotePort = ((System.Net.IPEndPoint)receiver.Client.LocalEndPoint).Port;
                var elevations = new float[3]; var wireElevations = new float[3];
                try
                {
                    typeof(MotionTelemetry).GetMethod("Start", hidden).Invoke(telemetry, null);
                    for (int i = 0; i < 3; i++)
                    {
                        // The water is fixed at y=2.2 with +0.2 m wave elevation.
                        // Move only the hull vertically: both, one, then no probe is wet.
                        hullObject.transform.position = Vector3.up * (1f + i);
                        body.position = hullObject.transform.position;
                        typeof(BoatBuoyancy).GetMethod("FixedUpdate", hidden).Invoke(boat, null);
                        Require(boat.submergedCount == 2 - i, "Fixture did not cross its wet/dry boundaries");
                        elevations[i] = boat.waterElevation;
                        typeof(MotionTelemetry).GetMethod("Send", hidden).Invoke(telemetry, null);
                        var sender = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                        byte[] bytes = receiver.Receive(ref sender);
                        Require(bytes.Length == 64 && BitConverter.ToUInt32(bytes, 0) == 0x44425436u,
                            "Missing normal DBT6 telemetry packet");
                        wireElevations[i] = BitConverter.ToSingle(bytes, 60);
                        Require(((BitConverter.ToUInt32(bytes, 20) & 2u) != 0) == (i == 2),
                            "Airborne flag did not report the independent wet/dry state");
                    }
                    float error = 0f;
                    for (int i = 0; i < 3; i++)
                        error = Mathf.Max(error, Mathf.Abs(elevations[i] - 0.2f), Mathf.Abs(wireElevations[i] - 0.2f));
                    Require(error < 0.000001f,
                        $"Fixed surface changed with hull immersion: field={string.Join(",", elevations)}; " +
                        $"UDP={string.Join(",", wireElevations)}; expected=0.2 m for each");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(hullObject);
                    UnityEngine.Object.DestroyImmediate(waveObject);
                }
            }
        }
    }
}
