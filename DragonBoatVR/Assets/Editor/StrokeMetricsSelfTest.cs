using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Analytic physics-observation oracles. No scene loading, render timing, device
    /// response, or live acquisition is needed to verify the integrals and lifecycle.</summary>
    public static class StrokeMetricsSelfTest
    {
        const float Dt = 0.1f;
        const double Epsilon = 1e-5;

        [MenuItem("DragonBoatVR/划桨统计与采集 HUD 自检")]
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("analytic unsmoothed force integral / complete stroke", TestImpulse, ref passed, ref failed);
            Check("entry-to-entry period includes recovery", TestCadence, ref passed, ref failed);
            Check("duplicate physics time has no effects", TestDuplicates, ref passed, ref failed);
            Check("missing wet step interrupts and requires dry rearming", TestMissingStep, ref passed, ref failed);
            Check("invalid input does not count as exit", TestInvalidInput, ref passed, ref failed);
            Check("nonfinite and negative model force interrupt", TestInvalidForce, ref passed, ref failed);
            Check("malformed observations preserve completed totals", TestMalformedObservations, ref passed, ref failed);
            Check("time reversal starts a fresh timeline", TestReversal, ref passed, ref failed);
            Check("wave entry is excluded until a new dry observation", TestWaveEntry, ref passed, ref failed);
            Check("horizontal distance is independent of paddle input", TestDistance, ref passed, ref failed);
            Check("explicit reset and recovery", TestReset, ref passed, ref failed);
            Check("HUD integrates raw model force, independent of rendering and feedback", TestHudPhysics, ref passed, ref failed);
            Check("HUD uses typed captured acquisition snapshot", TestHudAcquisition, ref passed, ref failed);
            Check("HUD accepts consumed asynchronous acknowledgements and rejects stale/wrong replies", TestHudAsyncFeedback, ref passed, ref failed);
            Check("HUD direct feedback retains exact request sequence and time matching", TestHudDirectFeedback, ref passed, ref failed);
            Check("HUD starts scene-only and separates compact metrics from diagnostics", TestHudDisplayDefaults, ref passed, ref failed);
            Check("HUD disable / enable / rebinding / timeline reset", TestHudLifecycle, ref passed, ref failed);
            Debug.Log($"[METRICS] passed={passed} failed={failed}");
            Debug.Log($"[METRICS] RESULT={(failed == 0 ? "PASS" : "FAIL")} analytic physics statistics and HUD lifecycle");
        }

        static void Check(string name, Func<bool> test, ref int passed, ref int failed)
        {
            try
            {
                bool ok = test();
                if (ok) passed++; else failed++;
                Debug.Log($"[METRICS] {name}={ok}");
            }
            catch (Exception error) { failed++; Debug.LogError($"[METRICS] {name}: {error}"); }
        }

        static bool Near(double actual, double expected) => !double.IsNaN(actual) && !double.IsInfinity(actual)
            && Math.Abs(actual - expected) < Epsilon;

        static StrokeObservation Observation(double time, bool wet = false, float force = 0f,
            bool valid = true, bool waveEntry = false, Vector3? position = null, float dt = Dt) => new StrokeObservation
        {
            timeSeconds = time, fixedStepSeconds = dt, inputValid = valid, bladeInWater = wet,
            waveInducedEntry = waveEntry, modelForceN = force, boatPosition = position ?? Vector3.zero
        };

        static StrokeMetrics CompletedStroke()
        {
            var metrics = new StrokeMetrics();
            metrics.Observe(Observation(0d));
            metrics.Observe(Observation(0.1d, true, 10f));
            metrics.Observe(Observation(0.2d));
            return metrics;
        }

        static bool TestImpulse()
        {
            var metrics = new StrokeMetrics();
            // Starting underwater is not a known entry and cannot make a complete stroke.
            metrics.Observe(Observation(0d, true, 1000f));
            metrics.Observe(Observation(0.1d));
            metrics.Observe(Observation(0.2d, true, 10f));
            metrics.Observe(Observation(0.3d, true, 25f));
            metrics.Observe(Observation(0.4d, true, 5f));
            bool pending = metrics.StrokeCount == 0 && metrics.StrokeInProgress
                && Near(metrics.CurrentImpulse, 4d) && Near(metrics.CurrentPeakForceN, 25d);
            // Dry force must not be integrated, even if a filter still reports nonzero force.
            metrics.Observe(Observation(0.5d, false, 900f));
            return pending && metrics.StrokeCount == 1 && !metrics.StrokeInProgress
                && Near(metrics.LastImpulse, 4d) && Near(metrics.AverageImpulse, 4d)
                && Near(metrics.LastPeakForceN, 25d) && Near(metrics.CurrentImpulse, 0d)
                && metrics.ObservedSteps == 6 && !metrics.HasStrokeRate;
        }

        static bool TestCadence()
        {
            var metrics = CompletedStroke();
            metrics.Observe(Observation(0.3d));
            metrics.Observe(Observation(0.4d));
            metrics.Observe(Observation(0.5d));
            bool waiting = !metrics.HasStrokeRate;
            metrics.Observe(Observation(0.6d, true, 20f));
            // Entries at 0.1 and 0.6: 60 / .5 = 120, not 60 / .1 drive time.
            bool period = metrics.HasStrokeRate && Near(metrics.StrokeRateSpm, 120d)
                && metrics.StrokeCount == 1 && Near(metrics.CurrentImpulse, 2d);
            metrics.Observe(Observation(0.7d));
            return waiting && period && metrics.StrokeCount == 2 && Near(metrics.LastImpulse, 2d)
                && Near(metrics.AverageImpulse, 1.5d) && Near(metrics.StrokeRateSpm, 120d);
        }

        static bool TestDuplicates()
        {
            var metrics = new StrokeMetrics();
            metrics.Observe(Observation(0d));
            metrics.Observe(Observation(0.1d, true, 10f, position: Vector3.right));
            for (int i = 0; i < 120; i++)
            {
                // Repeated render reads and duplicate delivery cannot increase the integral.
                double ignored = metrics.CurrentImpulse + metrics.DistanceMeters + metrics.StrokeRateSpm;
                if (double.IsNaN(ignored) || metrics.Observe(Observation(0.1d, false, 999f,
                    valid: false, position: Vector3.one * 999f))) return false;
            }
            var malformedDuplicate = Observation(0.1d, dt: float.NaN,
                position: new Vector3(float.NaN, 0f, 0f));
            bool unchanged = !metrics.Observe(malformedDuplicate) && metrics.ObservedSteps == 2
                && metrics.StrokeInProgress && Near(metrics.CurrentImpulse, 1d) && Near(metrics.DistanceMeters, 1d);
            metrics.Observe(Observation(0.2d, position: Vector3.right * 2f));
            bool complete = metrics.StrokeCount == 1 && Near(metrics.LastImpulse, 1d)
                && Near(metrics.DistanceMeters, 2d) && metrics.ObservedSteps == 3;
            bool duplicateExit = !metrics.Observe(Observation(0.2d, true, 1000f))
                && metrics.StrokeCount == 1 && !metrics.StrokeInProgress;
            return unchanged && complete && duplicateExit;
        }

        static bool TestMissingStep()
        {
            var metrics = CompletedStroke();
            metrics.Observe(Observation(0.3d, true, 50f));
            bool previousRate = metrics.HasStrokeRate;
            // 0.4 is missing while wet; never fill its integral from held force or elapsed time.
            metrics.Observe(Observation(0.5d, true, 50f));
            metrics.Observe(Observation(0.6d, true, 50f));
            bool aborted = metrics.StrokeCount == 1 && !metrics.StrokeInProgress
                && Near(metrics.LastImpulse, 1d) && Near(metrics.CurrentImpulse, 0d) && !metrics.HasStrokeRate;
            metrics.Observe(Observation(0.7d));
            metrics.Observe(Observation(0.8d, true, 20f));
            bool noBridgeRate = !metrics.HasStrokeRate;
            metrics.Observe(Observation(0.9d));
            return previousRate && aborted && noBridgeRate && metrics.StrokeCount == 2
                && Near(metrics.AverageImpulse, 1.5d);
        }

        static bool TestInvalidInput()
        {
            var metrics = CompletedStroke();
            metrics.Observe(Observation(0.3d, true, 90f));
            metrics.Observe(Observation(0.4d, false, valid: false));
            metrics.Observe(Observation(0.5d, true, 90f));
            bool aborted = metrics.StrokeCount == 1 && !metrics.StrokeInProgress && !metrics.HasStrokeRate;
            metrics.Observe(Observation(0.6d));
            metrics.Observe(Observation(0.7d, true, 20f));
            metrics.Observe(Observation(0.8d));
            return aborted && metrics.StrokeCount == 2 && Near(metrics.LastImpulse, 2d)
                && Near(metrics.AverageImpulse, 1.5d) && !metrics.HasStrokeRate;
        }

        static bool TestInvalidForce()
        {
            foreach (float force in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
            {
                var metrics = CompletedStroke();
                metrics.Observe(Observation(0.3d, true, 10f));
                bool acceptedTime = metrics.Observe(Observation(0.4d, true, force, position: Vector3.right));
                metrics.Observe(Observation(0.5d, true, 10f, position: Vector3.right * 2f));
                if (!acceptedTime || metrics.StrokeCount != 1 || metrics.StrokeInProgress || metrics.HasStrokeRate
                    || !Near(metrics.LastImpulse, 1d) || !Near(metrics.DistanceMeters, 2d)) return false;
                metrics.Observe(Observation(0.6d));
                metrics.Observe(Observation(0.7d, true, 20f));
                metrics.Observe(Observation(0.8d));
                if (metrics.StrokeCount != 2 || !Near(metrics.LastImpulse, 2d)) return false;
            }
            return true;
        }

        static bool TestMalformedObservations()
        {
            var malformed = new[]
            {
                Observation(double.NaN), Observation(double.PositiveInfinity), Observation(double.NegativeInfinity),
                Observation(0.4d, dt: 0f), Observation(0.4d, dt: -Dt), Observation(0.4d, dt: float.NaN),
                Observation(0.4d, dt: float.PositiveInfinity),
                Observation(0.4d, position: new Vector3(float.NaN, 0f, 0f)),
                Observation(0.4d, position: new Vector3(0f, float.PositiveInfinity, 0f))
            };
            foreach (var bad in malformed)
            {
                var metrics = CompletedStroke();
                metrics.Observe(Observation(0.3d, true, 10f, position: Vector3.right));
                int steps = metrics.ObservedSteps;
                if (metrics.Observe(bad) || metrics.ObservedSteps != steps || metrics.StrokeCount != 1
                    || metrics.StrokeInProgress || metrics.HasStrokeRate || !Near(metrics.LastImpulse, 1d)
                    || !Near(metrics.DistanceMeters, 1d)) return false;
                // No giant path segment is manufactured across an invalid position/step.
                metrics.Observe(Observation(0.5d, true, 99f, position: Vector3.right * 100f));
                if (metrics.StrokeInProgress || !Near(metrics.DistanceMeters, 1d)) return false;
                metrics.Observe(Observation(0.6d, position: Vector3.right * 101f));
                metrics.Observe(Observation(0.7d, true, 20f, position: Vector3.right * 102f));
                metrics.Observe(Observation(0.8d, position: Vector3.right * 103f));
                if (metrics.StrokeCount != 2 || !Near(metrics.LastImpulse, 2d)
                    || !Near(metrics.DistanceMeters, 4d) || metrics.HasStrokeRate) return false;
            }
            return true;
        }

        static bool TestReversal()
        {
            var metrics = CompletedStroke();
            metrics.Observe(Observation(0.3d, true, 10f, position: Vector3.right * 20f));
            metrics.Observe(Observation(0.05d, true, 99f, position: Vector3.right * 999f));
            bool cleared = metrics.ObservedSteps == 1 && metrics.StrokeCount == 0 && !metrics.StrokeInProgress
                && !metrics.HasStrokeRate && Near(metrics.LastImpulse, 0d) && Near(metrics.DistanceMeters, 0d);
            metrics.Observe(Observation(0.15d, position: Vector3.right * 1000f));
            metrics.Observe(Observation(0.25d, true, 20f, position: Vector3.right * 1001f));
            metrics.Observe(Observation(0.35d, position: Vector3.right * 1002f));
            return cleared && metrics.StrokeCount == 1 && Near(metrics.LastImpulse, 2d)
                && Near(metrics.DistanceMeters, 3d) && !metrics.HasStrokeRate;
        }

        static bool TestWaveEntry()
        {
            var metrics = CompletedStroke();
            metrics.Observe(Observation(0.3d, true, 99f, waveEntry: true));
            metrics.Observe(Observation(0.4d, true, 99f));
            bool excluded = metrics.StrokeCount == 1 && !metrics.StrokeInProgress && !metrics.HasStrokeRate;
            metrics.Observe(Observation(0.5d));
            metrics.Observe(Observation(0.6d, true, 20f));
            metrics.Observe(Observation(0.7d));
            return excluded && metrics.StrokeCount == 2 && Near(metrics.LastImpulse, 2d)
                && Near(metrics.AverageImpulse, 1.5d) && !metrics.HasStrokeRate;
        }

        static bool TestDistance()
        {
            var metrics = new StrokeMetrics();
            metrics.Observe(Observation(0d, position: new Vector3(0f, 0f, 0f)));
            metrics.Observe(Observation(0.1d, valid: false, position: new Vector3(3f, 10f, 4f)));
            metrics.Observe(Observation(0.2d, true, float.NaN, position: new Vector3(6f, -10f, 8f)));
            metrics.Observe(Observation(0.3d, true, 99f, waveEntry: true, position: new Vector3(6f, 999f, 8f)));
            metrics.Observe(Observation(0.4d, position: new Vector3(3f, 0f, 4f)));
            bool travel = metrics.StrokeCount == 0 && Near(metrics.DistanceMeters, 15d);
            // Finite float positions whose difference would overflow a Vector3 remain a finite distance.
            var large = new StrokeMetrics();
            large.Observe(Observation(0d, position: Vector3.right * -float.MaxValue));
            large.Observe(Observation(0.1d, position: Vector3.right * float.MaxValue));
            double expected = 2d * float.MaxValue;
            return travel && !double.IsInfinity(large.DistanceMeters) && large.DistanceMeters == expected;
        }

        static bool TestReset()
        {
            var metrics = CompletedStroke();
            metrics.Observe(Observation(0.3d, true, 50f, position: Vector3.right));
            metrics.Reset();
            bool clear = metrics.StrokeCount == 0 && metrics.ObservedSteps == 0 && !metrics.StrokeInProgress
                && !metrics.HasStrokeRate && Near(metrics.StrokeRateSpm, 0d) && Near(metrics.CurrentImpulse, 0d)
                && Near(metrics.LastImpulse, 0d) && Near(metrics.AverageImpulse, 0d)
                && Near(metrics.LastPeakForceN, 0d) && Near(metrics.CurrentPeakForceN, 0d)
                && Near(metrics.DistanceMeters, 0d);
            metrics.Observe(Observation(0d, true, 999f));
            metrics.Observe(Observation(0.1d));
            metrics.Observe(Observation(0.2d, true, 20f));
            metrics.Observe(Observation(0.3d));
            return clear && metrics.StrokeCount == 1 && Near(metrics.LastImpulse, 2d) && !metrics.HasStrokeRate;
        }

        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance).GetValue(target);
        static void Call(object target, string name, params object[] arguments) =>
            target.GetType().GetMethod(name, PrivateInstance).Invoke(target, arguments);
        static int Subscribers(SimulationSession session, string name) =>
            Field<Delegate>(session, name)?.GetInvocationList().Length ?? 0;
        static void Emit(SimulationSession session, SimulationFrame frame) =>
            Field<Action<SimulationFrame>>(session, "FrameCompleted")?.Invoke(frame);
        static void EmitReset(SimulationSession session) => Field<Action>(session, "TimelineReset")?.Invoke();

        static bool HudFeedback(RideHud hud, CableDeviceFeedback feedback, ForceFeedbackRequest request) =>
            (bool)typeof(RideHud).GetMethod("CurrentFeedback", PrivateInstance, null,
                new[] { typeof(CableDeviceFeedback), typeof(ForceFeedbackRequest) }, null)
                .Invoke(hud, new object[] { feedback, request });
        static bool HudFeedback(RideHud hud, PlatformDeviceFeedback feedback, PlatformMotionRequest request) =>
            (bool)typeof(RideHud).GetMethod("CurrentFeedback", PrivateInstance, null,
                new[] { typeof(PlatformDeviceFeedback), typeof(PlatformMotionRequest) }, null)
                .Invoke(hud, new object[] { feedback, request });
        static void SetHudFrame(RideHud hud, SimulationFrame frame) =>
            typeof(RideHud).GetField("_frame", PrivateInstance).SetValue(hud, frame);

        static bool TestHudAsyncFeedback()
        {
            var go = new GameObject("metrics HUD asynchronous feedback oracle");
            go.SetActive(false);
            try
            {
                var hud = go.AddComponent<RideHud>();
                var forceRequest = new ForceFeedbackRequest { valid = true, commandSequence = 10, timeSeconds = 1d };
                var platformRequest = new PlatformMotionRequest { valid = true, commandSequence = 10, timeSeconds = 1d };
                var frame = new SimulationFrame
                {
                    timeSeconds = 1d,
                    paddles = new[] { new PaddleSample { valid = true, sequence = 77 } },
                    cableFeedback = new CableDeviceFeedback { valid = true, commandSequence = 9, timeSeconds = 0.98d },
                    platformFeedback = new PlatformDeviceFeedback { valid = true, commandSequence = 9, timeSeconds = 0.98d },
                    closedLoop = new ClosedLoopTelemetry
                    {
                        active = true, ready = true, feedbackFresh = true, encoderConsumed = true,
                        commandSequence = 20, acknowledgedCommandSequence = 9, feedbackSequence = 77,
                        receivedAgeSeconds = 0.02d, commandAckAgeSeconds = 0.03d,
                        hostProcessId = 100, controllerProcessId = 200, status = "UDP_EXECUTION_FEEDBACK_CONSUMED",
                        feedback = new ClosedLoopFeedback { kind = "feedback", valid = true, commandSequence = 9 }
                    }
                };
                SetHudFrame(hud, frame);
                bool accepted = HudFeedback(hud, frame.cableFeedback, forceRequest)
                    && HudFeedback(hud, frame.platformFeedback, platformRequest);
                string text = (string)typeof(RideHud).GetMethod("IndependentCommunicationSummary", PrivateInstance).Invoke(hud, null);
                bool communicatesAck = text.Contains("独立 UDP") && text.Contains("本帧已消费有效执行回报")
                    && text.Contains("已发送命令 20") && text.Contains("回执对应命令 9") && text.Contains("回包序号 77");
                bool rejectsInvalid = true;
                foreach (Action<ClosedLoopTelemetry> corrupt in new Action<ClosedLoopTelemetry>[]
                {
                    t => t.acknowledgedCommandSequence = 8,
                    t => t.feedback.commandSequence = 8,
                    t => t.feedbackFresh = false,
                    t => t.encoderConsumed = false,
                    t => t.ready = false,
                    t => t.receivedAgeSeconds = 0.100001d,
                    t => t.commandAckAgeSeconds = 0.100001d,
                    t => t.receivedAgeSeconds = -1d,
                    t => t.receivedAgeSeconds = double.NaN,
                    t => t.feedback.valid = false,
                    t => t.feedback.watchdog = true,
                    t => t.feedbackSequence = 76
                })
                {
                    var invalid = JsonUtility.FromJson<SimulationFrame>(JsonUtility.ToJson(frame));
                    corrupt(invalid.closedLoop);
                    SetHudFrame(hud, invalid);
                    rejectsInvalid &= !HudFeedback(hud, invalid.cableFeedback, forceRequest)
                        && !HudFeedback(hud, invalid.platformFeedback, platformRequest);
                    string rejected = (string)typeof(RideHud).GetMethod("IndependentCommunicationSummary", PrivateInstance).Invoke(hud, null);
                    rejectsInvalid &= rejected.Contains("等待有效回报 / 回报过期");
                }
                SetHudFrame(hud, frame);
                frame.cableFeedback.commandSequence = 8;
                frame.platformFeedback.commandSequence = 8;
                bool rejectsDeviceIdentity = !HudFeedback(hud, frame.cableFeedback, forceRequest)
                    && !HudFeedback(hud, frame.platformFeedback, platformRequest);
                frame.cableFeedback.commandSequence = frame.platformFeedback.commandSequence = 9;
                frame.cableFeedback.valid = frame.platformFeedback.valid = false;
                bool rejectsInvalidDevice = !HudFeedback(hud, frame.cableFeedback, forceRequest)
                    && !HudFeedback(hud, frame.platformFeedback, platformRequest);
                frame.cableFeedback.valid = frame.platformFeedback.valid = true;
                frame.cableFeedback.timeSeconds = frame.platformFeedback.timeSeconds = 1.01d;
                bool rejectsFuture = !HudFeedback(hud, frame.cableFeedback, forceRequest)
                    && !HudFeedback(hud, frame.platformFeedback, platformRequest);
                return accepted && communicatesAck && rejectsInvalid && rejectsDeviceIdentity && rejectsInvalidDevice && rejectsFuture;
            }
            finally { Object.DestroyImmediate(go); }
        }

        static bool TestHudDirectFeedback()
        {
            var go = new GameObject("metrics HUD direct feedback oracle");
            go.SetActive(false);
            try
            {
                var hud = go.AddComponent<RideHud>();
                SetHudFrame(hud, new SimulationFrame { closedLoop = new ClosedLoopTelemetry { active = false } });
                var request = new ForceFeedbackRequest { valid = true, commandSequence = 10, timeSeconds = 2d };
                var platformRequest = new PlatformMotionRequest { valid = true, commandSequence = 10, timeSeconds = 2d };
                var cable = new CableDeviceFeedback { valid = true, commandSequence = 10, timeSeconds = 2d };
                var platform = new PlatformDeviceFeedback { valid = true, commandSequence = 10, timeSeconds = 2d };
                bool matching = HudFeedback(hud, cable, request) && HudFeedback(hud, platform, platformRequest);
                cable.commandSequence = platform.commandSequence = 9;
                bool oldSequence = !HudFeedback(hud, cable, request) && !HudFeedback(hud, platform, platformRequest);
                cable.commandSequence = platform.commandSequence = 10;
                cable.timeSeconds = platform.timeSeconds = 1.99d;
                bool oldTime = !HudFeedback(hud, cable, request) && !HudFeedback(hud, platform, platformRequest);
                cable.timeSeconds = platform.timeSeconds = 2d;
                cable.valid = platform.valid = false;
                bool invalid = !HudFeedback(hud, cable, request) && !HudFeedback(hud, platform, platformRequest);
                return matching && oldSequence && oldTime && invalid;
            }
            finally { Object.DestroyImmediate(go); }
        }

        static bool TestHudPhysics()
        {
            var sessionGo = new GameObject("metrics physics session oracle");
            sessionGo.SetActive(false);
            var paddleGo = new GameObject("metrics physics paddle oracle");
            paddleGo.SetActive(false);
            var hudGo = new GameObject("metrics physics HUD oracle");
            hudGo.SetActive(false);
            try
            {
                var session = sessionGo.AddComponent<SimulationSession>();
                var driver = paddleGo.AddComponent<PaddleDriver>();
                var hydro = paddleGo.AddComponent<PaddleHydrodynamics>();
                var hud = hudGo.AddComponent<RideHud>();
                session.drivers = new[] { driver }; session.hydrodynamics = new[] { hydro };
                hud.session = session; hud.driver = driver;
                Call(hud, "OnEnable");
                Action<double, bool, float, bool> step = (time, wet, rawForce, valid) =>
                {
                    typeof(PaddleHydrodynamics).GetProperty("BladeInWater").SetValue(hydro, wet);
                    typeof(PaddleHydrodynamics).GetProperty("WaveInducedEntry").SetValue(hydro, false);
                    typeof(PaddleHydrodynamics).GetProperty("InputValid").SetValue(hydro, valid);
                    Emit(session, new SimulationFrame
                    {
                        timeSeconds = time, fixedStepSeconds = Dt, boatPosition = Vector3.right * (float)time,
                        paddles = new[] { new PaddleSample { valid = valid, source = PaddleInputKind.Synthetic } },
                        rawWaterForceN = new[] { Vector3.right * rawForce },
                        filteredFeedbackForceN = new[] { Vector3.right },
                        feedbackRequests = new[] { new ForceFeedbackRequest { valid = valid, forceWorldN = Vector3.right * 2f } },
                        cableFeedback = new CableDeviceFeedback { valid = true, forceWorldN = Vector3.right * 3f }
                    });
                };
                step(0d, false, 999f, true);
                step(0.1d, true, 10f, true);
                for (int i = 0; i < 60; i++)
                {
                    float read = hud.LastImpulse + hud.AvgImpulse + hud.Distance + hud.StrokeRateSpm;
                    if (float.IsNaN(read)) return false;
                }
                step(0.1d, true, 999f, true);
                step(0.2d, true, 30f, true);
                step(0.3d, false, 999f, true);
                bool modelIntegral = hud.StrokeCount == 1 && Near(hud.LastImpulse, 4d)
                    && Near(hud.AvgImpulse, 4d) && Near(hud.Distance, 0.3d)
                    && Field<StrokeMetrics>(hud, "_metrics").ObservedSteps == 4;
                step(0.4d, true, 50f, true);
                step(0.5d, false, 0f, false);
                bool invalidExit = hud.StrokeCount == 1 && Near(hud.LastImpulse, 4d)
                    && !Field<StrokeMetrics>(hud, "_metrics").StrokeInProgress;
                return modelIntegral && invalidExit;
            }
            finally
            {
                var hud = hudGo.GetComponent<RideHud>();
                if (hud != null) Call(hud, "OnDisable");
                Object.DestroyImmediate(hudGo); Object.DestroyImmediate(paddleGo); Object.DestroyImmediate(sessionGo);
            }
        }

        static bool TestHudAcquisition()
        {
            var go = new GameObject("metrics HUD acquisition oracle");
            go.SetActive(false);
            try
            {
                var hud = go.AddComponent<RideHud>();
                var snapshot = new AcquisitionSnapshot
                {
                    replayed = true, sourceId = "captured-source", rawValidCount = 3, calibratedValidCount = 1,
                    samePacket = false, status = "CAPTURED_PARTIAL", channels = new AcquisitionChannelSnapshot[4]
                };
                for (int i = 0; i < 4; i++) snapshot.channels[i] = new AcquisitionChannelSnapshot
                {
                    channelId = "CH" + (i + 1), sourceKind = AcquisitionSourceKind.Fixture,
                    present = true, hasRaw = true, rawNumericFinite = true, raw = 100 + i, rawUnits = "adc-count",
                    rawValid = i < 3, rawStatus = i < 3 ? "OK" : "STALE", ageSeconds = 0.025d,
                    packetSequence = 17, sampleSequence = (uint)(23 + i), mapping = "Unassigned",
                    forceStatus = "UNCALIBRATED"
                };
                snapshot.channels[0].calibratedValid = snapshot.channels[0].calibrationMeasured = true;
                snapshot.channels[0].forceN = 12.5d;
                snapshot.channels[0].calibrationSourceId = "captured-calibration";
                snapshot.channels[0].calibrationVersion = "v7";
                snapshot.channels[3].rawNumericFinite = false;
                snapshot.channels[3].rawText = "NaN";
                var frame = new SimulationFrame { hasAcquisitionSnapshot = true, acquisitionSnapshot = snapshot };
                // Frame has no paddler arrays: raw acquisition remains displayable independently.
                Call(hud, "OnFrameCompleted", frame);
                string status = Field<string>(hud, "_acquisitionStatus");
                string[] rows = Field<string[]>(hud, "_acquisitionRows");
                bool captured = status.Contains("回放") && status.Contains("captured-source")
                    && status.Contains("CAPTURED_PARTIAL") && status.Contains("3/4")
                    && rows.Length == 4 && rows[0].Contains("100 adc-count") && rows[0].Contains("captured-calibration/v7")
                    && rows[1].Contains("101 adc-count") && rows[1].Contains("UNCALIBRATED")
                    && rows[2].Contains("CH3") && rows[3].Contains("NaN") && rows[3].Contains("STALE")
                    && Near(hud.LastImpulse, 0d) && hud.StrokeCount == 0;
                // The schema flag is authoritative, even if an object was accidentally retained.
                frame.hasAcquisitionSnapshot = false;
                Call(hud, "OnFrameCompleted", frame);
                bool missing = Field<string>(hud, "_acquisitionStatus").Contains("没有采集快照")
                    && Array.TrueForAll(Field<string[]>(hud, "_acquisitionRows"), row => row.Contains("未收到"));
                frame.hasAcquisitionSnapshot = true;
                Call(hud, "OnFrameCompleted", frame);
                hud.ResetStatistics();
                bool reset = Array.TrueForAll(Field<string[]>(hud, "_acquisitionRows"), row => row.Contains("未收到"));
                return captured && missing && reset
                    && typeof(RideHud).GetMethod("SetRawAcquisitionDisplay") == null;
            }
            finally { Object.DestroyImmediate(go); }
        }

        static bool TestHudLifecycle()
        {
            var firstGo = new GameObject("metrics first session oracle");
            firstGo.SetActive(false);
            var secondGo = new GameObject("metrics second session oracle");
            secondGo.SetActive(false);
            var hudGo = new GameObject("metrics HUD lifecycle oracle");
            hudGo.SetActive(false);
            try
            {
                var first = firstGo.AddComponent<SimulationSession>();
                var second = secondGo.AddComponent<SimulationSession>();
                var hud = hudGo.AddComponent<RideHud>();
                hud.session = first;
                // Batch executeMethod runs outside Play Mode. Invoke the lifecycle methods
                // explicitly on inactive objects so the oracle does not depend on editor callbacks.
                Call(hud, "OnEnable");
                bool enabled = Subscribers(first, "FrameCompleted") == 1 && Subscribers(first, "TimelineReset") == 1;
                Call(hud, "TrySubscribe"); Call(hud, "TrySubscribe");
                bool noDuplicate = Subscribers(first, "FrameCompleted") == 1;
                var metrics = Field<StrokeMetrics>(hud, "_metrics");
                metrics.Observe(Observation(0d)); metrics.Observe(Observation(0.1d, true, 10f));
                metrics.Observe(Observation(0.2d));
                EmitReset(first);
                bool timeline = hud.StrokeCount == 0 && metrics.ObservedSteps == 0;
                Call(hud, "OnDisable");
                bool disabled = Subscribers(first, "FrameCompleted") == 0 && Subscribers(first, "TimelineReset") == 0;
                Emit(first, new SimulationFrame { hasAcquisitionSnapshot = true,
                    acquisitionSnapshot = new AcquisitionSnapshot { status = "DISABLED_EVENT" } });
                bool ignoresDisabled = !Field<string>(hud, "_acquisitionStatus").Contains("DISABLED_EVENT");
                Call(hud, "OnEnable");
                bool immediateRebind = Subscribers(first, "FrameCompleted") == 1 && Subscribers(first, "TimelineReset") == 1;
                Emit(first, new SimulationFrame { hasAcquisitionSnapshot = true,
                    acquisitionSnapshot = new AcquisitionSnapshot { status = "FIRST_EVENT" } });
                bool receives = Field<string>(hud, "_acquisitionStatus").Contains("FIRST_EVENT");
                hud.session = second;
                Call(hud, "TrySubscribe");
                bool swapped = Subscribers(first, "FrameCompleted") == 0 && Subscribers(first, "TimelineReset") == 0
                    && Subscribers(second, "FrameCompleted") == 1 && Subscribers(second, "TimelineReset") == 1;
                Emit(first, new SimulationFrame { hasAcquisitionSnapshot = true,
                    acquisitionSnapshot = new AcquisitionSnapshot { status = "OLD_SESSION" } });
                bool ignoresOld = !Field<string>(hud, "_acquisitionStatus").Contains("OLD_SESSION");
                Emit(second, new SimulationFrame { hasAcquisitionSnapshot = true,
                    acquisitionSnapshot = new AcquisitionSnapshot { status = "SECOND_EVENT" } });
                bool newSession = Field<string>(hud, "_acquisitionStatus").Contains("SECOND_EVENT");
                Call(hud, "OnDisable");
                Call(hud, "OnDestroy");
                bool destroyed = Subscribers(second, "FrameCompleted") == 0 && Subscribers(second, "TimelineReset") == 0;
                return enabled && noDuplicate && timeline && disabled && ignoresDisabled && immediateRebind
                    && receives && swapped && ignoresOld && newSession && destroyed;
            }
            finally
            {
                if (hudGo != null)
                {
                    var hud = hudGo.GetComponent<RideHud>();
                    if (hud != null) Call(hud, "OnDisable");
                    Object.DestroyImmediate(hudGo);
                }
                Object.DestroyImmediate(firstGo); Object.DestroyImmediate(secondGo);
            }
        }

        static bool TestHudDisplayDefaults()
        {
            var go = new GameObject("metrics HUD display defaults oracle");
            go.SetActive(false);
            try
            {
                var hud = go.AddComponent<RideHud>();
                bool cleanStart = !hud.visible && !hud.compactMetricsVisible
                    && hud.toggleKey == KeyCode.F1 && hud.toggleDiagnosticsKey == KeyCode.F9;
                hud.ToggleCompactMetrics();
                bool compactOnly = hud.compactMetricsVisible && !hud.visible;
                hud.ToggleDiagnosticsPanel();
                bool diagnosticsIndependent = hud.compactMetricsVisible && hud.visible;
                hud.ToggleDiagnosticsPanel();
                hud.ToggleCompactMetrics();
                bool hiddenAgain = !hud.visible && !hud.compactMetricsVisible;
                hud.ShowAcquisitionDetails();
                bool acquisitionOpensDiagnostics = hud.visible && hud.detailsExpanded && !hud.compactMetricsVisible;
                return cleanStart && compactOnly && diagnosticsIndependent && hiddenAgain && acquisitionOpensDiagnostics;
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
