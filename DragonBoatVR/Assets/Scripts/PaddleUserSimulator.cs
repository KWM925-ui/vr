using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Development-only simulated user. Feeds arbitrary 6DoF poses into the
    /// normal input path and observes the normal fixed/post-physics loop. Never auto-installed.</summary>
    [DefaultExecutionOrder(-2000)]
    public class PaddleUserSimulator : MonoBehaviour
    {
        public string artifactsDirectory;
        public bool Completed { get; private set; }
        public bool Passed { get; private set; }
        public string ActionName => Names[Mathf.Clamp(_stage, 0, Names.Length - 1)];
        static readonly string[] Names = { "AirFreeMotion", "TiltedPartialEntry", "IncreasingImmersion",
            "PullBackward", "PushForward", "RotateAtFixedCentre", "CompleteExit", "StillUnderwater", "SensorMounting",
            "SparseUdpRotation", "SparseUdpExpiry", "PaddleWaterEffects", "BoatCoastWake", "ExperienceControls" };
        [Serializable] public class ActionResult
        {
            public string action;
            public int observedFrames;
            public bool passed = true;
            public float maximumPoseErrorM, maximumPoseErrorDeg, maximumAreaErrorM2;
            public float posePositionToleranceM;
            public float maximumForceErrorN, maximumForceN, maximumTorqueNm;
            public float maximumAngularVelocityError, maximumBoatYawTorqueNm;
            public int sentPackets, newPacketFrames, heldPacketFrames, heldMotionFrames, validFrames, invalidFrames;
            public int stoppedValidFrames, stoppedHeldFrames;
            public float minimumAngularVelocityRadS, maximumAngularVelocityRadS, minimumTorqueXNm, maximumTorqueXNm;
            public double minimumSendIntervalMs, meanSendIntervalMs, maximumSendIntervalMs;
            public double minimumReceiveIntervalMs, maximumReceiveIntervalMs;
            public double firstExpiredAcceptedAgeMs = -1, firstExpiredAfterSenderStopMs = -1;
            public long finalAcceptedReceiveTicks;
            public uint finalAcceptedSequence;
            public int entryBursts, exitEvents, splashParticles, dripParticles, wakeRipples, visibleEffectRenderFrames;
            public string failure;
        }
        [Serializable] public class Result
        {
            public string result;
            public string scope = "Normal Unity Play; synthetic user poses, software water/boat/cable models; no real XR or hardware acceptance.";
            public List<ActionResult> actions = new List<ActionResult>();
            public string[] screenshots = { "01_partial_entry.png", "02_rotating_paddle.png", "03_out_of_water.png",
                "04_entry_splash.png", "05_exit_droplets.png", "06_coasting_wake.png" };
            public float boatAdvanceM;
            public float bladeAreaM2, bladeLengthM;
            public List<string> errors = new List<string>();
        }
        SimulationSession _session;
        PaddleDriver _driver;
        PaddleHydrodynamics _hydro;
        HardwareUdpBridge _bridge;
        readonly Result _result = new Result();
        int _stage, _stageFrames;
        bool _ready;
        float _time;
        uint _sequence;
        Vector3 _expectedPosition, _expectedVelocity, _boatStart;
        Quaternion _expectedRotation;
        float _expectedArea, _expectedTorque;
        bool _exitDry;
        float _previousArea, _previousForce;
        bool _hadWetExit;
        string _pendingScreenshot;
        Vector3 _lastSensorBlade, _sensorVelocity, _sensorAngularVelocity;
        Quaternion _lastSensorRotation;
        long _lastSensorReceiveTicks;
        Vector3 _expectedSampleVelocity, _expectedSampleAngularVelocity;
        SparseUdpSender _udpSender;
        FieldInfo _udpReceiveTicksField;
        long _udpStageStartedTicks, _udpExpiryStartedTicks, _udpFixedStartedTicks, _udpFixedReceivedTicks;
        long _udpPreviousObservedReceiveTicks, _udpFinalReceivedTicks, _udpPreviousFixedStartTicks;
        uint _udpFixedSequence, _udpPreviousObservedSequence;
        Vector3 _udpPreviousVelocity, _udpPreviousAngularVelocity;
        bool _udpHasObservedPose, _udpMotionEstablished;
        uint _udpFirstSequence;
        bool _udpFirstPoseReady;
        WaterInteractionVisuals _waterEffects;
        Vector3 _initialPaddlePosition, _experienceHeldBoatPosition;
        Quaternion _initialPaddleRotation;
        double _experienceHeldSimulationTime;
        float _initialWaveTime, _experienceHeldWaveTime;
        int _effectEntries, _effectExits, _effectSplashes, _effectDrips, _effectWakes, _effectRenderFrames;
        const double UdpIntervalSeconds = 0.04, UdpRotationSeconds = 2.2, UdpExpirySeconds = 1.2;
        const double UdpTtlSeconds = 0.1;
        static readonly Vector3 UdpBladeCentre = new Vector3(1.25f, -0.55f, 0f);

        float EncoderPositionToleranceM
        {
            get
            {
                var encoder = _session != null && _session.encoderPipelines != null && _session.encoderPipelines.Length > 0
                    ? _session.encoderPipelines[0] : null;
                if (encoder == null || !SimulationValue.Finite(encoder.countsPerMeter) || encoder.countsPerMeter <= 0f)
                    return 0f;
                // Position is intentionally quantized by the declared encoder resolution.
                // Bound the independent 3-axis rounding error and a small float transform
                // round-trip term instead of treating quantization as a pose failure.
                return Mathf.Sqrt(3f) * 0.5f / encoder.countsPerMeter + 2e-5f;
            }
        }

        // Only .NET code runs on this worker. The socket always targets this dedicated
        // demo's loopback listener; poses use actual send time rather than catch-up ticks.
        sealed class SparseUdpSender : IDisposable
        {
            public struct Snapshot
            {
                public int packets;
                public uint lastSequence;
                public long stoppedTicks;
                public double minimumIntervalMs, meanIntervalMs, maximumIntervalMs;
                public string error;
            }
            readonly object _gate = new object();
            readonly ManualResetEvent _stop = new ManualResetEvent(false);
            readonly Thread _worker;
            readonly int _port;
            readonly float _shaftToBlade;
            readonly uint _firstSequence;
            Snapshot _snapshot;
            double _intervalSumMs;
            public SparseUdpSender(int port, float shaftToBlade, uint firstSequence)
            {
                _port = port; _shaftToBlade = shaftToBlade; _firstSequence = firstSequence;
                _worker = new Thread(SendLoop) { IsBackground = true, Name = "PaddleDemoLoopback25Hz" };
                _worker.Start();
            }
            public Snapshot Read()
            {
                lock (_gate) return _snapshot;
            }
            public void RequestStop() { _stop.Set(); }
            void SendLoop()
            {
                try
                {
                    using (var socket = new UdpClient(AddressFamily.InterNetwork))
                    {
                        var destination = new IPEndPoint(IPAddress.Loopback, _port);
                        long origin = System.Diagnostics.Stopwatch.GetTimestamp(), previous = 0;
                        uint sequence = _firstSequence;
                        while (!_stop.WaitOne(0))
                        {
                            long now = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (previous != 0)
                            {
                                double remaining = UdpIntervalSeconds - (now - previous) / (double)System.Diagnostics.Stopwatch.Frequency;
                                if (remaining > 0)
                                {
                                    _stop.WaitOne(Math.Max(1, (int)Math.Ceiling(remaining * 1000)));
                                    continue;
                                }
                            }
                            double elapsed = (now - origin) / (double)System.Diagnostics.Stopwatch.Frequency;
                            double angle = 2.0 * elapsed;
                            // Grip = blade centre + rotated shaft offset, for world-space
                            // UdpDirect with zero sensor offsets and rotation about world X.
                            byte[] packet;
                            using (var stream = new MemoryStream(48))
                            using (var writer = new BinaryWriter(stream))
                            {
                                writer.Write(0x44424F41u); writer.Write(1u);
                                writer.Write((ulong)(elapsed * 1000000) + 1); writer.Write(sequence);
                                writer.Write(1.25f); writer.Write(-0.55f + _shaftToBlade * (float)Math.Cos(angle));
                                writer.Write(_shaftToBlade * (float)Math.Sin(angle));
                                writer.Write((float)(angle * 180.0 / Math.PI)); writer.Write(0f); writer.Write(0f); writer.Write(0f);
                                packet = stream.ToArray();
                            }
                            if (_stop.WaitOne(0)) break;
                            socket.Send(packet, packet.Length, destination);
                            lock (_gate)
                            {
                                _snapshot.packets++; _snapshot.lastSequence = sequence;
                                if (previous != 0)
                                {
                                    double intervalMs = (now - previous) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                                    _intervalSumMs += intervalMs;
                                    if (_snapshot.packets == 2 || intervalMs < _snapshot.minimumIntervalMs) _snapshot.minimumIntervalMs = intervalMs;
                                    _snapshot.maximumIntervalMs = Math.Max(_snapshot.maximumIntervalMs, intervalMs);
                                    _snapshot.meanIntervalMs = _intervalSumMs / (_snapshot.packets - 1);
                                }
                            }
                            sequence++; previous = now;
                        }
                    }
                }
                catch (Exception error) { lock (_gate) _snapshot.error = error.Message; }
                finally { lock (_gate) _snapshot.stoppedTicks = System.Diagnostics.Stopwatch.GetTimestamp(); }
            }
            public void Dispose()
            {
                RequestStop();
                if (!_worker.Join(500)) throw new InvalidOperationException("Loopback paddle sender did not stop.");
                _stop.Dispose();
            }
        }

        IEnumerator Start()
        {
            while ((_session = FindObjectOfType<SimulationSession>()) == null || _session.hydrodynamics == null)
                yield return null;
            _driver = _session.drivers[0]; _hydro = _session.hydrodynamics[0]; _bridge = _session.bridge;
            _waterEffects = _session.GetComponent<WaterInteractionVisuals>();
            // These analytical actions require flat, stationary water. A live
            // environment panel must not retarget it during the dedicated test.
            // Only this unsaved diagnostic session is isolated; the production
            // environment controls remain available in the normal runtime gate.
            if (_session.riverEnvironment != null) _session.riverEnvironment.enabled = false;
            _session.riverEnvironment = null;
            _session.wave.intensity = 0f;
            _session.wave.intensityRatePerSecond = 0f;
            _session.wave.backgroundCurrentMps = Vector2.zero;
            _session.forceDeviceAdapter = _session.platformDeviceAdapter = null;
            _hydro.balanceYawRoll = false;
            _hydro.smoothing = 0.05f;
            _driver.SetMode(PaddleDriver.Mode.External);
            _initialPaddlePosition = _session.InitialPaddlePosition(0);
            _initialPaddleRotation = _session.InitialPaddleRotation(0);
            _initialWaveTime = _session.InitialWaveTime;
            _session.showDebugOverlay = _bridge.showDebugOverlay = false;
            if (_session.cable != null)
                foreach (var renderer in _session.cable.GetComponentsInChildren<LineRenderer>()) renderer.enabled = false;
            foreach (var camera in FindObjectsOfType<Camera>()) if (camera != Camera.main) camera.enabled = false;
            var split = FindObjectOfType<SplitViewController>(); if (split != null) split.enabled = false;
            var hud = FindObjectOfType<RideHud>(); if (hud != null) hud.enabled = false;
            if (Camera.main != null)
            {
                var follow = Camera.main.GetComponent<DebugFollowCamera>(); if (follow != null) follow.enabled = false;
                var head = Camera.main.GetComponent<HeadPoseAttachment>(); if (head != null) head.enabled = false;
                var seat = Camera.main.GetComponent<BoatSeatCamera>(); if (seat != null) seat.enabled = false;
                Camera.main.rect = new Rect(0, 0, 1, 1);
                Camera.main.fieldOfView = 48f;
            }
            _boatStart = _session.InitialBoatPosition;
            _result.bladeAreaM2 = _hydro.bladeArea; _result.bladeLengthM = _hydro.bladeLength;
            foreach (string name in Names) _result.actions.Add(new ActionResult
                { action = name, posePositionToleranceM = EncoderPositionToleranceM });
            _session.FrameCompleted += Observe;
            Application.logMessageReceived += OnLog;
            _ready = true;
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _result.errors.Add(message);
        }

        void FixedUpdate()
        {
            if (!_ready || Completed) return;
            if (_stage == 13)
            {
                StepExperienceControls();
                return;
            }
            if (_stage == 9 || _stage == 10)
            {
                if (_stage == 9 && _stageFrames == 0) BeginSparseUdp();
                _udpFixedStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _bridge.TryGetPaddleAt(_udpFixedStartedTicks, float.MaxValue, out var incoming, out _udpFixedReceivedTicks);
                _udpFixedSequence = incoming.seq;
                if (!_udpFirstPoseReady && unchecked((int)(incoming.seq - _udpFirstSequence)) >= 0)
                {
                    _driver.SetMode(PaddleDriver.Mode.UdpDirect);
                    _udpFirstPoseReady = true;
                }
                _stageFrames++;
                return;
            }
            float dt = Time.fixedDeltaTime;
            if (_stageFrames == 0)
            {
                _hydro.ResetDynamics(); _driver.SetMode(PaddleDriver.Mode.External);
                _previousArea = _previousForce = 0f;
                if (_stage >= 11)
                {
                    _effectEntries = _waterEffects.EntryBursts; _effectExits = _waterEffects.ExitEvents;
                    _effectSplashes = _waterEffects.SplashParticles; _effectDrips = _waterEffects.DripParticles;
                    _effectWakes = _waterEffects.WakeRipples; _effectRenderFrames = _waterEffects.VisibleRenderFrames;
                    if (_stage == 12) _session.boat.BoatRb.velocity = _session.boat.transform.forward * 0.8f;
                }
                if (_stage == 8)
                {
                    _driver.sensorToReferencePosition = new Vector3(0.03f, -0.02f, 0.04f);
                    _driver.sensorToReferenceEulerDeg = new Vector3(7f, -13f, 9f);
                    _driver.SetMode(PaddleDriver.Mode.UdpDirect);
                }
            }
            _time = _stageFrames * dt;
            _expectedRotation = Quaternion.identity;
            _expectedPosition = new Vector3(1.25f, -0.5f, -0.5f * _time);
            _expectedVelocity = new Vector3(0f, 0f, -0.5f);
            _expectedArea = _hydro.bladeArea; _expectedTorque = 0f;
            _exitDry = false;
            switch (_stage)
            {
                case 0:
                    _expectedPosition = new Vector3(1.25f + 0.12f * Mathf.Sin(3.1f * _time),
                        0.75f + 0.07f * Mathf.Cos(2.3f * _time), 0.2f * Mathf.Sin(1.7f * _time));
                    _expectedRotation = Quaternion.Euler(65f * Mathf.Sin(2.1f * _time), 85f * Mathf.Cos(1.3f * _time), 50f * Mathf.Sin(3.7f * _time));
                    _expectedVelocity = new Vector3(0.372f * Mathf.Cos(3.1f * _time), -0.161f * Mathf.Sin(2.3f * _time), 0.34f * Mathf.Cos(1.7f * _time));
                    _expectedArea = 0f;
                    break;
                case 1:
                    _expectedPosition.y = 0.08f;
                    _expectedRotation = Quaternion.Euler(30f, 0f, 0f);
                    _expectedArea = _hydro.bladeArea * (0.5f - 0.08f / (_hydro.bladeLength * Mathf.Cos(30f * Mathf.Deg2Rad)));
                    break;
                case 2:
                    _expectedPosition.y = 0.22f - 0.22f * _time;
                    _expectedVelocity.y = -0.22f;
                    _expectedArea = _hydro.bladeArea * Mathf.Clamp01(0.5f - _expectedPosition.y / _hydro.bladeLength);
                    break;
                case 3:
                    break;
                case 4:
                    _expectedPosition.z = 0.5f * _time;
                    _expectedVelocity.z = 0.5f;
                    break;
                case 5:
                    _time = (_stageFrames / 2) * 2f * dt;
                    _expectedPosition = new Vector3(1.25f, -0.55f, 0f);
                    _expectedVelocity = Vector3.zero;
                    _expectedRotation = Quaternion.AngleAxis(2f * _time * Mathf.Rad2Deg, Vector3.right);
                    // Independent integral for a rectangle rotating about its width axis:
                    // tau = -rho*Cd*width*omega*|omega|*length^4 / 64.
                    _expectedTorque = -_hydro.waterDensity * _hydro.cdFlat * _hydro.BladeWidth * 4f * Mathf.Pow(_hydro.bladeLength, 4f) / 64f;
                    break;
                case 6:
                    _exitDry = _time >= 1f;
                    _expectedPosition.y = _exitDry ? 0.75f : -0.5f;
                    _expectedArea = _exitDry ? 0f : _hydro.bladeArea;
                    break;
                case 7:
                    _expectedPosition = new Vector3(1.25f, -0.5f, 0f);
                    _expectedVelocity = Vector3.zero;
                    break;
                case 8:
                    _time = (_stageFrames / 2) * 2f * dt;
                    Quaternion sensorRotation = Quaternion.Euler(30f * Mathf.Sin(_time), 25f * Mathf.Cos(1.7f * _time), 18f * Mathf.Sin(2.3f * _time));
                    // Exercise the same mounting rotations inside the locked hull envelope.
                    // Including the shaft offset, this puts blade X below 1.21 m; the
                    // separate input-boundary test exercises intentional envelope clipping.
                    Vector3 sensorPosition = new Vector3(0.70f + 0.05f * Mathf.Sin(_time), 2f, 0.1f * Mathf.Cos(_time));
                    Quaternion localRotation = sensorRotation * Quaternion.Euler(_driver.sensorToReferenceEulerDeg);
                    Vector3 localPosition = sensorPosition + sensorRotation * _driver.sensorToReferencePosition - localRotation * Vector3.up * _driver.shaftToBlade;
                    _expectedPosition = _session.boat.transform.TransformPoint(localPosition);
                    _expectedRotation = _session.boat.transform.rotation * localRotation;
                    _expectedArea = 0f;
                    if (_stageFrames % 2 == 0)
                    {
                        long receivedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        float interval = (float)((receivedTicks - _lastSensorReceiveTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
                        _sensorVelocity = _stageFrames > 0 ? (localPosition - _lastSensorBlade) / interval : Vector3.zero;
                        _sensorAngularVelocity = _stageFrames > 0 ? SimulationValue.AngularVelocity(_lastSensorRotation, localRotation, interval) : Vector3.zero;
                        _lastSensorBlade = localPosition; _lastSensorRotation = localRotation; _lastSensorReceiveTicks = receivedTicks;
                        _bridge.AcceptPaddle(new HardwareUdpBridge.PaddleState { seq = ++_sequence,
                            timestampUs = (ulong)((_session.SimulationTime + dt) * 1e6), position = sensorPosition,
                            eulerDeg = sensorRotation.eulerAngles }, receivedTicks);
                    }
                    _expectedSampleVelocity = _session.boat.transform.rotation * _sensorVelocity + _session.boat.BoatRb.GetPointVelocity(_expectedPosition);
                    _expectedSampleAngularVelocity = _session.boat.transform.rotation * _sensorAngularVelocity + _session.boat.BoatRb.angularVelocity;
                    break;
                case 11:
                    float phase = 2f * Mathf.PI * 0.5f * _time;
                    _expectedPosition = new Vector3(1.25f, 0.26f * Mathf.Cos(phase), -0.25f * _time);
                    _expectedVelocity = new Vector3(0f, -0.26f * 2f * Mathf.PI * 0.5f * Mathf.Sin(phase), -0.25f);
                    _expectedArea = _hydro.bladeArea * Mathf.Clamp01(0.5f - _expectedPosition.y / _hydro.bladeLength);
                    break;
                case 12:
                    _expectedPosition = new Vector3(1.25f, 0.75f, 0f);
                    _expectedVelocity = Vector3.zero; _expectedArea = 0f;
                    break;
            }
            if (_stage != 8 && (_stage != 5 || _stageFrames % 2 == 0))
            {
                var sample = new PaddleSample { sequence = ++_sequence, timeSeconds = _session.SimulationTime + dt,
                    bladePosition = _expectedPosition, bladeRotation = _expectedRotation,
                    bladeVelocity = _expectedVelocity, valid = true };
                if (!_driver.SetExternalSample(sample)) Fail("Simulated input rejected");
            }
            _stageFrames++;
        }

        void StepExperienceControls()
        {
            ActionResult action = _result.actions[_stage];
            var rb = _session.boat.BoatRb;
            if (_stageFrames == 0)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                _experienceHeldBoatPosition = rb.position;
                _result.boatAdvanceM = rb.position.z - _boatStart.z;
                _experienceHeldSimulationTime = _session.SimulationTime;
                _experienceHeldWaveTime = _session.wave.SimTime;
                _driver.transform.position += new Vector3(0.3f, 0.2f, 0.1f);
                if (!_session.EndExperience() || !_session.ExperienceEnded || !rb.isKinematic
                    || _hydro.InputValid || _hydro.WaterForceN.sqrMagnitude > 1e-12f
                    || _hydro.FeedbackRequest.valid)
                    Fail("PC end control did not stop the ride and clear paddle force.");
                _stageFrames++;
                action.observedFrames++;
                return;
            }
            if (_stageFrames < 100)
            {
                if (_session.ExperienceActive || !rb.isKinematic
                    || _session.SimulationTime != _experienceHeldSimulationTime
                    || _session.wave.SimTime != _experienceHeldWaveTime
                    || Vector3.Distance(rb.position, _experienceHeldBoatPosition) > 1e-5f)
                    Fail("Ended experience did not remain frozen.");
                action.observedFrames++;
                _stageFrames++;
                return;
            }

            bool repositioned = _session.RepositionExperience();
            float boatPositionError = Vector3.Distance(rb.position, _session.InitialBoatPosition);
            float paddlePositionError = Vector3.Distance(_driver.transform.position, _initialPaddlePosition);
            float paddleRotationError = Quaternion.Angle(_driver.transform.rotation, _initialPaddleRotation);
            if (!repositioned || !_session.ExperienceActive || rb.isKinematic || boatPositionError > 1e-5f
                || paddlePositionError > 1e-5f || paddleRotationError > 0.01f || _driver.mode != PaddleDriver.Mode.External
                || _session.SimulationTime != 0d || _session.wave.SimTime != _initialWaveTime)
                Fail("PC reposition did not restore the start and resume: ok=" + repositioned
                    + "; active=" + _session.ExperienceActive + "; kinematic=" + rb.isKinematic
                    + "; boatErrorM=" + boatPositionError + "; paddleErrorM=" + paddlePositionError
                    + "; paddleErrorDeg=" + paddleRotationError + "; mode=" + _driver.mode
                    + "; time=" + _session.SimulationTime + "; wave=" + _session.wave.SimTime);
            if (_waterEffects.ActiveRipples != 0 || _waterEffects.ActiveParticles != 0)
                Fail("PC reposition control retained water interaction effects.");
            action.observedFrames++;
            Debug.Log("[PADDLEDEMO] ExperienceControls=" + action.passed + " frames=" + action.observedFrames);
            _stageFrames++;
            Finish();
        }

        float IndependentFlatWaterArea(PaddleSample pose)
        {
            // The controlled entry/immersion actions use a flat, zero-intensity surface.
            // Clip the decoded paddle rectangle analytically; this stays independent of
            // PaddleHydrodynamics' sampled mesh grid while respecting encoder quantization.
            float halfWidth = _hydro.BladeWidth * 0.5f;
            float halfLength = _hydro.bladeLength * 0.5f;
            var source = new List<Vector2>
            {
                new Vector2(-halfWidth, -halfLength), new Vector2(halfWidth, -halfLength),
                new Vector2(halfWidth, halfLength), new Vector2(-halfWidth, halfLength)
            };
            var clipped = new List<Vector2>(8);
            float waterY = _session.wave.BaseHeight;
            Vector2 previous = source[source.Count - 1];
            float previousDepth = waterY - (pose.bladePosition + pose.bladeRotation
                * new Vector3(previous.x, previous.y, 0f)).y;
            bool previousWet = previousDepth > 0f;
            for (int i = 0; i < source.Count; i++)
            {
                Vector2 current = source[i];
                float currentDepth = waterY - (pose.bladePosition + pose.bladeRotation
                    * new Vector3(current.x, current.y, 0f)).y;
                bool currentWet = currentDepth > 0f;
                if (currentWet != previousWet)
                {
                    float t = previousDepth / (previousDepth - currentDepth);
                    clipped.Add(Vector2.LerpUnclamped(previous, current, t));
                }
                if (currentWet) clipped.Add(current);
                previous = current;
                previousDepth = currentDepth;
                previousWet = currentWet;
            }
            if (clipped.Count < 3) return 0f;
            double twiceArea = 0d;
            for (int i = 0; i < clipped.Count; i++)
            {
                Vector2 a = clipped[i], b = clipped[(i + 1) % clipped.Count];
                twiceArea += (double)a.x * b.y - (double)a.y * b.x;
            }
            return (float)(Math.Abs(twiceArea) * 0.5d);
        }

        void Observe(SimulationFrame frame)
        {
            if (!_ready || Completed) return;
            if (_session.wave.intensity != 0f || _session.wave.intensityRatePerSecond != 0f
                || _session.wave.backgroundCurrentMps != Vector2.zero)
                Fail("Analytical action lost its flat, stationary water prerequisite");
            if (_stage == 9 || _stage == 10)
            {
                ObserveSparseUdp(frame, System.Diagnostics.Stopwatch.GetTimestamp());
                return;
            }
            ActionResult action = _result.actions[_stage];
            action.observedFrames++;
            if (_stage == 1 || _stage == 2 || _stage == 11)
                _expectedArea = IndependentFlatWaterArea(_driver.Sample);
            if (_stage >= 11)
            {
                action.entryBursts = _waterEffects.EntryBursts - _effectEntries;
                action.exitEvents = _waterEffects.ExitEvents - _effectExits;
                action.splashParticles = _waterEffects.SplashParticles - _effectSplashes;
                action.dripParticles = _waterEffects.DripParticles - _effectDrips;
                action.wakeRipples = _waterEffects.WakeRipples - _effectWakes;
                action.visibleEffectRenderFrames = _waterEffects.VisibleRenderFrames - _effectRenderFrames;
                if (_stage == 11 && _stageFrames == 30) _pendingScreenshot = "04_entry_splash.png";
                if (_stage == 11 && _stageFrames == 195) _pendingScreenshot = "05_exit_droplets.png";
                if (_stage == 12 && _stageFrames == 100) _pendingScreenshot = "06_coasting_wake.png";
                if (_stageFrames * Time.fixedDeltaTime >= 2.2f)
                {
                    if (_stage == 11 && (action.entryBursts < 1 || action.exitEvents < 1
                        || action.splashParticles < 12 || action.dripParticles < 2 || action.visibleEffectRenderFrames < 10))
                        Fail("Moving paddle did not produce visible entry splashes and exit droplets.");
                    if (_stage == 12 && (action.wakeRipples < 10 || action.visibleEffectRenderFrames < 10
                        || action.entryBursts != 0 || action.exitEvents != 0))
                        Fail("Coasting boat did not retain a visible wake without paddle contact.");
                }
            }
            float poseError = Vector3.Distance(_driver.transform.position, _expectedPosition);
            float angleError = Quaternion.Angle(_driver.transform.rotation, _expectedRotation);
            action.maximumPoseErrorM = Mathf.Max(action.maximumPoseErrorM, poseError);
            action.maximumPoseErrorDeg = Mathf.Max(action.maximumPoseErrorDeg, angleError);
            if (!_hydro.InputValid || poseError > EncoderPositionToleranceM || angleError > 0.05f)
                Fail($"6DoF pose exceeds encoder-resolution position or orientation tolerance "
                   + $"poseErr={poseError:E3}m tol={EncoderPositionToleranceM:E3}m angleErr={angleError:F3}deg "
                   + $"inputValid={_hydro.InputValid} expected={_expectedPosition} actual={_driver.transform.position}");
            float areaError = Mathf.Abs(_hydro.WettedAreaM2 - _expectedArea);
            action.maximumAreaErrorM2 = Mathf.Max(action.maximumAreaErrorM2, areaError);
            if (areaError > 2e-6f) Fail("Wetted area differs from independent geometric expectation");
            float force = _hydro.RawWaterForceN.magnitude;
            action.maximumForceN = Mathf.Max(action.maximumForceN, force);
            action.maximumTorqueNm = Mathf.Max(action.maximumTorqueNm, _hydro.RawTorqueAtBladeNm.magnitude);
            action.maximumBoatYawTorqueNm = Mathf.Max(action.maximumBoatYawTorqueNm, Mathf.Abs(_hydro.BoatPropulsionTorqueNm.y));
            if (_expectedArea == 0f || _stage == 7)
            {
                if (force > 1e-6f || _hydro.RawTorqueAtBladeNm.magnitude > 1e-6f
                    || _hydro.WaterForceN.magnitude > 1e-6f || _hydro.FeedbackRequest.torqueAtGripWorldNm.magnitude > 1e-6f
                    || _hydro.BoatPropulsionForceN.magnitude > 1e-6f || _hydro.BoatPropulsionTorqueNm.magnitude > 1e-6f)
                    Fail("Dry/still paddle retains water force or torque");
                if (_stage == 6 && _exitDry && !_hadWetExit) Fail("Exit test had no preceding nonzero water resistance");
            }
            else if (_stage == 5 && _stageFrames > 4)
            {
                float omegaError = (_hydro.BladeAngularVelocity - Vector3.right * 2f).magnitude;
                action.maximumAngularVelocityError = Mathf.Max(action.maximumAngularVelocityError, omegaError);
                if (omegaError > 0.002f || force > 0.002f || _hydro.RawTorqueAtBladeNm.x >= 0f
                    || Mathf.Abs(_hydro.RawTorqueAtBladeNm.x - _expectedTorque) > Mathf.Abs(_expectedTorque) * 0.03f)
                    Fail("Rotation loses its resisting moment or creates spurious propulsion");
            }
            else if (_stage != 5)
            {
                Vector3 direction = _expectedVelocity.normalized;
                float sinA = Mathf.Abs(Vector3.Dot(direction, _driver.Sample.bladeRotation * Vector3.forward));
                float cd = _hydro.cdFlat * sinA * sinA + _hydro.cdEdge * (1f - sinA * sinA);
                Vector3 expectedForce = -direction * (0.5f * _hydro.waterDensity * cd * _expectedArea * _expectedVelocity.sqrMagnitude);
                float error = Vector3.Distance(_hydro.RawWaterForceN, expectedForce);
                action.maximumForceErrorN = Mathf.Max(action.maximumForceErrorN, error);
                if (error > 0.003f) Fail("Translation drag differs from the reference-area formula");
                if (_stage == 2 && (_hydro.WettedAreaM2 + 1e-6f < _previousArea || force + 1e-4f < _previousForce))
                    Fail("Increasing immersion does not increase drag at the same velocity");
                if (_stage == 6 && !_exitDry && _hydro.WaterForceN.magnitude > 1f) _hadWetExit = true;
            }
            _previousArea = _hydro.WettedAreaM2; _previousForce = force;
            if (_stage == 8 && _stageFrames > 4)
            {
                if (Vector3.Distance(_driver.Sample.bladeVelocity, _expectedSampleVelocity) > 0.002f
                    || Vector3.Distance(_driver.Sample.bladeAngularVelocity, _expectedSampleAngularVelocity) > 0.002f)
                    Fail("Lower-rate UDP pose produces velocity pulses or drops its derivative");
            }
            if ((_stageFrames == 150 && _stage == 6) || (_stageFrames == 75 && (_stage == 1 || _stage == 5)))
                _pendingScreenshot = _stage == 1 ? "01_partial_entry.png" : _stage == 5 ? "02_rotating_paddle.png" : "03_out_of_water.png";
            if (_stageFrames * Time.fixedDeltaTime >= 2.2f)
            {
                Debug.Log("[PADDLEDEMO] " + action.action + "=" + action.passed + " frames=" + action.observedFrames);
                _stage++; _stageFrames = 0;
                if (_stage == Names.Length) Finish();
            }
        }

        void BeginSparseUdp()
        {
            CloseSparseUdp();
            _hydro.ResetDynamics();
            _driver.sensorToReferencePosition = _driver.sensorToReferenceEulerDeg = Vector3.zero;
            _driver.applyCalibration = false; _driver.paddleInBoatSpace = false;
            // The preceding mounting action used a different reference frame. Its
            // still-fresh packet must not be reinterpreted as this new user's pose.
            _driver.SetMode(PaddleDriver.Mode.External);
            if (Math.Abs(_driver.inputTimeoutSeconds - UdpTtlSeconds) > 1e-6)
                Fail("Sparse UDP demo requires the unchanged 100 ms paddle TTL.");
            _udpReceiveTicksField = typeof(PaddleDriver).GetField("_udpPoseReceivedTicks", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_udpReceiveTicksField == null) throw new InvalidOperationException("Consumed UDP receive timestamp is unavailable.");
            _bridge.TryGetPaddle(float.MaxValue, out var previous, out _);
            _udpFirstSequence = previous.seq + 1;
            _udpFirstPoseReady = false;
            _udpStageStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _udpPreviousFixedStartTicks = 0;
            _udpSender = new SparseUdpSender(_bridge.listenPort, _driver.shaftToBlade, _udpFirstSequence);
        }

        void ObserveSparseUdp(SimulationFrame frame, long postTicks)
        {
            ActionResult action = _result.actions[_stage];
            action.observedFrames++;
            long previousFixedStartTicks = _udpPreviousFixedStartTicks;
            _udpPreviousFixedStartTicks = _udpFixedStartedTicks;
            SparseUdpSender.Snapshot sender = _udpSender.Read();
            action.sentPackets = sender.packets;
            action.minimumSendIntervalMs = sender.minimumIntervalMs; action.meanSendIntervalMs = sender.meanIntervalMs;
            action.maximumSendIntervalMs = sender.maximumIntervalMs;
            if (sender.error != null) Fail("Loopback sender: " + sender.error);
            if (!_udpFirstPoseReady)
            {
                action.invalidFrames++;
                if (_hydro.InputValid || _hydro.WaterForceN.sqrMagnitude > 1e-12f
                    || _hydro.TorqueAtBladeNm.sqrMagnitude > 1e-12f)
                    Fail("Previous mounting input leaked into the new UDP action.");
                if ((postTicks - _udpStageStartedTicks) / (double)System.Diagnostics.Stopwatch.Frequency > 0.2)
                    Fail("The real loopback sender did not deliver its first packet.");
                return;
            }
            var sample = frame.paddles[0];
            bool valid = sample.valid && _hydro.InputValid;
            if (_driver.mode != PaddleDriver.Mode.UdpDirect || sample.source != PaddleInputKind.Udp)
                Fail("Real loopback UDP did not pass through the normal UdpDirect input.");
            if (valid)
            {
                action.validFrames++;
                long consumedTicks = (long)_udpReceiveTicksField.GetValue(_driver);
                bool held = _udpHasObservedPose && sample.sequence == _udpPreviousObservedSequence;
                if (held) action.heldPacketFrames++;
                else
                {
                    action.newPacketFrames++;
                    if (_udpHasObservedPose)
                    {
                        double interval = (consumedTicks - _udpPreviousObservedReceiveTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (action.minimumReceiveIntervalMs == 0 || interval < action.minimumReceiveIntervalMs) action.minimumReceiveIntervalMs = interval;
                        action.maximumReceiveIntervalMs = Math.Max(action.maximumReceiveIntervalMs, interval);
                    }
                }
                double elapsed = (sample.deviceTimeUs - 1) / 1000000.0;
                Quaternion expectedRotation = Quaternion.AngleAxis((float)(2 * elapsed * 180 / Math.PI), Vector3.right);
                float poseError = Vector3.Distance(sample.bladePosition, UdpBladeCentre);
                float angleError = Quaternion.Angle(sample.bladeRotation, expectedRotation);
                action.maximumPoseErrorM = Mathf.Max(action.maximumPoseErrorM, poseError);
                action.maximumPoseErrorDeg = Mathf.Max(action.maximumPoseErrorDeg, angleError);
                if (poseError > EncoderPositionToleranceM || angleError > 0.05f
                    || Mathf.Abs(_hydro.WettedAreaM2 - _hydro.bladeArea) > 2e-6f)
                    Fail("Loopback UDP pose exceeds encoder-resolution tolerance or changes full blade immersion.");
                float omega = sample.bladeAngularVelocity.x;
                if (omega > 0.1f) _udpMotionEstablished = true;
                if (_udpMotionEstablished)
                {
                    if (action.minimumAngularVelocityRadS == 0 || omega < action.minimumAngularVelocityRadS) action.minimumAngularVelocityRadS = omega;
                    action.maximumAngularVelocityRadS = Mathf.Max(action.maximumAngularVelocityRadS, omega);
                    float torqueX = _hydro.RawTorqueAtBladeNm.x;
                    if (action.minimumTorqueXNm == 0 || torqueX < action.minimumTorqueXNm) action.minimumTorqueXNm = torqueX;
                    if (action.maximumTorqueXNm == 0 || torqueX > action.maximumTorqueXNm) action.maximumTorqueXNm = torqueX;
                    float referenceTorque = -_hydro.waterDensity * _hydro.cdFlat * _hydro.BladeWidth * omega * Mathf.Abs(omega)
                        * Mathf.Pow(_hydro.bladeLength, 4f) / 64f;
                    if (omega < 0.5f || omega > 4f || Mathf.Abs(sample.bladeAngularVelocity.y) > 0.002f
                        || Mathf.Abs(sample.bladeAngularVelocity.z) > 0.002f || sample.bladeVelocity.magnitude > 0.002f
                        || torqueX >= -0.01f || Mathf.Abs(torqueX - referenceTorque) > Mathf.Abs(referenceTorque) * 0.03f
                        || _hydro.RawWaterForceN.magnitude > 0.003f)
                        Fail("Sparse UDP rotation loses its opposite water-resistance moment or creates spurious translation.");
                    if (held)
                    {
                        action.heldMotionFrames++;
                        if (consumedTicks != _udpPreviousObservedReceiveTicks
                            || Vector3.Distance(sample.bladeAngularVelocity, _udpPreviousAngularVelocity) > 1e-5f
                            || Vector3.Distance(sample.bladeVelocity, _udpPreviousVelocity) > 1e-5f)
                            Fail("Reading the same UDP sequence refreshes its receive time or drops its cached velocity.");
                    }
                }
                else if (action.newPacketFrames >= 3) Fail("UDP poses arrived without establishing rotation velocity.");
                action.maximumForceN = Mathf.Max(action.maximumForceN, _hydro.RawWaterForceN.magnitude);
                action.maximumTorqueNm = Mathf.Max(action.maximumTorqueNm, _hydro.RawTorqueAtBladeNm.magnitude);
                _udpHasObservedPose = true; _udpPreviousObservedSequence = sample.sequence;
                _udpPreviousObservedReceiveTicks = consumedTicks;
                _udpPreviousVelocity = sample.bladeVelocity; _udpPreviousAngularVelocity = sample.bladeAngularVelocity;
            }
            else
            {
                action.invalidFrames++;
                if (_stage == 9 && _udpHasObservedPose) Fail("UDP input expired while the 25 Hz sender was active.");
                if (_hydro.RawWaterForceN.sqrMagnitude > 1e-12f || _hydro.WaterForceN.sqrMagnitude > 1e-12f
                    || _hydro.RawTorqueAtBladeNm.sqrMagnitude > 1e-12f || _hydro.TorqueAtBladeNm.sqrMagnitude > 1e-12f
                    || _hydro.FeedbackRequest.valid || _hydro.FeedbackRequest.torqueAtGripWorldNm.sqrMagnitude > 1e-12f
                    || _hydro.BoatPropulsionForceN.sqrMagnitude > 1e-12f || _hydro.BoatPropulsionTorqueNm.sqrMagnitude > 1e-12f
                    || _hydro.BladeVelocity.sqrMagnitude > 1e-12f || _hydro.BladeAngularVelocity.sqrMagnitude > 1e-12f)
                    Fail("Expired UDP input retains water force, torque, propulsion or motion velocity.");
            }
            if (_stage == 9)
            {
                if ((postTicks - _udpStageStartedTicks) / (double)System.Diagnostics.Stopwatch.Frequency < UdpRotationSeconds) return;
                if (sender.packets < 30 || action.newPacketFrames < 25 || action.heldMotionFrames < 25
                    || action.meanSendIntervalMs < 39 || action.meanSendIntervalMs > 60)
                    Fail("Insufficient real 25 Hz UDP packets or held physics frames to exercise the cached water resistance.");
                Debug.Log("[PADDLEDEMO] " + action.action + "=" + action.passed + " frames=" + action.observedFrames);
                _udpSender.RequestStop(); // No join, logging of packet lists or disk writes during the expiry observation.
                _udpExpiryStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _stage++; _stageFrames = 0;
                return;
            }
            _bridge.TryGetPaddleAt(postTicks, float.MaxValue, out var latest, out long latestReceivedTicks);
            if (sender.stoppedTicks != 0 && latest.seq == sender.lastSequence)
            {
                if (_udpFinalReceivedTicks != 0 && _udpFinalReceivedTicks != latestReceivedTicks)
                    Fail("The final UDP sequence refreshed its receive timestamp after the sender stopped.");
                _udpFinalReceivedTicks = latestReceivedTicks;
                action.finalAcceptedReceiveTicks = latestReceivedTicks; action.finalAcceptedSequence = latest.seq;
                double acceptedAge = (postTicks - latestReceivedTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (valid)
                {
                    action.stoppedValidFrames++;
                    if (sample.sequence == latest.seq && _udpPreviousObservedSequence == latest.seq) action.stoppedHeldFrames++;
                    double fixedStartAge = (_udpFixedStartedTicks - _udpFixedReceivedTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (_udpFixedSequence == latest.seq && fixedStartAge > UdpTtlSeconds)
                        Fail("Final UDP input remains valid after its unchanged 100 ms receive-age TTL.");
                    if (action.firstExpiredAcceptedAgeMs >= 0) Fail("UDP input became valid again after expiry without a new packet.");
                }
                else if (action.firstExpiredAcceptedAgeMs < 0)
                {
                    double fixedFrameAge = (_udpFixedStartedTicks - latestReceivedTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                    action.firstExpiredAcceptedAgeMs = fixedFrameAge * 1000;
                    action.firstExpiredAfterSenderStopMs = (postTicks - sender.stoppedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    double fixedStartGap = previousFixedStartTicks > 0
                        ? (_udpFixedStartedTicks - previousFixedStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency : 0d;
                    if (fixedFrameAge < UdpTtlSeconds || previousFixedStartTicks <= 0
                        || fixedFrameAge - UdpTtlSeconds > fixedStartGap + 1e-4)
                        Fail("The first fixed-frame expiry did not occur on the first observation after the unchanged 100 ms TTL.");
                }
            }
            if ((postTicks - _udpExpiryStartedTicks) / (double)System.Diagnostics.Stopwatch.Frequency < UdpExpirySeconds) return;
            if (sender.stoppedTicks == 0 || action.firstExpiredAcceptedAgeMs < 0 || action.stoppedValidFrames == 0
                || action.stoppedHeldFrames == 0 || action.invalidFrames < 30)
                Fail("Stopped UDP did not exercise held fresh input followed by sustained cleared expiry.");
            Debug.Log("[PADDLEDEMO] " + action.action + "=" + action.passed + " frames=" + action.observedFrames);
            _stage++; _stageFrames = 0;
            CloseSparseUdp();
        }

        void CloseSparseUdp()
        {
            if (_udpSender == null) return;
            _udpSender.Dispose(); _udpSender = null;
        }

        void LateUpdate()
        {
            if (!_ready || Camera.main == null) return;
            Vector3 focus = _driver.transform.position + _driver.transform.up * 0.3f;
            if (_stage == 12) focus = _session.boat.transform.position;
            Camera.main.transform.position = focus + (_stage == 12 ? new Vector3(5f, 3.5f, -6f) : new Vector3(1.1f, 0.65f, -2.3f));
            Camera.main.transform.LookAt(focus);
            if (_pendingScreenshot != null)
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(artifactsDirectory, _pendingScreenshot));
                _pendingScreenshot = null;
            }
        }
        void Fail(string reason)
        {
            _result.actions[_stage].passed = false;
            if (_result.actions[_stage].failure == null)
            {
                var sample = _driver.Sample;
                _result.actions[_stage].failure = reason + "; frame=" + _stageFrames
                    + "; waveIntensity=" + _session.wave.intensity.ToString("G9")
                    + "; waveRate=" + _session.wave.intensityRatePerSecond.ToString("G9")
                    + "; current=" + _session.wave.backgroundCurrentMps.ToString("G9")
                    + "; bladeVelocity=" + sample.bladeVelocity.ToString("G9")
                    + "; bladeOmega=" + sample.bladeAngularVelocity.ToString("G9")
                    + "; waterVelocity=" + _hydro.WaterVelocity.ToString("G9")
                    + "; rawForce=" + _hydro.RawWaterForceN.ToString("G9")
                    + "; rawTorque=" + _hydro.RawTorqueAtBladeNm.ToString("G9")
                    + "; areaActual/expected=" + _hydro.WettedAreaM2.ToString("G9") + "/" + _expectedArea.ToString("G9");
            }
        }
        void Finish()
        {
            CloseSparseUdp();
            _ready = false; Completed = true;
            _session.FrameCompleted -= Observe;
            _driver.SetMode(PaddleDriver.Mode.External);
            _hydro.ResetDynamics();
            _result.boatAdvanceM = _session.boat.transform.position.z - _boatStart.z;
            Passed = _result.errors.Count == 0 && _result.actions.TrueForAll(x => x.passed && x.observedFrames >= 100);
            _result.result = Passed ? "PASS" : "FAIL";
            File.WriteAllText(Path.Combine(artifactsDirectory, "result.json"), JsonUtility.ToJson(_result, true));
            Debug.Log("[PADDLEDEMO] RESULT=" + _result.result);
        }
        void OnDestroy()
        {
            CloseSparseUdp();
            if (_session != null) _session.FrameCompleted -= Observe;
            Application.logMessageReceived -= OnLog;
        }
        void OnGUI()
        {
            if (!_ready) return;
            GUI.Box(new Rect(12, 12, 400, 86), "Simulated real-paddle use (software only)");
            GUI.Label(new Rect(22, 36, 380, 22), ActionName);
            GUI.Label(new Rect(22, 58, 380, 22), "Wet area " + _hydro.WettedAreaM2.ToString("F4") + " m2   Force "
                + _hydro.CurrentDrag.ToString("F2") + " N   Torque " + _hydro.RawTorqueAtBladeNm.magnitude.ToString("F3") + " Nm");
        }
    }
}
