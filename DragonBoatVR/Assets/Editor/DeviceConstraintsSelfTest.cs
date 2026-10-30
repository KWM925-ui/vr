using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Deterministic software oracles: analytic one-cable wrench and explicit vertical platform legs.
    /// Successful checks establish constraint handling, not real motor/platform calibration or arbitrary 6D capability.</summary>
    public static class DeviceConstraintsSelfTest
    {
        const float Dt = 0.01f;
        const float Epsilon = 0.002f;

        [MenuItem("DragonBoatVR/⑨ 设备约束与异常自检")]
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("bounded solver / pretension / final composition", TestSolver, ref passed, ref failed);
            Check("analytic force + torque / saturation / invalid zero", TestCable, ref passed, ref failed);
            Check("shared motor coupling / disabled and empty rigs", TestCoupling, ref passed, ref failed);
            Check("platform inverse geometry / lag / stroke rejection", TestPlatform, ref passed, ref failed);
            Check("configuration isolation / invalid neutral", TestConfiguration, ref passed, ref failed);
            Debug.Log($"[DEVICE] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[DEVICE] RESULT=PASS analytic software constraint oracles"
                : "[DEVICE] RESULT=FAIL see individual checks");
        }

        static void Check(string name, Func<bool> test, ref int passed, ref int failed)
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                bool result = test();
                if (result) passed++; else failed++;
                Debug.Log($"[DEVICE] {name}={result}");
            }
            catch (Exception error) { failed++; Debug.LogError($"[DEVICE] {name}: {error}"); }
        }

        static bool Near(float a, float b) => SimulationValue.Finite(a) && SimulationValue.Finite(b) && Mathf.Abs(a - b) < Epsilon;
        static bool Near(Vector3 a, Vector3 b) => SimulationValue.Finite(a) && SimulationValue.Finite(b) && (a - b).magnitude < Epsilon;
        static bool Zero(float[] values)
        {
            if (values == null) return false;
            foreach (float value in values) if (value != 0f) return false;
            return true;
        }

        static bool TestSolver()
        {
            var columns = new[] { new[] { 1f, 0f, 0f, 0f, 0f, 1f } };
            var target = new[] { 10f, 0f, 0f, 0f, 0f, 10f };
            var tensions = new[] { 99f };
            var lower = new[] { 5f }; var upper = new[] { 15f }; var sum = new float[6];
            bool exact = TendonRig.SolveBounded(columns, target, tensions, lower, upper) && Near(tensions[0], 10f)
                && TendonRig.TryCompose(columns, tensions, sum) && Near(sum[0], 10f) && Near(sum[5], 10f);
            target[0] = target[5] = 100f;
            bool saturated = TendonRig.SolveBounded(columns, target, tensions, lower, upper)
                && Near(tensions[0], 15f) && TendonRig.TryCompose(columns, tensions, sum)
                && Near(sum[0], 15f) && Near(sum[5], 15f);
            target[0] = target[5] = 0f;
            bool preloaded = TendonRig.SolveBounded(columns, target, tensions, lower, upper) && Near(tensions[0], 5f);
            target[2] = float.NaN;
            bool nanRejected = !TendonRig.SolveBounded(columns, target, tensions, lower, upper) && Zero(tensions);
            target[2] = 0f; lower[0] = 20f; tensions[0] = 99f;
            bool limitsRejected = !TendonRig.SolveBounded(columns, target, tensions, lower, upper) && Zero(tensions);
            sum[0] = 99f;
            bool malformed = !TendonRig.TryCompose(new[] { new float[2] }, new[] { 1f }, sum) && Zero(sum);
            bool overflow = !TendonRig.TryCompose(new[] { new[] { float.MaxValue, 0f, 0f, 0f, 0f, 0f } }, new[] { 2f }, sum) && Zero(sum);
            return exact && saturated && preloaded && nanRejected && limitsRejected && malformed && overflow;
        }

        static CableDriveTwin CreateCable(TendonSpec[] specs)
        {
            var boat = new GameObject("analytic boat frame").AddComponent<BoatBuoyancy>();
            var paddle = new GameObject("analytic paddle frame").AddComponent<PaddleHydrodynamics>();
            var cable = new GameObject("cable device").AddComponent<CableDriveTwin>();
            cable.paddle = paddle; cable.boat = boat; cable.showCables = false; cable.sendUdp = false;
            cable.sensorNoiseN = 0f; cable.externallyStepped = true;
            cable.rig = new TendonRigConfig { charLengthM = 0.6f, tendons = specs };
            return cable;
        }

        static TendonSpec OneCable(int motorId = 1) => new TendonSpec { motorId = motorId,
            anchorLocal = new Vector3(1f, 0.4f, 0f), attachLocal = new Vector3(0f, 0.4f, 0f),
            preTensionN = 5f, maxTensionN = 30f };
        static ForceFeedbackRequest CableRequest(float force, uint sequence = 7) => new ForceFeedbackRequest
        {
            commandSequence = sequence, inputSequence = 4, timeSeconds = 0.2d, valid = true,
            gripWorldPosition = Vector3.up, forceWorldN = Vector3.right * force,
            torqueAtGripWorldNm = Vector3.forward * (0.6f * force)
        };

        static bool TestCable()
        {
            CableDriveTwin cable = CreateCable(new[] { OneCable() });
            cable.Receive(CableRequest(20f), Dt);
            bool exact = cable.Feedback.valid && !cable.Feedback.capabilityLimited && cable.Feedback.simulated
                && cable.Feedback.commandSequence == 7 && cable.Feedback.timeSeconds == 0.2d
                && Near(cable.motorTensionsN[0], 20f) && Near(cable.cableLengthsMm[0], 1000f)
                && Near(cable.Feedback.forceWorldN, new Vector3(20, 0, 0))
                && Near(cable.Feedback.torqueAtGripWorldNm, new Vector3(0, 0, 12))
                && Near(cable.forceRestorationRate, 1f) && Near(cable.directionErrorDegrees, 0f);
            cable.Feedback.tensionsN[0] = 999f;
            bool separateSnapshot = Near(cable.motorTensionsN[0], 20f);
            cable.Receive(CableRequest(100f, 8), Dt);
            bool saturated = cable.Feedback.valid && cable.Feedback.capabilityLimited
                && Near(cable.motorTensionsN[0], 30f) && Near(cable.Feedback.forceWorldN.x, 30f)
                && Near(cable.Feedback.torqueAtGripWorldNm.z, 18f)
                && Near(cable.Feedback.forceResidualN, 70f) && Near(cable.Feedback.torqueResidualNm, 42f);
            cable.Receive(CableRequest(-20f, 9), Dt);
            bool impossibleDirection = cable.Feedback.valid && cable.Feedback.capabilityLimited
                && Near(cable.motorTensionsN[0], 5f) && Near(cable.Feedback.forceResidualN, 25f)
                && Near(cable.directionErrorDegrees, 180f) && Near(cable.forceRestorationRate, -0.25f);
            cable.Receive(CableRequest(0f, 10), Dt);
            bool preloaded = cable.Feedback.capabilityLimited && Near(cable.motorTensionsN[0], 5f)
                && Near(cable.Feedback.forceResidualN, 5f) && Near(cable.Feedback.torqueAtGripWorldNm.z, 3f);
            var invalid = CableRequest(20f, 11); invalid.forceWorldN.x = float.NaN;
            cable.Receive(invalid, Dt);
            bool zero = !cable.Feedback.valid && cable.Feedback.simulated && cable.Feedback.commandSequence == 11
                && Zero(cable.motorTensionsN) && Zero(cable.Feedback.tensionsN) && Zero(cable.cableLengthsMm)
                && Near(cable.Feedback.forceWorldN, Vector3.zero) && Near(cable.Feedback.torqueAtGripWorldNm, Vector3.zero);
            cable.Receive(CableRequest(20f), Dt);
            cable.Receive(CableRequest(20f), float.NaN);
            bool invalidDt = !cable.Feedback.valid && Zero(cable.motorTensionsN);
            return exact && separateSnapshot && saturated && impossibleDirection && preloaded && zero && invalidDt;
        }

        static bool TestCoupling()
        {
            var cable = CreateCable(new[] { OneCable(1), OneCable(1) });
            cable.Receive(CableRequest(20), Dt);
            bool coupled = !cable.Feedback.valid && cable.Feedback.capabilityLimited && Zero(cable.motorTensionsN)
                && cable.Feedback.status == "UNSUPPORTED_SHARED_MOTOR_COUPLING";
            cable.rig.tendons[1].motorId = 2;
            cable.Receive(CableRequest(20), Dt);
            bool changedMotor = cable.Feedback.valid && Near(cable.Feedback.forceWorldN.x, 20f);
            cable.rig.tendons[1].motorId = 1;
            cable.Receive(CableRequest(20), Dt);
            bool changedBack = !cable.Feedback.valid && Zero(cable.motorTensionsN);
            var disabled = OneCable(1); disabled.enabled = false;
            cable.ApplyRig(new TendonRigConfig { tendons = new[] { OneCable(1), disabled } });
            bool configurationClears = !cable.Feedback.valid && Zero(cable.motorTensionsN);
            cable.Receive(CableRequest(20), Dt);
            bool disabledExcluded = cable.tendonCount == 1 && cable.Feedback.valid;
            cable.ApplyRig(new TendonRigConfig { tendons = new TendonSpec[0] });
            cable.Receive(CableRequest(20), Dt);
            bool emptyRejected = cable.tendonCount == 0 && !cable.Feedback.valid && Zero(cable.motorTensionsN);
            return coupled && changedMotor && changedBack && configurationClears && disabledExcluded && emptyRejected;
        }

        static PlatformRigConfig VerticalPlatform()
        {
            var config = new PlatformRigConfig { provenance = "Analytic vertical-leg software fixture", measured = false,
                useExplicitJointGeometry = true, neutralHeight = 0.6f, servoLagTimeConstant = 0.02f,
                explicitBaseJointsLocal = new Vector3[6], explicitTopJointsLocal = new Vector3[6] };
            for (int i = 0; i < 6; i++)
            {
                var joint = new Vector3((i % 3 - 1) * 0.2f, 0f, (i < 3 ? -1 : 1) * 0.2f);
                config.explicitBaseJointsLocal[i] = joint; config.explicitTopJointsLocal[i] = joint;
                config.minActuatorLengthsMm[i] = 500f; config.maxActuatorLengthsMm[i] = 700f;
            }
            return config;
        }
        static PlatformMotionRequest PlatformRequest(float heave, uint sequence = 23) => new PlatformMotionRequest
        { commandSequence = sequence, timeSeconds = 0.2d, valid = true,
            translationM = Vector3.up * heave, rawTranslationM = Vector3.up * heave };
        static StewartPlatformTwin CreatePlatform()
        {
            var platform = new GameObject("platform device").AddComponent<StewartPlatformTwin>();
            platform.autoBuildModel = false; platform.loadRuntimeConfiguration = false; platform.externallyStepped = true;
            return platform;
        }

        static bool TestPlatform()
        {
            var platform = CreatePlatform();
            bool configured = platform.ApplyConfiguration(VerticalPlatform());
            platform.Receive(PlatformRequest(0.03f), Dt);
            // alpha = .01/(.02+.01) = 1/3, so 30 mm desired heave produces 10 mm simulated heave.
            bool lag = platform.Feedback.valid && platform.Feedback.simulated && platform.Feedback.commandSequence == 23
                && Near(platform.actualTranslationM.y, 0.01f) && Near(platform.targetActuatorLengthsMm[0], 630f);
            foreach (float length in platform.Feedback.actuatorLengthsMm) lag &= Near(length, 610f);
            bool rankLimited = platform.kinematicRank < 6 && platform.Feedback.capabilityLimited
                && platform.Feedback.status.Contains("CAPABILITY_LIMITED") && platform.Feedback.status.Contains("UNMEASURED");
            platform.Receive(PlatformRequest(0.15f, 24), Dt);
            bool strokeRejected = !platform.Feedback.valid && platform.Feedback.capabilityLimited
                && platform.Feedback.commandSequence == 24 && platform.Feedback.status.Contains("ACTUATOR_LIMIT_REQUEST_REJECTED_HOLD")
                && Near(platform.actualTranslationM.y, 0.01f) && Near(platform.Feedback.actuatorLengthsMm[0], 610f);
            var invalid = PlatformRequest(0.03f, 25); invalid.translationM.y = float.PositiveInfinity;
            platform.Receive(invalid, Dt);
            bool held = !platform.Feedback.valid && platform.Feedback.commandSequence == 25
                && Near(platform.actualTranslationM.y, 0.01f) && !platform.LastRequest.valid
                && SimulationValue.Finite(platform.LastRequest.translationM);
            return configured && lag && rankLimited && strokeRejected && held;
        }

        static bool TestConfiguration()
        {
            var platform = CreatePlatform(); var config = VerticalPlatform();
            bool first = platform.ApplyConfiguration(config);
            config.explicitBaseJointsLocal[0] = Vector3.one * 99;
            config.minActuatorLengthsMm[0] = 99999;
            platform.Receive(PlatformRequest(0f), Dt);
            bool isolated = platform.Feedback.valid && Near(platform.Feedback.actuatorLengthsMm[0], 600f);
            var impossible = VerticalPlatform(); impossible.minActuatorLengthsMm[0] = 650;
            bool neutralRejected = !platform.ApplyConfiguration(impossible) && !platform.Feedback.valid;
            platform.Receive(PlatformRequest(0.1f), Dt);
            bool cannotContinue = !platform.Feedback.valid && Near(platform.actualTranslationM.y, 0f);
            var measured = VerticalPlatform(); measured.measured = true;
            bool recovery = platform.ApplyConfiguration(measured);
            platform.Receive(PlatformRequest(0f), Dt);
            bool stillSimulated = platform.Feedback.valid && platform.Feedback.simulated
                && platform.Feedback.status.Contains("MEASURED_CONFIGURATION_SOURCE");
            return first && isolated && neutralRejected && cannotContinue && recovery && stillSimulated;
        }
    }
}
