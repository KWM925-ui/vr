using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Analytic observation-frame checks. No physics simulation or scene asset changes.</summary>
    public static class CableObservationPoseSelfTest
    {
        const float Dt = 0.01f;
        const float Tolerance = 0.002f;

        [MenuItem("DragonBoatVR/绳路观测坐标一致性自检")]
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("explicit pose survives live scene motion without sticky state", TestSnapshot, ref passed, ref failed);
            Check("world rigid transform preserves tension and transforms wrench", TestCovariance, ref passed, ref failed);
            Check("invalid observation revokes previous output", TestInvalidPose, ref passed, ref failed);
            Check("request and tendon limits remain enforced", TestConstraints, ref passed, ref failed);
            Check("execution reference identity and expiry clear stale metrics without changing allocation", TestExecutionReference, ref passed, ref failed);
            Debug.Log($"[CABLE_OBSERVATION] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[CABLE_OBSERVATION] RESULT=PASS same-observation allocation"
                : "[CABLE_OBSERVATION] RESULT=FAIL see individual checks");
        }

        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                test(); passed++; Debug.Log($"[CABLE_OBSERVATION] {name}=True");
            }
            catch (Exception error) { failed++; Debug.LogError($"[CABLE_OBSERVATION] {name}: {error}"); }
        }

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static bool Near(float a, float b) => SimulationValue.Finite(a) && Mathf.Abs(a - b) < Tolerance;
        static bool Near(Vector3 a, Vector3 b) => SimulationValue.Finite(a) && (a - b).magnitude < Tolerance;

        static CableDriveTwin Fixture()
        {
            var boat = new GameObject("observation boat").AddComponent<BoatBuoyancy>();
            var paddle = new GameObject("observation paddle").AddComponent<PaddleHydrodynamics>();
            var cable = new GameObject("observation cable").AddComponent<CableDriveTwin>();
            cable.boat = boat; cable.paddle = paddle; cable.externallyStepped = true;
            cable.sendUdp = cable.showCables = cable.showMotorUnits = false; cable.sensorNoiseN = 0f;
            cable.rig = new TendonRigConfig { charLengthM = 0.6f, tendons = new[]
            {
                new TendonSpec { motorId = 1, anchorLocal = new Vector3(1f, 0.4f, 0f),
                    attachLocal = new Vector3(0f, 0.4f, 0f), preTensionN = 5f, maxTensionN = 30f }
            } };
            return cable;
        }

        static ForceFeedbackRequest Request(float force = 20f, uint sequence = 7) => new ForceFeedbackRequest
        {
            commandSequence = sequence, inputSequence = 4, timeSeconds = 0.2d, valid = true,
            gripWorldPosition = Vector3.up, forceWorldN = Vector3.right * force,
            torqueAtGripWorldNm = Vector3.forward * (0.6f * force)
        };

        static void IdentityReceive(CableDriveTwin cable, ForceFeedbackRequest request, float dt = Dt)
            => cable.ReceiveAtPose(request, dt, Matrix4x4.identity, Vector3.zero, Quaternion.identity);

        static void CheckAnalytic(CableDriveTwin cable, float tension, Quaternion worldRotation)
        {
            Require(cable.Feedback.valid, "analytic request was rejected: " + cable.Feedback.status);
            Require(Near(cable.motorTensionsN[0], tension) && Near(cable.cableLengthsMm[0], 1000f),
                "wrong analytic tension or cable length");
            Require(Near(cable.Feedback.forceWorldN, worldRotation * (Vector3.right * tension))
                && Near(cable.Feedback.torqueAtGripWorldNm, worldRotation * (Vector3.forward * (0.6f * tension))),
                "force or grip-reference moment differs from the analytic cable");
        }

        static void TestSnapshot()
        {
            var cable = Fixture(); var request = Request();
            cable.Receive(request, Dt); CheckAnalytic(cable, 20f, Quaternion.identity);
            var movedBoat = new Vector3(2f, 1f, 3f); var movedBlade = new Vector3(-2f, 4f, 1f);
            var boatRotation = Quaternion.Euler(20f, 50f, 10f); var bladeRotation = Quaternion.Euler(30f, -15f, 5f);
            cable.boat.transform.SetPositionAndRotation(movedBoat, boatRotation);
            cable.paddle.transform.SetPositionAndRotation(movedBlade, bladeRotation);
            IdentityReceive(cable, request); CheckAnalytic(cable, 20f, Quaternion.identity);
            Require(cable.boat.transform.position == movedBoat && cable.paddle.transform.position == movedBlade
                && Quaternion.Angle(cable.boat.transform.rotation, boatRotation) < 0.01f
                && Quaternion.Angle(cable.paddle.transform.rotation, bladeRotation) < 0.01f,
                "allocation mutated live scene transforms");
            cable.Receive(request, Dt);
            Require(cable.Feedback.valid && !Near(cable.Feedback.forceWorldN, Vector3.right * 20f),
                "ordinary Receive retained a prior explicit observation");
            cable.boat.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            cable.paddle.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            cable.Receive(request, Dt); CheckAnalytic(cable, 20f, Quaternion.identity);
        }

        static void TestCovariance()
        {
            var cable = Fixture();
            Quaternion rotation = Quaternion.Euler(27f, -48f, 19f);
            Vector3 translation = new Vector3(4f, -2f, 3f);
            Matrix4x4 world = Matrix4x4.TRS(translation, rotation, Vector3.one);
            var request = Request();
            request.gripWorldPosition = world.MultiplyPoint3x4(request.gripWorldPosition);
            request.forceWorldN = rotation * request.forceWorldN;
            request.torqueAtGripWorldNm = rotation * request.torqueAtGripWorldNm;
            cable.ReceiveAtPose(request, Dt, world, translation, rotation);
            CheckAnalytic(cable, 20f, rotation);
            Require(!cable.Feedback.capabilityLimited && Near(cable.Feedback.forceResidualN, 0f)
                && Near(cable.Feedback.torqueResidualNm, 0f), "rigid frame change introduced a residual");
        }

        static void CheckZero(CableDriveTwin cable, uint sequence, string status)
        {
            Require(!cable.Feedback.valid && cable.Feedback.commandSequence == sequence && cable.Feedback.status == status,
                "invalid observation/request retained validity or lost rejection identity");
            Require(cable.Feedback.forceWorldN == Vector3.zero && cable.Feedback.torqueAtGripWorldNm == Vector3.zero,
                "invalid observation/request retained wrench");
            foreach (float tension in cable.motorTensionsN) Require(tension == 0f, "invalid observation retained motor tension");
        }

        static void TestInvalidPose()
        {
            var cable = Fixture();
            for (int scenario = 0; scenario < 7; scenario++)
            {
                IdentityReceive(cable, Request()); CheckAnalytic(cable, 20f, Quaternion.identity);
                Matrix4x4 boat = Matrix4x4.identity; Vector3 blade = Vector3.zero; Quaternion rotation = Quaternion.identity;
                if (scenario == 0) boat.m03 = float.NaN;
                if (scenario == 1) boat.m00 = 2f;
                if (scenario == 2) boat.m00 = -1f;
                if (scenario == 3) boat.m30 = 0.1f;
                if (scenario == 4) blade.z = float.PositiveInfinity;
                if (scenario == 5) rotation = new Quaternion(0f, 0f, 0f, 0f);
                if (scenario == 6) rotation = new Quaternion(0f, 0f, 0f, 2f);
                var request = Request(20f, (uint)(20 + scenario));
                cable.ReceiveAtPose(request, Dt, boat, blade, rotation);
                CheckZero(cable, request.commandSequence, "INVALID_OR_MISSING_GEOMETRY");
            }
        }

        static void TestExecutionReference()
        {
            var cable = Fixture();
            IdentityReceive(cable, Request());
            var solverBefore = cable.CaptureReplayState();
            var observation = new CableDeviceFeedback { commandSequence = 19, valid = true,
                status = "ANALYTIC_EXECUTION", lengthsMm = new[] { 1000f }, tensionsN = new[] { 10f },
                forceWorldN = Vector3.right * 10f, torqueAtGripWorldNm = Vector3.forward * 6f,
                forceResidualN = 10f, torqueResidualNm = 6f };
            cable.ApplyControllerObservation(observation, 19, Vector3.right * 20f, Vector3.forward * 12f);
            Require(Near(cable.allocationRatio, 0.5f) && Near(cable.forceRestorationRate, 0.5f)
                && Near(cable.restorationRate, 0.5f) && Near(cable.directionErrorDegrees, 0f),
                "half execution retained full allocation metrics");
            // Execution differs from allocation, so the allocation-only replay
            // snapshot contract does not apply. Inspect the solver caches read-only.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic;
            float[] SolverValues(string field) => (float[])typeof(CableDriveTwin).GetField(field, flags).GetValue(cable);
            var targetAfter = SolverValues("_target");
            var sumAfter = SolverValues("_sum");
            var tensionsAfter = SolverValues("_t");
            for (int i = 0; i < solverBefore.solverTarget.Length; i++)
                Require(solverBefore.solverTarget[i] == targetAfter[i]
                    && solverBefore.solverSum[i] == sumAfter[i], "execution report changed allocator caches");
            for (int i = 0; i < solverBefore.solverTensions.Length; i++)
                Require(solverBefore.solverTensions[i] == tensionsAfter[i], "execution report changed allocation tension");
            for (int scenario = 0; scenario < 5; scenario++)
            {
                observation.valid = true;
                cable.ApplyControllerObservation(observation, 19, Vector3.right * 20f, Vector3.forward * 12f);
                if (scenario == 0) cable.ApplyControllerObservation(observation, 20, Vector3.right * 20f, Vector3.forward * 12f);
                if (scenario == 1) cable.ApplyControllerObservation(observation);
                if (scenario == 2) { observation.valid = false; cable.ApplyControllerObservation(observation, 19, Vector3.right * 20f, Vector3.forward * 12f); }
                if (scenario == 3) cable.ApplyControllerObservation(observation, 19, new Vector3(float.NaN, 0, 0), Vector3.zero);
                if (scenario == 4) cable.ApplyControllerObservation(null, 19, Vector3.right * 20f, Vector3.forward * 12f);
                Require(cable.allocationRatio == 0f && cable.restorationRate == 0f
                    && cable.forceRestorationRate == 0f && cable.directionErrorDegrees == 0f,
                    "missing, mismatched or invalid execution reference retained previous metrics");
            }
            IdentityReceive(cable, Request()); CheckAnalytic(cable, 20f, Quaternion.identity);
            Require(Near(cable.allocationRatio, 1f) && Near(cable.restorationRate, 1f), "next allocation retained execution metrics");
        }

        static void TestConstraints()
        {
            var cable = Fixture();
            IdentityReceive(cable, Request(100f)); CheckAnalytic(cable, 30f, Quaternion.identity);
            Require(cable.Feedback.capabilityLimited && Near(cable.Feedback.forceResidualN, 70f), "upper bound/residual changed");
            IdentityReceive(cable, Request(0f)); CheckAnalytic(cable, 5f, Quaternion.identity);
            Require(cable.Feedback.capabilityLimited && Near(cable.Feedback.forceResidualN, 5f), "pretension/residual changed");
            for (int scenario = 0; scenario < 3; scenario++)
            {
                IdentityReceive(cable, Request());
                var request = Request(20f, (uint)(30 + scenario)); float dt = Dt;
                if (scenario == 0) request.valid = false;
                if (scenario == 1) request.forceWorldN.x = float.NaN;
                if (scenario == 2) dt = float.NaN;
                IdentityReceive(cable, request, dt); CheckZero(cable, request.commandSequence, "INVALID_REQUEST");
            }
            var spec = cable.rig.tendons[0];
            cable.ApplyRig(new TendonRigConfig { charLengthM = 0.6f, tendons = new[] { spec, spec } });
            IdentityReceive(cable, Request(20f, 40)); CheckZero(cable, 40, "UNSUPPORTED_SHARED_MOTOR_COUPLING");
        }
    }
}
