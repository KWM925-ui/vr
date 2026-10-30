using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR
{
    /// <summary>Host/controller boundary. Only received UDP observations can drive the actual
    /// paddle and device feedback. The worker owns actuator and compliant-grip dynamics.
    /// The host owns water/boat physics, allocation, rendering and the desired stroke.</summary>
    public sealed class UdpClosedLoopClient : MonoBehaviour
    {
        const float EncoderCountsPerMeter = 10000f, EncoderCountsPerDegree = 100f;
        public ClosedLoopConfiguration configuration = new ClosedLoopConfiguration();
        public bool SendCommands = true;
        public bool Active { get; private set; }
        public bool Ready => Active && _port > 0 && _process != null && !_process.HasExited;
        public ClosedLoopTelemetry Telemetry
        {
            get
            {
                var snapshot = SimulationSnapshot.Copy(_telemetry);
                // Unity can materialize an empty serializable child for a null field.
                // A stopped session must still report that no accepted observation exists.
                if (_telemetry.feedback == null) snapshot.feedback = null;
                return snapshot;
            }
        }
        public CableDeviceFeedback CableFeedback { get; private set; } = new CableDeviceFeedback();
        public PlatformDeviceFeedback PlatformFeedback { get; private set; } = new PlatformDeviceFeedback();
        public Vector3 ObservationBoatPosition => _telemetry.observationBoatPosition;
        public Quaternion ObservationBoatRotation => _telemetry.observationBoatRotation;
        public uint ObservationSequence => _receivedSequence;
        public string ObservationSessionId => _telemetry.sessionId;
        public bool EncoderObservationConsumed => _telemetry.encoderConsumed;
        SimulationSession _session;
        Process _process;
        UdpClient _udp;
        volatile int _port;
        volatile int _lifecycleGeneration;
        volatile string _workerError;
        bool _connected;
        bool _modeSelected;
        bool _encoderSequenceNeedsLocalReset;
        string _rigSignature;
        ulong _sessionId;
        uint _sentSequence, _receivedSequence, _decodedSequence;
        double _receivedAt = double.NegativeInfinity, _priorEncoderTime = double.NaN;
        Vector3 _priorEncoderPosition, _relativeVelocity, _relativeAngularVelocity;
        Quaternion _priorEncoderRotation;
        Vector3 _decodedLocalPosition;
        Quaternion _decodedLocalRotation = Quaternion.identity;
        Vector3 _desiredBlade, _desiredGrip, _desiredVelocity, _desiredAngularVelocity, _priorDesiredGrip;
        Quaternion _desiredRotation, _priorDesiredRotation;
        bool _hasIntent;
        float _lastIntentPhase;
        double _priorIntentAt = double.NaN;
        ClosedLoopFeedback _feedback;
        byte[] _receivedEncoderPacket;
        ClosedLoopTelemetry _telemetry = new ClosedLoopTelemetry();
        sealed class Sent { public double at; public ClosedLoopCommand command; public string packet, sourcePacket; }
        readonly Dictionary<uint, Sent> _pending = new Dictionary<uint, Sent>();
        readonly Queue<uint> _pendingOrder = new Queue<uint>();
        float[] _hardwareStopGeometry = System.Array.Empty<float>();
        bool HardwareStopsSelected => configuration.hardwareStopsEnabled || TendonRigProfiles.CadDesignRequested;
        static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public void Initialize(SimulationSession session, ClosedLoopConfiguration config)
        {
            _session = session;
            configuration = config;
            StartController();
        }

        public void RejectStartup(SimulationSession session, string reason)
        {
            _session = session;
            configuration = new ClosedLoopConfiguration();
            FailClosed("CONTROLLER_START_REJECTED: " + reason);
        }

        public void StartController()
        {
            StopController();
            if (configuration == null || !configuration.enabled) return;
            if (_session?.drivers != null && _session.drivers.Length > 0 && _session.drivers[0] != null
                && _session.drivers[0].mode != PaddleDriver.Mode.VirtualPaddler)
            {
                _telemetry.status = "CONTROLLER_SUSPENDED_INPUT_MODE_" + _session.drivers[0].mode;
                return;
            }
            // Selected-but-failed remains active so the session cannot silently fall back
            // to a prescribed kinematic paddle when the mechanical process is unavailable.
            _modeSelected = true;
            Active = true;
            _sessionId = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) & 0x7FFFFFFFFFFFFFFFUL;
            if (_sessionId == 0) _sessionId = 1;
            _sentSequence = _receivedSequence = _decodedSequence = 0;
            _receivedAt = double.NegativeInfinity;
            _priorEncoderTime = double.NaN;
            _priorIntentAt = double.NaN;
            _desiredVelocity = _desiredAngularVelocity = Vector3.zero;
            _hasIntent = false; _feedback = null; _workerError = null; SendCommands = true;
            _pending.Clear(); _pendingOrder.Clear();
            _telemetry = new ClosedLoopTelemetry { active = true, status = "STARTING_CONTROLLER",
                hostProcessId = Process.GetCurrentProcess().Id, sessionId = _sessionId.ToString() };
            try
            {
                ValidateStartup();
                _hardwareStopGeometry = BuildHardwareStopGeometry();
                _rigSignature = RigSignature();
                _session.encoderPipelines[0].ResetPipeline();
                _encoderSequenceNeedsLocalReset = true;
                string script = Path.Combine(Application.streamingAssetsPath, "closed_loop_controller.py");
                if (!File.Exists(script)) throw new InvalidDataException("Controller script missing: " + script);
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int generation = _lifecycleGeneration;
                string executable = configuration.pythonExecutable;
                string bundled = Path.Combine(Application.streamingAssetsPath, "controller_runtime", "python.exe");
                if (string.Equals(executable, "python.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(bundled))
                    executable = bundled;
                var info = new ProcessStartInfo(executable,
                    "-u \"" + script + "\" --session " + _sessionId + " --parent-pid " + _telemetry.hostProcessId)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Application.streamingAssetsPath };
                _process = new Process { StartInfo = info };
                _process.OutputDataReceived += (_, e) =>
                {
                    if (generation != _lifecycleGeneration || e.Data == null
                        || !e.Data.StartsWith("READY ", StringComparison.Ordinal)) return;
                    string[] fields = e.Data.Split(' ');
                    if (fields.Length >= 3 && int.TryParse(fields[1], out int port) && port > 0 && port < 65536)
                        _port = port;
                };
                _process.ErrorDataReceived += (_, e) =>
                { if (generation == _lifecycleGeneration && !string.IsNullOrWhiteSpace(e.Data)) _workerError = e.Data; };
                _process.Start();
                _telemetry.controllerProcessId = _process.Id;
                _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
                Debug.Log("[CLOSEDLOOP] independent controller started pid=" + _process.Id);
            }
            catch (Exception error)
            {
                FailClosed("CONTROLLER_START_REJECTED: " + error.Message);
            }
        }

        public void SynchronizeInputMode(PaddleDriver.Mode mode)
        {
            bool selected = configuration != null && configuration.enabled && mode == PaddleDriver.Mode.VirtualPaddler;
            if (selected && !_modeSelected) StartController();
            else if (!selected && (_modeSelected || Active || _process != null || _udp != null)) StopController();
            if (!selected)
                _telemetry.status = configuration != null && configuration.enabled
                    ? "CONTROLLER_SUSPENDED_INPUT_MODE_" + mode : "CONTROLLER_DISABLED";
        }

        /// <summary>Transfer the sequence window only when the next local input is
        /// about to consume it. Stopping after physics must retain that frame's raw
        /// encoder telemetry; the local packet generator then starts a fresh window.</summary>
        public void PrepareLocalEncoderInput(PaddleEncoderPipeline decoder)
        {
            if (Active || !_encoderSequenceNeedsLocalReset || decoder == null) return;
            decoder.ResetPipeline();
            _encoderSequenceNeedsLocalReset = false;
        }

        void FailClosed(string status)
        {
            StopController();
            _modeSelected = true;
            Active = true;
            _workerError = status;
            _telemetry.active = true;
            _telemetry.status = status;
            CableFeedback.status = PlatformFeedback.status = status;
        }

        public void StopController()
        {
            LifecycleTrace.Mark("controller stop begin pid=" + _telemetry.controllerProcessId);
            _lifecycleGeneration++;
            if (_udp != null && _connected)
            {
                try { SendPacket(new ClosedLoopCommand { kind = "shutdown", commandSequence = ++_sentSequence }); }
                catch (Exception) { }
            }
            Active = false; _modeSelected = false; _port = 0; _connected = false;
            LifecycleTrace.Mark("controller udp close begin");
            _udp?.Close(); _udp = null;
            LifecycleTrace.Mark("controller udp close end");
            if (_process != null)
            {
                try
                {
                    LifecycleTrace.Mark("controller process wait begin");
                    if (!_process.HasExited && !_process.WaitForExit(500))
                    { _process.Kill(); _process.WaitForExit(500); }
                    LifecycleTrace.Mark("controller process wait end");
                }
                catch (InvalidOperationException) { }
                finally
                {
                    LifecycleTrace.Mark("controller process dispose begin");
                    _process.Dispose(); _process = null;
                    LifecycleTrace.Mark("controller process dispose end");
                }
            }
            CableFeedback = new CableDeviceFeedback { simulated = true, valid = false, status = "CONTROLLER_STOPPED" };
            PlatformFeedback = new PlatformDeviceFeedback { simulated = true, valid = false, status = "CONTROLLER_STOPPED" };
            _telemetry.active = _telemetry.ready = _telemetry.feedbackFresh = _telemetry.encoderConsumed = false;
            _telemetry.commandSent = _telemetry.commandSourceFresh = _telemetry.deviceFeedbackFresh = false;
            _telemetry.commandPacketBase64 = null;
            _telemetry.acknowledgedCommandPacketBase64 = _telemetry.acknowledgedSourcePacketBase64 = null;
            _telemetry.commandSourceReceivedAgeSeconds = _telemetry.commandSourceAckAgeSeconds = -1;
            _telemetry.deviceFeedbackReceivedAgeSeconds = _telemetry.deviceFeedbackAckAgeSeconds = -1;
            _telemetry.status = "CONTROLLER_STOPPED";
            _feedback = null; _receivedEncoderPacket = null; _hasIntent = false; _workerError = null; _rigSignature = null;
            _pending.Clear(); _pendingOrder.Clear();
            _receivedAt = double.NegativeInfinity; _priorEncoderTime = double.NaN;
            _sentSequence = _receivedSequence = _decodedSequence = 0;
            _relativeVelocity = _relativeAngularVelocity = _decodedLocalPosition = Vector3.zero;
            _decodedLocalRotation = Quaternion.identity;
            _telemetry.feedback = null;
            _telemetry.feedbackPacketBase64 = _telemetry.encoderPacketBase64 = null;
            _telemetry.commandSequence = _telemetry.feedbackSequence = _telemetry.acknowledgedCommandSequence = 0;
            _telemetry.receivedAgeSeconds = _telemetry.commandAckAgeSeconds = -1;
            LifecycleTrace.Mark("controller stop end");
        }

        static void ModelRange(float value, float minimum, float maximum, string name)
        {
            if (!SimulationValue.Finite(value) || value < minimum || value > maximum)
                throw new InvalidDataException(name + " is outside the independent model's supported bounds.");
        }

        static void ModelPoint(Vector3 point, string name)
        {
            if (!SimulationValue.Finite(point) || Mathf.Abs(point.x) > 20f || Mathf.Abs(point.y) > 20f || Mathf.Abs(point.z) > 20f)
                throw new InvalidDataException(name + " requires finite local coordinates within 20 metres.");
        }

        void ValidateStartup()
        {
            if (_session == null || _session.boat == null || _session.drivers == null || _session.drivers.Length != 1
                || _session.drivers[0] == null || _session.encoderPipelines == null || _session.encoderPipelines.Length != 1
                || _session.encoderPipelines[0] == null || _session.cable == null || _session.platform == null)
                throw new InvalidDataException("Closed loop requires one paddle, decoder, boat, cable rig and platform.");
            ValidateDeviceAdapterExclusion();
            ValidateEncoderContract(_session.encoderPipelines[0]);
            if (configuration.measured || string.IsNullOrWhiteSpace(configuration.pythonExecutable))
                throw new InvalidDataException("An unmeasured software profile and a Python executable are required.");
            ModelRange(configuration.feedbackTimeoutSeconds, float.Epsilon, 0.1f, "feedbackTimeoutSeconds");
            ModelRange(configuration.motorTauSeconds, 0.001f, 2f, "motorTauSeconds");
            ModelRange(configuration.motorSlewNPerSecond, 1f, 100000f, "motorSlewNPerSecond");
            ModelRange(configuration.platformTauSeconds, 0.001f, 5f, "platformTauSeconds");
            ModelRange(configuration.platformSpeedMmPerSecond, 1f, 10000f, "platformSpeedMmPerSecond");
            ModelRange(configuration.gripStiffnessNPerM, 1f, 50000f, "gripStiffnessNPerM");
            ModelRange(configuration.gripDampingNsPerM, 0.01f, 5000f, "gripDampingNsPerM");
            ModelRange(configuration.gripMassKg, 0.1f, 100f, "gripMassKg");
            ModelRange(configuration.angularStiffness, 0.01f, 10000f, "angularStiffness");
            ModelRange(configuration.angularDamping, 0.001f, 1000f, "angularDamping");
            ModelRange(configuration.angularInertia, 0.001f, 100f, "angularInertia");
            if (configuration.gripStiffnessNPerM * 0.005f * 0.005f / configuration.gripMassKg > 0.5f
                || configuration.angularStiffness * 0.005f * 0.005f / configuration.angularInertia > 0.5f)
                throw new InvalidDataException("Spring coefficients exceed the 200 Hz integration stability bound.");
            ModelRange(_session.drivers[0].shaftToBlade, 0.05f, 3f, "shaftLength");
            ModelRange(_session.drivers[0].inboardShaftLeanDeg, 0f, 35f, "inboardShaftLeanDeg");
            ModelRange(_session.drivers[0].upperGripTravelFraction, 0f, 0.5f, "upperGripTravelFraction");
            if (HardwareStopsSelected)
                ModelRange(configuration.hardwareStopClearanceM, 0.001f, 0.1f, "hardwareStopClearanceM");
            var rig = _session.cable.rig;
            if (rig?.tendons == null)
                throw new InvalidDataException("An explicit tendon_rig.json configuration is required for closed loop.");
            var motors = new HashSet<int>();
            int count = 0;
            foreach (var tendon in rig.tendons)
            {
                if (tendon == null || !tendon.enabled) continue;
                count++;
                if (tendon.motorId <= 0 || !motors.Add(tendon.motorId))
                    throw new InvalidDataException("Closed loop requires independent, positive tendon motor IDs.");
                ModelPoint(tendon.anchorLocal, "Tendon anchor"); ModelPoint(tendon.attachLocal, "Tendon attachment");
                ModelRange(tendon.maxTensionN, 0f, 10000f, "Tendon maximum tension");
                ModelRange(tendon.preTensionN, 0f, tendon.maxTensionN, "Tendon pretension");
            }
            if (count < 1 || count > 64 || _session.cable.motorTensionsN == null || _session.cable.motorTensionsN.Length != count)
                throw new InvalidDataException("Closed loop requires 1 to 64 configured tendons matching the allocation channels.");
            var platform = _session.platform;
            ModelRange(platform.neutralHeight, 0.05f, 10f, "Platform neutral height");
            if (platform.minActuatorLengthsMm == null || platform.maxActuatorLengthsMm == null
                || platform.minActuatorLengthsMm.Length != 6 || platform.maxActuatorLengthsMm.Length != 6)
                throw new InvalidDataException("Platform requires six actuator length bounds.");
            if (platform.useExplicitJointGeometry && (platform.explicitBaseJointsLocal == null || platform.explicitTopJointsLocal == null
                || platform.explicitBaseJointsLocal.Length != 6 || platform.explicitTopJointsLocal.Length != 6))
                throw new InvalidDataException("Explicit platform geometry requires six base and six top joints.");
            if (!platform.useExplicitJointGeometry)
            {
                ModelRange(platform.baseRadius, float.Epsilon, 20f, "Platform base radius");
                ModelRange(platform.topRadius, float.Epsilon, 20f, "Platform top radius");
            }
            for (int i = 0; i < 6; i++)
            {
                PlatformJoints(platform, i, out Vector3 bottom, out Vector3 top);
                ModelPoint(bottom, "Platform base joint"); ModelPoint(top, "Platform top joint");
                float low = platform.minActuatorLengthsMm[i], high = platform.maxActuatorLengthsMm[i];
                ModelRange(low, 1f, 100000f, "Platform minimum length");
                ModelRange(high, 1f, 100000f, "Platform maximum length");
                float neutral = (top + Vector3.up * platform.neutralHeight - bottom).magnitude * 1000f;
                if (low >= high || neutral < low || neutral > high)
                    throw new InvalidDataException("Platform neutral geometry violates actuator " + i + " limits.");
            }
        }

        const string DeviceAdapterConflict = "Independent scripted mechanics and external device adapters cannot own the same ride; remove the adapters and reposition, or explicitly select the external backend.";

        void ValidateDeviceAdapterExclusion()
        {
            if (_session != null && (_session.forceDeviceAdapter != null || _session.platformDeviceAdapter != null))
                throw new InvalidDataException(DeviceAdapterConflict);
        }

        /// <summary>Called before any device consumer, including a backend change after
        /// the input/physics boundary. A rejected software selection remains active and
        /// invalid; it never silently hands authority to the conflicting adapter.</summary>
        public bool RejectDeviceAdapterConflict()
        {
            if (!Active || _session == null
                || (_session.forceDeviceAdapter == null && _session.platformDeviceAdapter == null)) return false;
            string status = "CONTROLLER_BACKEND_CONFLICT: " + DeviceAdapterConflict;
            if (_telemetry.status != status) FailClosed(status);
            return true;
        }

        void ValidateEncoderContract(PaddleEncoderPipeline decoder)
        {
            // DBEP v1 has no scale fields. The independent software endpoint uses
            // these fixed units; accepting another decoder scale would silently move
            // the actual paddle and its water force while the packet CRC stayed valid.
            if (decoder == null || _session.encoderPipelines == null || _session.encoderPipelines.Length != 1
                || decoder != _session.encoderPipelines[0] || decoder.boat != _session.boat
                || decoder.driver != _session.drivers[0]
                || decoder.countsPerMeter != EncoderCountsPerMeter || decoder.countsPerDegree != EncoderCountsPerDegree)
                throw new InvalidDataException("Independent encoder requires its session boat/paddle and 10000 counts/m, 100 counts/degree; restore the decoder configuration and reposition.");
        }

        static void PlatformJoints(StewartPlatformTwin platform, int index, out Vector3 bottom, out Vector3 top)
        {
            if (platform.useExplicitJointGeometry)
            { bottom = platform.explicitBaseJointsLocal[index]; top = platform.explicitTopJointsLocal[index]; }
            else
            {
                float a = index * 60f * Mathf.Deg2Rad, b = (index * 60f + 30f) * Mathf.Deg2Rad;
                bottom = new Vector3(Mathf.Cos(a) * platform.baseRadius, 0, Mathf.Sin(a) * platform.baseRadius);
                top = new Vector3(Mathf.Cos(b) * platform.topRadius, 0, Mathf.Sin(b) * platform.topRadius);
            }
        }

        float[] BuildHardwareStopGeometry()
        {
            if (!HardwareStopsSelected) return System.Array.Empty<float>();
            var driver = _session.drivers[0];
            var hydro = driver.GetComponent<PaddleHydrodynamics>();
            var model = _session.boat.transform.Find("RealDragonBoat");
            if (hydro == null || model == null || !model.gameObject.activeInHierarchy)
                throw new InvalidDataException("Ideal hardware stops require the displayed hull and paddle geometry.");
            bool found = false;
            Bounds bounds = default;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
                Bounds mesh = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3((i & 1) == 0 ? mesh.min.x : mesh.max.x,
                        (i & 2) == 0 ? mesh.min.y : mesh.max.y, (i & 4) == 0 ? mesh.min.z : mesh.max.z);
                    Vector3 point = _session.boat.transform.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                    else bounds.Encapsulate(point);
                }
            }
            if (!found) throw new InvalidDataException("No displayed hull bounds for ideal hardware stops.");
            float halfBeam = Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x));
            ModelRange(halfBeam, 0.05f, 1.2f, "hardwareStopHullHalfBeamM");
            ModelRange(bounds.max.y, -1f, 10f, "hardwareStopHullTopM");
            ModelRange(hydro.BladeWidth * 0.5f, 0.001f, 0.3f, "hardwareStopBladeHalfWidthM");
            ModelRange(hydro.bladeLength * 0.5f, 0.001f, 0.6f, "hardwareStopBladeHalfLengthM");
            ModelRange(hydro.bladeThickness * 0.5f, 0.001f, 0.1f, "hardwareStopBladeHalfThicknessM");
            return new[] { driver.leftSide ? -1f : 1f, halfBeam, bounds.max.y,
                hydro.BladeWidth * 0.5f, hydro.bladeLength * 0.5f, hydro.bladeThickness * 0.5f,
                0.0225f, configuration.hardwareStopClearanceM };
        }

        string RigSignature()
        {
            var platform = _session.platform;
            return JsonUtility.ToJson(_session.cable.rig) + "|"
                + _session.drivers[0].shaftToBlade.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|"
                + JsonUtility.ToJson(new PlatformRigConfig { useExplicitJointGeometry = platform.useExplicitJointGeometry,
                    explicitBaseJointsLocal = platform.explicitBaseJointsLocal, explicitTopJointsLocal = platform.explicitTopJointsLocal,
                    baseRadius = platform.baseRadius, topRadius = platform.topRadius, neutralHeight = platform.neutralHeight,
                    minActuatorLengthsMm = platform.minActuatorLengthsMm, maxActuatorLengthsMm = platform.maxActuatorLengthsMm })
                + "|" + HardwareStopsSelected + "|" + configuration.hardwareStopClearanceM.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        public PaddleSample ObservePaddle(PaddleSample intent, PaddleEncoderPipeline decoder, float dt)
        {
            try { ValidateDeviceAdapterExclusion(); ValidateEncoderContract(decoder); }
            catch (InvalidDataException error)
            {
                FailClosed("CONTROLLER_CONFIGURATION_REJECTED: " + error.Message);
                return new PaddleSample { valid = false, source = PaddleInputKind.Encoder,
                    originalSource = PaddleInputKind.Synthetic, timeSeconds = _session.SimulationTime };
            }
            Transform boat = _session.boat.transform;
            Poll();
            // A physical grip cannot follow the legacy display fixture's rapid
            // vertical transition and shaft flip. Use a continuous stroke target
            // with the same declared excursion and cadence for the compliant plant.
            var driver = _session.drivers[0];
            UpdateScriptedIntent(driver, intent.valid, !SendCommands || !Fresh());
            _telemetry.desiredBladePositionLocal = _desiredBlade;
            _telemetry.observationBoatPosition = boat.position;
            _telemetry.observationBoatRotation = boat.rotation;
            _telemetry.encoderConsumed = false;
            if (!Fresh() || !_hasIntent)
            {
                if (!_hasIntent) _telemetry.feedbackFresh = false;
                RefreshDeviceFeedback();
                return new PaddleSample { valid = false, source = PaddleInputKind.Encoder,
                    originalSource = PaddleInputKind.Synthetic, timeSeconds = _session.SimulationTime };
            }
            if (_decodedSequence != _receivedSequence)
            {
                byte[] encoder = _receivedEncoderPacket;
                PaddleSample decoded = decoder.DecodePacket(encoder, _session.SimulationTime, dt,
                    PaddleInputKind.Encoder, PaddleInputKind.Synthetic);
                if (!decoded.valid) return decoded;
                // Consume the decoder's output, including its calibrated pose mapping.
                // Derivatives use distinct sensor observations on the controller clock.
                _decodedLocalPosition = boat.InverseTransformPoint(decoded.bladePosition);
                _decodedLocalRotation = Quaternion.Inverse(boat.rotation) * decoded.bladeRotation;
                double interval = _feedback.controllerTimeSeconds - _priorEncoderTime;
                _relativeVelocity = interval > 1e-6 && interval <= 0.1 ? (_decodedLocalPosition - _priorEncoderPosition) / (float)interval : Vector3.zero;
                _relativeAngularVelocity = interval > 1e-6 && interval <= 0.1
                    ? SimulationValue.AngularVelocity(_priorEncoderRotation, _decodedLocalRotation, (float)interval) : Vector3.zero;
                _priorEncoderPosition = _decodedLocalPosition; _priorEncoderRotation = _decodedLocalRotation;
                _priorEncoderTime = _feedback.controllerTimeSeconds; _decodedSequence = _receivedSequence;
                _telemetry.encoderPacketBase64 = Convert.ToBase64String(encoder);
            }
            Vector3 local = _decodedLocalPosition;
            Quaternion rotation = _decodedLocalRotation;
            Vector3 world = boat.TransformPoint(local);
            var sample = new PaddleSample { valid = true, source = PaddleInputKind.Encoder,
                originalSource = PaddleInputKind.Synthetic, sequence = _receivedSequence,
                timeSeconds = _session.SimulationTime, deviceTimeUs = (ulong)(_feedback.controllerTimeSeconds * 1e6),
                bladePosition = world, bladeRotation = boat.rotation * rotation,
                bladeVelocity = boat.rotation * _relativeVelocity + _session.boat.BoatRb.GetPointVelocity(world),
                bladeAngularVelocity = boat.rotation * _relativeAngularVelocity + _session.boat.BoatRb.angularVelocity,
                hasAngularVelocity = true, gripForceN = Vector(_feedback.actualGripEffortN).magnitude };
            _telemetry.actualBladePositionLocal = local;
            _telemetry.actualPoseErrorM = Vector3.Distance(local, _desiredBlade);
            _telemetry.actualGripEffortN = Vector(_feedback.actualGripEffortN);
            _telemetry.encoderConsumed = true;
            RefreshDeviceFeedback();
            return sample;
        }

        void UpdateScriptedIntent(PaddleDriver driver, bool intentValid, bool pauseIntent)
        {
            pauseIntent |= !intentValid;
            // The human reference and its derivatives share the independently
            // integrated sensor clock. Repeated host FixedUpdates must not turn
            // one physical observation into several strokes or a velocity spike.
            double intentAt = !pauseIntent && _feedback != null ? _feedback.controllerTimeSeconds : double.NaN;
            double intentInterval = intentAt - _priorIntentAt;
            bool advance = _hasIntent && intentInterval > 1e-6 && intentInterval <= 0.1;
            // Begin at the raised recovery position. Phase zero is already the
            // maximum-speed downward part of the stroke, partly under water.
            driver.SetPhase01(_hasIntent ? _lastIntentPhase : 0.75f);
            if (advance)
                driver.SetPhase01(_lastIntentPhase + driver.strokesPerMinute / 60f * (float)intentInterval);
            _lastIntentPhase = driver.Phase01;
            _telemetry.intentPaused = pauseIntent;
            SampleScriptedIntent(driver, out _desiredBlade, out _desiredRotation);
            _desiredGrip = _desiredBlade + _desiredRotation * Vector3.up * driver.shaftToBlade;
            if (advance)
            {
                _desiredVelocity = Vector3.ClampMagnitude((_desiredGrip - _priorDesiredGrip) / (float)intentInterval, 4f);
                _desiredAngularVelocity = Vector3.ClampMagnitude(
                    SimulationValue.AngularVelocity(_priorDesiredRotation, _desiredRotation, (float)intentInterval), 30f);
            }
            else if (!_hasIntent || intentInterval != 0)
            {
                // Startup, expiry and recovery establish a new observation
                // baseline. They never catch up through an unavailable interval.
                _desiredVelocity = _desiredAngularVelocity = Vector3.zero;
            }
            _priorIntentAt = intentAt;
            _priorDesiredGrip = _desiredGrip; _priorDesiredRotation = _desiredRotation; _hasIntent = intentValid;
        }

        /// <summary>The unforced human intention, before the independent plant's
        /// compliant motion, actuator loads and encoder observation.</summary>
        public static void SampleScriptedIntent(PaddleDriver driver, out Vector3 blade, out Quaternion rotation)
        {
            float phase = driver.Phase01 * 2f * Mathf.PI;
            float reach = driver.sweepAmplitude * Mathf.Cos(phase);
            float low = driver.LowestHandheldBladeY(reach);
            float high = driver.bladeAirHeight;
            float side = driver.leftSide ? -1f : 1f;
            blade = new Vector3(side * Mathf.Max(0.43f, Mathf.Abs(driver.pivotLocalPos.x) + driver.sideOffset),
                driver.strokesPerMinute > 0.1f ? (low + high) * 0.5f - (high - low) * 0.5f * Mathf.Sin(phase) : high,
                driver.pivotLocalPos.z + reach);
            driver.ResolveHandheldPose(blade, out blade, out rotation);
        }

        public void Dispatch(ForceFeedbackRequest force, PlatformMotionRequest platformRequest)
        {
            _telemetry.commandSent = _telemetry.commandSourceFresh = false;
            _telemetry.commandPacketBase64 = null;
            _telemetry.commandSourceReceivedAgeSeconds = _telemetry.commandSourceAckAgeSeconds = -1;
            if (_session?.drivers != null && _session.drivers.Length > 0 && _session.drivers[0] != null
                && _session.drivers[0].mode != PaddleDriver.Mode.VirtualPaddler)
            {
                SynchronizeInputMode(_session.drivers[0].mode);
                return;
            }
            if (!Active || !_hasIntent) return;
            // Receive only at the input boundary. Post-physics polling would mix a newer
            // device observation with the paddle sample consumed earlier in this frame.
            if (!Ready || !SendCommands) { RefreshDeviceFeedback(); return; }
            try
            {
                ValidateStartup();
                if (_rigSignature != RigSignature())
                    throw new InvalidDataException("Rig geometry changed; reposition the experience to start a new controller session.");
            }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException)
            { FailClosed("CONTROLLER_CONFIGURATION_REJECTED: " + error.Message); return; }
            var cable = _session.cable;
            var platform = _session.platform;
            var tendons = new List<TendonSpec>();
            if (cable.rig?.tendons != null) foreach (var t in cable.rig.tendons) if (t != null && t.enabled) tendons.Add(t);
            int n = tendons.Count;
            var command = new ClosedLoopCommand { commandSequence = ++_sentSequence,
                sourceFeedbackSequence = _feedback != null ? _receivedSequence : 0,
                sourceSampleTick = _feedback != null ? _feedback.plantSampleTick : 0,
                simulationTime = _session.SimulationTime, active = _session.ExperienceActive,
                cableValid = force.valid && cable.Feedback.valid, platformValid = platformRequest.valid,
                desiredGripPosition = Array(_desiredGrip), desiredGripRotation = new[] { _desiredRotation.x, _desiredRotation.y, _desiredRotation.z, _desiredRotation.w },
                desiredGripVelocity = Array(_desiredVelocity), desiredGripAngularVelocity = Array(_desiredAngularVelocity),
                dragFeedbackEnabled = _session.ExperienceActive && force.valid && force.dissipativePowerW > 1e-8f,
                dragDissipationW = force.dissipativePowerW,
                sourceGripVelocity = Array(_relativeVelocity + Vector3.Cross(_relativeAngularVelocity,
                    _decodedLocalRotation * Vector3.up * _session.drivers[0].shaftToBlade)),
                sourceGripAngularVelocity = Array(_relativeAngularVelocity), minTensions = new float[n],
                shaftLength = _session.drivers[0].shaftToBlade,
                tensionTargets = (float[])cable.motorTensionsN.Clone(), maxTensions = new float[n], anchors = new float[n * 3], attachments = new float[n * 3],
                requestForce = Array(Quaternion.Inverse(_telemetry.observationBoatRotation) * force.forceWorldN),
                requestTorque = Array(Quaternion.Inverse(_telemetry.observationBoatRotation) * force.torqueAtGripWorldNm),
                platformTargetLengthsMm = (float[])platform.targetActuatorLengthsMm.Clone(),
                platformMinMm = (float[])platform.minActuatorLengthsMm.Clone(), platformMaxMm = (float[])platform.maxActuatorLengthsMm.Clone(),
                platformBase = new float[18], platformTop = new float[18], platformNeutralHeight = platform.neutralHeight,
                platformTargetPosition = Array(platformRequest.translationM), platformTargetEuler = Array(platformRequest.eulerDegrees),
                motorTauSeconds = configuration.motorTauSeconds, motorSlewNPerSecond = configuration.motorSlewNPerSecond,
                platformTauSeconds = configuration.platformTauSeconds, platformSpeedMmPerSecond = configuration.platformSpeedMmPerSecond,
                gripStiffnessNPerM = configuration.gripStiffnessNPerM, gripDampingNsPerM = configuration.gripDampingNsPerM,
                gripMassKg = configuration.gripMassKg, angularStiffness = configuration.angularStiffness,
                angularDamping = configuration.angularDamping, angularInertia = configuration.angularInertia,
                hardwareStopsEnabled = HardwareStopsSelected, hardwareStopGeometry = (float[])_hardwareStopGeometry.Clone() };
            for (int i = 0; i < n; i++)
            {
                command.minTensions[i] = tendons[i].preTensionN;
                command.maxTensions[i] = tendons[i].maxTensionN;
                Copy(tendons[i].anchorLocal, command.anchors, i * 3);
                Copy(tendons[i].attachLocal, command.attachments, i * 3);
            }
            for (int i = 0; i < 6; i++)
            {
                PlatformJoints(platform, i, out Vector3 b, out Vector3 t);
                Copy(b, command.platformBase, i * 3); Copy(t, command.platformTop, i * 3);
            }
            // The observation may have expired while PhysX or command mapping ran.
            // Keep that historical physics sample intact; revoke only this output.
            bool fresh = FreshAt(Now, out _telemetry.commandSourceReceivedAgeSeconds,
                out _telemetry.commandSourceAckAgeSeconds);
            _telemetry.commandSourceFresh = fresh && _telemetry.encoderConsumed;
            command.cableValid &= _telemetry.commandSourceFresh;
            command.platformValid &= _telemetry.commandSourceFresh;
            if (!command.cableValid)
            {
                command.tensionTargets = new float[n];
                command.requestForce = command.requestTorque = new float[3];
                command.dragFeedbackEnabled = false; command.dragDissipationW = 0f;
                command.sourceGripVelocity = command.sourceGripAngularVelocity = new float[3];
            }
            if (!_telemetry.commandSourceFresh || !command.active || !(command.cableValid || command.platformValid))
                command.desiredGripVelocity = command.desiredGripAngularVelocity = new float[3];
            _pending[command.commandSequence] = new Sent { at = Now, command = command,
                sourcePacket = _telemetry.feedbackPacketBase64 };
            _pendingOrder.Enqueue(command.commandSequence);
            while (_pendingOrder.Count > 256) _pending.Remove(_pendingOrder.Dequeue());
            try { SendPacket(command); }
            catch (Exception error) when (error is SocketException || error is ObjectDisposedException || error is InvalidDataException)
            { FailClosed("CONTROLLER_SEND_FAILED: " + error.Message); return; }
            _telemetry.commandSequence = command.commandSequence;
            RefreshDeviceFeedback();
        }

        void Poll()
        {
            _telemetry.active = Active; _telemetry.ready = Ready;
            if (!Ready)
            {
                _telemetry.feedbackFresh = _telemetry.encoderConsumed = false;
                _telemetry.status = NotReadyStatus();
                return;
            }
            if (!_connected) { _udp.Connect(IPAddress.Loopback, _port); _connected = true; }
            for (int i = 0; i < 64 && _udp.Available > 0; i++)
            {
                var remote = new IPEndPoint(IPAddress.Loopback, _port);
                byte[] data;
                try { data = _udp.Receive(ref remote); }
                catch (SocketException) { break; }
                try
                {
                    if (!TryUnpack(data, _sessionId, out uint sequence, out string json)
                        || (_receivedSequence != 0 && unchecked((int)(sequence - _receivedSequence)) <= 0))
                        throw new InvalidDataException("Envelope identity/CRC/sequence rejected");
                    var f = JsonUtility.FromJson<ClosedLoopFeedback>(json);
                    if (f == null || f.kind != "feedback" || !_pending.TryGetValue(f.commandSequence, out var sent)
                        || !ValidFeedback(f, sent.command.tensionTargets.Length)
                        || !ValidHardwareStopFeedback(f, sent.command)
                        || f.commandSourceFeedbackSequence != sent.command.sourceFeedbackSequence
                        || f.commandSourceSampleTick != sent.command.sourceSampleTick
                        || (f.cableExecutionEnabled && !sent.command.cableValid)
                        || (f.platformExecutionEnabled && !sent.command.platformValid)
                        || f.dragFeedbackActive != (f.cableExecutionEnabled && sent.command.dragFeedbackEnabled)
                        || (_feedback != null && (f.controllerTimeSeconds <= _feedback.controllerTimeSeconds
                            || f.plantSampleTick <= _feedback.plantSampleTick
                            || f.controllerWallTimeSeconds <= _feedback.controllerWallTimeSeconds
                            || f.plantDroppedTicks < _feedback.plantDroppedTicks)))
                        throw new InvalidDataException("Feedback fields or command identity rejected");
                    byte[] encoder = ReceivedEncoderPacket(sequence, f);
                    _feedback = f; _receivedSequence = sequence; _receivedAt = Now;
                    _receivedEncoderPacket = encoder;
                    _telemetry.receivedPackets++; _telemetry.feedbackSequence = sequence;
                    _telemetry.acknowledgedCommandSequence = f.commandSequence;
                    _telemetry.acknowledgedCommandPacketBase64 = sent.packet;
                    _telemetry.acknowledgedSourcePacketBase64 = sent.sourcePacket;
                    _telemetry.feedbackPacketBase64 = Convert.ToBase64String(data);
                    _telemetry.feedback = f; _telemetry.platformFitResidualMm = f.platformFitResidualMm;
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidDataException
                    || error is OverflowException || error is FormatException)
                { _telemetry.rejectedPackets++; }
            }
            Fresh();
        }

        bool Fresh()
        {
            bool fresh = FreshAt(Now, out _telemetry.receivedAgeSeconds, out _telemetry.commandAckAgeSeconds);
            _telemetry.feedbackFresh = fresh;
            _telemetry.status = !Ready ? NotReadyStatus()
                : _workerError ?? (fresh ? "UDP_EXECUTION_FEEDBACK_CONSUMED" : _feedback?.status ?? "WAITING_FOR_FEEDBACK");
            return fresh;
        }

        bool FreshAt(double now, out double receivedAge, out double acknowledgedAge)
        {
            receivedAge = _feedback == null ? -1 : now - _receivedAt;
            acknowledgedAge = _feedback != null && _pending.TryGetValue(_feedback.commandSequence, out var sent) ? now - sent.at : -1;
            return Ready && _feedback != null && _feedback.valid && !_feedback.watchdog
                && receivedAge >= 0 && receivedAge <= configuration.feedbackTimeoutSeconds
                && acknowledgedAge >= 0 && acknowledgedAge <= configuration.feedbackTimeoutSeconds;
        }

        string NotReadyStatus() => _workerError ?? (_process != null && _process.HasExited
            ? "CONTROLLER_EXITED_CODE_" + _process.ExitCode : "WAITING_FOR_CONTROLLER");

        void RefreshDeviceFeedback()
        {
            // Device output is evaluated after physics. Do not overwrite the
            // already consumed input's freshness if the same sample has expired.
            bool fresh = FreshAt(Now, out _telemetry.deviceFeedbackReceivedAgeSeconds,
                out _telemetry.deviceFeedbackAckAgeSeconds);
            _telemetry.deviceFeedbackFresh = fresh;
            if (!Ready)
            {
                _telemetry.status = NotReadyStatus();
            }
            if (_feedback == null || !_pending.TryGetValue(_feedback.commandSequence, out var sent))
            {
                CableFeedback = new CableDeviceFeedback { simulated = true, valid = false, status = "NO_CONTROLLER_FEEDBACK" };
                PlatformFeedback = new PlatformDeviceFeedback { simulated = true, valid = false, status = "NO_CONTROLLER_FEEDBACK" };
                return;
            }
            Quaternion observationRotation = _telemetry.observationBoatRotation;
            Vector3 force = Vector(_feedback.forceLocal), torque = Vector(_feedback.torqueLocal);
            _telemetry.forceResidualN = Vector3.Distance(force, Vector(sent.command.requestForce));
            _telemetry.torqueResidualNm = Vector3.Distance(torque, Vector(sent.command.requestTorque));
            CableFeedback = new CableDeviceFeedback { simulated = true, valid = fresh && sent.command.cableValid && _feedback.cableExecutionEnabled,
                capabilityLimited = true, status = fresh ? "UDP_DYNAMIC_CAPABILITY_LIMITED" : "UDP_FEEDBACK_EXPIRED",
                commandSequence = _feedback.commandSequence, timeSeconds = sent.command.simulationTime,
                lengthsMm = (float[])_feedback.cableLengthsMm.Clone(), tensionsN = (float[])_feedback.tensionN.Clone(),
                forceWorldN = fresh ? observationRotation * force : Vector3.zero,
                torqueAtGripWorldNm = fresh ? observationRotation * torque : Vector3.zero,
                forceResidualN = _telemetry.forceResidualN, torqueResidualNm = _telemetry.torqueResidualNm };
            PlatformFeedback = new PlatformDeviceFeedback { simulated = true, valid = fresh && sent.command.platformValid && _feedback.platformExecutionEnabled,
                capabilityLimited = _feedback.capabilityLimited || _feedback.geometryRank < 6,
                status = fresh ? "UDP_ACTUATOR_FORWARD_KINEMATICS_UNMEASURED" : "UDP_FEEDBACK_EXPIRED",
                commandSequence = _feedback.commandSequence, timeSeconds = sent.command.simulationTime,
                eulerDegrees = Vector(_feedback.platformEuler), translationM = Vector(_feedback.platformPosition),
                actuatorLengthsMm = (float[])_feedback.platformLengthsMm.Clone() };
        }

        /// <summary>Forward one execution sample together with its acknowledged
        /// wrench. Dispatch may already have sent a newer, different target.</summary>
        public void ApplyCableObservation(CableDriveTwin cable)
        {
            if (cable == null) return;
            if (CableFeedback == null || !_pending.TryGetValue(CableFeedback.commandSequence, out var sent))
            {
                cable.ApplyControllerObservation(CableFeedback);
                return;
            }
            Quaternion rotation = _telemetry.observationBoatRotation;
            cable.ApplyControllerObservation(CableFeedback, sent.command.commandSequence,
                rotation * Vector(sent.command.requestForce), rotation * Vector(sent.command.requestTorque));
        }

        public ClosedLoopTelemetry CaptureTelemetry() => Telemetry;
        static bool ValidFeedback(ClosedLoopFeedback f, int n)
        {
            if (double.IsNaN(f.commandAgeSeconds) || double.IsInfinity(f.commandAgeSeconds) || f.commandAgeSeconds < 0
                || double.IsNaN(f.commandSourceAgeSeconds) || double.IsInfinity(f.commandSourceAgeSeconds)
                || (f.commandSourceAgeSeconds < 0 && f.commandSourceAgeSeconds != -1)
                || f.commandSourceSampleTick < 0
                || (f.commandExecutionFresh && (!f.valid || f.watchdog || f.commandAgeSeconds > 0.1
                    || f.commandSourceAgeSeconds > 0.1))
                || ((f.cableExecutionEnabled || f.platformExecutionEnabled)
                    && (!f.commandExecutionFresh || f.commandSourceAgeSeconds < 0))) return false;
            if (double.IsNaN(f.controllerTimeSeconds) || double.IsInfinity(f.controllerTimeSeconds) || f.controllerTimeSeconds < 0
                || double.IsNaN(f.controllerWallTimeSeconds) || double.IsInfinity(f.controllerWallTimeSeconds)
                || f.controllerWallTimeSeconds < 0 || f.plantSampleTick < 0 || f.plantDroppedTicks < 0
                || f.plantStepSeconds != 0.005
                || Math.Abs(f.controllerTimeSeconds - f.plantSampleTick * f.plantStepSeconds) > 1e-9
                || f.rawEncoderCounts == null || f.rawEncoderCounts.Length != 6
                || f.countsPerMeter != EncoderCountsPerMeter || f.countsPerDegree != EncoderCountsPerDegree
                || !Finite(f.tensionN, n) || !Finite(f.cableLengthsMm, n) || !Finite(f.forceLocal, 3) || !Finite(f.torqueLocal, 3)
                || !Finite(f.platformPosition, 3) || !Finite(f.platformEuler, 3) || !Finite(f.platformLengthsMm, 6)
                || !Finite(f.platformVelocity, 3) || !Finite(f.platformAngularVelocity, 3)
                || !Finite(f.platformAcceleration, 3) || !Finite(f.platformAngularAcceleration, 3)
                || !Finite(f.platformInducedAcceleration, 3) || !Finite(f.platformInducedAngularAcceleration, 3)
                || !Finite(f.gripPosition, 3) || !Finite(f.gripVelocity, 3) || !Finite(f.actualGripEffortN, 3)
                || !Finite(f.gripAngularVelocity, 3) || !Finite(f.effectiveTensionTargets, n)
                || !SimulationValue.Finite(f.dragTargetScale) || f.dragTargetScale < 0f || f.dragTargetScale > 1f
                || !SimulationValue.Finite(f.platformFitResidualMm)) return false;
            foreach (float t in f.tensionN) if (t < 0 || t > 10000) return false;
            return true;
        }
        static bool Finite(float[] a, int n) { if (a == null || a.Length != n) return false; foreach (float v in a) if (!SimulationValue.Finite(v)) return false; return true; }
        static bool ValidHardwareStopFeedback(ClosedLoopFeedback feedback, ClosedLoopCommand command)
        {
            if (feedback.hardwareStopsEnabled != command.hardwareStopsEnabled) return false;
            if (!command.hardwareStopsEnabled) return !feedback.hardwareStopActive;
            if (!Finite(feedback.hardwareStopGeometry, 8) || !Finite(command.hardwareStopGeometry, 8)
                || !Finite(feedback.hardwareStopReactionForce, 3) || !Finite(feedback.hardwareStopReactionTorque, 3)
                || !SimulationValue.Finite(feedback.hardwareStopProjectionM) || feedback.hardwareStopProjectionM < 0f
                || !SimulationValue.Finite(feedback.hardwareStopProjectionRadians) || feedback.hardwareStopProjectionRadians < 0f)
                return false;
            for (int i = 0; i < 8; i++)
                if (feedback.hardwareStopGeometry[i] != command.hardwareStopGeometry[i]) return false;
            return true;
        }
        static float[] Array(Vector3 v) => new[] { v.x, v.y, v.z };
        static Vector3 Vector(float[] a) => new Vector3(a[0], a[1], a[2]);
        static void Copy(Vector3 v, float[] a, int p) { a[p] = v.x; a[p + 1] = v.y; a[p + 2] = v.z; }

        void SendPacket(ClosedLoopCommand command)
        {
            if (!_connected || _udp == null) return;
            byte[] data = Pack(_sessionId, command.commandSequence, JsonUtility.ToJson(command));
            if (command.kind == "step")
            {
                // Serialization can itself be interrupted. Check again next to
                // the send; the receiver separately enforces its own source clock.
                _telemetry.commandSourceFresh = FreshAt(Now, out _telemetry.commandSourceReceivedAgeSeconds,
                    out _telemetry.commandSourceAckAgeSeconds) && _telemetry.encoderConsumed;
                if (!_telemetry.commandSourceFresh)
                {
                    command.cableValid = command.platformValid = false;
                    command.tensionTargets = new float[command.tensionTargets.Length];
                    command.requestForce = command.requestTorque = command.desiredGripVelocity = command.desiredGripAngularVelocity = new float[3];
                    command.dragFeedbackEnabled = false; command.dragDissipationW = 0f;
                    command.sourceGripVelocity = command.sourceGripAngularVelocity = new float[3];
                    data = Pack(_sessionId, command.commandSequence, JsonUtility.ToJson(command));
                }
                if (_pending.TryGetValue(command.commandSequence, out var sent)) sent.at = Now;
            }
            _udp.Send(data, data.Length); _telemetry.sentPackets++;
            if (command.kind == "step")
            {
                _telemetry.commandSent = true;
                _telemetry.commandPacketBase64 = Convert.ToBase64String(data);
                if (_pending.TryGetValue(command.commandSequence, out var sent)) sent.packet = _telemetry.commandPacketBase64;
            }
        }
        public static byte[] Pack(ulong sessionId, uint sequence, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            if (payload.Length > 60000) throw new InvalidDataException("Controller packet too large");
            var data = new byte[28 + payload.Length];
            Encoding.ASCII.GetBytes("DBCL").CopyTo(data, 0);
            BitConverter.GetBytes(1u).CopyTo(data, 4); BitConverter.GetBytes(sequence).CopyTo(data, 8);
            BitConverter.GetBytes(sessionId).CopyTo(data, 12); BitConverter.GetBytes((uint)payload.Length).CopyTo(data, 20);
            payload.CopyTo(data, 24); BitConverter.GetBytes(Crc32(data, data.Length - 4)).CopyTo(data, data.Length - 4);
            return data;
        }
        public static bool TryUnpack(byte[] data, ulong sessionId, out uint sequence, out string json)
        {
            sequence = 0; json = null;
            if (data == null || data.Length < 28 || data.Length > 60028 || Encoding.ASCII.GetString(data, 0, 4) != "DBCL"
                || BitConverter.ToUInt32(data, 4) != 1 || BitConverter.ToUInt64(data, 12) != sessionId
                || BitConverter.ToUInt32(data, 20) != data.Length - 28
                || BitConverter.ToUInt32(data, data.Length - 4) != Crc32(data, data.Length - 4)) return false;
            sequence = BitConverter.ToUInt32(data, 8);
            json = new UTF8Encoding(false, true).GetString(data, 24, data.Length - 28);
            return true;
        }
        static byte[] ReceivedEncoderPacket(uint sequence, ClosedLoopFeedback f)
        {
            if (string.IsNullOrEmpty(f.encoderPacketBase64))
                throw new InvalidDataException("Worker feedback omitted its encoder packet.");
            byte[] data = Convert.FromBase64String(f.encoderPacketBase64);
            if (data.Length != 52 || BitConverter.ToUInt32(data, 0) != PaddleEncoderPipeline.Magic
                || BitConverter.ToUInt32(data, 4) != PaddleEncoderPipeline.ProtocolVersion
                || BitConverter.ToUInt32(data, 8) != sequence
                || BitConverter.ToUInt32(data, 48) != Crc32(data, 48)
                || f.controllerTimeSeconds > ulong.MaxValue / 1e6
                || BitConverter.ToUInt64(data, 12) != (ulong)(f.controllerTimeSeconds * 1e6))
                throw new InvalidDataException("Received encoder packet identity, timestamp or CRC rejected.");
            for (int i = 0; i < 6; i++)
                if (BitConverter.ToInt32(data, 20 + i * 4) != f.rawEncoderCounts[i])
                    throw new InvalidDataException("Encoder counts differ from the associated feedback observation.");
            float effort = BitConverter.ToSingle(data, 44), expected = Vector(f.actualGripEffortN).magnitude;
            if (!SimulationValue.Finite(effort) || effort < 0 || Mathf.Abs(effort - expected) > 0.00001f + expected * 0.00001f)
                throw new InvalidDataException("Encoder force differs from the associated feedback observation.");
            return data;
        }
        public static uint Crc32(byte[] bytes, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < count; i++) { crc ^= bytes[i]; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
            return ~crc;
        }
        void OnDestroy() { StopController(); }
        void OnApplicationQuit()
        {
            LifecycleTrace.Mark("controller OnApplicationQuit begin");
            StopController();
            LifecycleTrace.Mark("controller OnApplicationQuit end");
        }
    }
}
