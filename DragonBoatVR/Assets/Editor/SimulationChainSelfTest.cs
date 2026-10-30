using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    public static class SimulationChainSelfTest
    {
        const float Dt = 0.01f;
        static int _passed;
        static int _failed;

        [MenuItem("DragonBoatVR/⑧ 输入与回放链路自检")]
        public static void Run()
        {
            _passed = _failed = 0;
            bool automatic = Physics.autoSimulation;
            Physics.autoSimulation = false;
            try
            {
                Check("freshness / finite / sequence wrap", TestPackets);
                Check("single-user configuration / spare scene input isolation", TestSingleUser);
                Check("left/right force and torque", TestSides);
                Check("coasting after paddling stops", TestCoasting);
                Check("waves independently move boat", TestWaves);
                Check("unified recording/replay and expiry", TestReplay);
                Check("nonzero-origin recording and mismatched configuration", TestNonzeroReplay);
                Check("50Hz UDP observations / repeated sequence / invalid-frame replay", TestUdpObservationReplay);
                Check("external sequence gate cannot be bypassed by source tags", TestExternalGate);
                Check("recording configuration stability and complete frame order", TestRecordingIntegrity);
                Check("acquisition capture / replay / tamper / profile mutation", TestAcquisitionRecording);
                Check("explicit raw profile / transactional listener / recording and replay refusal", TestRawAcquisitionProfile);
                Check("raw profile restart replay / live isolation / listener restoration", TestRawProfileRestartReplay);
                Check("unique recordings / no overwrite / latest replay / start failure", TestRecordingPreservation);
                Check("recording index survives new session / index and writer failures", TestRecordingPersistence);
                Check("background recording FIFO / bounded queue / faults / timeout", BackgroundRecordingWriterSelfTest.CheckAll);
                Check("background recording failure preserves complete file and index", TestBackgroundRecordingPreservation);
                Check("recording content hash primitives", RecordingIntegritySelfTest.CheckAll);
                Check("recording completion / tail truncation / content mutation / legacy status", TestRecordingCompletion);
                Check("replay HUD input switch / recording controls / EOF", TestReplayControls);
                Check("replay rejects live configuration / input mode / timestep changes", TestReplayMutation);
                Check("platform-relative HMD transform", TestHeadTransform);
            }
            finally { Physics.autoSimulation = automatic; }
            Debug.Log($"[CHAIN] passed={_passed} failed={_failed}");
            Debug.Log(_failed == 0 ? "[CHAIN] RESULT=PASS software input/physics/replay checks"
                : "[CHAIN] RESULT=FAIL see individual checks");
        }

        static void Check(string name, Func<bool> test)
        {
            try
            {
                bool pass = test();
                if (pass) _passed++; else _failed++;
                Debug.Log($"[CHAIN] {name}={pass}");
            }
            catch (Exception e) { _failed++; Debug.LogError($"[CHAIN] {name}: {e}"); }
        }
        static void NewScene() { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        static void Call(object x, string name) => x.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(x, null);

        static bool TestPackets()
        {
            NewScene();
            var bridge = new GameObject("bridge").AddComponent<HardwareUdpBridge>();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            var state = new HardwareUdpBridge.PaddleState { seq = uint.MaxValue - 1, position = new Vector3(1, 2, 3), eulerDeg = new Vector3(5, 6, 7), gripForce = 8 };
            bool first = bridge.AcceptPaddle(state, now);
            bool duplicate = !bridge.AcceptPaddle(state, now);
            state.seq = uint.MaxValue; bool beforeWrap = bridge.AcceptPaddle(state, now);
            state.seq = 0; bool wrap = bridge.AcceptPaddle(state, now);
            state.seq = uint.MaxValue - 3; bool old = !bridge.AcceptPaddle(state, now);
            state.seq = 1; state.position.x = float.NaN; bool nan = !bridge.AcceptPaddle(state, now);
            bool fresh = bridge.TryGetPaddleAt(now, 0.1f, out var accepted) && accepted.seq == 0;
            bool stale = !bridge.TryGetPaddleAt(now + (long)(System.Diagnostics.Stopwatch.Frequency * 0.11), 0.1f, out _);
            bool future = !bridge.TryGetPaddleAt(now - 1, 0.1f, out _);
            bool truncated = HardwareUdpBridge.ParsePaddleState(new byte[3]) == null;
            return first && duplicate && beforeWrap && wrap && old && nan && fresh && stale && future && truncated;
        }

        static SimulationSession CreateSession(bool waves = false, bool initialize = true)
        {
            NewScene();
            var wave = new GameObject("wave").AddComponent<WaveField>();
            wave.intensity = waves ? 1 : 0;
            Call(wave, "Awake");
            var boat = new GameObject("boat").AddComponent<BoatBuoyancy>();
            boat.transform.position = new Vector3(0, 0.12f, 0);
            var rb = boat.GetComponent<Rigidbody>(); rb.mass = 150;
            // 与当前Demo船体尺寸一致：没有碰撞几何时，单位惯量不足以承受偏心浮力。
            var hull = boat.gameObject.AddComponent<BoxCollider>(); hull.size = new Vector3(1.2f, 0.6f, 8f);
            rb.ResetInertiaTensor();
            Call(boat, "Awake"); Call(boat, "Start");
            var go = new GameObject("paddle");
            var driver = go.AddComponent<PaddleDriver>(); driver.boat = boat;
            var hydro = go.AddComponent<PaddleHydrodynamics>(); hydro.targetBoat = boat;
            var session = new GameObject("session").AddComponent<SimulationSession>();
            session.boat = boat; session.wave = wave; session.drivers = new[] { driver };
            session.configuration = new SessionConfiguration { equivalentBoatMassKg = 150,
                paddlers = new[] { new PaddlerConfiguration() } };
            if (!initialize) return session;
            session.Initialize(false);
            foreach (var d in session.drivers) Call(d, "Start");
            foreach (var h in session.hydrodynamics) Call(h, "Start");
            Physics.SyncTransforms();
            return session;
        }

        static void Step(SimulationSession s)
        {
            s.StepInputs(Dt);
            Call(s.boat, "FixedUpdate");
            Physics.Simulate(Dt);
            s.StepDevices(Dt);
        }
        static bool TestSides()
        {
            // The same single-user setup is exercised on each side in separate scenes.
            bool ObserveSide(bool leftSide, out Vector3 force, out Vector3 torque)
            {
                var s = CreateSession();
                s.drivers[0].leftSide = leftSide;
                s.hydrodynamics[0].balanceYawRoll = false;
                s.drivers[0].SetMode(PaddleDriver.Mode.External);
                s.drivers[0].SetExternalSample(new PaddleSample { sequence = 1, timeSeconds = Dt,
                    bladePosition = new Vector3(leftSide ? -1 : 1, -0.2f, 0), bladeRotation = Quaternion.identity,
                    bladeVelocity = new Vector3(0, 0, -2), valid = true, source = PaddleInputKind.External });
                s.StepInputs(Dt);
                var hydro = s.hydrodynamics[0];
                force = hydro.RawWaterForceN;
                torque = hydro.BoatPropulsionTorqueNm;
                return hydro.InputValid && !hydro.balanceYawRoll && s.drivers.Length == 1;
            }
            bool right = ObserveSide(false, out var rightForce, out var rightTorque);
            bool left = ObserveSide(true, out var leftForce, out var leftTorque);
            float forceError = (rightForce - leftForce).magnitude;
            float torqueError = Mathf.Abs(rightTorque.y + leftTorque.y);
            Debug.Log($"[CHAIN] single-user sides force={rightForce.z:F2}N opposite yaw={rightTorque.y:F2}/{leftTorque.y:F2}Nm");
            return right && left && rightForce.z > 1 && forceError < 1e-4 && torqueError < 1e-4
                && rightTorque.y * leftTorque.y < 0;
        }
        static bool TestSingleUser()
        {
            var s = CreateSession(initialize: false);
            var spare = new GameObject("spare scene paddle").AddComponent<PaddleDriver>();
            spare.gameObject.AddComponent<PaddleHydrodynamics>();
            s.drivers = new[] { s.drivers[0], spare };
            bool RejectCount(int count)
            {
                s.configuration.paddlers = new PaddlerConfiguration[count];
                for (int i = 0; i < count; i++) s.configuration.paddlers[i] = new PaddlerConfiguration { id = "paddler-" + i };
                try { s.Initialize(false); return false; }
                catch (InvalidOperationException) { return true; }
            }
            int originalInputs = UnityEngine.Object.FindObjectsOfType<PaddleDriver>(true).Length;
            bool empty = RejectCount(0), multiple = RejectCount(2);
            bool noClone = UnityEngine.Object.FindObjectsOfType<PaddleDriver>(true).Length == originalInputs;
            s.configuration.paddlers = new[] { new PaddlerConfiguration() };
            s.Initialize(false);
            Call(s.drivers[0], "Start"); Call(s.hydrodynamics[0], "Start");
            Physics.SyncTransforms(); Step(s);
            bool isolated = s.drivers.Length == 1 && s.hydrodynamics.Length == 1 && !spare.gameObject.activeSelf
                && s.LastFrame.paddles.Length == 1 && s.LastFrame.feedbackRequests.Length == 1
                && s.LastFrame.rawWaterForceN.Length == 1;
            Debug.Log($"[CHAIN] single-user emptyRejected={empty} multipleRejected={multiple} noClone={noClone} spareIsolated={isolated}");
            return empty && multiple && noClone && isolated;
        }
        static bool TestCoasting()
        {
            var s = CreateSession();
            s.drivers[0].strokesPerMinute = 0;
            var rb = s.boat.BoatRb; rb.velocity = Vector3.forward;
            Vector3 initial = rb.position;
            for (int i = 0; i < 100; i++) Step(s);
            float speed = rb.velocity.z; float distance = rb.position.z - initial.z;
            Debug.Log($"[CHAIN] coast distance={distance:F3}m speed={speed:F3}m/s");
            return speed > 0.2f && speed < 1 && distance > 0.4f && s.hydrodynamics[0].RawWaterForceN.magnitude < 1e-4f;
        }
        static bool TestWaves()
        {
            var s = CreateSession(waves: true); s.drivers[0].strokesPerMinute = 0;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 600; i++) { Step(s); if (i > 150) { min = Mathf.Min(min, s.boat.transform.position.y); max = Mathf.Max(max, s.boat.transform.position.y); } }
            Debug.Log($"[CHAIN] independent waves boatY range={max-min:F4}m");
            return max - min > 0.02f && max - min < 0.6f && Mathf.Abs(max) < 1f && Mathf.Abs(min) < 1f
                && s.wave.SimTime > 5.9f && s.drivers[0].strokesPerMinute == 0;
        }
        static bool TestReplay()
        {
            var s = CreateSession();
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../tools/check_recordings/chain-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            s.BeginRecording(path);
            var reference = new System.Collections.Generic.List<SimulationFrame>();
            for (int i = 0; i < 300; i++) { Step(s); reference.Add(JsonUtility.FromJson<SimulationFrame>(JsonUtility.ToJson(s.LastFrame))); }
            var closeWatch = System.Diagnostics.Stopwatch.StartNew();
            s.StopRecording(); closeWatch.Stop();
            bool completed = string.IsNullOrEmpty(s.RecordingError)
                && s.LastRecordingWrittenFrames == reference.Count && s.LatestRecordingPath == path;
            Debug.Log($"[CHAIN] replay recording closed={completed} frames={s.LastRecordingWrittenFrames}/{reference.Count} "
                + $"closeMs={closeWatch.Elapsed.TotalMilliseconds:F3} maxWriteMs={s.LastRecordingMaximumWriteSeconds * 1000:F3} "
                + $"error={s.RecordingError}; path={path}");
            // A failed close is already a failed diagnostic. Do not replace its
            // evidence with a sharing error from reopening the worker-owned file.
            if (!completed) return false;
            s.BeginReplay(path);
            float inputError = 0, forceError = 0, boatError = 0;
            bool identity = true;
            for (int i = 0; i < 300; i++)
            {
                Step(s);
                inputError = Mathf.Max(inputError, (reference[i].paddles[0].bladePosition - s.LastFrame.paddles[0].bladePosition).magnitude);
                forceError = Mathf.Max(forceError, (reference[i].rawWaterForceN[0] - s.LastFrame.rawWaterForceN[0]).magnitude);
                boatError = Mathf.Max(boatError, (reference[i].boatPosition - s.LastFrame.boatPosition).magnitude);
                identity &= s.LastFrame.feedbackRequests[0].inputSequence == s.LastFrame.paddles[0].sequence
                    && s.LastFrame.paddles[0].source == PaddleInputKind.Replay
                    && s.LastFrame.paddles[0].originalSource == PaddleInputKind.Synthetic;
            }
            for (int i = 0; i < 15; i++) Step(s);
            bool expired = !s.drivers[0].Sample.valid && s.hydrodynamics[0].WaterForceN == Vector3.zero
                && !s.hydrodynamics[0].FeedbackRequest.valid;
            Debug.Log($"[CHAIN] replay inputError={inputError:E2}m forceError={forceError:E2}N boatError={boatError:E2}m expired={expired}");
            return identity && inputError < 1e-5 && forceError < 1e-3 && boatError < 0.005 && expired;
        }
        static bool TestHeadTransform()
        {
            Matrix4x4 stage = Matrix4x4.TRS(new Vector3(3, 2, 4), Quaternion.Euler(20, 30, 15), Vector3.one);
            Matrix4x4 localHead = Matrix4x4.TRS(new Vector3(0, 1.2f, 0.2f), Quaternion.Euler(5, 40, 0), Vector3.one);
            Matrix4x4 boat = Matrix4x4.TRS(new Vector3(5, 0.1f, 8), Quaternion.Euler(-10, 25, -5), Vector3.one);
            var actual = HeadPoseAttachment.Compose(boat, stage, stage * localHead);
            var expected = boat * localHead;
            float error = 0;
            for (int i = 0; i < 16; i++) error = Mathf.Max(error, Mathf.Abs(actual[i] - expected[i]));
            return error < 1e-5;
        }

        static bool TestExternalGate()
        {
            NewScene();
            var driver = new GameObject("external gate").AddComponent<PaddleDriver>();
            driver.SetMode(PaddleDriver.Mode.External);
            var sample = new PaddleSample { sequence = 50, timeSeconds = Dt, valid = true,
                bladeRotation = Quaternion.identity, source = PaddleInputKind.Replay };
            bool first = driver.SetExternalSample(sample);
            driver.Tick(Dt);
            bool source = driver.Sample.source == PaddleInputKind.External && driver.Sample.originalSource == PaddleInputKind.External;
            bool duplicate = !driver.SetExternalSample(sample);
            sample.sequence = 49; bool old = !driver.SetExternalSample(sample);
            sample.sequence = 51; sample.timeSeconds = 0; bool oldTime = !driver.SetExternalSample(sample);
            driver.SetMode(PaddleDriver.Mode.VirtualPaddler);
            driver.SetMode(PaddleDriver.Mode.External);
            driver.Tick(Dt);
            bool clearedOnSwitch = !driver.Sample.valid;
            sample.sequence = 52; sample.timeSeconds = 3 * (double)Dt;
            bool newSample = driver.SetExternalSample(sample);
            driver.Tick(Dt);
            bool recovered = driver.Sample.valid && driver.Sample.sequence == 52;
            driver.SetMode(PaddleDriver.Mode.Replay);
            sample.timeSeconds = 4 * (double)Dt;
            bool replayInjection = !driver.SetExternalSample(sample);
            driver.SetMode(PaddleDriver.Mode.External);
            driver.Tick(Dt);
            bool replayCleared = !driver.Sample.valid;
            return first && source && duplicate && old && oldTime && replayInjection
                && clearedOnSwitch && newSample && recovered && replayCleared;
        }

        sealed class ReplayFixtureBridge : HardwareUdpBridge
        {
            public long nowTicks;
            protected override long ObservationClockTicks() => nowTicks;
        }

        static bool TestUdpObservationReplay()
        {
            var s = CreateSession();
            var bridge = new GameObject("50Hz deterministic UDP bridge").AddComponent<ReplayFixtureBridge>();
            bridge.externallyStepped = true;
            s.bridge = bridge;
            var driver = s.drivers[0]; driver.bridge = bridge;
            driver.SetMode(PaddleDriver.Mode.UdpInterpolated);
            string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/udp-observations-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            var reference = new System.Collections.Generic.List<SimulationFrame>();
            s.BeginRecording(path);
            int repeated = 0, changedRepeated = 0, invalid = 0;
            long clockOrigin = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < 200; i++)
            {
                // This is a deterministic observation/replay fixture, not a socket
                // scheduling test. Packet age advances by the same 10 ms as physics;
                // file I/O or Editor scheduling cannot invent another dropped frame.
                bridge.nowTicks = clockOrigin + (long)(i * (double)Dt * System.Diagnostics.Stopwatch.Frequency);
                // Accept a device packet every other 100Hz physics step. The production UDP
                // interpolator still produces a distinct observation on each intervening step.
                if (i % 2 == 0)
                {
                    uint sequence = 1000u + (uint)(i / 2);
                    var packet = new HardwareUdpBridge.PaddleState { seq = sequence, timestampUs = sequence * 20000UL,
                        position = new Vector3(1.55f, 0.9f, 0.2f + 0.45f * Mathf.Cos(i * Dt * 4f)),
                        eulerDeg = Vector3.zero, gripForce = 23.5f };
                    if (!bridge.AcceptPaddle(packet, bridge.nowTicks)) return false;
                }
                if (i == 160) driver.bridge = null;
                if (i == 164) driver.bridge = bridge;
                Step(s);
                var f = JsonUtility.FromJson<SimulationFrame>(JsonUtility.ToJson(s.LastFrame));
                if (!f.paddles[0].valid) invalid++;
                if (reference.Count > 0 && f.paddles[0].valid)
                {
                    PaddleSample previous = reference[reference.Count - 1].paddles[0], current = f.paddles[0];
                    if (current.sequence == previous.sequence)
                    {
                        repeated++;
                        if ((current.bladePosition - previous.bladePosition).magnitude > 1e-6f
                            || (current.bladeVelocity - previous.bladeVelocity).magnitude > 1e-6f) changedRepeated++;
                    }
                }
                reference.Add(f);
            }
            s.StopRecording(); s.BeginReplay(path);
            float positionError = 0, velocityError = 0, forceError = 0, boatError = 0;
            bool identity = true;
            for (int i = 0; i < reference.Count; i++)
            {
                Step(s);
                PaddleSample expected = reference[i].paddles[0], actual = s.LastFrame.paddles[0];
                identity &= actual.source == PaddleInputKind.Replay && actual.originalSource == PaddleInputKind.Udp
                    && actual.sequence == expected.sequence && actual.deviceTimeUs == expected.deviceTimeUs
                    && actual.valid == expected.valid && Math.Abs(actual.timeSeconds - expected.timeSeconds) < 1e-6
                    && Mathf.Abs(actual.gripForceN - expected.gripForceN) < 1e-6
                    && s.LastFrame.feedbackRequests[0].commandSequence == reference[i].feedbackRequests[0].commandSequence;
                positionError = Mathf.Max(positionError, (actual.bladePosition - expected.bladePosition).magnitude);
                velocityError = Mathf.Max(velocityError, (actual.bladeVelocity - expected.bladeVelocity).magnitude);
                forceError = Mathf.Max(forceError, (reference[i].rawWaterForceN[0] - s.LastFrame.rawWaterForceN[0]).magnitude);
                boatError = Mathf.Max(boatError, (reference[i].boatPosition - s.LastFrame.boatPosition).magnitude);
            }
            Debug.Log($"[CHAIN] UDP replay repeated={repeated} changedRepeated={changedRepeated} invalid={invalid} identity={identity} " +
                $"positionError={positionError:E2}m velocityError={velocityError:E2}m/s forceError={forceError:E2}N boatError={boatError:E2}m");
            return repeated >= 80 && changedRepeated >= 80 && invalid == 4 && identity
                && positionError < 1e-5f && velocityError < 1e-5f && forceError < 0.001f && boatError < 0.005f;
        }

        static bool TestRecordingIntegrity()
        {
            var s = CreateSession(waves: true);
            string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/integrity-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            s.BeginRecording(path);
            for (int i = 0; i < 5; i++) Step(s);
            s.StopRecording();
            bool Rejected(string file)
            { try { s.BeginReplay(file); return false; } catch (InvalidDataException) { return true; } }
            Vector3 originalWavePosition = s.wave.transform.position, originalGravity = Physics.gravity;
            bool waterHeight, gravity;
            try
            {
                s.wave.transform.position += Vector3.up * 0.1f;
                waterHeight = Rejected(path);
                s.wave.transform.position = originalWavePosition;
                Physics.gravity = originalGravity * 0.9f;
                gravity = Rejected(path);
            }
            finally { s.wave.transform.position = originalWavePosition; Physics.gravity = originalGravity; }
            var lines = new System.Collections.Generic.List<string>(File.ReadAllLines(path));
            lines.RemoveAt(2);
            string gapPath = path + ".gap.jsonl"; File.WriteAllLines(gapPath, lines);
            bool gap = Rejected(gapPath);
            string changedPath = path + ".changed.jsonl";
            s.BeginRecording(changedPath); Step(s);
            float intensity = s.wave.intensity;
            s.wave.intensity *= 0.5f; Step(s);
            bool stopped = !s.IsRecording && !string.IsNullOrEmpty(s.RecordingError);
            s.wave.intensity = intensity;
            bool invalidated = Rejected(changedPath);
            Debug.Log($"[CHAIN] recording guards height={waterHeight} gravity={gravity} missingFrame={gap} mutationStopped={stopped} invalidatedRejected={invalidated}");
            return waterHeight && gravity && gap && stopped && invalidated;
        }

        static bool TestAcquisitionRecording()
        {
            var s = CreateSession();
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            var packet = new AcquisitionPacket { format = AcquisitionPacket.Format, sourceId = "unassigned-pcb",
                sourceKind = AcquisitionSourceKind.Fixture, sequenceValid = true, sequence = 1,
                channels = new AcquisitionChannelInput[4] };
            for (int i = 0; i < 4; i++) packet.channels[i] = new AcquisitionChannelInput
            {
                channelId = "CH" + (i + 1), sampleSequenceValid = true, sampleSequence = 1,
                valid = true, hasRaw = true, raw = 100 + 17 * i, rawUnits = "raw"
            };
            if (!s.acquisition.Accept(packet, ticks)) return false;
            string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/acquisition-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            s.BeginRecording(path);
            int observedOriginal = 0;
            Action<SimulationFrame> mutate = frame =>
            {
                frame.paddles[0].gripForceN = 123456;
                frame.acquisitionSnapshot.channels[0].raw = -123456;
            };
            Action<SimulationFrame> observe = frame =>
            {
                if (frame.paddles[0].gripForceN != 123456 && frame.acquisitionSnapshot.channels[0].raw == 100)
                    observedOriginal++;
            };
            s.FrameCompleted += mutate;
            s.FrameCompleted += observe;
            var expected = new System.Collections.Generic.List<SimulationFrame>();
            for (int i = 0; i < 5; i++) { Step(s); expected.Add(s.LastFrame); }
            s.FrameCompleted -= mutate;
            s.FrameCompleted -= observe;
            var externalCopy = s.LastFrame; externalCopy.acquisitionSnapshot.channels[0].raw = -123456;
            bool snapshotIsolation = observedOriginal == 5 && s.LastFrame.acquisitionSnapshot.channels[0].raw == 100;
            s.StopRecording(); s.BeginReplay(path);
            for (int i = 0; i < 5; i++)
            {
                Step(s);
                if (!s.LastFrame.hasAcquisitionSnapshot || JsonUtility.ToJson(s.LastFrame.acquisitionSnapshot)
                    != JsonUtility.ToJson(AcquisitionSnapshot.ForReplay(expected[i].acquisitionSnapshot))) return false;
            }
            Step(s);
            bool eof = !s.LastFrame.acquisitionSnapshot.valid && s.LastFrame.acquisitionSnapshot.rawValidCount == 0
                && s.LastFrame.acquisitionSnapshot.status == "REPLAY_ACQUISITION_EXPIRED";
            s.StopReplay();
            bool cleared = s.acquisition.Snapshot().rawValidCount == 0;
            bool Rejected(string file)
            { try { s.BeginReplay(file); return false; } catch (InvalidDataException) { return true; } }
            var lines = File.ReadAllLines(path);
            var frame = JsonUtility.FromJson<SimulationFrame>(lines[1]);
            frame.acquisitionSnapshot.channels[0].valid = !frame.acquisitionSnapshot.channels[0].rawValid;
            lines[1] = JsonUtility.ToJson(frame);
            string tamperPath = path + ".tampered.jsonl"; File.WriteAllLines(tamperPath, lines);
            bool tamper = Rejected(tamperPath);
            lines = File.ReadAllLines(path); frame = JsonUtility.FromJson<SimulationFrame>(lines[1]);
            frame.hasAcquisitionSnapshot = false; lines[1] = JsonUtility.ToJson(frame);
            string missingPath = path + ".missing.jsonl"; File.WriteAllLines(missingPath, lines);
            bool missing = Rejected(missingPath);
            var originalProfile = s.acquisition.CurrentProfile;
            var changedProfile = s.acquisition.CurrentProfile; changedProfile.channels[0].mapping = "changed-fixture";
            s.acquisition.Configure(changedProfile);
            bool profileMismatch = Rejected(path);
            s.acquisition.Configure(originalProfile);
            var registration = new GameObject("recording registration fixture").AddComponent<SpatialCalibrator>();
            registration.allowUnmeasuredDesktopPreview = true;
            var points = new RegistrationPointSet { provenance = "SOFTWARE_FIXTURE_ONLY", points = new[] {
                new RegistrationPointPair { id = "A", platformMeters = Vector3.zero, stageMeters = Vector3.zero },
                new RegistrationPointPair { id = "B", platformMeters = Vector3.right, stageMeters = Vector3.right },
                new RegistrationPointPair { id = "C", platformMeters = Vector3.forward, stageMeters = Vector3.forward } } };
            bool registrationFit = registration.SetCorrespondences(points);
            s.drivers[0].calibrator = registration; s.drivers[0].applyCalibration = true;
            s.BeginRecording(path + ".registration.jsonl"); Step(s);
            points.points[0].stageMeters += Vector3.up * .001f;
            points.points[1].stageMeters += Vector3.up * .001f;
            points.points[2].stageMeters += Vector3.up * .001f;
            registrationFit &= registration.SetCorrespondences(points); Step(s);
            bool registrationMutation = registrationFit && !s.IsRecording && !string.IsNullOrEmpty(s.RecordingError);
            s.BeginRecording(path + ".mutation.jsonl"); Step(s);
            s.acquisition.Configure(changedProfile); Step(s);
            bool mutation = !s.IsRecording && !string.IsNullOrEmpty(s.RecordingError);
            s.acquisition.Configure(originalProfile);
            Debug.Log($"[CHAIN] acquisition EOF={eof} cleared={cleared} tamper={tamper} missing={missing} profileMismatch={profileMismatch} mutationStopped={mutation}");
            return snapshotIsolation && eof && cleared && tamper && missing && profileMismatch && mutation && registrationMutation;
        }

        static bool TestRawAcquisitionProfile()
        {
            var s = CreateSession();
            var hud = new GameObject("Raw profile controls HUD").AddComponent<RideHud>();
            hud.session = s; hud.driver = s.drivers[0];
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/raw-profile-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            int FreePort()
            {
                using (var socket = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
                    return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint).Port;
            }
            var profile = AcquisitionProfile.Default();
            profile.sourceId = "explicit-csv-source"; profile.version = "pc-csv-raw-only-v1";
            profile.enableLoopbackUdp = true; profile.loopbackPort = FreePort();
            foreach (var channel in profile.channels)
            { channel.rawUnits = "original-count"; channel.calibration.points = new AcquisitionCalibrationPoint[0]; }
            string path = Path.Combine(directory, "raw-profile.json");
            File.WriteAllText(path, JsonUtility.ToJson(profile));
            hud.rawAcquisitionProfilePath = path;
            bool loaded = hud.LoadRawAcquisitionProfile() && s.acquisition.LocalPort == profile.loopbackPort
                && s.acquisition.CurrentProfile.sourceId == profile.sourceId && hud.detailsExpanded;
            string signature = s.acquisition.ConfigurationSignature;
            var packet = new AcquisitionPacket { format = AcquisitionPacket.Format, sourceId = profile.sourceId,
                sourceKind = AcquisitionSourceKind.CsvImport, sequenceValid = true, sequence = 7,
                channels = new[] { new AcquisitionChannelInput { channelId = "CH1", rawUnits = "original-count",
                    sampleSequenceValid = true, sampleSequence = 7, hasRaw = true, raw = 1234, valid = true } } };
            bool accepted = s.acquisition.Accept(packet, System.Diagnostics.Stopwatch.GetTimestamp());
            long receiveTicks = s.acquisition.Snapshot().channels[0].receivedMonotonicTicks;
            bool unchanged() => s.acquisition.ConfigurationSignature == signature
                && s.acquisition.LocalPort == profile.loopbackPort
                && s.acquisition.Snapshot().channels[0].receivedMonotonicTicks == receiveTicks;
            bool rejected = true;
            foreach (string json in new[] { "{}", JsonUtility.ToJson(profile).Replace("\"maximumAgeSeconds\":0.1", "\"maximumAgeSeconds\":0.2"),
                JsonUtility.ToJson(profile).Replace("\"points\":[]", "\"points\":null") })
            { File.WriteAllText(path, json); rejected &= !hud.LoadRawAcquisitionProfile() && unchanged(); }
            profile.channels[0].calibration.mode = AcquisitionCalibrationMode.ZeroScale;
            File.WriteAllText(path, JsonUtility.ToJson(profile));
            rejected &= !hud.LoadRawAcquisitionProfile() && unchanged();
            profile.channels[0].calibration.mode = AcquisitionCalibrationMode.None;
            using (var occupied = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            {
                int originalPort = profile.loopbackPort;
                profile.loopbackPort = ((System.Net.IPEndPoint)occupied.Client.LocalEndPoint).Port;
                File.WriteAllText(path, JsonUtility.ToJson(profile));
                rejected &= !hud.LoadRawAcquisitionProfile();
                profile.loopbackPort = originalPort;
                rejected &= unchanged();
            }
            File.WriteAllText(path, JsonUtility.ToJson(profile));
            bool reload = hud.LoadRawAcquisitionProfile() && s.acquisition.LocalPort == profile.loopbackPort
                && !s.acquisition.Snapshot().channels[0].present;
            string recording = Path.Combine(directory, "recording.jsonl");
            s.BeginRecording(recording); Step(s);
            bool recordingRefused = !hud.LoadRawAcquisitionProfile() && s.IsRecording
                && s.acquisition.ConfigurationSignature == signature;
            // The asynchronous writer may complete between calls. Compare only after
            // StopRecording has drained and closed the sink.
            s.StopRecording();
            recordingRefused &= string.IsNullOrEmpty(s.RecordingError)
                && File.ReadAllLines(recording).Length == 3 && s.LastRecordingWrittenFrames == 1;
            s.BeginReplay(recording);
            bool replayRefused = !hud.LoadRawAcquisitionProfile() && s.IsReplaying
                && s.acquisition.ConfigurationSignature == signature;
            s.StopReplay(); s.acquisition.StopLoopback();
            Debug.Log($"[CHAIN] raw profile loaded={loaded} accepted={accepted} rejectedAtomic={rejected} samePortReload={reload} recordingRefused={recordingRefused} replayRefused={replayRefused}");
            return loaded && accepted && rejected && reload && recordingRefused && replayRefused;
        }

        static bool TestRecordingPreservation()
        {
            var s = CreateSession();
            s.recordingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/preservation-" + Guid.NewGuid().ToString("N")));
            bool legacyFallback = s.LatestRecordingPath == null
                && s.ReplayRecordingPath == Path.Combine(s.RecordingDirectoryPath, "latest.jsonl");
            // Exercise the same no-path control used by F5; every invocation must choose a
            // new file instead of truncating a previous segment at latest.jsonl.
            if (!s.ToggleRecording() || !s.IsRecording) return false;
            string firstPath = s.CurrentRecordingPath;
            for (int i = 0; i < 3; i++) Step(s);
            if (!s.ToggleRecording() || s.IsRecording) return false;
            string firstContents = File.ReadAllText(firstPath);
            bool firstCompleted = s.LatestRecordingPath == firstPath && s.CurrentRecordingPath == null;

            if (!s.ToggleRecording() || !s.IsRecording) return false;
            string secondPath = s.CurrentRecordingPath;
            for (int i = 0; i < 5; i++) Step(s);
            if (!s.ToggleRecording() || s.IsRecording) return false;
            string secondContents = File.ReadAllText(secondPath);
            var secondFirstFrame = JsonUtility.FromJson<SimulationFrame>(File.ReadAllLines(secondPath)[1]);
            bool independent = firstPath != secondPath && s.LatestRecordingPath == secondPath
                && File.ReadAllText(firstPath) == firstContents;

            bool existingRejected = false;
            try { s.BeginRecording(firstPath); }
            catch (IOException) { existingRejected = true; }
            existingRejected &= !s.IsRecording && s.CurrentRecordingPath == null
                && s.LatestRecordingPath == secondPath && File.ReadAllText(firstPath) == firstContents;

            string failureDirectory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/start-failure-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(failureDirectory);
            string blocker = Path.Combine(failureDirectory, "existing-file");
            File.WriteAllText(blocker, "recording-path-test-sentinel");
            string failedPath = Path.Combine(blocker, "recording.jsonl");
            bool failedStart = !s.ToggleRecording(failedPath) && !s.IsRecording
                && s.CurrentRecordingPath == null && s.LatestRecordingPath == secondPath
                && !File.Exists(failedPath) && File.ReadAllText(blocker) == "recording-path-test-sentinel";
            // No failed-start handle may prevent the pre-existing object being opened exclusively.
            using (var exclusive = File.Open(blocker, FileMode.Open, FileAccess.Read, FileShare.None))
                failedStart &= exclusive.Length > 0;

            if (!s.ToggleRecording()) return false;
            string emptyPath = s.CurrentRecordingPath;
            if (!s.ToggleRecording()) return false;
            bool emptyPreserved = emptyPath != firstPath && emptyPath != secondPath
                && s.LatestRecordingPath == secondPath && File.ReadAllLines(emptyPath).Length == 1;

            bool latestReplay = s.ReplayLatestRecording() && s.IsReplaying && !s.IsRecording;
            if (latestReplay)
            {
                Step(s);
                latestReplay = s.LastFrame.paddles[0].source == PaddleInputKind.Replay
                    && s.LastFrame.paddles[0].sequence == secondFirstFrame.paddles[0].sequence
                    && (s.LastFrame.paddles[0].bladePosition - secondFirstFrame.paddles[0].bladePosition).magnitude < 1e-6f
                    && s.LatestRecordingPath == secondPath;
            }
            s.StopReplay();

            if (!s.ToggleRecording()) return false;
            string activePath = s.CurrentRecordingPath;
            Step(s); Step(s);
            bool activeReplay = s.ReplayLatestRecording() && s.IsReplaying && !s.IsRecording
                && s.CurrentRecordingPath == null && s.LatestRecordingPath == activePath;
            s.StopReplay();

            if (!s.ToggleRecording()) return false;
            Step(s);
            float area = s.hydrodynamics[0].bladeArea;
            s.hydrodynamics[0].bladeArea = area * 2;
            Step(s);
            s.hydrodynamics[0].bladeArea = area;
            bool invalidatedPreserved = !s.IsRecording && !string.IsNullOrEmpty(s.RecordingError)
                && s.LatestRecordingPath == activePath && s.CurrentRecordingPath == null;
            bool filesUnchanged = File.ReadAllText(firstPath) == firstContents
                && File.ReadAllText(secondPath) == secondContents;
            Debug.Log($"[CHAIN] recording preservation legacy={legacyFallback} first={firstCompleted} unique={independent} existingRejected={existingRejected} failedStart={failedStart} empty={emptyPreserved} latestReplay={latestReplay} activeReplay={activeReplay} invalidated={invalidatedPreserved} unchanged={filesUnchanged}; first={firstPath}; second={secondPath}");
            return legacyFallback && firstCompleted && independent && existingRejected && failedStart
                && emptyPreserved && latestReplay && activeReplay && invalidatedPreserved && filesUnchanged;
        }

        sealed class FailingRecordingWriter : StreamWriter
        {
            readonly bool _failFlush, _failDispose;
            public FailingRecordingWriter(bool flush, bool dispose) : base(new MemoryStream())
            { _failFlush = flush; _failDispose = dispose; }
            public override void Flush()
            {
                if (_failFlush) throw new IOException("Injected recording flush failure.");
                base.Flush();
            }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && _failDispose) throw new IOException("Injected recording close failure.");
            }
        }
        static void InjectRecordingFailure(SimulationSession session, bool flush, bool dispose)
        {
            // Exercise I/O failure handling without exhausting the user's disk or changing ACLs.
            // The real header/file is closed first; the replacement models only the failed sink.
            var field = typeof(SimulationSession).GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic);
            ((StreamWriter)field.GetValue(session)).Dispose();
            field.SetValue(session, new FailingRecordingWriter(flush, dispose));
        }
        static bool TestRecordingPersistence()
        {
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/persistence-" + Guid.NewGuid().ToString("N")));
            SimulationSession Fresh(string path)
            {
                var session = CreateSession(); session.recordingDirectory = path; return session;
            }
            var s = Fresh(directory);
            if (!s.ToggleRecording()) return false;
            Step(s); Step(s);
            if (!s.ToggleRecording()) return false;
            string previousPath = s.LatestRecordingPath;
            string previousContents = File.ReadAllText(previousPath);
            if (!s.ToggleRecording()) return false;
            Step(s); Step(s); Step(s);
            if (!s.ToggleRecording()) return false;
            string recordedPath = s.LatestRecordingPath;
            string recordedContents = File.ReadAllText(recordedPath);
            string indexPath = s.RecordingIndexPath;
            string indexContents = File.ReadAllText(indexPath);
            bool indexAdvanced = recordedPath != previousPath && string.IsNullOrEmpty(s.RecordingIndexError)
                && File.ReadAllText(previousPath) == previousContents;

            s = Fresh(directory);
            bool restarted = s.LatestRecordingPath == null && s.ReplayRecordingPath == recordedPath
                && s.ReplayLatestRecording() && s.IsReplaying;
            if (restarted) { Step(s); restarted &= s.LastFrame.paddles[0].source == PaddleInputKind.Replay; }
            s.StopReplay();

            string explicitPath = Path.Combine(directory, "explicit-programmatic.jsonl");
            s.BeginRecording(explicitPath); Step(s); s.StopRecording();
            bool explicitIsolated = s.LatestRecordingPath == explicitPath
                && File.ReadAllText(indexPath) == indexContents;
            s = Fresh(directory);
            explicitIsolated &= s.ReplayRecordingPath == recordedPath;

            if (!s.ToggleRecording() || !s.ToggleRecording()) return false;
            bool emptyPreserved = File.ReadAllText(indexPath) == indexContents;
            bool existingRejected = false;
            try { s.BeginRecording(recordedPath); }
            catch (IOException) { existingRejected = true; }
            existingRejected &= !s.IsRecording && File.ReadAllText(indexPath) == indexContents;
            if (!s.ToggleRecording()) return false;
            Step(s);
            float area = s.hydrodynamics[0].bladeArea;
            s.hydrodynamics[0].bladeArea = area * 2;
            Step(s);
            s.hydrodynamics[0].bladeArea = area;
            bool invalidatedPreserved = !s.IsRecording && File.ReadAllText(indexPath) == indexContents;

            if (!s.ToggleRecording()) return false;
            InjectRecordingFailure(s, flush: true, dispose: false);
            Step(s);
            bool flushFailure = !s.IsRecording && s.CurrentRecordingPath == null
                && !string.IsNullOrEmpty(s.RecordingError) && s.LatestRecordingPath == null
                && File.ReadAllText(indexPath) == indexContents;

            if (!s.ToggleRecording()) return false;
            Step(s);
            InjectRecordingFailure(s, flush: false, dispose: true);
            bool closeFailure = !s.ToggleRecording() && !s.IsRecording && s.CurrentRecordingPath == null
                && !string.IsNullOrEmpty(s.RecordingError) && s.LatestRecordingPath == null
                && File.ReadAllText(indexPath) == indexContents;

            if (!s.ToggleRecording()) return false;
            Step(s); Step(s);
            string unindexedPath = s.CurrentRecordingPath;
            bool indexFailure;
            // Deny delete-sharing on the existing index to make atomic replacement fail on the
            // supported Windows editor, while leaving the complete recording itself writable.
            using (var lockedIndex = File.Open(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                indexFailure = s.ToggleRecording() && !s.IsRecording && s.LatestRecordingPath == unindexedPath
                    && string.IsNullOrEmpty(s.RecordingError) && !string.IsNullOrEmpty(s.RecordingIndexError)
                    && File.ReadAllText(indexPath) == indexContents;
            }
            bool unindexedReadable = s.ReplayLatestRecording() && s.IsReplaying;
            s.StopReplay();
            s = Fresh(directory);
            indexFailure &= s.ReplayRecordingPath == recordedPath && File.ReadAllText(indexPath) == indexContents;

            string legacyDirectory = Path.Combine(directory, "legacy-only");
            Directory.CreateDirectory(legacyDirectory);
            string legacyPath = Path.Combine(legacyDirectory, "latest.jsonl");
            File.WriteAllText(legacyPath, recordedContents);
            s = Fresh(legacyDirectory);
            bool legacyReadable = !File.Exists(s.RecordingIndexPath) && s.ReplayRecordingPath == legacyPath
                && s.ReplayLatestRecording() && s.IsReplaying;
            s.StopReplay();
            bool originalUnchanged = File.ReadAllText(recordedPath) == recordedContents
                && File.ReadAllText(indexPath) == indexContents;
            Debug.Log($"[CHAIN] recording persistence indexAdvanced={indexAdvanced} restart={restarted} explicitIsolated={explicitIsolated} empty={emptyPreserved} existingRejected={existingRejected} invalidated={invalidatedPreserved} flushFailure={flushFailure} closeFailure={closeFailure} indexFailure={indexFailure} unindexedReadable={unindexedReadable} legacyReadable={legacyReadable} unchanged={originalUnchanged}; directory={directory}");
            return indexAdvanced && restarted && explicitIsolated && emptyPreserved && existingRejected && invalidatedPreserved
                && flushFailure && closeFailure && indexFailure && unindexedReadable && legacyReadable && originalUnchanged;
        }

        static bool TestBackgroundRecordingPreservation()
        {
            var s = CreateSession();
            s.recordingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/background-" + Guid.NewGuid().ToString("N")));
            if (!s.ToggleRecording()) return false;
            Step(s); Step(s);
            if (!s.ToggleRecording()) return false;
            string completePath = s.LatestRecordingPath;
            string completeContents = File.ReadAllText(completePath);
            string indexContents = File.ReadAllText(s.RecordingIndexPath);
            bool preserved = s.LastRecordingWrittenFrames == 2;
            var field = typeof(SimulationSession).GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (int mode in new[] { 0, 1, 2 })
            {
                if (!s.ToggleRecording()) return false;
                ((StreamWriter)field.GetValue(s)).Dispose();
                string headerJson = File.ReadAllLines(s.CurrentRecordingPath)[0];
                var sink = new FailingRecordingWriter(flush: mode == 0, dispose: mode == 1);
                field.SetValue(s, new BackgroundRecordingWriter(sink, mode == 2 ? 1 : 4096 * 1024, headerJson));
                Step(s);
                if (s.IsRecording) s.StopRecording();
                preserved &= !s.IsRecording && s.CurrentRecordingPath == null && !string.IsNullOrEmpty(s.RecordingError)
                    && s.LatestRecordingPath == completePath && File.ReadAllText(completePath) == completeContents
                    && File.ReadAllText(s.RecordingIndexPath) == indexContents;
            }
            bool replay = s.ReplayLatestRecording() && s.IsReplaying;
            s.StopReplay();
            Debug.Log($"[CHAIN] background recording asynchronous flush/close/overflow preserve={preserved}; previousReplay={replay}");
            return preserved && replay;
        }

        static bool TestRawProfileRestartReplay()
        {
            var s = CreateSession();
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/raw-restart-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            int FreePort()
            {
                using (var socket = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
                    return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint).Port;
            }
            var profile = AcquisitionProfile.Default();
            profile.sourceId = "software-raw-restart"; profile.version = "software-raw-restart-v1";
            profile.enableLoopbackUdp = true; profile.loopbackPort = FreePort();
            foreach (var c in profile.channels)
            { c.rawUnits = "example-count"; c.calibration.points = new AcquisitionCalibrationPoint[0]; }
            string profilePath = Path.Combine(directory, "raw.json");
            File.WriteAllText(profilePath, JsonUtility.ToJson(profile));
            if (!s.LoadRawAcquisitionProfile(profilePath)) return false;
            s.recordingDirectory = directory;
            var packet = new AcquisitionPacket { format = AcquisitionPacket.Format, sourceId = profile.sourceId,
                sourceKind = AcquisitionSourceKind.Fixture, sequenceValid = true, sequence = 1,
                channels = new AcquisitionChannelInput[4] };
            for (int i = 0; i < 4; i++) packet.channels[i] = new AcquisitionChannelInput
                { channelId = "CH" + (i + 1), sampleSequenceValid = true, sampleSequence = 1,
                    valid = true, hasRaw = true, raw = 100 + i, rawUnits = "example-count" };
            if (!s.acquisition.Accept(packet, System.Diagnostics.Stopwatch.GetTimestamp()) || !s.ToggleRecording()) return false;
            var expected = new System.Collections.Generic.List<AcquisitionSnapshot>();
            for (int i = 0; i < 5; i++) { Step(s); expected.Add(s.LastFrame.acquisitionSnapshot); }
            if (!s.ToggleRecording()) return false;
            string recording = s.LatestRecordingPath;
            string[] lines = File.ReadAllLines(recording);
            string index = File.ReadAllText(s.RecordingIndexPath);
            s.acquisition.StopLoopback();

            s = CreateSession(); s.recordingDirectory = directory;
            var live = s.acquisition.CurrentProfile;
            live.enableLoopbackUdp = true; live.loopbackPort = FreePort();
            s.acquisition.ConfigureLoopback(live);
            string liveSignature = s.acquisition.ConfigurationSignature;
            int livePort = s.acquisition.LocalPort;
            bool restarted, observations = true, preserved, eof, restored, repeated, rejectedAtomic;
            using (var occupied = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, profile.loopbackPort)))
            {
                restarted = s.ReplayRecordingPath == recording && s.ReplayLatestRecording() && s.IsReplaying
                    && s.ReplayIntegrityStatus == "COMPLETE_CONTENT_VERIFIED" && s.acquisition.LocalPort == 0
                    && s.acquisition.ConfigurationSignature == liveSignature;
                if (!restarted) return false;
                for (int i = 0; i < 5; i++)
                {
                    Step(s); observations &= JsonUtility.ToJson(s.LastFrame.acquisitionSnapshot)
                        == JsonUtility.ToJson(AcquisitionSnapshot.ForReplay(expected[i]));
                }
                Step(s); eof = s.IsReplaying && s.LastFrame.acquisitionSnapshot.status == "REPLAY_ACQUISITION_EXPIRED";
                repeated = s.ReplayLatestRecording() && s.acquisition.LocalPort == 0;
                var rb = s.boat.GetComponent<Rigidbody>(); double time = s.SimulationTime; Vector3 position = rb.position;
                string incomplete = recording + ".incomplete";
                File.WriteAllLines(incomplete, new[] { lines[0], lines[1] });
                bool refused = false;
                try { s.BeginReplay(incomplete); } catch (InvalidDataException) { refused = true; }
                rejectedAtomic = refused && s.IsReplaying && s.SimulationTime == time && rb.position == position
                    && s.acquisition.ConfigurationSignature == liveSignature && s.acquisition.LocalPort == 0;
                s.StopReplay();
                restored = s.acquisition.LocalPort == livePort && s.acquisition.ConfigurationSignature == liveSignature
                    && !s.acquisition.Snapshot().channels[0].present;
            }
            var livePacket = JsonUtility.FromJson<AcquisitionPacket>(JsonUtility.ToJson(packet));
            livePacket.sourceId = live.sourceId;
            foreach (var channel in livePacket.channels) channel.rawUnits = "raw";
            bool acceptsLive = s.acquisition.Accept(livePacket, System.Diagnostics.Stopwatch.GetTimestamp());
            s.StopReplay();
            acceptsLive &= s.acquisition.LocalPort == livePort && s.acquisition.Snapshot().channels[0].present;
            s.BeginReplay(recording);
            bool restoreFailureClosed;
            using (var occupiedLivePort = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, livePort)))
            {
                s.StopReplay();
                restoreFailureClosed = !s.IsReplaying && s.acquisition.LocalPort == 0
                    && s.acquisition.ConfigurationSignature == liveSignature
                    && s.RawAcquisitionProfileStatus.Contains("实时采集监听未恢复");
            }
            s.acquisition.StopLoopback();
            s.BeginReplay(recording); s.StopReplay();
            bool stoppedRemainsStopped = s.acquisition.LocalPort == 0;

            s.BeginReplay(recording); s.acquisition.StartLoopback(); Step(s);
            bool unexpectedListenerRefused = !s.IsReplaying && !string.IsNullOrEmpty(s.ReplayError)
                && !s.LastFrame.paddles[0].valid && s.acquisition.LocalPort == 0;

            var legacy = new System.Collections.Generic.List<string>();
            for (int i = 0; i < lines.Length - 1; i++)
            { var f = JsonUtility.FromJson<SimulationFrame>(lines[i]); f.schemaVersion = 4; legacy.Add(JsonUtility.ToJson(f)); }
            string legacyPath = recording + ".legacy-different-profile"; File.WriteAllLines(legacyPath, legacy);
            bool legacyRefused = false;
            try { s.BeginReplay(legacyPath); } catch (InvalidDataException) { legacyRefused = true; }

            float area = s.hydrodynamics[0].bladeArea;
            s.hydrodynamics[0].bladeArea *= 2;
            bool physicalRefused = false;
            try { s.BeginReplay(recording); } catch (InvalidDataException) { physicalRefused = true; }
            s.hydrodynamics[0].bladeArea = area;
            string[] changed = (string[])lines.Clone();
            var header = JsonUtility.FromJson<SimulationFrame>(changed[0]);
            header.acquisitionProfile.sourceId += "-tampered";
            header.acquisitionSnapshot.sourceId = header.acquisitionProfile.sourceId;
            foreach (var c in header.acquisitionSnapshot.channels) c.sourceId = header.acquisitionProfile.sourceId;
            changed[0] = JsonUtility.ToJson(header);
            var completion = JsonUtility.FromJson<RecordingCompletion>(changed[changed.Length - 1]);
            completion.lastFrameTimeSeconds = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(lines[lines.Length - 1], true);
            using (var hash = new RecordingContentHash())
            { for (int i = 0; i < changed.Length - 1; i++) hash.Append(changed[i]); completion.contentSha256 = hash.FinishHex(); }
            changed[changed.Length - 1] = completion.ToJson();
            string tampered = recording + ".profile-tampered"; File.WriteAllLines(tampered, changed);
            bool profileRefused = false;
            try { s.BeginReplay(tampered); } catch (InvalidDataException) { profileRefused = true; }
            s.BeginReplay(recording);
            live.channels[0].mapping = "changed-live-fixture";
            s.acquisition.Configure(live); Step(s);
            bool mutationRefused = !s.IsReplaying && !string.IsNullOrEmpty(s.ReplayError)
                && !s.LastFrame.paddles[0].valid && !s.LastFrame.acquisitionSnapshot.replayed
                && s.acquisition.CurrentProfile.channels[0].mapping == "changed-live-fixture";
            preserved = File.ReadAllText(s.RecordingIndexPath) == index && s.ReplayRecordingPath == recording;
            Debug.Log($"[CHAIN] raw restart={restarted} recordedObservations={observations} occupiedRecordedPort=True repeated={repeated} rejectedAtomic={rejectedAtomic} EOF={eof} restored={restored} acceptsLive={acceptsLive} stoppedRemainsStopped={stoppedRemainsStopped} restoreFailureClosed={restoreFailureClosed} unexpectedListenerRefused={unexpectedListenerRefused} legacyDifferentProfileRefused={legacyRefused} physicalRefused={physicalRefused} profileRefused={profileRefused} mutationRefused={mutationRefused} indexPreserved={preserved}");
            return restarted && observations && repeated && rejectedAtomic && eof && restored && acceptsLive
                && stoppedRemainsStopped && restoreFailureClosed && unexpectedListenerRefused && legacyRefused
                && physicalRefused && profileRefused && mutationRefused && preserved;
        }

        static bool TestRecordingCompletion()
        {
            var s = CreateSession(waves: true);
            s.recordingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/completion-" + Guid.NewGuid().ToString("N")));
            if (!s.ToggleRecording()) return false;
            for (int i = 0; i < 5; i++) Step(s);
            string path = s.CurrentRecordingPath;
            if (!s.ToggleRecording()) return false;
            string[] original = File.ReadAllLines(path);
            string indexContents = File.ReadAllText(s.RecordingIndexPath);
            bool finished = original.Length == 7 && s.LastRecordingWrittenFrames == 5;
            var footer = JsonUtility.FromJson<RecordingCompletion>(original[original.Length - 1]);
            finished &= footer.schemaVersion == 5 && footer.frameCount == 5;
            s.BeginReplay(path);
            finished &= s.ReplayIntegrityStatus == "COMPLETE_CONTENT_VERIFIED";
            s.StopReplay();
            bool encodingRefused = RecordingEncodingSelfTest.CheckRefusals(s, path);
            Vector3 beforePosition = s.boat.GetComponent<Rigidbody>().position;
            double beforeTime = s.SimulationTime;
            string ChangedFooter(Action<RecordingCompletion> change)
            {
                var copy = JsonUtility.FromJson<RecordingCompletion>(original[original.Length - 1]);
                change(copy); return JsonUtility.ToJson(copy);
            }
            var variants = new System.Collections.Generic.Dictionary<string, string[]>
            {
                ["no-completion"] = new[] { original[0], original[1], original[2], original[3], original[4], original[5] },
                ["tail-and-completion-lost"] = new[] { original[0], original[1], original[2], original[3], original[4] },
                ["whole-tail-frame"] = new[] { original[0], original[1], original[2], original[3], original[4], original[6] },
                ["partial-completion"] = new[] { original[0], original[1], original[2], original[3], original[4], original[5], original[6].Substring(0, 100) },
                ["trailing-frame"] = new[] { string.Join("\n", original), original[5] },
                ["duplicate-completion"] = new[] { string.Join("\n", original), original[6] },
                ["trailing-empty-line"] = new[] { string.Join("\n", original), "" },
                ["middle-empty-line"] = new[] { original[0], "", string.Join("\n", original, 1, original.Length - 1) }
            };
            foreach (var change in new Action<RecordingCompletion>[] {
                c => c.frameCount--, c => c.lastFrameTimeSeconds += .01,
                c => c.configurationSignature += "changed", c => c.contentSha256 = new string('0', 64) })
            {
                string[] lines = (string[])original.Clone(); lines[lines.Length - 1] = ChangedFooter(change);
                variants.Add("changed-footer-" + variants.Count, lines);
            }
            string[] tampered = (string[])original.Clone();
            var alteredFrame = JsonUtility.FromJson<SimulationFrame>(tampered[2]);
            alteredFrame.rawWaterForceN[0] += Vector3.one;
            tampered[2] = JsonUtility.ToJson(alteredFrame);
            variants.Add("finite-content-change", tampered);
            bool rejected = true;
            foreach (var variant in variants)
            {
                string candidate = path + "." + variant.Key;
                File.WriteAllLines(candidate, variant.Value);
                bool refused = false;
                try { s.BeginReplay(candidate); } catch (InvalidDataException) { refused = true; }
                rejected &= refused && !s.IsReplaying && s.LatestRecordingPath == path
                    && File.ReadAllText(s.RecordingIndexPath) == indexContents
                    && s.SimulationTime == beforeTime && s.boat.GetComponent<Rigidbody>().position == beforePosition;
            }
            string legacyPath = path + ".legacy-v4";
            var legacy = new System.Collections.Generic.List<string>();
            for (int i = 0; i < original.Length - 1; i++)
            {
                var frame = JsonUtility.FromJson<SimulationFrame>(original[i]);
                frame.schemaVersion = 4; legacy.Add(JsonUtility.ToJson(frame));
            }
            File.WriteAllLines(legacyPath, legacy);
            s.BeginReplay(legacyPath);
            bool legacyMarked = s.ReplayIntegrityStatus == "LEGACY_COMPLETENESS_UNVERIFIED";
            s.StopReplay();
            Debug.Log($"[CHAIN] completion finished={finished} corruptVariantsRejected={rejected} variants={variants.Count} legacyMarked={legacyMarked}");
            return finished && rejected && legacyMarked && encodingRefused;
        }

        static bool TestReplayControls()
        {
            var s = CreateSession();
            var hud = new GameObject("session controls HUD").AddComponent<RideHud>();
            hud.session = s; hud.driver = s.drivers[0];
            string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/controls-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            if (!s.ToggleRecording(path) || !s.IsRecording) return false;
            for (int i = 0; i < 12; i++) Step(s);
            if (!s.ToggleRecording(path) || s.IsRecording) return false;
            string recorded = File.ReadAllText(path);
            s.BeginReplay(path); Step(s);
            bool programmaticRefusal = false;
            try { s.BeginRecording(path); }
            catch (InvalidOperationException) { programmaticRefusal = true; }
            bool uiRefusal = !s.ToggleRecording(path) && s.IsReplaying && !s.IsRecording
                && File.ReadAllText(path) == recorded;
            hud.CycleInputMode(); Step(s);
            bool middleExit = !s.IsReplaying && !s.LastFrame.acquisitionSnapshot.replayed;
            foreach (var d in s.drivers) middleExit &= d.mode == PaddleDriver.Mode.VirtualPaddler;
            hud.CycleInputMode(); Step(s);
            bool udpUnavailable = s.drivers[0].mode == PaddleDriver.Mode.UdpDirect && !s.LastFrame.paddles[0].valid
                && s.LastFrame.rawWaterForceN[0] == Vector3.zero && !s.LastFrame.feedbackRequests[0].valid;
            hud.CycleInputMode(); Step(s);
            bool synthetic = s.drivers[0].mode == PaddleDriver.Mode.VirtualPaddler && s.LastFrame.paddles[0].valid;
            s.BeginReplay(path);
            for (int i = 0; i < 30; i++) Step(s);
            bool eofHeld = s.IsReplaying && !s.LastFrame.paddles[0].valid;
            hud.CycleInputMode(); Step(s);
            bool eofExit = !s.IsReplaying && !s.LastFrame.acquisitionSnapshot.replayed;
            foreach (var d in s.drivers) eofExit &= d.mode == PaddleDriver.Mode.VirtualPaddler;
            Debug.Log($"[CHAIN] replay controls programmaticRefusal={programmaticRefusal} uiRefusal={uiRefusal} middleExit={middleExit} udpUnavailable={udpUnavailable} synthetic={synthetic} eofHeld={eofHeld} eofExit={eofExit}");
            return programmaticRefusal && uiRefusal && middleExit && udpUnavailable && synthetic && eofHeld && eofExit;
        }

        static bool TestReplayMutation()
        {
            var s = CreateSession(waves: true);
            string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../tools/check_recordings/replay-mutation-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            s.BeginRecording(path);
            for (int i = 0; i < 20; i++) Step(s);
            s.StopRecording();
            bool Rejected(Action mutate, Action restore, bool eof = false, float dt = Dt)
            {
                s.BeginReplay(path);
                for (int i = 0; i < (eof ? 35 : 2); i++) Step(s);
                mutate();
                s.StepInputs(dt); Call(s.boat, "FixedUpdate"); Physics.Simulate(dt); s.StepDevices(dt);
                bool result = !s.IsReplaying && !string.IsNullOrEmpty(s.ReplayError)
                    && !s.LastFrame.paddles[0].valid && !s.LastFrame.feedbackRequests[0].valid
                    && s.LastFrame.rawWaterForceN[0] == Vector3.zero
                    && s.LastFrame.boatPropulsionForceN[0] == Vector3.zero
                    && !s.LastFrame.acquisitionSnapshot.replayed
                    && s.drivers[0].mode == PaddleDriver.Mode.External;
                restore();
                return result;
            }
            float area = s.hydrodynamics[0].bladeArea, ttl = s.drivers[0].inputTimeoutSeconds;
            float intensity = s.wave.intensity;
            bool hydro = Rejected(() => s.hydrodynamics[0].bladeArea *= 2, () => s.hydrodynamics[0].bladeArea = area);
            bool timeout = Rejected(() => s.drivers[0].inputTimeoutSeconds *= 2, () => s.drivers[0].inputTimeoutSeconds = ttl);
            bool waves = Rejected(() => s.wave.intensity *= .5f, () => s.wave.intensity = intensity, eof: true);
            bool mode = Rejected(() => s.drivers[0].SetMode(PaddleDriver.Mode.VirtualPaddler), () => { });
            bool timestep = Rejected(() => { }, () => { }, dt: Dt * 2);
            s.StopReplay(); Step(s);
            bool recovery = s.LastFrame.paddles[0].valid && s.ReplayError == null;
            Debug.Log($"[CHAIN] replay mutation hydro={hydro} timeout={timeout} wavesAfterEof={waves} mode={mode} timestep={timestep} recovery={recovery}");
            return hydro && timeout && waves && mode && timestep && recovery;
        }

        static bool TestNonzeroReplay()
        {
            var s = CreateSession(waves: true);
            for (int i = 0; i < 117; i++) Step(s);
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../tools/check_recordings/nonzero-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            s.BeginRecording(path);
            var reference = new System.Collections.Generic.List<SimulationFrame>();
            for (int i = 0; i < 150; i++) { Step(s); reference.Add(JsonUtility.FromJson<SimulationFrame>(JsonUtility.ToJson(s.LastFrame))); }
            s.StopRecording();
            float original = s.hydrodynamics[0].cdFlat;
            s.hydrodynamics[0].cdFlat *= 2;
            bool rejected = false;
            try { s.BeginReplay(path); } catch (InvalidDataException) { rejected = true; }
            s.hydrodynamics[0].cdFlat = original;
            s.BeginReplay(path);
            float boatError = 0, forceError = 0, feedbackError = 0;
            for (int i = 0; i < reference.Count; i++)
            {
                Step(s);
                boatError = Mathf.Max(boatError, (reference[i].boatPosition - s.LastFrame.boatPosition).magnitude);
                forceError = Mathf.Max(forceError, (reference[i].rawWaterForceN[0] - s.LastFrame.rawWaterForceN[0]).magnitude);
                feedbackError = Mathf.Max(feedbackError, (reference[i].filteredFeedbackForceN[0] - s.LastFrame.filteredFeedbackForceN[0]).magnitude);
            }
            Debug.Log($"[CHAIN] nonzero-origin boatError={boatError:E2}m rawForceError={forceError:E2}N feedbackError={feedbackError:E2}N mismatchRejected={rejected}");
            return rejected && boatError < 0.005f && forceError < 0.001f && feedbackError < 0.001f;
        }
    }
}
