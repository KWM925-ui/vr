using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Exercise the command handed to an adapter after platform validation.
    /// Rejected requests must hold both the model and a consumer that executes only valid commands.</summary>
    public static class PlatformRejectionSelfTest
    {
        const float Dt = 0.01f;

        [MenuItem("DragonBoatVR/平台拒绝请求转发自检")]
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("accepted command reaches adapter", TestAccepted, ref passed, ref failed);
            Check("invalid request and timestep hold adapter", TestInvalidRequest, ref passed, ref failed);
            Check("invalid configuration revokes prior command", TestInvalidConfiguration, ref passed, ref failed);
            Check("invalid live geometry and mapping hold adapter", TestInvalidGeometry, ref passed, ref failed);
            Check("target actuator limit holds adapter", TestTargetLimit, ref passed, ref failed);
            Check("intermediate actuator limit holds adapter", TestResponseLimit, ref passed, ref failed);
            Debug.Log($"[PLATFORM_REJECTION] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[PLATFORM_REJECTION] RESULT=PASS rejected commands cannot execute through an adapter"
                : "[PLATFORM_REJECTION] RESULT=FAIL see individual checks");
        }

        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                test();
                passed++;
                Debug.Log($"[PLATFORM_REJECTION] {name}=True");
            }
            catch (Exception error)
            {
                failed++;
                Debug.LogError($"[PLATFORM_REJECTION] {name}: {error}");
            }
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        // This consumer deliberately knows nothing about the mapper's Feedback. It follows
        // the public IPlatformMotionDevice contract used by SimulationSession's adapter path.
        sealed class AdapterConsumer : IPlatformMotionDevice
        {
            public int Executed { get; private set; }
            public Vector3 Position { get; private set; }
            public Vector3 Euler { get; private set; }
            public PlatformDeviceFeedback Feedback { get; private set; }

            public void Receive(PlatformMotionRequest request, float dt)
            {
                if (request.valid)
                {
                    Executed++;
                    Position = request.translationM;
                    Euler = request.eulerDegrees;
                }
                Feedback = new PlatformDeviceFeedback { commandSequence = request.commandSequence,
                    timeSeconds = request.timeSeconds, valid = request.valid, simulated = true,
                    translationM = Position, eulerDegrees = Euler };
            }
        }

        sealed class Fixture : IDisposable
        {
            readonly GameObject _object;
            public readonly StewartPlatformTwin Platform;
            public readonly AdapterConsumer Adapter = new AdapterConsumer();

            public Fixture(float minimumLengthMm = 500f)
            {
                _object = new GameObject("PlatformRejectionFixture");
                Platform = _object.AddComponent<StewartPlatformTwin>();
                Platform.autoBuildModel = false;
                Platform.loadRuntimeConfiguration = false;
                Platform.externallyStepped = true;
                Require(Platform.ApplyConfiguration(Configuration(minimumLengthMm)), "fixture configuration rejected");
            }

            public void Forward() => Adapter.Receive(Platform.Request, Dt);

            public void Accept(PlatformMotionRequest request, float dt = Dt)
            {
                Platform.Receive(request, dt);
                Require(Platform.Feedback.valid && Platform.Request.valid, "positive control request rejected");
                int previous = Adapter.Executed;
                Forward();
                Require(Adapter.Executed == previous + 1 && Adapter.Feedback.valid,
                    "positive control did not execute in adapter");
            }

            public void Dispose() => UnityEngine.Object.DestroyImmediate(_object);
        }

        static PlatformRigConfig Configuration(float minimumLengthMm = 500f)
        {
            var config = new PlatformRigConfig { provenance = "Analytic software fixture for platform rejection forwarding",
                measured = false, useExplicitJointGeometry = true, neutralHeight = 0.6f,
                servoLagTimeConstant = 0.02f, explicitBaseJointsLocal = new Vector3[6],
                explicitTopJointsLocal = new Vector3[6] };
            for (int i = 0; i < 6; i++)
            {
                var joint = new Vector3((i % 3 - 1) * 0.2f, 0f, (i < 3 ? -1 : 1) * 0.2f);
                config.explicitBaseJointsLocal[i] = joint;
                config.explicitTopJointsLocal[i] = joint;
                config.minActuatorLengthsMm[i] = minimumLengthMm;
                config.maxActuatorLengthsMm[i] = 700f;
            }
            return config;
        }

        static PlatformMotionRequest Request(Vector3 position, uint sequence = 1) => new PlatformMotionRequest
        {
            commandSequence = sequence, timeSeconds = sequence * 0.01d, valid = true,
            translationM = position, rawTranslationM = position
        };

        static void CheckHeld(Fixture fixture, Action reject, string expectedStatus)
        {
            var before = fixture.Platform.Feedback;
            int executed = fixture.Adapter.Executed;
            Vector3 adapterPosition = fixture.Adapter.Position, adapterEuler = fixture.Adapter.Euler;
            reject();
            var feedback = fixture.Platform.Feedback;
            Require(!feedback.valid && feedback.status.StartsWith(expectedStatus, StringComparison.Ordinal),
                "wrong rejection: " + feedback.status);
            // Forward exactly what SimulationSession forwards, without consulting Feedback.valid.
            fixture.Forward();
            Require(!fixture.Platform.Request.valid && !fixture.Adapter.Feedback.valid && fixture.Adapter.Executed == executed,
                "rejected command executed in downstream adapter");
            Require(fixture.Platform.Request.commandSequence == feedback.commandSequence &&
                fixture.Platform.Request.timeSeconds == feedback.timeSeconds,
                "rejected request and feedback identities differ");
            Require(fixture.Adapter.Position == adapterPosition && fixture.Adapter.Euler == adapterEuler,
                "adapter moved after rejected request");
            Require(feedback.translationM == before.translationM && feedback.eulerDegrees == before.eulerDegrees &&
                fixture.Platform.actualTranslationM == before.translationM && fixture.Platform.actualEuler == before.eulerDegrees,
                "rejection changed platform pose");
            for (int i = 0; i < 6; i++)
                Require(feedback.actuatorLengthsMm[i] == before.actuatorLengthsMm[i],
                    "rejection changed actuator length " + i);
        }

        static void TestAccepted()
        {
            using (var fixture = new Fixture())
            {
                fixture.Accept(Request(Vector3.up * 0.03f));
                Require(fixture.Adapter.Position == Vector3.up * 0.03f,
                    "adapter did not receive accepted target");
                Require(Mathf.Abs(fixture.Platform.actualTranslationM.y - 0.01f) < 0.000001f,
                    "one-third servo response changed");
            }
        }

        static void TestInvalidRequest()
        {
            for (int scenario = 0; scenario < 5; scenario++)
                using (var fixture = new Fixture())
                {
                    fixture.Accept(Request(Vector3.up * 0.03f));
                    var request = Request(Vector3.up * 0.02f, 2);
                    float dt = Dt;
                    if (scenario == 0) request.valid = false;
                    if (scenario == 1) request.timeSeconds = double.NaN;
                    if (scenario == 2) request.translationM.x = float.PositiveInfinity;
                    if (scenario == 3) dt = 0f;
                    if (scenario == 4) dt = float.NaN;
                    CheckHeld(fixture, () => fixture.Platform.Receive(request, dt), "INVALID_REQUEST_HOLD");
                }
        }

        static void TestInvalidConfiguration()
        {
            for (int scenario = 0; scenario < 4; scenario++)
                using (var fixture = new Fixture())
                {
                    fixture.Accept(Request(Vector3.up * 0.03f));
                    var config = Configuration();
                    if (scenario == 0) config = null;
                    if (scenario == 1) config.maxActuatorLengthsMm = new float[5];
                    if (scenario == 2) config.servoLagTimeConstant = float.NaN;
                    if (scenario == 3) config.minActuatorLengthsMm[0] = 650f;
                    string status = scenario == 3 ? "NEUTRAL_ACTUATOR_LIMIT_CONFIGURATION_REJECTED_HOLD"
                        : "INVALID_CONFIGURATION_HOLD";
                    CheckHeld(fixture, () => Require(!fixture.Platform.ApplyConfiguration(config),
                        "invalid configuration accepted"), status);
                    CheckHeld(fixture, () => fixture.Platform.Receive(Request(Vector3.up * 0.02f, 3), Dt),
                        "INVALID_GEOMETRY_OR_LIMITS_HOLD");
                }
        }

        static void TestInvalidGeometry()
        {
            var mutations = new Action<StewartPlatformTwin>[]
            {
                p => p.explicitBaseJointsLocal[0] = new Vector3(float.NaN, 0f, 0f),
                p => p.maxActuatorLengthsMm[0] = 400f,
                p => p.neutralHeight = float.NaN,
                p => p.servoLagTimeConstant = 0f
            };
            foreach (var mutate in mutations)
                using (var fixture = new Fixture())
                {
                    fixture.Accept(Request(Vector3.up * 0.03f));
                    mutate(fixture.Platform);
                    CheckHeld(fixture, () => fixture.Platform.Receive(Request(Vector3.up * 0.02f, 2), Dt),
                        "INVALID_GEOMETRY_OR_LIMITS_HOLD");
                }
        }

        static void TestTargetLimit()
        {
            using (var fixture = new Fixture())
            {
                fixture.Accept(Request(Vector3.up * 0.03f));
                CheckHeld(fixture, () => fixture.Platform.Receive(Request(Vector3.up * 0.15f, 2), Dt),
                    "ACTUATOR_LIMIT_REQUEST_REJECTED_HOLD");
                Require(fixture.Platform.Request.translationM == Vector3.up * 0.15f &&
                    Mathf.Abs(fixture.Platform.targetActuatorLengthsMm[0] - 750f) < 0.002f,
                    "rejected target diagnostic was lost");
            }
        }

        static void TestResponseLimit()
        {
            using (var fixture = new Fixture(585f))
            {
                // Both endpoints (+/-100 mm, -20 mm heave) require 588.5588 mm legs.
                // Their halfway pose requires 580 mm, below the 585 mm minimum.
                for (uint sequence = 1; sequence <= 12; sequence++)
                    fixture.Accept(Request(new Vector3(0.1f, -0.02f, 0f), sequence), 0.02f);
                CheckHeld(fixture, () => fixture.Platform.Receive(
                    Request(new Vector3(-0.1f, -0.02f, 0f), 13), 0.02f),
                    "ACTUATOR_LIMIT_RESPONSE_REJECTED_HOLD");
                foreach (float length in fixture.Platform.targetActuatorLengthsMm)
                    Require(length >= 585f && length <= 700f,
                        "response fixture must have an individually reachable target");
            }
        }
    }
}
