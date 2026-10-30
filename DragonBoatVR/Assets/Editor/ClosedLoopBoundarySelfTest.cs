using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Exercise startup, mode and freshness boundaries with the independent worker.
    /// Run only in the exclusive editor check process; no scene assets are saved.</summary>
    public static class ClosedLoopBoundarySelfTest
    {
        const float Dt = 0.01f;

        [MenuItem("DragonBoatVR/独立闭环边界自检")]
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("snapshot preserves clock bits and isolates nested mutable data", TestSnapshotCopy, ref passed, ref failed);
            Check("intent uses distinct physical observations across catch-up and recovery", TestIntentObservationClock, ref passed, ref failed);
            Check("angular differentiation retains encoder-scale rotations", TestAngularDerivativeResolution, ref passed, ref failed);
            Check("invalid startup remains fail closed", TestStartupFailures, ref passed, ref failed);
            Check("mode changes stop and replace controller sessions", TestModeChanges, ref passed, ref failed);
            Check("acknowledgement expiry and worker exit invalidate feedback", TestFreshness, ref passed, ref failed);
            Check("live rig change requires explicit restart", TestRigChange, ref passed, ref failed);
            Check("sample clocks and inertial observations reject malformed data", TestObservationFields, ref passed, ref failed);
            Check("execution metrics use the acknowledged request and actual feedback", TestExecutionMetrics, ref passed, ref failed);
            Check("CAD design geometry reaches the independent controller and its wrench feedback", TestCadProfile, ref passed, ref failed);
            Check("decoder units and boat binding reject before physics consumption", TestEncoderContract, ref passed, ref failed);
            Check("startup backend conflicts never call adapters", TestBackendStartup, ref passed, ref failed);
            Check("live backend conflict preserves physics history and rejects dispatch", TestBackendRuntime, ref passed, ref failed);
            Check("explicit external backend records actual adapter feedback", TestExternalBackend, ref passed, ref failed);
            Check("post-physics platform request uses the completed boat state", TestCompletedPlatformState, ref passed, ref failed);
            Check("expired observation cannot authorize a later dispatch", TestDispatchGap, ref passed, ref failed);
            Check("expired remote platform holds its last observed display pose", TestPlatformObservationHold, ref passed, ref failed);
            Debug.Log($"[CLOSEDLOOP_BOUNDARY] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[CLOSEDLOOP_BOUNDARY] RESULT=PASS independent process boundary checks"
                : "[CLOSEDLOOP_BOUNDARY] RESULT=FAIL see individual checks");
        }

        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try { test(); passed++; Debug.Log($"[CLOSEDLOOP_BOUNDARY] {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"[CLOSEDLOOP_BOUNDARY] {name}: {error}"); }
        }

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        static void TestSnapshotCopy()
        {
            double clock = 12 * (double)Dt;
            var original = new SimulationFrame { timeSeconds = clock,
                paddles = new[] { new PaddleSample { timeSeconds = clock, deviceTimeUs = 9007199254740993UL } },
                closedLoop = new ClosedLoopTelemetry { receivedAgeSeconds = clock,
                    feedback = new ClosedLoopFeedback { controllerTimeSeconds = clock, rawEncoderCounts = new[] { 41, 42 },
                        tensionN = new[] { 12.5f, 31f } } },
                cableFeedback = new CableDeviceFeedback { timeSeconds = clock, tensionsN = new[] { 13f } } };
            var first = SimulationSnapshot.Copy(original);
            Require(BitConverter.DoubleToInt64Bits(first.paddles[0].timeSeconds) == BitConverter.DoubleToInt64Bits(clock)
                && BitConverter.DoubleToInt64Bits(first.closedLoop.receivedAgeSeconds) == BitConverter.DoubleToInt64Bits(clock)
                && BitConverter.DoubleToInt64Bits(first.cableFeedback.timeSeconds) == BitConverter.DoubleToInt64Bits(clock)
                && first.paddles[0].deviceTimeUs == 9007199254740993UL, "Snapshot rounded a clock or sensor identity");
            first.paddles[0].valid = true; first.closedLoop.feedback.rawEncoderCounts[0] = -1;
            first.closedLoop.feedback.tensionN[0] = -2f; first.cableFeedback.tensionsN[0] = -3f;
            var second = SimulationSnapshot.Copy(original);
            Require(!second.paddles[0].valid && second.closedLoop.feedback.rawEncoderCounts[0] == 41
                && second.closedLoop.feedback.tensionN[0] == 12.5f && second.cableFeedback.tensionsN[0] == 13f,
                "Subscriber changed the source or another subscriber's nested arrays");
        }

        static void Call(object target, string method) => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);

        static void TestIntentObservationClock()
        {
            using (var fixture = new Fixture())
            {
                var type = typeof(UdpClosedLoopClient);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var update = type.GetMethod("UpdateScriptedIntent", flags);
                var feedback = type.GetField("_feedback", flags);
                Vector3 Velocity() => (Vector3)type.GetField("_desiredVelocity", flags).GetValue(fixture.Client);
                void Observe(double sampleTime, bool paused = false)
                {
                    // Simulate an ordinary driver FixedUpdate, including catch-up
                    // calls which can see the same independent physical sample.
                    fixture.Driver.SetPhase01(fixture.Driver.Phase01 + fixture.Driver.strokesPerMinute / 60f * Dt);
                    feedback.SetValue(fixture.Client, new ClosedLoopFeedback { controllerTimeSeconds = sampleTime });
                    update.Invoke(fixture.Client, new object[] { fixture.Driver, true, paused });
                }
                fixture.Driver.SetPhase01(0f);
                Observe(1.0);
                float first = fixture.Driver.Phase01;
                UdpClosedLoopClient.SampleScriptedIntent(fixture.Driver, out Vector3 raisedBlade, out Quaternion raisedRotation);
                Require(Mathf.Abs(first - 0.75f) < 1e-6f
                    && Mathf.Abs(raisedBlade.y - fixture.Driver.bladeAirHeight) < 1e-6f,
                    "Initial hand reference began inside the downward water stroke");
                // Test the physical startup condition, including the whole blade;
                // a centre coordinate alone cannot establish dry recovery.
                var hydro = fixture.Driver.GetComponent<PaddleHydrodynamics>();
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 offset = new Vector3((corner & 1) == 0 ? -hydro.BladeWidth / 2 : hydro.BladeWidth / 2,
                        (corner & 2) == 0 ? -hydro.bladeLength / 2 : hydro.bladeLength / 2,
                        (corner & 4) == 0 ? -hydro.bladeThickness / 2 : hydro.bladeThickness / 2);
                    Vector3 world = fixture.Driver.boat.transform.TransformPoint(raisedBlade + raisedRotation * offset);
                    Require(world.y > fixture.Session.wave.Sample(world.x, world.z).height,
                        "Initial recovery left a blade corner in the water");
                }
                Observe(1.0);
                Require(Mathf.Abs(fixture.Driver.Phase01 - first) < 1e-6f && Velocity().sqrMagnitude < 1e-12f,
                    $"Repeated sensor sample advanced intent: before={first:R} after={fixture.Driver.Phase01:R} velocity={Velocity():F6}");
                Observe(1.015);
                float expected = first + fixture.Driver.strokesPerMinute / 60f * 0.015f;
                Require(Mathf.Abs(fixture.Driver.Phase01 - expected) < 1e-6f,
                    "Intent cadence used the number of host calls rather than elapsed physical sample time");
                Vector3 velocity = Velocity();
                Require(velocity.magnitude > 1e-4f && velocity.magnitude < 2f,
                    "Nominal stroke became a catch-up velocity spike: " + velocity.ToString("F6"));
                for (int i = 0; i < 4; i++) Observe(1.015);
                Require(Mathf.Abs(fixture.Driver.Phase01 - expected) < 1e-6f && Vector3.Distance(velocity, Velocity()) < 1e-6f,
                    "Extra host calls changed the same timestamped intention");
                Observe(1.03, true);
                Require(Mathf.Abs(fixture.Driver.Phase01 - expected) < 1e-6f && Velocity() == Vector3.zero,
                    "Paused intention retained feedforward motion");
                Observe(2.0);
                Require(Mathf.Abs(fixture.Driver.Phase01 - expected) < 1e-6f && Velocity() == Vector3.zero,
                    "Recovery caught up over the unavailable interval");
                Observe(2.015);
                expected += fixture.Driver.strokesPerMinute / 60f * 0.015f;
                Require(Mathf.Abs(fixture.Driver.Phase01 - expected) < 1e-6f, "Fresh recovery failed to resume the held phase");
                Observe(2.4);
                Require(Mathf.Abs(fixture.Driver.Phase01 - expected) < 1e-6f && Velocity() == Vector3.zero,
                    "An unobserved interval longer than the source lifetime advanced intention");
            }
        }

        static void TestAngularDerivativeResolution()
        {
            foreach (float degrees in new[] { -1f, -0.1f, -0.01f, 0.01f, 0.1f, 1f })
            {
                const float interval = 0.015f;
                Vector3 actual = SimulationValue.AngularVelocity(Quaternion.identity,
                    Quaternion.AngleAxis(degrees, Vector3.up), interval);
                Vector3 expected = Vector3.up * (degrees * Mathf.Deg2Rad / interval);
                Require(Vector3.Distance(actual, expected) < 1e-5f,
                    $"Finite encoder rotation lost or distorted: degrees={degrees:R} actual={actual:F8} expected={expected:F8}");
            }
        }

        sealed class Fixture : IDisposable
        {
            public readonly SimulationSession Session;
            public readonly UdpClosedLoopClient Client;
            public PaddleDriver Driver => Session.drivers[0];

            public Fixture(bool buildPlatformModel = false)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var wave = new GameObject("boundary wave").AddComponent<WaveField>();
                wave.intensity = 0; Call(wave, "Awake");
                var boat = new GameObject("boundary boat").AddComponent<BoatBuoyancy>();
                boat.transform.position = Vector3.up * 0.12f;
                Call(boat, "Awake"); Call(boat, "Start");
                var paddle = new GameObject("boundary paddle");
                var driver = paddle.AddComponent<PaddleDriver>(); driver.boat = boat;
                var hydro = paddle.AddComponent<PaddleHydrodynamics>(); hydro.targetBoat = boat;
                var cable = new GameObject("boundary cables").AddComponent<CableDriveTwin>();
                cable.boat = boat; cable.paddle = hydro; cable.showCables = false; cable.showMotorUnits = false;
                cable.ApplyRig(new TendonRigConfig { tendons = new[]
                {
                    new TendonSpec { motorId = 1, anchorLocal = new Vector3(1.4f, 0.5f, 1.6f) },
                    new TendonSpec { motorId = 2, anchorLocal = new Vector3(1.4f, 0.5f, -1.2f) },
                    new TendonSpec { motorId = 3, anchorLocal = new Vector3(0.6f, -1.4f, 0.2f) }
                } });
                var platform = new GameObject("boundary platform").AddComponent<StewartPlatformTwin>();
                platform.sourceBoat = boat; platform.autoBuildModel = buildPlatformModel; platform.loadRuntimeConfiguration = false;
                Require(platform.ApplyConfiguration(new PlatformRigConfig()), "platform fixture rejected");
                Session = new GameObject("boundary session").AddComponent<SimulationSession>();
                Session.boat = boat; Session.wave = wave; Session.drivers = new[] { driver };
                Session.cable = cable; Session.platform = platform;
                Session.Initialize(false);
                Call(driver, "Start"); Call(hydro, "Start");
                Client = Session.gameObject.AddComponent<UdpClosedLoopClient>();
                Session.closedLoop = Client;
            }

            public void Start(ClosedLoopConfiguration config = null) => Client.Initialize(Session, config ?? new ClosedLoopConfiguration());
            public void Tick() { Session.StepInputs(Dt); Session.StepDevices(Dt); }

            public void Until(Func<bool> predicate, string failure, double timeout = 3)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < timeout)
                {
                    Tick();
                    if (predicate()) return;
                    Thread.Sleep(5);
                }
                throw new InvalidOperationException(failure + ": " + Client.Telemetry.status);
            }

            public void Ready() => Until(() => Client.Telemetry.encoderConsumed && Client.Telemetry.feedbackFresh,
                "no independently decoded fresh feedback");

            public void Dispose()
            {
                Client.StopController();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void TestExecutionMetrics()
        {
            using (var fixture = new Fixture())
            {
                fixture.Start(); fixture.Ready();
                ClosedLoopCommand acknowledged = null;
                fixture.Until(() =>
                {
                    var telemetry = fixture.Client.Telemetry;
                    if (!fixture.Client.CableFeedback.valid || string.IsNullOrEmpty(telemetry.acknowledgedCommandPacketBase64)) return false;
                    Require(UdpClosedLoopClient.TryUnpack(Convert.FromBase64String(telemetry.acknowledgedCommandPacketBase64),
                        ulong.Parse(telemetry.sessionId), out _, out string json), "acknowledged packet failed its envelope");
                    acknowledged = JsonUtility.FromJson<ClosedLoopCommand>(json);
                    return new Vector3(acknowledged.requestForce[0], acknowledged.requestForce[1], acknowledged.requestForce[2]).magnitude > 1f;
                }, "no nonzero acknowledged cable request", 4);
                var feedback = fixture.Client.CableFeedback;
                var cable = fixture.Session.cable;
                Vector3 wanted = fixture.Client.ObservationBoatRotation * new Vector3(
                    acknowledged.requestForce[0], acknowledged.requestForce[1], acknowledged.requestForce[2]);
                Vector3 moment = fixture.Client.ObservationBoatRotation * new Vector3(
                    acknowledged.requestTorque[0], acknowledged.requestTorque[1], acknowledged.requestTorque[2]);
                float expectedRatio = feedback.forceWorldN.magnitude / wanted.magnitude;
                float expectedForce = 1f - (feedback.forceWorldN - wanted).magnitude / wanted.magnitude;
                float characteristic = cable.rig.charLengthM;
                float targetNorm = Mathf.Sqrt(wanted.sqrMagnitude + moment.sqrMagnitude / (characteristic * characteristic));
                float errorNorm = Mathf.Sqrt((feedback.forceWorldN - wanted).sqrMagnitude
                    + (feedback.torqueAtGripWorldNm - moment).sqrMagnitude / (characteristic * characteristic));
                float expectedWrench = 1f - errorNorm / targetNorm;
                float expectedDirection = feedback.forceWorldN.magnitude > 1e-5f ? Vector3.Angle(feedback.forceWorldN, wanted) : 180f;
                Require(Mathf.Abs(cable.allocationRatio - expectedRatio) < 0.002f
                    && Mathf.Abs(cable.forceRestorationRate - expectedForce) < 0.002f
                    && Mathf.Abs(cable.restorationRate - expectedWrench) < 0.002f
                    && Mathf.Abs(cable.directionErrorDegrees - expectedDirection) < 0.02f,
                    $"mixed allocation/execution metrics ack={acknowledged.commandSequence} ratio={cable.allocationRatio:R}/{expectedRatio:R} " +
                    $"force={cable.forceRestorationRate:R}/{expectedForce:R} wrench={cable.restorationRate:R}/{expectedWrench:R} " +
                    $"direction={cable.directionErrorDegrees:R}/{expectedDirection:R}");
            }
        }

        static void TestCadProfile()
        {
            using (var fixture = new Fixture())
            {
                var profile = TendonRigProfiles.LoadCadDesign();
                Require(!profile.measured && profile.geometryAssumed && !profile.motorMappingConfirmed,
                    "CAD design lost its unmeasured registration/channel provenance");
                Require(profile.tendons.Length == 3
                    && Mathf.Abs(profile.tendons[0].attachmentFromBladeTip.y - 0.46f) < 1e-6f
                    && Mathf.Abs(profile.tendons[1].attachmentFromBladeTip.x - 0.08f) < 1e-6f
                    && Mathf.Abs(profile.tendons[2].attachmentFromBladeTip.x + 0.08f) < 1e-6f
                    && Mathf.Abs(profile.tendons[1].attachmentFromBladeTip.y - 0.01f) < 1e-6f
                    && Mathf.Abs(profile.tendons[2].attachmentFromBladeTip.y - 0.01f) < 1e-6f,
                    "CAD bore geometry was replaced by drawn cord endpoints or the excluded centre hole");
                fixture.Session.cable.ApplyRig(profile);
                fixture.Start(); fixture.Ready();
                fixture.Until(() => fixture.Client.CableFeedback.valid
                    && fixture.Client.CableFeedback.tensionsN[0] > 1f, "no CAD controller execution");
                var telemetry = fixture.Client.Telemetry;
                Require(UdpClosedLoopClient.TryUnpack(Convert.FromBase64String(telemetry.acknowledgedCommandPacketBase64),
                    ulong.Parse(telemetry.sessionId), out _, out string json), "CAD command envelope rejected");
                var command = JsonUtility.FromJson<ClosedLoopCommand>(json);
                Require(command.tensionTargets.Length == 3 && command.commandSequence == fixture.Client.CableFeedback.commandSequence,
                    "CAD execution does not belong to the acknowledged three-cord command");
                for (int i = 0; i < 3; i++)
                {
                    var spec = profile.tendons[i];
                    Vector3 anchor = new Vector3(command.anchors[i * 3], command.anchors[i * 3 + 1], command.anchors[i * 3 + 2]);
                    Vector3 attachment = new Vector3(command.attachments[i * 3], command.attachments[i * 3 + 1], command.attachments[i * 3 + 2]);
                    Require(Vector3.Distance(anchor, spec.anchorLocal) < 1e-6f
                        && Vector3.Distance(attachment, profile.bladeTipLocal + spec.attachmentFromBladeTip) < 1e-6f
                        && command.minTensions[i] == 5f && command.maxTensions[i] == 300f,
                        "CAD geometry/bounds did not reach the independent controller");
                }
                // Independent reconstruction from the worker's encoder and tension
                // observations; 0.01 degree encoder rounding bounds this comparison.
                var observed = telemetry.feedback;
                var rotation = Quaternion.Euler(observed.rawEncoderCounts[3] / observed.countsPerDegree,
                    observed.rawEncoderCounts[4] / observed.countsPerDegree, observed.rawEncoderCounts[5] / observed.countsPerDegree);
                var grip = new Vector3(observed.gripPosition[0], observed.gripPosition[1], observed.gripPosition[2]);
                var blade = grip - rotation * Vector3.up * command.shaftLength;
                Vector3 force = Vector3.zero, torque = Vector3.zero;
                for (int i = 0; i < 3; i++)
                {
                    var point = blade + rotation * profile.tendons[i].attachLocal;
                    var direction = (profile.tendons[i].anchorLocal - point).normalized;
                    var cableForce = direction * observed.tensionN[i];
                    force += cableForce; torque += Vector3.Cross(point - grip, cableForce);
                }
                Require(Vector3.Distance(force, new Vector3(observed.forceLocal[0], observed.forceLocal[1], observed.forceLocal[2])) < 0.02f
                    && Vector3.Distance(torque, new Vector3(observed.torqueLocal[0], observed.torqueLocal[1], observed.torqueLocal[2])) < 0.02f,
                    "CAD feedback wrench differs from actual encoder/tension geometry");
            }
        }

        static void TestStartupFailures()
        {
            for (int scenario = 0; scenario < 4; scenario++)
                using (var fixture = new Fixture())
                {
                    var config = new ClosedLoopConfiguration();
                    if (scenario == 0) fixture.Session.cable.rig = null;
                    if (scenario == 1) config.motorTauSeconds = 0;
                    if (scenario == 2) config.pythonExecutable = "dragonboat_missing_python_executable_for_test.exe";
                    if (scenario == 3) fixture.Session.platform = null;
                    fixture.Start(config);
                    Require(fixture.Client.Active && !fixture.Client.Ready,
                        "startup failure silently selected a kinematic fallback");
                    Require(fixture.Client.Telemetry.status.StartsWith("CONTROLLER_START_REJECTED", StringComparison.Ordinal),
                        "startup failure was not reported explicitly");
                    fixture.Tick();
                    Require(!fixture.Driver.Sample.valid && !fixture.Session.hydrodynamics[0].FeedbackRequest.valid
                        && !fixture.Client.CableFeedback.valid && !fixture.Client.PlatformFeedback.valid,
                        "startup failure produced valid physical input or device feedback");
                }
        }

        static void TestModeChanges()
        {
            using (var fixture = new Fixture())
            {
                fixture.Start(); fixture.Ready();
                foreach (var mode in new[] { PaddleDriver.Mode.Mouse, PaddleDriver.Mode.UdpDirect, PaddleDriver.Mode.External })
                {
                    string previousSession = fixture.Client.Telemetry.sessionId;
                    int previousPid = fixture.Client.Telemetry.controllerProcessId;
                    fixture.Driver.SetMode(mode);
                    // Exercise a switch after the input observation, before the device boundary.
                    fixture.Session.StepDevices(Dt);
                    var stopped = fixture.Client.Telemetry;
                    string stoppedState = $"mode={mode} phase=after_device_boundary active={fixture.Client.Active}" +
                        $" ready={fixture.Client.Ready} fresh={stopped.feedbackFresh} consumed={stopped.encoderConsumed}" +
                        $" feedbackNull={stopped.feedback == null} packetEmpty={string.IsNullOrEmpty(stopped.feedbackPacketBase64)}" +
                        $" status={stopped.status} previousPid={previousPid}";
                    Require(!fixture.Client.Active && !fixture.Client.Ready && !stopped.feedbackFresh && !stopped.encoderConsumed
                        && stopped.feedback == null && string.IsNullOrEmpty(stopped.feedbackPacketBase64),
                        "mode switch retained an active process or accepted observation: " + stoppedState);
                    Require(!Alive(previousPid), "mode switch left the previous worker alive: " + stoppedState);
                    fixture.Driver.SetMode(PaddleDriver.Mode.VirtualPaddler);
                    fixture.Session.StepInputs(Dt);
                    Require(fixture.Client.Active && fixture.Client.Telemetry.sessionId != previousSession
                        && !fixture.Client.Telemetry.feedbackFresh, "return to virtual input reused an old session: " +
                        $"mode={mode} phase=return_to_virtual active={fixture.Client.Active}" +
                        $" oldSession={previousSession} newSession={fixture.Client.Telemetry.sessionId}" +
                        $" fresh={fixture.Client.Telemetry.feedbackFresh} status={fixture.Client.Telemetry.status}");
                    fixture.Ready();
                }
            }
        }

        static void TestFreshness()
        {
            using (var fixture = new Fixture())
            {
                fixture.Start(); fixture.Ready();
                fixture.Client.SendCommands = false;
                float heldPhase = fixture.Driver.Phase01;
                fixture.Until(() => !fixture.Client.Telemetry.feedbackFresh && !fixture.Driver.Sample.valid,
                    "continued feedback packets refreshed an expired command", 1);
                Require(Mathf.Abs(Mathf.DeltaAngle(heldPhase * 360f, fixture.Driver.Phase01 * 360f)) < 1e-4f,
                    "script advanced the target while commands were disconnected");
                Require(!fixture.Client.CableFeedback.valid && !fixture.Client.PlatformFeedback.valid,
                    "expired acknowledgement retained valid device feedback");
                fixture.Client.SendCommands = true;
                fixture.Ready();
                float recoveredPhaseAdvance = Mathf.Repeat(fixture.Driver.Phase01 - heldPhase, 1f);
                Require(recoveredPhaseAdvance <= fixture.Driver.strokesPerMinute / 60f * Dt + 1e-5f,
                    "feedback recovery jumped over the missing stroke interval");
                string consumedBeforeExit = JsonUtility.ToJson(fixture.Driver.Sample);
                bool inputWasFresh = fixture.Client.Telemetry.feedbackFresh;
                using (var worker = Process.GetProcessById(fixture.Client.Telemetry.controllerProcessId))
                { worker.Kill(); Require(worker.WaitForExit(1000), "owned worker did not exit"); }
                fixture.Session.StepDevices(Dt);
                Require(!fixture.Client.Telemetry.deviceFeedbackFresh && !fixture.Client.CableFeedback.valid
                    && !fixture.Client.PlatformFeedback.valid, "worker exit retained cached fresh feedback");
                Require(fixture.Client.Telemetry.feedbackFresh == inputWasFresh
                    && JsonUtility.ToJson(fixture.Session.LastFrame.paddles[0]) == consumedBeforeExit,
                    "worker exit rewrote the already consumed physical input; actual=" + JsonUtility.ToJson(fixture.Session.LastFrame.paddles[0])
                    + "; driver=" + JsonUtility.ToJson(fixture.Driver.Sample));
                fixture.Session.StepInputs(Dt);
                Require(!fixture.Driver.Sample.valid, "worker exit left valid encoder-driven input");
                Require(fixture.Client.Telemetry.status.StartsWith("CONTROLLER_EXITED_CODE_", StringComparison.Ordinal),
                    "worker exit was not visible in status");
            }
        }

        static void TestCompletedPlatformState()
        {
            using (var fixture = new Fixture())
            {
                var boat = fixture.Session.boat;
                var body = boat.BoatRb;
                body.velocity = Vector3.zero;
                fixture.Session.StepDevices(Dt);
                Call(boat, "FixedUpdate");
                // The physics solver has now produced a different pose and velocity.
                // The platform must consume that completed state, not the pre-step cache.
                body.position += Vector3.up * 0.01f;
                body.rotation = Quaternion.Euler(7f, 11f, -4f);
                body.velocity = new Vector3(0.01f, 0.02f, 0.03f);
                fixture.Session.StepDevices(Dt);
                var frame = fixture.Session.LastFrame;
                var request = frame.platformRequest;
                var expectedAcceleration = Quaternion.Inverse(frame.boatRotation) * (frame.boatVelocity / Dt);
                Require(Mathf.Abs(Mathf.DeltaAngle(request.rawEulerDegrees.x, frame.boatRotation.eulerAngles.x)) < 0.0001f
                    && Mathf.Abs(Mathf.DeltaAngle(request.rawEulerDegrees.z, frame.boatRotation.eulerAngles.z)) < 0.0001f,
                    "platform attitude is not from the completed physics pose");
                Require(Mathf.Abs(request.rawTranslationM.x - expectedAcceleration.x * fixture.Session.platform.accelToTranslation) < 1e-6f
                    && Mathf.Abs(request.rawTranslationM.z - expectedAcceleration.z * fixture.Session.platform.accelToTranslation) < 1e-6f,
                    "platform acceleration is not from consecutive completed physics states");
            }
        }

        static void TestDispatchGap()
        {
            using (var fixture = new Fixture())
            {
                fixture.Start(); fixture.Ready();
                var hydro = fixture.Session.hydrodynamics[0];
                fixture.Until(() => fixture.Driver.Sample.valid && hydro.FeedbackRequest.forceWorldN.magnitude > 1f,
                    "no loaded source observation for dispatch-gap test");
                fixture.Session.StepInputs(Dt);
                Require(fixture.Driver.Sample.valid && hydro.FeedbackRequest.valid, "input did not pass before the pause");
                string source = JsonUtility.ToJson(fixture.Driver.Sample);
                string candidate = JsonUtility.ToJson(hydro.FeedbackRequest);
                Thread.Sleep(160);
                fixture.Session.StepDevices(Dt);
                var pending = (System.Collections.IDictionary)typeof(UdpClosedLoopClient)
                    .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Client);
                object sent = pending[fixture.Client.Telemetry.commandSequence];
                var command = (ClosedLoopCommand)sent.GetType().GetField("command").GetValue(sent);
                Require(!command.cableValid && !command.platformValid,
                    "a source older than 100 ms was sent as a valid actuator command");
                Require(JsonUtility.ToJson(fixture.Session.LastFrame.paddles[0]) == source
                    && JsonUtility.ToJson(fixture.Session.LastFrame.feedbackRequests[0]) == candidate,
                    "dispatch rejection rewrote the already consumed physical observation");
                foreach (float tension in command.tensionTargets)
                    Require(tension == 0f, "expired source retained an active tension target");
                var telemetry = fixture.Client.Telemetry;
                Require(telemetry.commandSent && !telemetry.commandSourceFresh && !telemetry.deviceFeedbackFresh
                    && telemetry.feedbackFresh && telemetry.encoderConsumed
                    && telemetry.commandSourceReceivedAgeSeconds > 0.1
                    && UdpClosedLoopClient.TryUnpack(Convert.FromBase64String(telemetry.commandPacketBase64),
                        ulong.Parse(telemetry.sessionId), out uint sequence, out string json)
                    && sequence == command.commandSequence && !JsonUtility.FromJson<ClosedLoopCommand>(json).platformValid,
                    "actual outgoing bytes and separate input/output expiry evidence are missing");
            }
        }

        static void TestPlatformObservationHold()
        {
            using (var fixture = new Fixture(true))
            {
                fixture.Start(); fixture.Ready();
                fixture.Client.SendCommands = false;
                fixture.Until(() => !fixture.Client.Telemetry.feedbackFresh && !fixture.Client.PlatformFeedback.valid,
                    "platform observation did not expire", 1);
                var platform = fixture.Session.platform;
                var top = (Transform)typeof(StewartPlatformTwin).GetField("_topPlate",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(platform);
                Require(top != null, "test did not build the visible platform top");
                Vector3 heldPosition = top.localPosition;
                Quaternion heldRotation = top.localRotation;
                for (int i = 0; i < 8; i++)
                {
                    var body = fixture.Session.boat.BoatRb;
                    body.position += Vector3.up * 0.002f;
                    body.rotation = Quaternion.Euler(3f + i, 0f, -2f);
                    fixture.Session.boat.currentEulerAngles = body.rotation.eulerAngles;
                    fixture.Tick();
                    Require(!fixture.Client.PlatformFeedback.valid, "test unexpectedly recovered remote feedback");
                    Require(Vector3.Distance(top.localPosition, heldPosition) < 1e-6f
                        && Quaternion.Angle(top.localRotation, heldRotation) < 0.0001f,
                        "local prediction moved the visible platform after remote feedback expired");
                    Require(!fixture.Session.LastFrame.deviceProtocol.platformExecuted,
                        "local protocol claimed execution while the independent controller owns the platform");
                }
            }
        }

        static void TestRigChange()
        {
            using (var fixture = new Fixture())
            {
                fixture.Start(); fixture.Ready();
                fixture.Session.cable.rig.tendons[0].anchorLocal += Vector3.right * 0.01f;
                fixture.Session.StepDevices(Dt);
                Require(fixture.Client.Active && !fixture.Client.Ready && !fixture.Client.Telemetry.feedbackFresh
                    && fixture.Client.Telemetry.status.StartsWith("CONTROLLER_CONFIGURATION_REJECTED", StringComparison.Ordinal),
                    "live geometry change was silently ignored by the worker");
                fixture.Session.StepInputs(Dt);
                Require(!fixture.Driver.Sample.valid, "rig rejection selected a kinematic fallback");
            }
        }

        static void TestEncoderContract()
        {
            for (int scenario = 0; scenario < 4; scenario++)
                using (var fixture = new Fixture())
                {
                    var decoder = fixture.Session.encoderPipelines[0];
                    if (scenario == 0) decoder.countsPerMeter = 5000;
                    if (scenario == 1) decoder.countsPerDegree = 200;
                    if (scenario == 2) decoder.boat = null;
                    if (scenario == 3) decoder.driver = null;
                    fixture.Start();
                    Require(fixture.Client.Active && !fixture.Client.Ready,
                        "incompatible encoder units or binding started a controller");
                    fixture.Session.StepInputs(Dt);
                    Require(!fixture.Driver.Sample.valid && !fixture.Session.hydrodynamics[0].FeedbackRequest.valid,
                        "incompatible startup encoder reached hydrodynamics");
                }
            using (var fixture = new Fixture())
            {
                fixture.Start(); fixture.Ready();
                string priorSession = fixture.Client.Telemetry.sessionId;
                var decoder = fixture.Session.encoderPipelines[0];
                decoder.countsPerMeter = 5000;
                // Input must be rejected here, before the post-physics Dispatch guard.
                fixture.Session.StepInputs(Dt);
                Require(!fixture.Driver.Sample.valid && !fixture.Session.hydrodynamics[0].FeedbackRequest.valid
                    && fixture.Session.hydrodynamics[0].RawWaterForceN == Vector3.zero
                    && !fixture.Client.Ready && !fixture.Client.Telemetry.encoderConsumed,
                    "live resolution change generated one falsely scaled physical sample");
                decoder.countsPerMeter = 10000;
                fixture.Client.StartController(); fixture.Ready();
                Require(fixture.Client.Telemetry.sessionId != priorSession, "resolution repair reused the rejected session");
            }
        }

        public sealed class ForceAdapterSpy : MonoBehaviour, IForceFeedbackDevice
        {
            public int Calls { get; private set; }
            public ForceFeedbackRequest LastRequest { get; private set; }
            public CableDeviceFeedback Feedback { get; private set; }
            public void Receive(ForceFeedbackRequest request, float dt)
            {
                Calls++; LastRequest = request;
                Feedback = new CableDeviceFeedback { commandSequence = request.commandSequence,
                    timeSeconds = request.timeSeconds, valid = request.valid, simulated = true,
                    status = "EXTERNAL_FORCE_SPY", forceWorldN = new Vector3(3, 4, 5),
                    tensionsN = new[] { 7f, 8f, 9f }, lengthsMm = new[] { 101f, 102f, 103f } };
            }
        }

        public sealed class PlatformAdapterSpy : MonoBehaviour, IPlatformMotionDevice
        {
            public int Calls { get; private set; }
            public PlatformMotionRequest LastRequest { get; private set; }
            public PlatformDeviceFeedback Feedback { get; private set; }
            public void Receive(PlatformMotionRequest request, float dt)
            {
                Calls++; LastRequest = request;
                Feedback = new PlatformDeviceFeedback { commandSequence = request.commandSequence,
                    timeSeconds = request.timeSeconds, valid = request.valid, simulated = true,
                    status = "EXTERNAL_PLATFORM_SPY", translationM = new Vector3(0.01f, 0.02f, 0.03f),
                    actuatorLengthsMm = new[] { 611f, 612f, 613f, 614f, 615f, 616f } };
            }
        }

        static void RequireDispatchRejected(Fixture fixture, ForceAdapterSpy force, PlatformAdapterSpy platform)
        {
            var frame = fixture.Session.LastFrame;
            Require(force.Calls == 0 && platform.Calls == 0, "conflicting adapter received a command");
            Require(fixture.Client.Active && !fixture.Client.Ready && !fixture.Client.Telemetry.feedbackFresh,
                "backend conflict silently selected another owner");
            Require(frame != null && frame.deviceDispatchRejected && !string.IsNullOrEmpty(frame.deviceDispatchRejection)
                && frame.deviceDispatchRejection.Contains("BACKEND_CONFLICT"), "dispatch rejection was not recorded");
            Require(frame.cableFeedback != null && !frame.cableFeedback.valid
                && frame.platformFeedback != null && !frame.platformFeedback.valid && !frame.platformRequest.valid,
                "dispatch rejection retained valid device output");
        }

        static void TestBackendStartup()
        {
            for (int scenario = 0; scenario < 3; scenario++)
                using (var fixture = new Fixture())
                {
                    var force = fixture.Session.gameObject.AddComponent<ForceAdapterSpy>();
                    var platform = fixture.Session.gameObject.AddComponent<PlatformAdapterSpy>();
                    if (scenario != 1) fixture.Session.forceDeviceAdapter = force;
                    if (scenario != 0) fixture.Session.platformDeviceAdapter = platform;
                    int completed = 0;
                    fixture.Session.FrameCompleted += _ => completed++;
                    fixture.Start();
                    Require(!fixture.Client.Ready && fixture.Client.Telemetry.controllerProcessId == 0,
                        "conflicting startup launched the software worker");
                    fixture.Tick();
                    RequireDispatchRejected(fixture, force, platform);
                    Require(completed == 1 && !fixture.Session.LastFrame.paddles[0].valid,
                        "startup conflict lost its completed invalid observation");
                }
        }

        static void TestBackendRuntime()
        {
            for (int phase = 0; phase < 2; phase++)
                using (var fixture = new Fixture())
                {
                    fixture.Start(); fixture.Ready();
                    int pid = fixture.Client.Telemetry.controllerProcessId;
                    string sessionId = fixture.Client.Telemetry.sessionId;
                    var force = fixture.Session.gameObject.AddComponent<ForceAdapterSpy>();
                    var platform = fixture.Session.gameObject.AddComponent<PlatformAdapterSpy>();
                    var hydro = fixture.Session.hydrodynamics[0];
                    if (phase == 0)
                    {
                        fixture.Session.forceDeviceAdapter = force;
                        fixture.Session.platformDeviceAdapter = platform;
                        fixture.Session.StepInputs(Dt);
                        Require(!fixture.Driver.Sample.valid && hydro.RawWaterForceN == Vector3.zero,
                            "input-boundary conflict consumed another physical observation");
                    }
                    else
                    {
                        fixture.Until(() => fixture.Driver.Sample.valid && hydro.RawWaterForceN.magnitude > 0.01f,
                            "fixture never produced a loaded input before output rejection");
                        fixture.Session.StepInputs(Dt);
                        Require(fixture.Driver.Sample.valid && hydro.RawWaterForceN.magnitude > 0.01f,
                            "post-input rejection fixture did not retain its loaded source");
                        // The current input phase is complete; attaching the backend must
                        // stop dispatch without rewriting that phase's real observation.
                        fixture.Session.forceDeviceAdapter = force;
                        fixture.Session.platformDeviceAdapter = platform;
                    }
                    string sample = JsonUtility.ToJson(fixture.Driver.Sample);
                    string candidate = JsonUtility.ToJson(hydro.FeedbackRequest);
                    Vector3 rawForce = hydro.RawWaterForceN;
                    SimulationFrame completed = null;
                    fixture.Session.FrameCompleted += frame => completed = frame;
                    fixture.Session.StepDevices(Dt);
                    RequireDispatchRejected(fixture, force, platform);
                    Require(completed != null && completed.deviceDispatchRejected
                        && JsonUtility.ToJson(completed.paddles[0]) == sample
                        && JsonUtility.ToJson(completed.feedbackRequests[0]) == candidate
                        && completed.rawWaterForceN[0] == rawForce,
                        "output rejection erased the already consumed input or water force; sample=" + sample
                        + "; completed=" + JsonUtility.ToJson(completed?.paddles[0])
                        + "; request=" + candidate + "; completedRequest=" + JsonUtility.ToJson(completed?.feedbackRequests[0])
                        + "; rawBefore=" + rawForce.ToString("G9") + "; rawAfter=" + completed?.rawWaterForceN[0].ToString("G9"));
                    Require(!Alive(pid), "backend conflict left the software worker alive");
                    fixture.Session.forceDeviceAdapter = fixture.Session.platformDeviceAdapter = null;
                    fixture.Tick();
                    Require(!fixture.Client.Ready && !fixture.Driver.Sample.valid,
                        "removing an adapter silently restarted the rejected session");
                    fixture.Client.StartController(); fixture.Ready();
                    Require(fixture.Client.Telemetry.sessionId != sessionId && !fixture.Session.LastFrame.deviceDispatchRejected,
                        "explicit recovery retained the rejected backend or session");
                }
        }

        static void TestExternalBackend()
        {
            for (int selection = 0; selection < 2; selection++)
                using (var fixture = new Fixture())
                {
                    fixture.Start(); fixture.Ready();
                    int pid = fixture.Client.Telemetry.controllerProcessId;
                    var force = fixture.Session.gameObject.AddComponent<ForceAdapterSpy>();
                    var platform = fixture.Session.gameObject.AddComponent<PlatformAdapterSpy>();
                    fixture.Session.forceDeviceAdapter = force;
                    fixture.Session.platformDeviceAdapter = platform;
                    if (selection == 0)
                    {
                        // Also covers disabling software after input but before output.
                        fixture.Client.configuration.enabled = false;
                        fixture.Session.StepDevices(Dt);
                    }
                    else
                    {
                        var external = fixture.Driver.Sample;
                        fixture.Driver.SetMode(PaddleDriver.Mode.External);
                        external.sequence++;
                        external.timeSeconds += Dt;
                        Require(fixture.Driver.SetExternalSample(external), "external control sample was rejected");
                        fixture.Tick();
                    }
                    var frame = fixture.Session.LastFrame;
                    Require(force.Calls == 1 && platform.Calls == 1 && !fixture.Client.Active && !Alive(pid),
                        "explicit external backend did not exclusively receive the request");
                    Require(frame != null && !frame.deviceDispatchRejected
                        && JsonUtility.ToJson(frame.cableFeedback) == JsonUtility.ToJson(force.Feedback)
                        && JsonUtility.ToJson(frame.platformFeedback) == JsonUtility.ToJson(platform.Feedback),
                        "external feedback was replaced by the stopped software backend; frameCable=" + JsonUtility.ToJson(frame?.cableFeedback)
                        + "; adapterCable=" + JsonUtility.ToJson(force.Feedback) + "; framePlatform=" + JsonUtility.ToJson(frame?.platformFeedback)
                        + "; adapterPlatform=" + JsonUtility.ToJson(platform.Feedback));
                    Require(frame.paddles[0].valid && force.LastRequest.valid && platform.LastRequest.valid,
                        "legal external selection did not retain valid input and accepted requests");
                    if (selection == 0)
                    {
                        fixture.Tick();
                        Require(fixture.Driver.Sample.valid && force.Calls == 2 && platform.Calls == 2,
                            "first local encoder sample after releasing software ownership was rejected");
                    }
                }
        }

        static void TestObservationFields()
        {
            using (var fixture = new Fixture())
            {
                fixture.Start(); fixture.Ready();
                var observed = fixture.Client.Telemetry.feedback;
                string json = JsonUtility.ToJson(observed);
                var validator = typeof(UdpClosedLoopClient).GetMethod("ValidFeedback", BindingFlags.Static | BindingFlags.NonPublic);
                Func<ClosedLoopFeedback, bool> valid = f => (bool)validator.Invoke(null, new object[] { f, f.tensionN.Length });
                Require(valid(observed), "actual independently received observation failed field validation");
                var corruptions = new Action<ClosedLoopFeedback>[]
                {
                    f => f.controllerTimeSeconds += 0.001,
                    f => f.plantSampleTick++,
                    f => f.plantSampleTick = -1,
                    f => f.plantStepSeconds = 0.01,
                    f => f.plantStepSeconds = double.NaN,
                    f => f.controllerWallTimeSeconds = double.PositiveInfinity,
                    f => f.controllerWallTimeSeconds = -0.001,
                    f => f.plantDroppedTicks = -1,
                    f => f.platformAngularVelocity = null,
                    f => f.platformAcceleration = new float[2],
                    f => f.platformAngularAcceleration[0] = float.NaN,
                    f => f.platformInducedAcceleration[1] = float.PositiveInfinity,
                    f => f.platformInducedAngularAcceleration = new float[0]
                };
                for (int i = 0; i < corruptions.Length; i++)
                {
                    var altered = JsonUtility.FromJson<ClosedLoopFeedback>(json);
                    corruptions[i](altered);
                    Require(!valid(altered), "malformed observation accepted: " + i);
                }
            }
        }

        static bool Alive(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }
}
