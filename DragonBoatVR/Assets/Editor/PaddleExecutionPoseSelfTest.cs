using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Exercise fixed-input/render ownership directly. This does not replace
    /// a normal Player rendering check and does not advance PhysX or change scene assets.</summary>
    public static class PaddleExecutionPoseSelfTest
    {
        const float Dt = 0.01f;
        static readonly Vector3 ActualLocal = new Vector3(0.91f, -0.17f, -0.31f);
        static readonly Quaternion ActualRotation = Quaternion.Euler(18f, 141f, -39f);

        [MenuItem("DragonBoatVR/实际桨姿态渲染所有权自检")]
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("intent tick and render retain accepted execution pose", TestOwner, ref passed, ref failed);
            Check("moved boat carries measured local pose without rewriting physics sample", TestMovingBoat, ref passed, ref failed);
            Check("invalid observations retain last actual render pose", TestInvalidObservations, ref passed, ref failed);
            Check("owner release and mode change restore original paths", TestRelease, ref passed, ref failed);
            Check("timeline reset clears ownership and previous render cache", TestReset, ref passed, ref failed);
            Debug.Log($"[PADDLE_EXECUTION_POSE] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[PADDLE_EXECUTION_POSE] RESULT=PASS execution pose ownership boundaries"
                : "[PADDLE_EXECUTION_POSE] RESULT=FAIL see individual checks");
        }

        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try { test(); passed++; Debug.Log($"[PADDLE_EXECUTION_POSE] {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"[PADDLE_EXECUTION_POSE] {name}: {error}"); }
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static void Pose(Transform actual, Vector3 position, Quaternion rotation, string message)
        {
            Require(Vector3.Distance(actual.position, position) < 0.00002f
                && Quaternion.Angle(actual.rotation, rotation) < 0.02f,
                message + $": position error={Vector3.Distance(actual.position, position):R}, angle={Quaternion.Angle(actual.rotation, rotation):R}");
        }
        static void Render(PaddleDriver driver)
        {
            MethodInfo method = typeof(PaddleDriver).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(method != null, "LateUpdate callback missing");
            method.Invoke(driver, null);
        }

        sealed class Fixture : IDisposable
        {
            readonly GameObject root = new GameObject("PaddleExecutionPoseSelfTest (software fixture)");
            public readonly BoatBuoyancy boat;
            public readonly Rigidbody body;
            public readonly PaddleDriver driver;
            uint sequence = 100;

            public Fixture()
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                var boatObject = new GameObject("rigidbody boat"); boatObject.transform.SetParent(root.transform, false);
                boat = boatObject.AddComponent<BoatBuoyancy>();
                body = boat.GetComponent<Rigidbody>(); body.useGravity = false; body.isKinematic = true;
                var paddleObject = new GameObject("execution paddle"); paddleObject.transform.SetParent(root.transform, false);
                driver = paddleObject.AddComponent<PaddleDriver>(); driver.boat = boat; driver.externallyStepped = true;
                driver.SetMode(PaddleDriver.Mode.VirtualPaddler); driver.SetPhase01(0.31f);
                driver.SetExecutionPoseOwner(true);
            }

            public PaddleSample Sample(Vector3 local, Quaternion rotation)
            {
                return new PaddleSample { sequence = ++sequence, timeSeconds = driver.Sample.timeSeconds + Dt,
                    bladePosition = boat.transform.TransformPoint(local), bladeRotation = boat.transform.rotation * rotation,
                    bladeVelocity = new Vector3(0.2f, -0.4f, 0.7f), bladeAngularVelocity = new Vector3(0.1f, 0.3f, -0.2f),
                    hasAngularVelocity = true, gripForceN = 12f, source = PaddleInputKind.Encoder,
                    originalSource = PaddleInputKind.Synthetic, valid = true };
            }
            public void Accept(Vector3 local, Quaternion rotation)
            { Require(driver.ApplyDecodedSample(Sample(local, rotation)), "fixture observation rejected"); }
            public void MoveBoat(Vector3 position, Quaternion rotation)
            {
                // Supply the rigid body's post-physics pose directly; no Physics.Simulate is used.
                body.position = position; body.rotation = rotation;
                body.transform.SetPositionAndRotation(position, rotation);
            }
            public void CheckLocal(Vector3 local, Quaternion rotation, string message)
                => Pose(driver.transform, boat.transform.TransformPoint(local), boat.transform.rotation * rotation, message);
            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void TestOwner()
        {
            using (var f = new Fixture())
            {
                f.driver.transform.SetPositionAndRotation(ActualLocal, ActualRotation);
                float before = f.driver.Phase01;
                f.driver.Tick(Dt); Render(f.driver);
                Pose(f.driver.transform, ActualLocal, ActualRotation, "ownership before first packet drew an ideal trajectory");
                Require(f.driver.Phase01 != before, "ownership stopped the intent phase clock");
                f.Accept(ActualLocal, ActualRotation);
                for (int i = 0; i < 8; i++)
                {
                    f.driver.SetExecutionPoseOwner(true); // Session repeats this each input step.
                    f.driver.Tick(Dt); Render(f.driver);
                    f.CheckLocal(ActualLocal, ActualRotation, "Tick/LateUpdate replaced actual pose with intent");
                }
                Vector3 next = ActualLocal + new Vector3(-0.07f, 0.08f, 0.12f);
                Quaternion nextRotation = Quaternion.Euler(-15f, 158f, -22f);
                f.Accept(next, nextRotation); f.driver.Tick(Dt); Render(f.driver);
                f.CheckLocal(next, nextRotation, "new execution observation did not replace the render pose");
            }
        }

        static void TestMovingBoat()
        {
            using (var f = new Fixture())
            {
                f.MoveBoat(new Vector3(1f, 0.2f, -2f), Quaternion.Euler(4f, 27f, -3f));
                f.Accept(ActualLocal, ActualRotation);
                string physicsSample = JsonUtility.ToJson(f.driver.Sample);
                float phase = f.driver.Phase01;
                f.MoveBoat(new Vector3(3f, -0.1f, 4f), Quaternion.Euler(12f, 65f, -8f));
                Render(f.driver);
                f.CheckLocal(ActualLocal, ActualRotation, "rendered actual local pose did not follow the moving hull");
                Require(JsonUtility.ToJson(f.driver.Sample) == physicsSample && f.driver.Phase01 == phase,
                    "render callback mutated the input observation or advanced intent time");
                Render(f.driver);
                f.CheckLocal(ActualLocal, ActualRotation, "repeated render callback accumulated boat motion");
            }
        }

        static void TestInvalidObservations()
        {
            using (var f = new Fixture())
            {
                f.Accept(ActualLocal, ActualRotation);
                for (int scenario = 0; scenario < 4; scenario++)
                {
                    f.driver.Tick(Dt);
                    PaddleSample invalid = f.Sample(new Vector3(-1.2f, 2f, 3f), Quaternion.Euler(70f, 20f, 10f));
                    if (scenario == 0) invalid.valid = false;
                    if (scenario == 1) invalid.bladePosition.x = float.NaN;
                    if (scenario == 2) invalid.bladeRotation = default;
                    if (scenario == 3) invalid.bladeVelocity.y = float.PositiveInfinity;
                    Require(!f.driver.ApplyDecodedSample(invalid), "invalid observation was accepted");
                    f.MoveBoat(new Vector3(0.1f * scenario, 0.2f, 0.3f * scenario), Quaternion.Euler(3f, 12f * scenario, -4f));
                    Render(f.driver);
                    f.CheckLocal(ActualLocal, ActualRotation, "invalid observation replaced the last actual render pose");
                    Require(!f.driver.Sample.valid && f.driver.Sample.bladeVelocity == Vector3.zero
                        && f.driver.Sample.bladeAngularVelocity == Vector3.zero && f.driver.Sample.gripForceN == 0f,
                        "render callback restored invalid input authority or effort");
                }
            }
        }

        static void TestRelease()
        {
            using (var f = new Fixture())
            {
                f.Accept(ActualLocal, ActualRotation);
                f.driver.SetExecutionPoseOwner(false);
                Require(!f.driver.ExecutionFeedbackOwnsPose, "explicit owner release was ignored");
                f.driver.Tick(Dt);
                Vector3 intended = f.driver.Sample.bladePosition; Quaternion intendedRotation = f.driver.Sample.bladeRotation;
                Require(Vector3.Distance(intended, ActualLocal) > 0.05f, "fixture did not distinguish intended and actual poses");
                f.driver.transform.SetPositionAndRotation(ActualLocal, ActualRotation);
                Render(f.driver);
                Pose(f.driver.transform, intended, intendedRotation, "original VirtualPaddler render path did not resume");

                f.driver.SetExecutionPoseOwner(true); // A new ownership interval must not resurrect old feedback.
                Render(f.driver);
                Pose(f.driver.transform, intended, intendedRotation, "new ownership reused a previous session render pose");
                f.Accept(ActualLocal, ActualRotation);
                f.driver.SetMode(PaddleDriver.Mode.External);
                Require(!f.driver.ExecutionFeedbackOwnsPose, "mode switch retained execution ownership");
                f.driver.Tick(Dt); // Establish the current driver timeline with no external packet yet.
                PaddleSample external = f.Sample(new Vector3(0.7f, 0.8f, -0.1f), Quaternion.Euler(11f, 38f, 29f));
                external.timeSeconds = f.driver.Sample.timeSeconds + Dt;
                Require(f.driver.SetExternalSample(external), "external sample rejected after owner release");
                f.driver.Tick(Dt); Render(f.driver);
                Require(f.driver.Sample.valid && f.driver.Sample.source == PaddleInputKind.External, "external mode did not regain authority");
                Pose(f.driver.transform, external.bladePosition, external.bladeRotation, "old execution pose overrode external input");
            }
        }

        static void TestReset()
        {
            using (var f = new Fixture())
            {
                f.Accept(ActualLocal, ActualRotation);
                f.driver.ResetInputTimeline();
                Require(!f.driver.ExecutionFeedbackOwnsPose && !f.driver.Sample.valid && f.driver.Phase01 == 0f,
                    "timeline reset retained execution ownership, sample authority or phase");
                Vector3 waiting = new Vector3(0.8f, 0.9f, 0.2f);
                Quaternion waitingRotation = Quaternion.Euler(9f, 34f, 12f);
                f.driver.transform.SetPositionAndRotation(waiting, waitingRotation);
                f.driver.SetExecutionPoseOwner(true);
                Render(f.driver);
                Pose(f.driver.transform, waiting, waitingRotation, "timeline reset left a previous execution render cache");
            }
        }
    }
}
