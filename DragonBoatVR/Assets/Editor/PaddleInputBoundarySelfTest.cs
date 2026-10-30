using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Analytical input-boundary checks. No sockets, real hardware or scene assets are changed.</summary>
    public static class PaddleInputBoundarySelfTest
    {
        const float Dt = 0.01f;
        const float PacketInterval = 0.04f;
        static int passed, failed;

        [MenuItem("DragonBoatVR/桨输入包络与无效样本自检")]
        public static void Run()
        {
            passed = failed = 0;
            Check("all six envelope faces, including sub-millimetre excursions", TestEnvelopeFaces);
            Check("clipped underwater motion has no phantom drag; re-entry uses bounded anchor", TestClippedDragAndReentry);
            Check("sparse packets retain tangential/rotational motion and moving-hull velocity", TestSparseMovingHull);
            Check("world-space input remains independent of moving hull", TestWorldInput);
            Check("unchanged receive-age expiry clears motion and hydrodynamics", TestExpiry);
            Check("invalid decoded fields fail closed before hydrodynamics", TestInvalidDecoded);
            Check("encoder rejects nonfinite upstream velocity before building a packet", TestInvalidEncoderSource);
            Debug.Log($"[PADDLEINPUT] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[PADDLEINPUT] RESULT=PASS analytical software input boundaries"
                : "[PADDLEINPUT] RESULT=FAIL see individual checks");
            if (failed != 0) throw new InvalidOperationException($"Paddle input self-test failed: {failed} groups.");
        }

        static void Check(string name, Action test)
        {
            try { test(); passed++; Debug.Log($"[PADDLEINPUT] {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"[PADDLEINPUT] {name}: {error}"); }
        }

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        static void Near(Vector3 actual, Vector3 expected, string message, float tolerance = 0.0003f)
        {
            Require(SimulationValue.Finite(actual) && Vector3.Distance(actual, expected) <= tolerance,
                $"{message}: actual={actual:F6} expected={expected:F6}");
        }

        static long Ticks(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);
        static void Call(object target, string method) => target.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);

        sealed class Fixture : IDisposable
        {
            readonly GameObject root = new GameObject("PaddleInputBoundarySelfTest (software fixture)");
            public readonly BoatBuoyancy boat;
            public readonly Rigidbody body;
            public readonly PaddleDriver driver;
            public readonly PaddleHydrodynamics hydro;
            public readonly PaddleEncoderPipeline encoder;
            public readonly HardwareUdpBridge bridge;
            public double time;

            public Fixture()
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                boat = Child("boat").AddComponent<BoatBuoyancy>();
                Call(boat, "Awake");
                body = boat.GetComponent<Rigidbody>();
                body.useGravity = false;
                bridge = Child("bridge (not listening)").AddComponent<HardwareUdpBridge>();
                driver = Child("paddle").AddComponent<PaddleDriver>();
                driver.boat = boat;
                driver.bridge = bridge;
                driver.externallyStepped = true;
                driver.SetMode(PaddleDriver.Mode.UdpDirect);
                encoder = driver.gameObject.AddComponent<PaddleEncoderPipeline>();
                encoder.Configure(driver, boat);
                hydro = driver.gameObject.AddComponent<PaddleHydrodynamics>();
                hydro.input = driver;
                hydro.targetBoat = boat;
                hydro.requireValidInput = true;
                hydro.externallyStepped = true;
                hydro.applyPropulsion = false;
                hydro.balanceYawRoll = false;
                // Bind an isolated flat-water field directly; do not alter WaveField.Instance.
                var water = Child("flat analytical water").AddComponent<WaveField>();
                water.intensity = 0f;
                water.backgroundCurrentMps = Vector2.zero;
                typeof(PaddleHydrodynamics).GetField("_wave", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(hydro, water);
            }

            GameObject Child(string name)
            {
                var child = new GameObject(name);
                child.transform.SetParent(root.transform, false);
                return child;
            }

            public void Send(uint sequence, Vector3 bladeInSource, Quaternion rotationInSource, long receivedTicks)
            {
                Require(bridge.AcceptPaddle(new HardwareUdpBridge.PaddleState
                {
                    seq = sequence,
                    timestampUs = 123456789u + sequence,
                    position = bladeInSource + rotationInSource * Vector3.up * driver.shaftToBlade,
                    eulerDeg = rotationInSource.eulerAngles
                }, receivedTicks), "fixture packet rejected");
            }

            public void Step()
            {
                time += Dt;
                driver.Tick(Dt);
                driver.ApplyDecodedSample(encoder.Process(driver.Sample, time, Dt));
                hydro.Step(Dt);
            }

            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void TestEnvelopeFaces()
        {
            var outside = new[] { new Vector3(1.4002f, -0.5f, 0), new Vector3(-1.4002f, -0.5f, 0),
                new Vector3(0.8f, 2.4002f, 0), new Vector3(0.8f, -1.2002f, 0),
                new Vector3(0.8f, -0.5f, 4.6002f), new Vector3(0.8f, -0.5f, -4.6002f) };
            var boundary = new[] { new Vector3(1.4f, -0.5f, 0), new Vector3(-1.4f, -0.5f, 0),
                new Vector3(0.8f, 2.4f, 0), new Vector3(0.8f, -1.2f, 0),
                new Vector3(0.8f, -0.5f, 4.6f), new Vector3(0.8f, -0.5f, -4.6f) };
            for (int face = 0; face < outside.Length; face++)
                using (var f = new Fixture())
                {
                    f.Send(1, outside[face], Quaternion.identity, Stopwatch.GetTimestamp());
                    f.Step();
                    Require(f.driver.Sample.valid, "bounded packet became invalid");
                    Near(f.driver.Sample.bladePosition, boundary[face], "locked envelope face " + face, 0.00001f);
                    Near(f.driver.Sample.bladeVelocity, Vector3.zero, "first-packet motion");
                }
        }

        static void TestClippedDragAndReentry()
        {
            using (var f = new Fixture())
            {
                long now = Stopwatch.GetTimestamp();
                f.Send(1, new Vector3(1.5f, -0.5f, 0), Quaternion.identity, now - Ticks(2 * PacketInterval));
                f.Step();
                f.Send(2, new Vector3(1.7f, -0.5f, 0), Quaternion.identity, now - Ticks(PacketInterval));
                f.Step();
                for (int held = 0; held < 3; held++)
                {
                    Require(f.hydro.InputValid && f.hydro.BladeInWater, "clipped paddle must stay valid and wet");
                    Near(f.driver.Sample.bladePosition, new Vector3(1.4f, -0.5f, 0), "bounded blade");
                    Near(f.driver.Sample.bladeVelocity, Vector3.zero, "clipped velocity");
                    Near(f.hydro.RawWaterForceN, Vector3.zero, "phantom underwater drag", 0.00001f);
                    f.Step();
                }
                f.Send(3, new Vector3(1.3f, -0.5f, 0), Quaternion.identity, now);
                f.Step();
                Vector3 velocity = new Vector3(-0.1f / PacketInterval, 0, 0);
                Near(f.driver.Sample.bladeVelocity, velocity, "re-entry derivative must start at accepted boundary");
                float expected = 0.5f * f.hydro.waterDensity * f.hydro.cdEdge * f.hydro.bladeArea * velocity.sqrMagnitude;
                Near(f.hydro.RawWaterForceN, Vector3.right * expected, "analytical re-entry drag", 0.003f);
            }
        }

        static void TestSparseMovingHull()
        {
            using (var f = new Fixture())
            {
                f.boat.transform.SetPositionAndRotation(new Vector3(2, 0.25f, -3), Quaternion.Euler(8, 30, -5));
                f.body.velocity = new Vector3(0.6f, 0.1f, -0.2f);
                f.body.angularVelocity = new Vector3(0.02f, 0.3f, -0.04f);
                Physics.SyncTransforms();
                long now = Stopwatch.GetTimestamp();
                f.Send(1, new Vector3(1.6f, -0.5f, 0), Quaternion.identity, now - Ticks(PacketInterval));
                f.Step();
                f.Send(2, new Vector3(1.8f, -0.5f, 0.04f), Quaternion.Euler(0, 2, 0), now);
                f.Step();
                for (int held = 0; held < 4; held++)
                {
                    var sample = f.driver.Sample;
                    Vector3 expectedPosition = f.boat.transform.TransformPoint(new Vector3(1.4f, -0.5f, 0.04f));
                    Vector3 expectedVelocity = f.boat.transform.rotation * Vector3.forward
                        + f.body.GetPointVelocity(expectedPosition);
                    Vector3 expectedAngular = f.boat.transform.rotation * (Vector3.up * (2f * Mathf.Deg2Rad / PacketInterval))
                        + f.body.angularVelocity;
                    Require(sample.valid && sample.sequence == 2, "held UDP packet lost validity or identity");
                    Near(sample.bladePosition, expectedPosition, "held hull-relative position");
                    Near(sample.bladeVelocity, expectedVelocity, "held tangential plus hull velocity");
                    Near(sample.bladeAngularVelocity, expectedAngular, "held angular plus hull velocity");
                    f.boat.transform.position += f.body.velocity * Dt;
                    f.boat.transform.rotation = Quaternion.AngleAxis(f.body.angularVelocity.magnitude * Dt * Mathf.Rad2Deg,
                        f.body.angularVelocity.normalized) * f.boat.transform.rotation;
                    Physics.SyncTransforms();
                    f.Step();
                }
            }
        }

        static void TestWorldInput()
        {
            using (var f = new Fixture())
            {
                f.driver.paddleInBoatSpace = false;
                f.boat.transform.position = new Vector3(-3, 1, 5);
                f.body.velocity = Vector3.right;
                long now = Stopwatch.GetTimestamp();
                f.Send(1, new Vector3(4, -0.5f, 0), Quaternion.identity, now - Ticks(PacketInterval));
                f.Step();
                f.Send(2, new Vector3(4, -0.5f, -0.02f), Quaternion.identity, now);
                f.Step();
                Near(f.driver.Sample.bladePosition, new Vector3(4, -0.5f, -0.02f), "world-space pose");
                Near(f.driver.Sample.bladeVelocity, new Vector3(0, 0, -0.5f), "world-space velocity excludes hull");
            }
        }

        static void TestExpiry()
        {
            using (var f = new Fixture())
            {
                long now = Stopwatch.GetTimestamp();
                f.Send(1, new Vector3(0.8f, -0.5f, 0), Quaternion.identity, now - Ticks(PacketInterval));
                f.Step();
                f.Send(2, new Vector3(0.8f, -0.5f, -0.04f), Quaternion.identity, now);
                f.Step();
                Require(f.hydro.CurrentDrag > 1f, "expiry fixture needs nonzero preceding resistance");
                f.Send(3, new Vector3(0.8f, -0.5f, -0.08f), Quaternion.identity,
                    Stopwatch.GetTimestamp() - Ticks(0.101));
                f.Step();
                Require(!f.driver.Sample.valid && !f.hydro.InputValid && !f.hydro.FeedbackRequest.valid,
                    "input remains valid after original 100 ms TTL");
                Near(f.driver.Sample.bladeVelocity, Vector3.zero, "expired linear velocity");
                Near(f.driver.Sample.bladeAngularVelocity, Vector3.zero, "expired angular velocity");
                Near(f.hydro.WaterForceN, Vector3.zero, "expired feedback");
            }
        }

        static PaddleSample ValidSample() => new PaddleSample
        {
            sequence = 1, timeSeconds = 0.01, valid = true,
            bladePosition = new Vector3(0.8f, -0.5f, 0), bladeRotation = Quaternion.identity,
            bladeVelocity = Vector3.back, hasAngularVelocity = true
        };

        static void TestInvalidDecoded()
        {
            using (var f = new Fixture())
                for (int field = 0; field < 7; field++)
                {
                    Require(f.driver.ApplyDecodedSample(ValidSample()), "valid precondition rejected");
                    f.hydro.Step(Dt);
                    Require(f.hydro.CurrentDrag > 1f, "rejection fixture needs preceding force");
                    PaddleSample bad = ValidSample();
                    switch (field)
                    {
                        case 0: bad.bladeVelocity.x = float.NaN; break;
                        case 1: bad.bladeAngularVelocity.y = float.PositiveInfinity; break;
                        case 2: bad.gripForceN = float.NaN; break;
                        case 3: bad.bladePosition.z = float.NegativeInfinity; break;
                        case 4: bad.bladeRotation.x = float.NaN; break;
                        case 5: bad.timeSeconds = double.NaN; break;
                        case 6: bad.valid = false; break;
                    }
                    Require(!f.driver.ApplyDecodedSample(bad), "invalid decoded field accepted: " + field);
                    f.hydro.Step(Dt);
                    Require(!f.driver.Sample.valid && !f.hydro.InputValid && !f.hydro.FeedbackRequest.valid,
                        "rejection did not reach hydrodynamics: " + field);
                    Near(f.driver.Sample.bladeVelocity, Vector3.zero, "rejected linear velocity");
                    Near(f.driver.Sample.bladeAngularVelocity, Vector3.zero, "rejected angular velocity");
                    Require(SimulationValue.Finite(f.driver.Sample.bladePosition)
                        && SimulationValue.Finite(f.driver.Sample.bladeRotation), "invalid pose leaked into telemetry");
                    Near(f.hydro.RawWaterForceN, Vector3.zero, "rejected raw force");
                    Near(f.hydro.WaterForceN, Vector3.zero, "rejected feedback");
                }
        }

        static void TestInvalidEncoderSource()
        {
            using (var f = new Fixture())
                for (int angular = 0; angular < 2; angular++)
                {
                    PaddleSample bad = ValidSample();
                    if (angular == 0) bad.bladeVelocity.x = float.PositiveInfinity;
                    else bad.bladeAngularVelocity.z = float.NaN;
                    PaddleSample decoded = f.encoder.Process(bad, 0.01, Dt);
                    Require(!decoded.valid && !f.encoder.Telemetry.packetBuilt && !f.encoder.Telemetry.packetAccepted,
                        "bad upstream derivative was encoded and accepted");
                    Require(!f.driver.ApplyDecodedSample(decoded), "invalid encoder result accepted");
                    f.hydro.Step(Dt);
                    Require(!f.hydro.InputValid && !f.hydro.FeedbackRequest.valid, "encoder rejection did not fail closed");
                }
        }
    }
}
