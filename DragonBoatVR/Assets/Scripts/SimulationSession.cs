using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DragonBoatVR
{
    [Serializable]
    public class PaddlerConfiguration
    {
        public string id = "paddler-1";
        public bool leftSide;
        public float strokesPerMinute = 45;
        public float phaseOffset01;
    }

    [Serializable]
    public class SessionConfiguration
    {
        public string provenance = "ASSUMED_SOFTWARE_FIXTURE: replace with measured data";
        public bool measured;
        public float equivalentBoatMassKg = 150;
        public float inputTimeoutSeconds = 0.10f;
        // One user and one real paddle. Keep the array shape for existing single-user recordings.
        public PaddlerConfiguration[] paddlers = { new PaddlerConfiguration() };
    }

    [Serializable]
    public class SimulationFrame
    {
        public int schemaVersion = 5;
        public float fixedStepSeconds;
        public string configurationSignature;
        public SessionConfiguration sessionConfiguration;
        public string recordType = "frame";
        public string error;
        public double timeSeconds;
        public PaddleSample[] paddles;
        public Vector3 boatPosition;
        public Quaternion boatRotation;
        public Vector3 boatVelocity;
        public Vector3 boatAngularVelocity;
        public bool hasSimulatedPaddlerLoad;
        public Vector3 paddlerCenterShiftLocalM;
        public Vector3 paddlerWeightShiftTorqueWorldNm;
        public float waveTimeSeconds;
        public Vector3[] rawWaterForceN;
        public Vector3[] boatPropulsionForceN;
        public ForceFeedbackRequest[] feedbackRequests;
        public Vector3[] filteredFeedbackForceN;
        public CableDeviceFeedback cableFeedback;
        public PlatformMotionRequest platformRequest;
        public PlatformDeviceFeedback platformFeedback;
        public BoatBuoyancyReplayState boatReplayState;
        public bool hasPlatformReplayState;
        public bool hasCableReplayState;
        public StewartPlatformReplayState platformReplayState;
        public CableDriveReplayState cableReplayState;
        public bool hasAcquisitionSnapshot;
        public AcquisitionSnapshot acquisitionSnapshot;
        public AcquisitionProfile acquisitionProfile;
        public PaddleEncoderTelemetry[] encoderTelemetry;
        public DeviceProtocolTelemetry deviceProtocol;
        public ClosedLoopTelemetry closedLoop;
        // Input/hydrodynamic candidates may already have affected PhysX when a device
        // backend conflict is detected. Preserve that history and mark dispatch separately.
        public bool deviceDispatchRejected;
        public string deviceDispatchRejection;
    }

    /// <summary>Desktop software chain. One fixed-step input/water/hydro phase, followed by
    /// post-physics device/record phase. The device models implement replaceable paired interfaces.</summary>
    [DefaultExecutionOrder(-1000)]
    public class SimulationSession : MonoBehaviour
    {
        public BoatBuoyancy boat;
        public WaveField wave;
        public PaddleDriver[] drivers;
        public PaddleHydrodynamics[] hydrodynamics;
        public CableDriveTwin cable;
        public StewartPlatformTwin platform;
        public HardwareUdpBridge bridge;
        public MotionTelemetry motionTelemetry;
        public SensorAcquisitionBridge acquisition;
        public PaddleEncoderPipeline[] encoderPipelines;
        public SoftwareDeviceProtocolTwin softwareDeviceProtocol;
        public UdpClosedLoopClient closedLoop;
        public RiverEnvironmentController riverEnvironment;
        public MonoBehaviour forceDeviceAdapter;
        public MonoBehaviour platformDeviceAdapter;
        public SessionConfiguration configuration = new SessionConfiguration();
        public string recordingDirectory = "tools/recordings";
        public bool showDebugOverlay = true;
        // Consumers own their copy. GUI/adapters cannot alter the frame written to disk or
        // another subscriber's observation by mutating a public serializable object.
        public SimulationFrame LastFrame => SimulationSnapshot.Copy(_lastFrame);
        SimulationFrame _lastFrame;
        public event Action<SimulationFrame> FrameCompleted;
        public event Action TimelineReset;
        public string Status { get; private set; } = "Software models; geometry assumed";
        public double SimulationTime { get; private set; }
        public Vector3 InitialBoatPosition => _initialPosition;
        public Quaternion InitialBoatRotation => _initialRotation;
        public float InitialWaveTime => _initialWaveTime;
        public Vector3 InitialPaddlePosition(int index) => _initialPaddlePositions[index];
        public Quaternion InitialPaddleRotation(int index) => _initialPaddleRotations[index];
        public bool ExperienceActive { get; private set; } = true;
        public bool ExperienceEnded => _initialized && !ExperienceActive;
        public bool IsReplaying => _replay != null;
        public bool IsRecording => _writer != null;
        public string RecordingError { get; private set; }
        public long RecordingQueuedBytes => (_writer as BackgroundRecordingWriter)?.QueuedBytes ?? 0;
        public long LastRecordingWrittenFrames { get; private set; }
        public double LastRecordingMaximumWriteSeconds { get; private set; }
        public string ReplayError { get; private set; }
        public string ReplayIntegrityStatus { get; private set; } = "NO_REPLAY";
        public string CurrentRecordingPath { get; private set; }
        public string LatestRecordingPath { get; private set; }
        public string RecordingIndexError { get; private set; }
        public string RecordingDirectoryPath => Path.GetFullPath(Path.IsPathRooted(recordingDirectory)
            ? recordingDirectory : Path.Combine(Application.dataPath, "../..", recordingDirectory));
        public string RecordingIndexPath => Path.Combine(RecordingDirectoryPath, "latest-recording.json");
        public string ReplayRecordingPath => LatestRecordingPath ?? ReadLatestRecordingPath();
        public string RawAcquisitionProfileStatus { get; private set; } = "原始采集配置尚未载入";
        StreamWriter _writer;
        int _recordedFrameCount;
        double _lastRecordedTime;
        bool _automaticRecording;
        [Serializable] sealed class RecordingIndex
        {
            public int schemaVersion;
            public string fileName;
        }
        List<SimulationFrame> _replay;
        int _replayIndex;
        Vector3 _initialPosition, _initialVelocity, _initialAngularVelocity;
        Quaternion _initialRotation;
        bool _initialIsKinematic;
        float _initialWaveTime;
        BoatBuoyancyReplayState _initialBoatState;
        Vector3[] _initialPaddlePositions;
        Quaternion[] _initialPaddleRotations;
        PaddleDriver.Mode[] _modesBeforeExperienceEnd;
        SimulationFrame _pendingReplay;
        AcquisitionSnapshot _replayAcquisition;
        AcquisitionProfile _replayAcquisitionProfile;
        string _replayLiveAcquisitionSignature;
        bool _resumeAcquisitionListener;
        bool _initialized;
        float _stepSeconds;
        string _configurationSignature;
        bool _deviceDispatchRejected;
        string _deviceDispatchRejection;
        SimulatedPaddlerLoad _paddlerLoad;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            AttachToScene();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) { AttachToScene(); }
        static void AttachToScene()
        {
            if (FindObjectOfType<SimulationSession>() != null) return;
            var buoy = FindObjectOfType<BoatBuoyancy>();
            if (buoy != null) new GameObject("SimulationSession").AddComponent<SimulationSession>();
        }
        void Start() { Initialize(true); }

        public void Initialize(bool loadConfiguration)
        {
            if (_initialized) return;
            boat = boat != null ? boat : FindObjectOfType<BoatBuoyancy>();
            _paddlerLoad = boat != null ? boat.GetComponent<SimulatedPaddlerLoad>() : null;
            if (_paddlerLoad != null && !_paddlerLoad.ValidOffset(Vector3.zero))
                throw new InvalidOperationException("Invalid assumed occupant load configuration.");
            wave = wave != null ? wave : FindObjectOfType<WaveField>();
            cable = cable != null ? cable : FindObjectOfType<CableDriveTwin>();
            platform = platform != null ? platform : FindObjectOfType<StewartPlatformTwin>();
            bridge = bridge != null ? bridge : FindObjectOfType<HardwareUdpBridge>();
            motionTelemetry = motionTelemetry != null ? motionTelemetry : FindObjectOfType<MotionTelemetry>();
            acquisition = acquisition != null ? acquisition : FindObjectOfType<SensorAcquisitionBridge>();
            if (acquisition == null) acquisition = gameObject.AddComponent<SensorAcquisitionBridge>();
            softwareDeviceProtocol = softwareDeviceProtocol != null ? softwareDeviceProtocol : GetComponent<SoftwareDeviceProtocolTwin>();
            if (softwareDeviceProtocol == null) softwareDeviceProtocol = gameObject.AddComponent<SoftwareDeviceProtocolTwin>();
            acquisition.loadProfileOnStart = false;
            acquisition.externallyStepped = true;
            var acquisitionProfile = acquisition.CurrentProfile;
            var acquisitionPath = Path.Combine(Application.streamingAssetsPath, "acquisition_profile.json");
            if (loadConfiguration && File.Exists(acquisitionPath))
                acquisitionProfile = JsonUtility.FromJson<AcquisitionProfile>(File.ReadAllText(acquisitionPath));
            acquisition.Configure(acquisitionProfile);
            if (acquisitionProfile.enableLoopbackUdp) acquisition.StartLoopback();
            if (drivers == null || drivers.Length == 0) drivers = FindObjectsOfType<PaddleDriver>();
            if (boat == null || wave == null || drivers.Length == 0)
                throw new InvalidOperationException("SimulationSession requires boat, wave and paddle inputs.");
            if (loadConfiguration)
            {
                string path = Path.Combine(Application.streamingAssetsPath, "paddlers.json");
                if (File.Exists(path)) configuration = JsonUtility.FromJson<SessionConfiguration>(File.ReadAllText(path));
            }
            if (configuration == null || !SimulationValue.Finite(configuration.equivalentBoatMassKg)
                || configuration.equivalentBoatMassKg <= 0 || configuration.paddlers == null
                || configuration.paddlers.Length != 1
                || !SimulationValue.Finite(configuration.inputTimeoutSeconds) || configuration.inputTimeoutSeconds <= 0)
                throw new InvalidOperationException("Session requires exactly one paddler, a positive boat mass and a positive input timeout.");
            // A spare scene input must not silently propel the single-user boat.
            var activeDriver = drivers[0];
            for (int i = 1; i < drivers.Length; i++)
            {
                drivers[i].gameObject.SetActive(false);
            }
            drivers = new[] { activeDriver };
            PaddleGeometryConfiguration paddleGeometry = null;
            string paddleGeometryPath = Path.Combine(Application.streamingAssetsPath, "paddle_geometry.json");
            if (loadConfiguration && File.Exists(paddleGeometryPath))
            {
                paddleGeometry = JsonUtility.FromJson<PaddleGeometryConfiguration>(File.ReadAllText(paddleGeometryPath));
                if (paddleGeometry == null) throw new InvalidOperationException("Missing paddle geometry.");
                paddleGeometry.Validate();
            }
            hydrodynamics = new PaddleHydrodynamics[drivers.Length];
            encoderPipelines = new PaddleEncoderPipeline[drivers.Length];
            for (int i = 0; i < drivers.Length; i++)
            {
                var driver = drivers[i];
                driver.boat = boat;
                driver.externallyStepped = true;
                driver.inputTimeoutSeconds = configuration.inputTimeoutSeconds;
                if (i < configuration.paddlers.Length)
                {
                    var cfg = configuration.paddlers[i];
                    if (cfg == null || string.IsNullOrWhiteSpace(cfg.id) || !SimulationValue.Finite(cfg.strokesPerMinute)
                        || cfg.strokesPerMinute < 0 || !SimulationValue.Finite(cfg.phaseOffset01))
                        throw new InvalidOperationException("Invalid paddler configuration.");
                    driver.leftSide = cfg.leftSide;
                    driver.strokesPerMinute = cfg.strokesPerMinute;
                    driver.SetPhase01(cfg.phaseOffset01);
                }
                hydrodynamics[i] = driver.GetComponent<PaddleHydrodynamics>();
                if (hydrodynamics[i] == null) throw new InvalidOperationException("Paddle input has no hydrodynamics.");
                hydrodynamics[i].targetBoat = boat;
                hydrodynamics[i].input = driver;
                hydrodynamics[i].requireValidInput = true;
                hydrodynamics[i].externallyStepped = true;
                if (paddleGeometry != null)
                {
                    driver.shaftToBlade = paddleGeometry.referenceToBladeCentreM;
                    driver.sensorToReferencePosition = paddleGeometry.sensorToReferencePositionM;
                    driver.sensorToReferenceEulerDeg = paddleGeometry.sensorToReferenceEulerDeg;
                    hydrodynamics[i].bladeArea = paddleGeometry.bladeAreaM2;
                    hydrodynamics[i].bladeLength = paddleGeometry.bladeLengthM;
                    hydrodynamics[i].bladeThickness = paddleGeometry.bladeThicknessM;
                }
                hydrodynamics[i].SynchronizeVisualGeometry();
                encoderPipelines[i] = driver.GetComponent<PaddleEncoderPipeline>();
                if (encoderPipelines[i] == null) encoderPipelines[i] = driver.gameObject.AddComponent<PaddleEncoderPipeline>();
                encoderPipelines[i].Configure(driver, boat);
            }
            boat.GetComponent<Rigidbody>().mass = configuration.equivalentBoatMassKg;
            // Install contact geometry before the first physical step. The
            // equivalent mass, centre and inertia remain independently defined.
            if (Application.isPlaying) BoatHullCollisionShape.Install(boat);
            wave.externallyStepped = true;
            if (bridge != null) bridge.externallyStepped = true;
            if (cable != null)
            {
                cable.externallyStepped = true;
                cable.paddle = hydrodynamics[0];
                cable.shaftToBlade = drivers[0].shaftToBlade;
                var rigPath = Path.Combine(Application.streamingAssetsPath, "tendon_rig.json");
                if (loadConfiguration && TendonRigProfiles.CadDesignRequested)
                    cable.ApplyRig(TendonRigProfiles.LoadCadDesign());
                else if (loadConfiguration && File.Exists(rigPath))
                    cable.ApplyRig(JsonUtility.FromJson<TendonRigConfig>(File.ReadAllText(rigPath)));
            }
            if (platform != null)
            {
                platform.externallyStepped = true;
                var path = Path.Combine(Application.streamingAssetsPath, "platform_rig.json");
                if (loadConfiguration && File.Exists(path)
                    && !platform.ApplyConfiguration(JsonUtility.FromJson<PlatformRigConfig>(File.ReadAllText(path))))
                    throw new InvalidOperationException("Invalid platform configuration.");
            }
            if (motionTelemetry != null) motionTelemetry.externallyStepped = true;
            if (Application.isPlaying)
            {
                riverEnvironment = riverEnvironment != null ? riverEnvironment : GetComponent<RiverEnvironmentController>();
                if (riverEnvironment == null) riverEnvironment = gameObject.AddComponent<RiverEnvironmentController>();
                riverEnvironment.Bind(this);
            }
            var rb = boat.GetComponent<Rigidbody>();
            _initialPosition = rb.position; _initialRotation = rb.rotation;
            _initialVelocity = rb.velocity; _initialAngularVelocity = rb.angularVelocity;
            _initialIsKinematic = rb.isKinematic;
            _initialWaveTime = wave.SimTime;
            _initialBoatState = boat.CaptureReplayState();
            _initialPaddlePositions = new Vector3[drivers.Length];
            _initialPaddleRotations = new Quaternion[drivers.Length];
            for (int i = 0; i < drivers.Length; i++)
            {
                _initialPaddlePositions[i] = drivers[i].transform.position;
                _initialPaddleRotations[i] = drivers[i].transform.rotation;
            }
            ExperienceActive = true;
            _configurationSignature = ConfigurationSignature();
            _initialized = true;
            boat.KinematicsOwnedBySession = true;
            // The production desktop ride uses an independent actuator/encoder process.
            // Existing analytical fixtures explicitly select their prescribed input mode.
            string closedLoopPath = Path.Combine(Application.streamingAssetsPath, "closed_loop.json");
            if (loadConfiguration && Application.isPlaying
                && Array.IndexOf(Environment.GetCommandLineArgs(), "-kinematic-fixture") < 0)
            {
                closedLoop = gameObject.AddComponent<UdpClosedLoopClient>();
                try
                {
                    if (!File.Exists(closedLoopPath)) throw new InvalidDataException("Required closed_loop.json is missing.");
                    var config = JsonUtility.FromJson<ClosedLoopConfiguration>(File.ReadAllText(closedLoopPath));
                    if (config == null) throw new InvalidDataException("closed_loop.json did not contain a configuration object.");
                    closedLoop.Initialize(this, config);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                { closedLoop.RejectStartup(this, error.Message); }
            }
            _configurationSignature = ConfigurationSignature();
            if (Application.isPlaying)
            {
                var waterEffects = GetComponent<WaterInteractionVisuals>();
                if (waterEffects == null) waterEffects = gameObject.AddComponent<WaterInteractionVisuals>();
                waterEffects.Bind(this);
            }
            if (Camera.main != null && Camera.main.GetComponent<HeadPoseAttachment>() == null)
            {
                var attachment = Camera.main.gameObject.AddComponent<HeadPoseAttachment>();
                attachment.virtualBoat = boat.transform;
            }
            if (Application.isPlaying && GetComponent<SimulationPostStep>() == null)
                gameObject.AddComponent<SimulationPostStep>().session = this;
        }

        void FixedUpdate() { if (_initialized && ExperienceActive) StepInputs(Time.fixedDeltaTime); }
        public void StepInputs(float dt)
        {
            if (!ExperienceActive) return;
            if (!SimulationValue.Finite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
            _stepSeconds = dt;
            if (IsReplaying)
            {
                string liveSignature = ConfigurationSignature();
                string rejection = liveSignature != _configurationSignature
                    ? "Physical configuration changed during replay; input stopped."
                    : acquisition != null && acquisition.ConfigurationSignature != _replayLiveAcquisitionSignature
                        ? "Live acquisition profile changed during replay; input stopped."
                    : acquisition != null && acquisition.LocalPort != 0
                        ? "Live acquisition listener started during replay; input stopped."
                    : Mathf.Abs(dt - _replay[0].fixedStepSeconds) > 1e-6f
                        ? "Replay requires the recorded fixed timestep; input stopped." : null;
                foreach (var driver in drivers)
                    if (driver.mode != PaddleDriver.Mode.Replay)
                        rejection = "Paddle input mode changed during replay; input stopped.";
                if (rejection != null)
                {
                    EndReplay(PaddleDriver.Mode.External);
                    _configurationSignature = ConfigurationSignature();
                    ReplayError = rejection;
                    Status = "Replay rejected: " + rejection;
                    Debug.LogWarning("[SimulationSession] " + Status);
                }
            }
            SimulationTime += dt;
            // Environment transitions share the simulation clock. During replay the
            // recorded environment configuration is held fixed for deterministic input.
            if (_replay == null && riverEnvironment != null) riverEnvironment.Step(dt);
            // Observe incoming ADC packets on the physics timeline, separately from paddle input.
            if (acquisition != null && !IsReplaying) acquisition.PollLoopback();
            if (_replay != null)
            {
                _pendingReplay = null;
                while (_replayIndex < _replay.Count && _replay[_replayIndex].timeSeconds <= SimulationTime + 1e-6)
                    _pendingReplay = _replay[_replayIndex++];
                if (_pendingReplay != null)
                {
                    _replayAcquisition = AcquisitionSnapshot.ForReplay(_pendingReplay.acquisitionSnapshot);
                    if (_pendingReplay.paddles.Length != drivers.Length) throw new InvalidDataException("Paddler count mismatch.");
                    wave.SetTime(_pendingReplay.waveTimeSeconds);
                    for (int i = 0; i < drivers.Length; i++)
                    {
                        drivers[i].RestoreReplayFrame(_pendingReplay.paddles[i], _pendingReplay.timeSeconds);
                    }
                }
                else
                {
                    _replayAcquisition = AcquisitionSnapshot.ForReplay(_replayAcquisition, true);
                    wave.Step(dt);
                }
            }
            else wave.Step(dt);
            for (int i = 0; i < drivers.Length; i++)
            {
                closedLoop?.SynchronizeInputMode(IsReplaying ? PaddleDriver.Mode.Replay : drivers[i].mode);
                drivers[i].SetExecutionPoseOwner(closedLoop != null && closedLoop.Active
                    && !IsReplaying && drivers[i].mode == PaddleDriver.Mode.VirtualPaddler);
                drivers[i].Tick(dt);
                if (closedLoop != null && closedLoop.Active && !IsReplaying && drivers[i].mode == PaddleDriver.Mode.VirtualPaddler)
                {
                    // Script state is intent only. Physics receives the measured packet
                    // from the independent compliant grip and motor plant.
                    drivers[i].ApplyDecodedSample(closedLoop.ObservePaddle(drivers[i].Sample, encoderPipelines[i], dt));
                }
                else if (!IsReplaying && encoderPipelines != null && i < encoderPipelines.Length && encoderPipelines[i] != null)
                {
                    closedLoop?.PrepareLocalEncoderInput(encoderPipelines[i]);
                    var decoded = encoderPipelines[i].Process(drivers[i].Sample, SimulationTime, dt);
                    drivers[i].ApplyDecodedSample(decoded);
                }
                hydrodynamics[i].Step(dt);
            }
            if (_paddlerLoad != null)
            {
                if (IsReplaying)
                    _paddlerLoad.Apply(boat, _pendingReplay != null
                        ? _pendingReplay.paddlerCenterShiftLocalM : Vector3.zero);
                else _paddlerLoad.Step(boat, drivers[0], SimulationTime);
            }
        }

        // Called by the post-physics companion once for every fixed tick, including catch-up ticks.
        public void StepDevices(float dt)
        {
            if (!_initialized || !ExperienceActive) return;
            boat.CaptureCompletedState(dt);
            // A mode switch between the input and device boundaries must also revoke the
            // old mechanical session before it can send a target or replace frame feedback.
            // Synchronize an explicit enabled=false here as well, before adapter dispatch.
            if (closedLoop != null)
                closedLoop.SynchronizeInputMode(IsReplaying ? PaddleDriver.Mode.Replay : drivers[0].mode);
            _deviceDispatchRejected = closedLoop != null && closedLoop.RejectDeviceAdapterConflict();
            _deviceDispatchRejection = _deviceDispatchRejected ? closedLoop.Telemetry.status : null;
            if (_deviceDispatchRejected)
            {
                Status = _deviceDispatchRejection;
                // Revoke device output without rewriting the sample or water force that
                // was already consumed by this physics step. The frame records both.
                var rejectedForce = hydrodynamics[0].FeedbackRequest;
                rejectedForce.valid = false;
                if (cable != null) cable.Receive(rejectedForce, dt);
                if (platform != null)
                {
                    var rejectedPlatform = platform.Request;
                    rejectedPlatform.valid = false;
                    rejectedPlatform.timeSeconds = SimulationTime;
                    platform.Receive(rejectedPlatform, dt);
                }
            }
            softwareDeviceProtocol?.BeginFrame();
            if (!_deviceDispatchRejected && forceDeviceAdapter != null)
            {
                if (!(forceDeviceAdapter is IForceFeedbackDevice device)) throw new InvalidOperationException("Invalid force-device adapter.");
                device.Receive(hydrodynamics[0].FeedbackRequest, dt);
            }
            else if (!_deviceDispatchRejected && cable != null && softwareDeviceProtocol != null)
            {
                if (closedLoop != null && closedLoop.Active && drivers[0].ExecutionFeedbackOwnsPose)
                {
                    var sample = drivers[0].Sample;
                    softwareDeviceProtocol.DispatchCableAtPose(cable, hydrodynamics[0].FeedbackRequest, dt,
                        Matrix4x4.TRS(closedLoop.ObservationBoatPosition, closedLoop.ObservationBoatRotation, Vector3.one),
                        sample.bladePosition, sample.bladeRotation);
                }
                else softwareDeviceProtocol.DispatchCable(cable, hydrodynamics[0].FeedbackRequest, dt);
            }
            PlatformMotionRequest platformRequest = default;
            if (!_deviceDispatchRejected && platform != null) platformRequest = platform.CreateRequest(dt);
            if (!_deviceDispatchRejected && platform != null && closedLoop != null && closedLoop.Active)
            {
                platform.PrepareControllerRequest(platformRequest, dt);
                if (softwareDeviceProtocol != null)
                    softwareDeviceProtocol.Telemetry.platformStatus = "INDEPENDENT_UDP_CONTROLLER_OWNS_EXECUTION";
            }
            else if (!_deviceDispatchRejected && platformDeviceAdapter != null)
            {
                if (!(platformDeviceAdapter is IPlatformMotionDevice device)) throw new InvalidOperationException("Invalid platform-device adapter.");
                if (platform == null) throw new InvalidOperationException("Platform request mapper missing.");
                platform.Receive(platformRequest, dt);
                var accepted = platform.Request;
                accepted.valid &= platform.Feedback != null && platform.Feedback.valid;
                device.Receive(accepted, dt);
            }
            else if (!_deviceDispatchRejected && platform != null && softwareDeviceProtocol != null)
                softwareDeviceProtocol.DispatchPlatform(platform, platformRequest, dt);
            if (closedLoop != null && closedLoop.Active)
            {
                if (!_deviceDispatchRejected)
                    closedLoop.Dispatch(hydrodynamics[0].FeedbackRequest, platform != null ? platform.Request : default);
                if (cable != null) closedLoop.ApplyCableObservation(cable);
                if (platform != null) platform.ApplyControllerObservation(closedLoop.PlatformFeedback);
            }
            if (!_deviceDispatchRejected && bridge != null) bridge.Pump(dt);
            if (!_deviceDispatchRejected && motionTelemetry != null) motionTelemetry.Pump(dt);
            _lastFrame = SimulationSnapshot.Copy(CaptureFrame());
            if (_writer != null)
            {
                try
                {
                    if (_configurationSignature != ConfigurationSignature())
                    {
                        RecordingError = "Physical configuration changed during recording; start a new recording.";
                        _writer.WriteLine(JsonUtility.ToJson(new SimulationFrame { recordType = "invalidated",
                            timeSeconds = SimulationTime, error = RecordingError }));
                        StopRecording();
                        Status = "Recording invalidated: " + RecordingError;
                        Debug.LogWarning("[SimulationSession] " + Status);
                    }
                    else
                    {
                        _writer.WriteLine(JsonUtility.ToJson(_lastFrame)); _writer.Flush(); _recordedFrameCount++;
                        _lastRecordedTime = SimulationTime;
                    }
                }
                catch (Exception error) when (RecordingIoFailure(error))
                {
                    RecordingError = "Recording write failed: " + error.Message;
                    StopRecording();
                    Status = RecordingError;
                    Debug.LogWarning("[SimulationSession] " + Status);
                }
            }
            if (_replay != null && _replayIndex >= _replay.Count)
            {
                // Stay in replay mode after EOF; freshness gate removes the final held sample's force.
                Status = ReplayIntegrityStatus == "LEGACY_COMPLETENESS_UNVERIFIED"
                    ? "旧录制回放结束：完整性未验证；输入过期，船体惯性和波浪继续"
                    : "Replay finished; input expires, boat coasts, waves continue";
            }
            if (FrameCompleted != null)
                foreach (Action<SimulationFrame> consumer in FrameCompleted.GetInvocationList())
                    consumer(SimulationSnapshot.Copy(_lastFrame));
        }

        public SimulationFrame CaptureFrame(bool includeReplayState = false)
        {
            var rb = boat.GetComponent<Rigidbody>();
            var frame = new SimulationFrame { timeSeconds = SimulationTime, waveTimeSeconds = wave.SimTime,
                fixedStepSeconds = _stepSeconds > 0 ? _stepSeconds : Time.fixedDeltaTime,
                configurationSignature = _configurationSignature,
                deviceDispatchRejected = _deviceDispatchRejected, deviceDispatchRejection = _deviceDispatchRejection,
                boatPosition = rb.position, boatRotation = rb.rotation,
                boatVelocity = boat.GetComponent<Rigidbody>().velocity,
                boatAngularVelocity = boat.GetComponent<Rigidbody>().angularVelocity,
                hasSimulatedPaddlerLoad = _paddlerLoad != null && _paddlerLoad.enabled,
                paddlerCenterShiftLocalM = _paddlerLoad != null ? _paddlerLoad.CenterShiftLocalM : Vector3.zero,
                paddlerWeightShiftTorqueWorldNm = _paddlerLoad != null ? _paddlerLoad.TorqueWorldNm : Vector3.zero,
                paddles = new PaddleSample[drivers.Length], rawWaterForceN = new Vector3[drivers.Length],
                boatPropulsionForceN = new Vector3[drivers.Length], feedbackRequests = new ForceFeedbackRequest[drivers.Length],
                filteredFeedbackForceN = new Vector3[drivers.Length],
                cableFeedback = closedLoop != null && closedLoop.Active ? closedLoop.CableFeedback
                    : forceDeviceAdapter is IForceFeedbackDevice fd ? fd.Feedback : softwareDeviceProtocol?.CableFeedback,
                platformFeedback = closedLoop != null && closedLoop.Active ? closedLoop.PlatformFeedback
                    : platformDeviceAdapter is IPlatformMotionDevice pd ? pd.Feedback : softwareDeviceProtocol?.PlatformFeedback,
                platformRequest = platform != null ? platform.Request : default,
                encoderTelemetry = new PaddleEncoderTelemetry[drivers.Length],
                deviceProtocol = softwareDeviceProtocol != null ? softwareDeviceProtocol.Telemetry : null };
            frame.closedLoop = closedLoop != null ? closedLoop.CaptureTelemetry() : null;
            frame.hasAcquisitionSnapshot = acquisition != null;
            frame.acquisitionSnapshot = IsReplaying ? AcquisitionSnapshot.ForReplay(_replayAcquisition)
                : (acquisition != null ? acquisition.Snapshot() : null);
            for (int i = 0; i < drivers.Length; i++)
            {
                frame.paddles[i] = drivers[i].Sample;
                frame.rawWaterForceN[i] = hydrodynamics[i].RawWaterForceN;
                frame.boatPropulsionForceN[i] = hydrodynamics[i].BoatPropulsionForceN;
                frame.feedbackRequests[i] = hydrodynamics[i].FeedbackRequest;
                frame.filteredFeedbackForceN[i] = hydrodynamics[i].WaterForceN;
                frame.encoderTelemetry[i] = encoderPipelines != null && i < encoderPipelines.Length && encoderPipelines[i] != null
                    ? encoderPipelines[i].Telemetry : null;
            }
            if (includeReplayState)
            {
                frame.boatReplayState = boat.CaptureReplayState();
                frame.hasPlatformReplayState = platform != null;
                frame.hasCableReplayState = cable != null;
                frame.platformReplayState = platform != null ? platform.CaptureReplayState() : null;
                frame.cableReplayState = cable != null ? cable.CaptureReplayState() : null;
            }
            return frame;
        }

        string ConfigurationSignature(AcquisitionProfile recordedAcquisition = null)
        {
            // 回放使用同一物理/几何配置。
            var signatureProfile = recordedAcquisition ?? (IsReplaying ? _replayAcquisitionProfile : null);
            string data = JsonUtility.ToJson(configuration)
                + (_paddlerLoad != null ? ParameterSignature(_paddlerLoad, "enabled", "provenance", "measured", "maximumCenterShiftM") : "")
                + ParameterSignature(boat.GetComponent<BoatModelChoice>(), "modelId", "collisionResource", "collisionSourceResource", "sourceObjAsset")
                + (closedLoop != null ? JsonUtility.ToJson(closedLoop.configuration) : "kinematic-fixture")
                + (acquisition != null ? (signatureProfile != null ? JsonUtility.ToJson(signatureProfile)
                    : acquisition.ConfigurationSignature) : "no-acquisition")
                + ParameterSignature(wave, "waves", "intensity", "backgroundCurrentMps", "timeScale", "horizontalDisplacement", "inverseIterations", "BaseHeight")
                + ParameterValue(Physics.gravity)
                + ParameterSignature(boat, "points", "centerOfMassOffset", "maxSubmergenceDepth", "totalBuoyancyMultiplier",
                    "verticalWaterDamping", "forwardDragCoeff", "sidewaysDragCoeff", "yawAngularDragCoeff",
                    "applyWaveDrift", "wetAreaPerPoint", "hullDragCoefficient", "fallbackWaterY")
                + ParameterSignature(boat.GetComponent<Rigidbody>(), "mass", "drag", "angularDrag", "useGravity",
                    "constraints", "inertiaTensor", "inertiaTensorRotation")
                + ParameterSignature(cable, "rig", "shaftToBlade", "anchorLocal", "maxTensionN", "preTensionN", "sensorNoiseN")
                + ParameterSignature(platform, "useExplicitJointGeometry", "explicitBaseJointsLocal", "explicitTopJointsLocal",
                    "baseRadius", "topRadius", "neutralHeight", "minActuatorLengthsMm", "maxActuatorLengthsMm",
                    "maxHeaveMeters", "maxTranslationMeters", "maxTiltDegrees", "maxYawDegrees",
                    "accelToTranslation", "accelToYaw", "servoLagTimeConstant");
            foreach (var hydro in hydrodynamics) data += ParameterSignature(hydro, "bladeArea", "bladeLength", "bladeThickness",
                "cdFlat", "cdEdge", "waterDensity", "applyPropulsion", "propulsionScale", "propulsionVerticalDamp",
                "balanceYawRoll", "balanceStrength", "maxFeedbackForceN", "maxFeedbackTorqueNm", "smoothing", "applyForceToRigidbody");
            foreach (var driver in drivers)
            {
                data += ParameterSignature(driver, "leftSide", "pivotLocalPos",
                    "strokesPerMinute", "sweepAmplitude", "bladeDepth", "bladeAirHeight", "sideOffset", "shaftToBlade",
                    "inboardShaftLeanDeg", "upperGripTravelFraction",
                    "inputTimeoutSeconds", "smoothing", "paddleInBoatSpace", "applyCalibration",
                    "sensorToReferencePosition", "sensorToReferenceEulerDeg");
                if (driver.applyCalibration) data += driver.calibrator != null
                    ? driver.calibrator.ConfigurationSignature : "missing-registration";
            }
            return Hash128.Compute(data).ToString();
        }

        void ValidateRecordedPaddlerLoad(SimulationFrame frame)
        {
            bool active = _paddlerLoad != null && _paddlerLoad.enabled;
            if (frame.hasSimulatedPaddlerLoad != active
                || !SimulationValue.Finite(frame.paddlerCenterShiftLocalM)
                || !SimulationValue.Finite(frame.paddlerWeightShiftTorqueWorldNm)
                || active && !_paddlerLoad.ValidOffset(frame.paddlerCenterShiftLocalM)
                || !active && (frame.paddlerCenterShiftLocalM != Vector3.zero || frame.paddlerWeightShiftTorqueWorldNm != Vector3.zero))
                throw new InvalidDataException("Recorded occupant load differs from the configured model.");
        }

        static string ParameterSignature(object source, params string[] names)
        {
            if (source == null) return "absent";
            string result = source.GetType().Name;
            foreach (string name in names)
            {
                var field = source.GetType().GetField(name);
                var property = source.GetType().GetProperty(name);
                var value = field != null ? field.GetValue(source) : property?.GetValue(source);
                // BuoyancyPoint 的 lastDepth/lastWet 是运行结果，不能写入配置身份。
                if (value is BoatBuoyancy.BuoyancyPoint[] probes)
                    foreach (var probe in probes) result += probe.localOffset.ToString("R");
                else if (value is Array array)
                    foreach (var element in array) result += ParameterValue(element);
                else result += ParameterValue(value);
                result += ";";
            }
            return result;
        }
        static string ParameterValue(object value)
        {
            if (value == null) return "null";
            if (value is Vector3 vector) return vector.ToString("R");
            if (value is Quaternion rotation) return rotation.ToString("R");
            if (value is IFormattable formattable) return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            if (value.GetType().IsPrimitive || value is string) return value.ToString();
            return JsonUtility.ToJson(value);
        }

        public void BeginRecording(string path)
        {
            if (IsReplaying) throw new InvalidOperationException("Stop replay before recording.");
            StopRecording();
            RecordingError = null;
            RecordingIndexError = null;
            LastRecordingWrittenFrames = 0;
            LastRecordingMaximumWriteSeconds = 0;
            string signature = ConfigurationSignature();
            var header = CaptureFrame(includeReplayState: true); header.recordType = "header";
            header.configurationSignature = signature;
            header.sessionConfiguration = configuration;
            header.acquisitionProfile = acquisition != null ? acquisition.CurrentProfile : null;
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            // Do not publish the writer until its header has been flushed. CreateNew protects
            // both named user recordings and a collision after the unique path was selected.
            FileStream stream = null;
            StreamWriter candidate = null;
            try
            {
                stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                candidate = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                string headerJson = JsonUtility.ToJson(header);
                candidate.WriteLine(headerJson); candidate.Flush();
                // Transfer ownership only after the background worker has started. Each
                // physical frame enqueues an immutable line without waiting for disk.
                _writer = new BackgroundRecordingWriter(candidate, headerJson: headerJson);
                candidate = null; stream = null;
                _configurationSignature = signature;
                CurrentRecordingPath = fullPath;
                _recordedFrameCount = 0;
                _lastRecordedTime = header.timeSeconds;
                _automaticRecording = false;
                Status = "Recording: " + fullPath;
            }
            finally
            {
                if (candidate != null) candidate.Dispose();
                else stream?.Dispose();
            }
        }
        public void StopRecording()
        {
            var writer = _writer;
            string path = CurrentRecordingPath;
            int queuedFrames = _recordedFrameCount;
            bool complete = _recordedFrameCount > 0 && string.IsNullOrEmpty(RecordingError);
            bool publishIndex = _automaticRecording;
            bool completionRequested = false;
            _writer = null; CurrentRecordingPath = null; _recordedFrameCount = 0;
            _automaticRecording = false;
            if (writer == null) return;
            try
            {
                if (complete && writer is BackgroundRecordingWriter recording)
                {
                    recording.CompleteRecording(queuedFrames, _lastRecordedTime, _configurationSignature);
                    completionRequested = true;
                }
                writer.Dispose();
            }
            catch (Exception error) when (RecordingIoFailure(error))
            {
                // A completion request may see a previously failed worker. Still release
                // its owned sink; failure must never advance the complete-recording index.
                if (!completionRequested)
                    try { writer.Dispose(); } catch (Exception closeError) when (RecordingIoFailure(closeError)) { }
                RecordingError = "Recording close failed: " + error.Message;
                Status = RecordingError;
                Debug.LogWarning("[SimulationSession] " + Status);
                return;
            }
            if (writer is BackgroundRecordingWriter background)
            {
                LastRecordingWrittenFrames = background.WrittenLines;
                LastRecordingMaximumWriteSeconds = background.MaximumWriteSeconds;
                if (complete && LastRecordingWrittenFrames != queuedFrames)
                {
                    RecordingError = "Recording frame count differs after draining; recording is incomplete.";
                    Status = RecordingError;
                    Debug.LogWarning("[SimulationSession] " + Status);
                    return;
                }
            }
            // An empty, invalidated or failed recording must not hide the previous usable one.
            if (complete)
            {
                LatestRecordingPath = path;
                if (publishIndex) PublishLatestRecording(path);
            }
        }
        static bool RecordingIoFailure(Exception error) => error is IOException
            || error is UnauthorizedAccessException || error is ObjectDisposedException;

        string ReadLatestRecordingPath()
        {
            string indexPath = RecordingIndexPath;
            if (!File.Exists(indexPath)) return Path.Combine(RecordingDirectoryPath, "latest.jsonl");
            if (new FileInfo(indexPath).Length > 4096) throw new InvalidDataException("Recording index is too large.");
            var index = JsonUtility.FromJson<RecordingIndex>(File.ReadAllText(indexPath));
            if (index == null || index.schemaVersion != 1 || string.IsNullOrWhiteSpace(index.fileName)
                || index.fileName != Path.GetFileName(index.fileName)
                || !index.fileName.StartsWith("recording-", StringComparison.Ordinal)
                || !index.fileName.EndsWith(".jsonl", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid latest recording index.");
            string path = Path.Combine(RecordingDirectoryPath, index.fileName);
            if (!File.Exists(path)) throw new FileNotFoundException("Latest recording file is missing.", path);
            return path;
        }

        void PublishLatestRecording(string path)
        {
            string temporary = null;
            try
            {
                string directory = Path.GetDirectoryName(path);
                string indexPath = Path.Combine(directory, "latest-recording.json");
                temporary = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(JsonUtility.ToJson(new RecordingIndex { schemaVersion = 1, fileName = Path.GetFileName(path) }));
                    writer.Flush();
                    stream.Flush(true);
                }
                // Both paths are in the recording directory. Never delete the old index first:
                // failure to replace it leaves the previous complete recording discoverable.
                if (File.Exists(indexPath)) File.Replace(temporary, indexPath, null);
                else File.Move(temporary, indexPath);
                temporary = null;
                RecordingIndexError = null;
            }
            catch (Exception error) when (RecordingIoFailure(error) || error is NotSupportedException || error is ArgumentException)
            {
                RecordingIndexError = error.Message;
                Status = "Recording saved: " + path + "; latest index unchanged: " + RecordingIndexError;
                Debug.LogWarning("[SimulationSession] " + Status);
            }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); }
                    catch (Exception error) when (RecordingIoFailure(error)) { }
            }
        }
        public void BeginReplay(string path)
        {
            if (closedLoop != null && closedLoop.Active)
                throw new InvalidOperationException("Live mechanical controller replay is not supported; use the explicit kinematic diagnostic fixture.");
            StopRecording();
            var frames = new List<SimulationFrame>(); SimulationFrame header = null;
            string headerJson = null;
            RecordingCompletion completion = null;
            double lastTime = double.NegativeInfinity;
            double lastIntegrityTime = double.NegativeInfinity;
            string contentSha256;
            using (var contentHash = new RecordingContentHash())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), false))
            {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (completion != null) throw new InvalidDataException("Data follows the recording completion marker.");
                if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Empty recording line.");
                AcquisitionJsonEvidence.ValidateFrameJson(line);
                var frame = JsonUtility.FromJson<SimulationFrame>(line);
                if (frame.recordType == "complete")
                {
                    if (header == null || header.schemaVersion != 5)
                        throw new InvalidDataException("Unexpected recording completion marker.");
                    completion = JsonUtility.FromJson<RecordingCompletion>(line);
                    completion.lastFrameTimeSeconds = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(line, true);
                    continue;
                }
                contentHash.Append(line);
                if (frame == null || double.IsNaN(frame.timeSeconds) || double.IsInfinity(frame.timeSeconds))
                    throw new InvalidDataException("Invalid recording time.");
                if (frame.recordType == "invalidated")
                    throw new InvalidDataException("Recording invalidated: " + frame.error);
                if (frame.recordType == "header" && header == null && frames.Count == 0)
                { header = frame; headerJson = line; lastTime = frame.timeSeconds; continue; }
                if (header == null) throw new InvalidDataException("Recording must start with its header.");
                if (frame.recordType != "frame" || frame.timeSeconds <= lastTime || frame.paddles == null)
                    throw new InvalidDataException("Invalid or unordered recording.");
                lastTime = frame.timeSeconds;
                lastIntegrityTime = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(line);
                frames.Add(frame);
            }
                contentSha256 = contentHash.FinishHex();
            }
            if (header == null || frames.Count == 0 || header.paddles == null || header.paddles.Length != drivers.Length)
                throw new InvalidDataException("Recording header/frames/paddler configuration mismatch.");
            if (header.schemaVersion == 5 && (completion == null || completion.frameCount != frames.Count
                || completion.lastFrameTimeSeconds != lastIntegrityTime || completion.configurationSignature != header.configurationSignature
                || completion.contentSha256 != contentSha256))
                throw new InvalidDataException(completion == null ? "Recording completion marker is missing."
                    : "Recording completion mismatch: count=" + completion.frameCount + "/" + frames.Count
                    + "; time=" + completion.lastFrameTimeSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + lastIntegrityTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "; timeBits=" + BitConverter.DoubleToInt64Bits(completion.lastFrameTimeSeconds)
                    + "/" + BitConverter.DoubleToInt64Bits(lastIntegrityTime)
                    + "; configurationEqual=" + (completion.configurationSignature == header.configurationSignature)
                    + "; hashEqual=" + (completion.contentSha256 == contentSha256));
            if (header.hasAcquisitionSnapshot != (acquisition != null))
                throw new InvalidDataException("Recording acquisition presence differs from the current session.");
            if (acquisition != null)
            {
                SensorAcquisitionBridge.ValidateRecordedSnapshot(header.acquisitionSnapshot, header.acquisitionProfile);
                // A raw recording owns only its historical observation identity. It must
                // never configure/authorize a live listener or replace calibration.
                if (JsonUtility.ToJson(header.acquisitionProfile) != acquisition.ConfigurationSignature)
                {
                    AcquisitionJsonEvidence.ValidateRawRecordingProfileJson(headerJson);
                    ValidateRawAcquisitionProfile(header.acquisitionProfile);
                }
            }
            if ((header.schemaVersion != 4 && header.schemaVersion != 5)
                || header.configurationSignature != ConfigurationSignature(header.acquisitionProfile)
                || !SimulationValue.Finite(header.fixedStepSeconds) || header.fixedStepSeconds <= 0)
                throw new InvalidDataException("Recording version or configuration differs from the current session.");
            if (header.filteredFeedbackForceN == null || header.filteredFeedbackForceN.Length != drivers.Length
                || header.feedbackRequests == null || header.feedbackRequests.Length != drivers.Length)
                throw new InvalidDataException("Missing initial feedback state.");
            foreach (var force in header.filteredFeedbackForceN)
                if (!SimulationValue.Finite(force)) throw new InvalidDataException("Invalid initial filtered feedback state.");
            ValidateRecordedPaddlerLoad(header);
            double origin = header.timeSeconds;
            double previousFrameTime = 0;
            AcquisitionSnapshot previousAcquisition = header.acquisitionSnapshot;
            foreach (var f in frames)
            {
                ValidateRecordedPaddlerLoad(f);
                if (f.hasAcquisitionSnapshot != header.hasAcquisitionSnapshot)
                    throw new InvalidDataException("Recording acquisition presence differs.");
                if (acquisition != null) SensorAcquisitionBridge.ValidateRecordedSnapshot(f.acquisitionSnapshot, header.acquisitionProfile);
                if (acquisition != null) AcquisitionJsonEvidence.ValidateTransition(previousAcquisition, f.acquisitionSnapshot);
                previousAcquisition = f.acquisitionSnapshot;
                f.timeSeconds -= origin;
                if (f.timeSeconds <= 0 || f.paddles.Length != drivers.Length
                    || f.schemaVersion != header.schemaVersion || f.configurationSignature != header.configurationSignature
                    || !SimulationValue.Finite(f.fixedStepSeconds) || !SimulationValue.Finite(f.waveTimeSeconds)
                    || Mathf.Abs(f.fixedStepSeconds - header.fixedStepSeconds) > 1e-6f
                    || Math.Abs(f.timeSeconds - previousFrameTime - header.fixedStepSeconds) > 1e-6)
                    throw new InvalidDataException("Invalid replay frame or timestep.");
                previousFrameTime = f.timeSeconds;
                for (int i = 0; i < f.paddles.Length; i++)
                {
                    var s = f.paddles[i]; s.timeSeconds -= origin;
                    if (!SimulationValue.Finite(s.bladePosition) || !SimulationValue.Finite(s.bladeRotation)
                        || !SimulationValue.Finite(s.bladeVelocity) || !SimulationValue.Finite(s.bladeAngularVelocity)
                        || !SimulationValue.Finite(s.gripForceN)
                        || double.IsNaN(s.timeSeconds) || double.IsInfinity(s.timeSeconds) || s.timeSeconds > f.timeSeconds + 1e-6
                        || !Enum.IsDefined(typeof(PaddleInputKind), s.source) || s.source == PaddleInputKind.Replay)
                        throw new InvalidDataException("Non-finite replay input.");
                    f.paddles[i] = s;
                }
            }
            if (!SimulationValue.Finite(header.boatPosition) || !SimulationValue.Finite(header.boatRotation)
                || !SimulationValue.Finite(header.boatVelocity) || !SimulationValue.Finite(header.boatAngularVelocity)
                || !SimulationValue.Finite(header.waveTimeSeconds))
                throw new InvalidDataException("Invalid initial boat state.");
            // JsonUtility may materialize an empty serializable class for a null field.
            // Explicit header flags describe component presence; each present component's
            // validator still rejects missing, empty or malformed model state.
            if ((platform != null) != header.hasPlatformReplayState
                || (cable != null) != header.hasCableReplayState)
                throw new InvalidDataException("Recording device model state differs from the current session.");
            boat.ValidateReplayState(header.boatReplayState);
            if (platform != null) platform.ValidateReplayState(header.platformReplayState);
            if (cable != null) cable.ValidateReplayState(header.cableReplayState);
            bool resumeListener = IsReplaying ? _resumeAcquisitionListener
                : acquisition != null && acquisition.LocalPort != 0;
            if (acquisition != null) acquisition.StopLoopback();
            _replayAcquisitionProfile = header.acquisitionProfile;
            _replayLiveAcquisitionSignature = acquisition != null ? acquisition.ConfigurationSignature : null;
            _resumeAcquisitionListener = resumeListener;
            var rb = boat.GetComponent<Rigidbody>();
            rb.position = header.boatPosition; rb.rotation = header.boatRotation;
            rb.velocity = header.boatVelocity; rb.angularVelocity = header.boatAngularVelocity;
            Physics.SyncTransforms();
            SimulationTime = 0; _replay = frames; _replayIndex = 0;
            _configurationSignature = header.configurationSignature;
            ReplayError = null;
            ReplayIntegrityStatus = header.schemaVersion == 5 ? "COMPLETE_CONTENT_VERIFIED"
                : "LEGACY_COMPLETENESS_UNVERIFIED";
            _pendingReplay = null; _lastFrame = null;
            _replayAcquisition = AcquisitionSnapshot.ForReplay(header.acquisitionSnapshot);
            foreach (var driver in drivers) { driver.ResetInputTimeline(); driver.mode = PaddleDriver.Mode.Replay; }
            for (int i = 0; i < hydrodynamics.Length; i++)
            {
                hydrodynamics[i].ResetDynamics();
                hydrodynamics[i].RestoreFeedbackState(header.filteredFeedbackForceN[i], header.feedbackRequests[i], header.paddles[i].bladePosition);
            }
            boat.RestoreReplayState(header.boatReplayState);
            if (platform != null) platform.RestoreReplayState(header.platformReplayState);
            if (cable != null) cable.RestoreReplayState(header.cableReplayState, origin);
            SynchronizeDeviceProtocolSequenceWindows();
            wave.SetTime(header.waveTimeSeconds);
            Status = header.schemaVersion == 5 ? "完整录制已核验；回放输入驱动原水动力链路"
                : "旧录制回放：缺少完成标记，完整性未验证";
            TimelineReset?.Invoke();
        }
        public void StopReplay()
        {
            EndReplay(PaddleDriver.Mode.VirtualPaddler);
            ReplayError = null;
            Status = "Synthetic input";
        }
        void EndReplay(PaddleDriver.Mode nextMode)
        {
            bool wasReplaying = IsReplaying;
            _replay = null;
            _pendingReplay = null;
            _replayAcquisition = null;
            _replayAcquisitionProfile = null;
            _replayLiveAcquisitionSignature = null;
            ReplayIntegrityStatus = "NO_REPLAY";
            _lastFrame = null;
            if (wasReplaying && acquisition != null)
            {
                acquisition.StopLoopback();
                acquisition.ClearObservations();
                if (wasReplaying && _resumeAcquisitionListener && acquisition.CurrentProfile.enableLoopbackUdp)
                {
                    try { acquisition.StartLoopback(); }
                    catch (System.Net.Sockets.SocketException error)
                    {
                        RawAcquisitionProfileStatus = "实时采集监听未恢复：" + error.SocketErrorCode;
                        Debug.LogWarning("[SimulationSession] " + RawAcquisitionProfileStatus);
                    }
                }
            }
            _resumeAcquisitionListener = false;
            foreach (var driver in drivers) driver.SetMode(nextMode);
            SynchronizeDeviceProtocolSequenceWindows();
            TimelineReset?.Invoke();
        }

        void SynchronizeDeviceProtocolSequenceWindows()
        {
            if (softwareDeviceProtocol == null) return;
            uint cableSequence = hydrodynamics != null && hydrodynamics.Length > 0
                ? hydrodynamics[0].CommandSequence : 0u;
            uint platformSequence = platform != null ? platform.CommandSequence : 0u;
            softwareDeviceProtocol.SynchronizeSequenceWindows(cableSequence, platformSequence);
        }
        // User controls report a refusal rather than throwing from Unity's Update callback.
        // BeginRecording retains its strict programmatic preconditions.
        public bool ToggleRecording(string path = null)
        {
            if (IsReplaying)
            {
                Status = "Stop replay before recording (F4 / F7).";
                return false;
            }
            try
            {
                if (IsRecording)
                {
                    StopRecording();
                    if (!string.IsNullOrEmpty(RecordingError)) return false;
                    if (string.IsNullOrEmpty(RecordingIndexError)) Status = "Recording stopped";
                    return true;
                }
                BeginRecording(path ?? NewRecordingPath());
                _automaticRecording = path == null;
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            { Status = "Recording rejected: " + error.Message; return false; }
        }
        public bool ReplayLatestRecording()
        {
            try
            {
                StopRecording();
                if (!string.IsNullOrEmpty(RecordingError))
                    Status = "Recording stopped with an error; replaying the previous complete recording.";
                BeginReplay(ReplayRecordingPath);
                return true;
            }
            catch (Exception error) { Status = "Replay rejected: " + error.Message; return false; }
        }

        /// <summary>End the current ride from the PC operator panel or F10.</summary>
        public bool EndExperience()
        {
            if (!_initialized) return false;
            if (!ExperienceActive) return true;
            _paddlerLoad?.Clear();

            _modesBeforeExperienceEnd = new PaddleDriver.Mode[drivers.Length];
            for (int i = 0; i < drivers.Length; i++)
                _modesBeforeExperienceEnd[i] = drivers[i].mode == PaddleDriver.Mode.Replay
                    ? PaddleDriver.Mode.VirtualPaddler : drivers[i].mode;
            if (IsReplaying) StopReplay();
            if (IsRecording) StopRecording();

            foreach (var driver in drivers)
            {
                driver.ResetInputTimeline();
                driver.SetMode(PaddleDriver.Mode.External);
            }
            foreach (var hydro in hydrodynamics) hydro.ResetDynamics();
            if (cable != null) cable.ResetModel();
            if (platform != null) platform.ResetModel();
            SynchronizeDeviceProtocolSequenceWindows();

            var rb = boat.GetComponent<Rigidbody>();
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            ExperienceActive = false;
            if (closedLoop != null) closedLoop.StopController();
            _lastFrame = null;
            Status = "体验已结束；按 F11 重新定位";
            TimelineReset?.Invoke();
            return true;
        }

        /// <summary>Return the software scene to its initial position and resume the ride.</summary>
        public bool RepositionExperience()
        {
            if (!_initialized) return false;
            _paddlerLoad?.Clear();
            if (ExperienceActive)
            {
                _modesBeforeExperienceEnd = new PaddleDriver.Mode[drivers.Length];
                for (int i = 0; i < drivers.Length; i++)
                    _modesBeforeExperienceEnd[i] = drivers[i].mode == PaddleDriver.Mode.Replay
                        ? PaddleDriver.Mode.VirtualPaddler : drivers[i].mode;
            }
            if (IsReplaying) StopReplay();
            if (IsRecording) StopRecording();

            var rb = boat.GetComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.position = _initialPosition;
            rb.rotation = _initialRotation;
            rb.isKinematic = _initialIsKinematic;
            if (!_initialIsKinematic)
            {
                rb.velocity = _initialVelocity;
                rb.angularVelocity = _initialAngularVelocity;
            }
            Physics.SyncTransforms();
            SimulationTime = 0d;
            _stepSeconds = 0f;
            _lastFrame = null;
            wave.SetTime(_initialWaveTime);
            boat.RestoreReplayState(_initialBoatState);
            foreach (var hydro in hydrodynamics) hydro.ResetDynamics();
            if (cable != null) cable.ResetModel();
            if (platform != null) platform.ResetModel();
            SynchronizeDeviceProtocolSequenceWindows();
            if (acquisition != null) acquisition.ClearObservations();
            var waterEffects = GetComponent<WaterInteractionVisuals>();
            if (waterEffects != null) waterEffects.ClearEffects();
            for (int i = 0; i < drivers.Length; i++)
            {
                drivers[i].transform.SetPositionAndRotation(_initialPaddlePositions[i], _initialPaddleRotations[i]);
                drivers[i].ResetInputTimeline();
                PaddleDriver.Mode mode = _modesBeforeExperienceEnd != null && i < _modesBeforeExperienceEnd.Length
                    ? _modesBeforeExperienceEnd[i] : PaddleDriver.Mode.VirtualPaddler;
                drivers[i].SetMode(mode);
            }
            ExperienceActive = true;
            Status = "已重新定位；体验已开始";
            if (encoderPipelines != null) foreach (var pipeline in encoderPipelines) pipeline?.ResetPipeline();
            if (closedLoop != null) closedLoop.StartController();
            TimelineReset?.Invoke();
            return true;
        }

        void Update()
        {
            if (!_initialized) return;
            if (Input.GetKeyDown(KeyCode.F10)) EndExperience();
            if (Input.GetKeyDown(KeyCode.F11)) RepositionExperience();
            if (!ExperienceActive) return;
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (IsReplaying) Status = "Stop replay before changing stroke rate (F4 / F7).";
                else foreach (var driver in drivers)
                    driver.strokesPerMinute = driver.strokesPerMinute > 0 ? 0 : 45;
            }
        }
        public static string DefaultRecordingPath() => Path.GetFullPath(Path.Combine(Application.dataPath, "../../tools/recordings/latest.jsonl"));
        string NewRecordingPath() => Path.Combine(RecordingDirectoryPath,
            "recording-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + ".jsonl");

        public bool LoadRawAcquisitionProfile(string path)
        {
            if (!_initialized || acquisition == null || IsRecording || IsReplaying)
            {
                RawAcquisitionProfileStatus = "当前输入阶段占用中，暂不能载入原始采集配置";
                return false;
            }
            try
            {
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择采集配置 JSON 路径");
                string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path
                    : Path.Combine(Application.dataPath, "../..", path));
                if (new FileInfo(fullPath).Length > 256 * 1024)
                    throw new InvalidDataException("采集配置超过 256 KiB");
                string json = File.ReadAllText(fullPath, new System.Text.UTF8Encoding(false, true));
                AcquisitionJsonEvidence.ValidateRawProfileJson(json);
                var profile = JsonUtility.FromJson<AcquisitionProfile>(json);
                ValidateRawAcquisitionProfile(profile);
                acquisition.ConfigureLoopback(profile);
                _lastFrame = null;
                RawAcquisitionProfileStatus = "原始采集已载入：" + profile.sourceId
                    + " · 127.0.0.1:" + acquisition.LocalPort + " · 100 ms";
                return true;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException || error is System.Net.Sockets.SocketException
                || error is System.Text.DecoderFallbackException || error is NotSupportedException)
            {
                RawAcquisitionProfileStatus = "配置未载入：" + error.Message;
                return false;
            }
        }
        static void ValidateRawAcquisitionProfile(AcquisitionProfile profile)
        {
            SensorAcquisitionBridge.ValidateProfile(profile);
            if (!profile.enableLoopbackUdp || profile.loopbackPort <= 0)
                throw new InvalidDataException("配置必须明确指定本机监听端口");
            foreach (var channel in profile.channels)
                if (channel.mapping != "Unassigned" || Math.Abs(channel.maximumAgeSeconds - 0.1) > 1e-9
                    || channel.rawRangeConfigured || channel.calibration == null
                    || channel.calibration.mode != AcquisitionCalibrationMode.None
                    || channel.calibration.measured || channel.calibration.forceRangeConfigured
                    || channel.calibration.scaleNPerRaw != 0
                    || channel.calibration.points != null && channel.calibration.points.Length != 0)
                    throw new InvalidDataException("此入口仅接收未映射、未标定的原始采集，过期时间为 100 ms");
        }
        void OnGUI()
        {
            if (!_initialized || !showDebugOverlay || !RideHud.DiagnosticsVisible || UnityEngine.XR.XRSettings.enabled) return;
            GUI.Box(new Rect(350, 10, 560, 82), "Software chain / desktop preview");
            GUI.Label(new Rect(360, 32, 540, 22), "F1 metrics   F8 toggle paddling   F9 diagnostics");
            GUI.Label(new Rect(360, 54, 540, 28), "F10 结束体验   F11 重新定位并开始 · " + Status);
        }
        void OnDestroy()
        {
            if (closedLoop != null) closedLoop.StopController();
            StopRecording();
            if (boat != null) boat.KinematicsOwnedBySession = false;
            if (wave != null) wave.externallyStepped = false;
            foreach (var d in drivers ?? new PaddleDriver[0]) if (d != null) d.externallyStepped = false;
            foreach (var h in hydrodynamics ?? new PaddleHydrodynamics[0]) if (h != null) h.externallyStepped = false;
            if (cable != null) cable.externallyStepped = false;
            if (platform != null) platform.externallyStepped = false;
            if (motionTelemetry != null) motionTelemetry.externallyStepped = false;
            if (bridge != null) bridge.externallyStepped = false;
            if (acquisition != null) acquisition.externallyStepped = false;
        }
    }
}
