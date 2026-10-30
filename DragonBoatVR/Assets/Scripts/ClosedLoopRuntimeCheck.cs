using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR
{
    /// <summary>Observe the default scene and its external UDP plant in the real player loop.
    /// Only public experience controls and command interruption are exercised; no manual stepping.</summary>
    public sealed class ClosedLoopRuntimeCheck : MonoBehaviour
    {
        public bool Completed { get; private set; }
        public bool Passed { get; private set; }
        public string artifactsDirectory;

        enum Stage { Starting, Normal, Disconnected, Recovery, Stopped, Restarted, Finished }
        [Serializable] public sealed class CheckResult { public string name; public bool passed; public string evidence; }
        [Serializable] public sealed class FocusObservation
        {
            public double realTimeSeconds;
            public bool isFocused, initialState;
            public string stage;
        }
        [Serializable] public sealed class ProcessEvidence
        {
            public string stage, sessionId, processName;
            public int hostProcessId, controllerProcessId;
            public bool observedAlive;
        }
        [Serializable] public sealed class VisualColliderObservation
        {
            public string name, owner;
            public bool enabled, active, collidesWithBoatLayer, overlapsHull;
            public float penetrationM;
            public Vector3 separationDirection, worldPosition;
            public int checkedHullColliders;
        }
        [Serializable] public sealed class NativeHullObservation
        {
            public string name, kind;
            public int instanceId, vertices, triangles;
            public bool convex, sameBody;
            public Vector3 boundsSize;
        }
        [Serializable] public sealed class StartupVisualObservation
        {
            public int unityFrameCount;
            public double fixedTimeSeconds;
            public bool automaticPhysics;
            public Vector3 boatPosition, boatVelocity;
            public List<NativeHullObservation> hulls = new List<NativeHullObservation>();
            public List<VisualColliderObservation> colliders = new List<VisualColliderObservation>();
        }
        [Serializable] public sealed class Observation
        {
            public string recordType = "frame", stage;
            public double realTimeSeconds, fixedTimeSeconds;
            public int unityFrameCount;
            public int waterEntries, waterExits;
            public bool physicsAutomatic, experienceActive, bodyKinematic, lastFrameCleared, controllerAlive;
            public float wettedFraction, driverPhase01;
            public Vector3 bodyPosition, bodyVelocity, rawForceN, filteredForceN;
            public Quaternion bodyRotation;
            public float waveTimeSeconds;
            public PaddleSample paddle;
            public ForceFeedbackRequest request;
            public ClosedLoopTelemetry telemetry;
            public SimulationFrame frame;
            public EnvironmentRuntimeSweep.Sample environment;
        }
        [Serializable] public sealed class Report
        {
            public int schemaVersion = 6;
            public EnvironmentRuntimeSweep.Report environmentSweep;
            public BoatCollisionMeshSource hullSource;
            public List<StartupVisualObservation> startupVisualGeometry = new List<StartupVisualObservation>();
            public string result = "FAIL", startedUtc, completedUtc;
            public string scope = "UNMEASURED_SOFTWARE_CLOSED_LOOP: normal scene, independent localhost UDP process; no physical hardware acceptance";
            public string execution = "FrameCompleted after normal FixedUpdate/PhysX; ordinary Update controls; no manual Tick, Physics.Simulate or Camera.Render";
            public string executionTarget;
            public bool runInBackgroundAtStart, isFocusedAtStart;
            public int hostProcessId, observedFrames, normalFrames, normalValidFrames, actualWaterEntries;
            public int rawEncoderFrames, motorFeedbackFrames, platformFeedbackFrames, loadedDeviationFrames;
            public int disconnectedExpiredFrames, watchdogZeroMotorFrames, recoveredValidFrames, restartedValidFrames;
            public int stopObservations, renderCallbacks, identityFailures, expiryViolations, stopViolations;
            public int commandPackets, commandSourceExpiredPackets, commandSourceViolations;
            public int executionRenderObservations, executionRenderViolations;
            public int presentationMainFrames, presentationTwinFrames, presentationViolations;
            public int interactionObservations, interactionViolations, interactionNormalEntries;
            public int interactionPaddlePaintEvents, interactionWakePaintEvents;
            public float maxPaddleAudioRms;
            public int motionAudioViolations, normalWoodCreaks;
            public float maxStrokeAudioRms, maxWoodAudioRms;
            public string audioCapture = "listener_mix.wav", audioCaptureSha256;
            public int audioSampleFrames, audioChannels, audioSampleRate, audioClippedSamples;
            public float audioPeak;
            public float maxExecutionRenderPositionErrorM, maxExecutionRenderRotationErrorDeg;
            public int intentContinuityObservations, intentContinuityViolations, intentPausedObservations;
            public float intentStrokesPerMinute, maxIntentPhaseStep;
            public string intentClock = "controller_sample";
            public int restartEntries;
            public float maxPoseDeviationM, maxForceN, maxPlatformFitResidualMm, inputTtlSeconds, feedbackTtlSeconds;
            public float countsPerMeter, countsPerDegree;
            public bool normalPhysics, restartResetImmediate, newControllerSession, sourceFramesComplete;
            public string initialSessionId, restartedSessionId, observationsSha256, screenshot = "normal_closed_loop.png";
            public int initialControllerPid, restartedControllerPid;
            public long queuedLines, writtenLines;
            public double normalSimulationSeconds, normalWallSeconds;
            public Vector3 initialBoatPosition;
            public Quaternion initialBoatRotation;
            public float initialWaveTime;
            public PlatformRigConfig platformGeometry;
            public List<CheckResult> checks = new List<CheckResult>();
            public List<string> errors = new List<string>();
            public List<ProcessEvidence> processes = new List<ProcessEvidence>();
            public List<FocusObservation> focusEvents = new List<FocusObservation>();
        }

        [Serializable] public sealed class RenderObservation
        {
            public string recordType = "render", stage;
            public string cameraName;
            public int unityFrameCount, cameraId, cameraClearFlags, reflectionSourceCameraId, reflectionFrame;
            public bool presentsToScreen, skyboxAvailable;
            public bool interactionAvailable;
            public bool waterShaderParametersAvailable;
            public float waterShaderTime, waterShaderIntensity;
            public Vector2 waterShaderCurrentMps;
            public int waterEntries, waterExits, splashSounds, dripSounds, foamEpoch;
            public int foamObservedFrames, foamPaddlePaintEvents, foamWakePaintEvents;
            public double foamSourceTime;
            public float paddleAudioRms;
            public double motionAudioSourceTime;
            public int motionAudioFrames, woodImpacts, woodCreaks;
            public float strokeWaterLevel, hullWaterLevel, strokeAudioRms, woodAudioRms;
            public uint feedbackSequence;
            public Vector3 actualBladeLocal, expectedBladeLocal;
            public Quaternion actualRotationLocal, expectedRotationLocal;
            public EnvironmentRuntimeSweep.Sample environment;
        }

        [Serializable] public sealed class InteractionResetObservation
        {
            public string recordType = "interaction_reset", stage;
            public double realTimeSeconds;
            public int waterEntries, waterExits, splashSounds, dripSounds, foamEpoch;
        }

        const double OverallTimeoutSeconds = 100;
        const float ZeroTolerance = 1e-5f;
        Stage _stage;
        SimulationSession _session;
        PlanarWaterReflection _planar;
        WaterInteractionVisuals _waterVisuals;
        RiverAudioController _audio;
        RuntimeAudioCapture _audioCapture;
        RuntimeMotionCapture _motionCapture;
        EnvironmentRuntimeSweep _environmentSweep;
        Renderer _waterRenderer;
        MaterialPropertyBlock _waterProperties;
        InteractionFoamTrail _foam;
        InteractionResetObservation _interactionBaseline;
        Rigidbody _body;
        BackgroundRecordingWriter _writer;
        Report _report;
        double _started, _stageStarted, _normalStartSimulation;
        bool _entryArmed, _restartEntryArmed, _screenshotRequested, _finishing;
        int _paddleViewsCaptured;
        string _oldSession;
        int _oldPid;
        float _lastActualY;
        bool _haveActualY;
        string _intentSession;
        float _priorIntentPhase;
        double _priorIntentSampleTime = double.NaN;
        readonly HashSet<string> _loggedLifecycleEvents = new HashSet<string>();

        static double WallTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        public static string RequestedArtifacts()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-closedLoopArtifacts");
            return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            string path = RequestedArtifacts();
            if (path == null || FindObjectOfType<ClosedLoopRuntimeCheck>() != null) return;
            var check = new GameObject("Closed loop runtime acceptance observer").AddComponent<ClosedLoopRuntimeCheck>();
            check.artifactsDirectory = path;
        }

        void Start()
        {
            _started = _stageStarted = WallTime;
            _report = new Report { startedUtc = DateTime.UtcNow.ToString("o"), hostProcessId = Process.GetCurrentProcess().Id,
                executionTarget = Application.isEditor ? "EditorPlay" : "StandalonePlayer",
                runInBackgroundAtStart = Application.runInBackground, isFocusedAtStart = Application.isFocused };
            LogLifecycle("START");
            RecordFocusObservation(Application.isFocused, true);
            try
            {
                artifactsDirectory = Path.GetFullPath(artifactsDirectory ?? RequestedArtifacts());
                Directory.CreateDirectory(artifactsDirectory);
                var stream = new FileStream(Path.Combine(artifactsDirectory, "observations.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _writer = new BackgroundRecordingWriter(new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024) { NewLine = "\n" });
                Application.logMessageReceived += OnLog;
                RenderPipelineManager.endCameraRendering += OnCameraRendered;
            }
            catch (Exception error) { Finish(error.ToString()); }
        }

        void FixedUpdate()
        {
            // Observe the first real physics steps, before deferred Destroy has
            // necessarily removed the colliders bundled with display primitives.
            // This probe does not step, move or disable anything in the scene.
            if (_report == null || _report.startupVisualGeometry.Count >= 4) return;
            var boat = FindObjectOfType<BoatBuoyancy>();
            if (boat == null || boat.BoatRb == null) return;
            var hulls = new List<Collider>();
            foreach (var collider in boat.GetComponentsInChildren<Collider>())
                if (collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger
                    && collider.attachedRigidbody == boat.BoatRb) hulls.Add(collider);
            if (hulls.Count == 0) return;
            var observation = new StartupVisualObservation
            {
                unityFrameCount = Time.frameCount, fixedTimeSeconds = Time.fixedTimeAsDouble,
                automaticPhysics = Physics.simulationMode == SimulationMode.FixedUpdate,
                boatPosition = boat.BoatRb.position, boatVelocity = boat.BoatRb.velocity
            };
            var shape = boat.GetComponent<BoatHullCollisionShape>();
            if (shape != null && shape.Ready) _report.hullSource = shape.Source;
            foreach (var hull in hulls)
            {
                var mesh = hull as MeshCollider;
                observation.hulls.Add(new NativeHullObservation { name = mesh != null ? mesh.sharedMesh.name : hull.name,
                    kind = hull.GetType().Name, instanceId = hull.GetInstanceID(), sameBody = hull.attachedRigidbody == boat.BoatRb,
                    convex = mesh != null && mesh.convex, vertices = mesh != null ? mesh.sharedMesh.vertexCount : 0,
                    triangles = mesh != null ? mesh.sharedMesh.triangles.Length / 3 : 0, boundsSize = hull.bounds.size });
            }
            foreach (var collider in FindObjectsOfType<Collider>(true))
            {
                string owner = collider.GetComponentInParent<CableDriveTwin>() != null ? "cable"
                    : collider.GetComponentInParent<StewartPlatformTwin>() != null ? "platform" : null;
                if (owner == null) continue;
                var item = new VisualColliderObservation
                {
                    name = collider.name, owner = owner, enabled = collider.enabled,
                    active = collider.gameObject.activeInHierarchy,
                    worldPosition = collider.transform.position
                };
                foreach (var hull in hulls)
                {
                    item.collidesWithBoatLayer |= !Physics.GetIgnoreLayerCollision(collider.gameObject.layer, hull.gameObject.layer);
                    if (!item.enabled || !item.active) continue;
                    item.checkedHullColliders++;
                    if (Physics.ComputePenetration(hull, hull.transform.position, hull.transform.rotation,
                        collider, collider.transform.position, collider.transform.rotation, out Vector3 direction, out float distance))
                    {
                        item.overlapsHull = true;
                        if (distance > item.penetrationM) { item.penetrationM = distance; item.separationDirection = direction; }
                    }
                }
                observation.colliders.Add(item);
            }
            _report.startupVisualGeometry.Add(observation);
        }

        void OnApplicationFocus(bool focus) { RecordFocusObservation(focus, false); }

        void RecordFocusObservation(bool focus, bool initialState)
        {
            if (_report == null || Completed) return;
            var observation = new FocusObservation { realTimeSeconds = WallTime, isFocused = focus,
                initialState = initialState, stage = _stage.ToString() };
            _report.focusEvents.Add(observation);
            Debug.Log("[CLOSEDLOOP_FOCUS] focus=" + focus + " timeSeconds="
                + observation.realTimeSeconds.ToString("F6", CultureInfo.InvariantCulture)
                + " stage=" + observation.stage + " initialState=" + initialState);
        }

        void LogLifecycle(string eventName, bool includeWriterStatistics = false)
        {
            if (!_loggedLifecycleEvents.Add(eventName)) return;
            double now = WallTime;
            string statistics = includeWriterStatistics && _writer != null
                ? " queuedLines=" + _report.queuedLines + " writtenLines=" + _writer.WrittenLines
                    + " queuedBytes=" + _writer.QueuedBytes + " writerCompleted=" + _writer.HasCompleted
                    + " maximumWriteSeconds=" + _writer.MaximumWriteSeconds.ToString("F6", CultureInfo.InvariantCulture)
                : "";
            Debug.Log("[CLOSEDLOOP_LIFECYCLE] event=" + eventName + " timeSeconds="
                + now.ToString("F6", CultureInfo.InvariantCulture) + " elapsedSeconds="
                + (now - _started).ToString("F6", CultureInfo.InvariantCulture) + " stage=" + _stage
                + " focus=" + Application.isFocused + " runInBackground=" + Application.runInBackground + statistics);
        }

        void OnCameraRendered(ScriptableRenderContext context, Camera camera)
        {
            if (Completed || _finishing || _report == null || camera == null || camera.cameraType != CameraType.Game
                || !camera.isActiveAndEnabled || camera.targetTexture != null) return;
            _report.renderCallbacks++;
            if (_session == null || !_session.ExperienceActive || _session.closedLoop == null) return;
            var driver = _session.drivers[0];
            var client = _session.closedLoop;
            if (_report.observedFrames == 0 || _intentSession != client.ObservationSessionId) return;
            if (!driver.ExecutionFeedbackOwnsPose || !client.EncoderObservationConsumed || !driver.Sample.valid) return;
            // Observe the transform after LateUpdate and the camera's real render.
            // Expected pose comes from the accepted physics sample, never the script trajectory.
            var inverseObservation = Quaternion.Inverse(client.ObservationBoatRotation);
            var boat = _session.boat.transform;
            if (_planar == null) _planar = FindObjectOfType<PlanarWaterReflection>();
            var sky = camera.GetComponent<Skybox>();
            var observation = new RenderObservation { stage = _stage.ToString(), unityFrameCount = Time.frameCount,
                cameraName = camera.name, cameraId = camera.GetInstanceID(), cameraClearFlags = (int)camera.clearFlags,
                presentsToScreen = camera.isActiveAndEnabled && camera.targetTexture == null,
                skyboxAvailable = sky != null && sky.enabled && sky.material != null || RenderSettings.skybox != null,
                reflectionSourceCameraId = _planar != null ? _planar.LastSourceCameraId : 0,
                reflectionFrame = _planar != null ? _planar.LastRenderedFrame : -1,
                feedbackSequence = client.ObservationSequence,
                actualBladeLocal = boat.InverseTransformPoint(driver.transform.position),
                actualRotationLocal = Quaternion.Inverse(boat.rotation) * driver.transform.rotation,
                expectedBladeLocal = inverseObservation * (driver.Sample.bladePosition - client.ObservationBoatPosition),
                expectedRotationLocal = inverseObservation * driver.Sample.bladeRotation };
            observation.interactionAvailable = _waterVisuals != null && _audio != null && _audio.AudioReady
                && _foam != null && _interactionBaseline != null;
            _report.interactionObservations++;
            if (observation.interactionAvailable)
            {
                observation.waterEntries = _waterVisuals.EntryBursts;
                observation.waterExits = _waterVisuals.ExitEvents;
                observation.splashSounds = _audio.SplashSounds;
                observation.dripSounds = _audio.DripSounds;
                observation.paddleAudioRms = _audio.SamplePaddleOutputRms();
                observation.motionAudioSourceTime = _audio.MotionSourceTime;
                observation.motionAudioFrames = _audio.MotionSourceFrames;
                observation.woodImpacts = _audio.WoodImpacts; observation.woodCreaks = _audio.WoodCreaks;
                observation.strokeWaterLevel = _audio.StrokeWaterLevel; observation.hullWaterLevel = _audio.HullWaterLevel;
                observation.strokeAudioRms = _audio.SampleStrokeOutputRms(); observation.woodAudioRms = _audio.SampleWoodOutputRms();
                if (observation.motionAudioFrames <= 0 || Math.Abs(observation.motionAudioSourceTime - _session.SimulationTime) > 1e-9)
                    _report.motionAudioViolations++;
                observation.foamEpoch = _foam.TimelineEpoch;
                observation.foamSourceTime = _foam.SourceObservationTime;
                observation.foamObservedFrames = _foam.ObservedFrames;
                observation.foamPaddlePaintEvents = _foam.PaddlePaintEvents;
                observation.foamWakePaintEvents = _foam.WakePaintEvents;
                if (observation.splashSounds - _interactionBaseline.splashSounds != observation.waterEntries - _interactionBaseline.waterEntries
                    || observation.dripSounds - _interactionBaseline.dripSounds != observation.waterExits - _interactionBaseline.waterExits
                    || observation.foamEpoch != _interactionBaseline.foamEpoch || observation.foamObservedFrames <= 0
                    || Math.Abs(observation.foamSourceTime - _session.SimulationTime) > 1e-9)
                    _report.interactionViolations++;
                if (_stage == Stage.Normal)
                {
                    _report.interactionNormalEntries = observation.waterEntries - _interactionBaseline.waterEntries;
                    _report.interactionPaddlePaintEvents = observation.foamPaddlePaintEvents;
                    _report.interactionWakePaintEvents = observation.foamWakePaintEvents;
                    _report.maxPaddleAudioRms = Mathf.Max(_report.maxPaddleAudioRms, observation.paddleAudioRms);
                    _report.maxStrokeAudioRms = Mathf.Max(_report.maxStrokeAudioRms, observation.strokeAudioRms);
                    _report.maxWoodAudioRms = Mathf.Max(_report.maxWoodAudioRms, observation.woodAudioRms);
                    _report.normalWoodCreaks = observation.woodCreaks;
                }
            }
            else _report.interactionViolations++;
            float positionError = Vector3.Distance(observation.actualBladeLocal, observation.expectedBladeLocal);
            float angleError = Quaternion.Angle(observation.actualRotationLocal, observation.expectedRotationLocal);
            _report.executionRenderObservations++;
            _report.maxExecutionRenderPositionErrorM = Mathf.Max(_report.maxExecutionRenderPositionErrorM, positionError);
            _report.maxExecutionRenderRotationErrorDeg = Mathf.Max(_report.maxExecutionRenderRotationErrorDeg, angleError);
            if (!SimulationValue.Finite(positionError) || !SimulationValue.Finite(angleError)
                || positionError > 0.0001f || angleError > 0.05f) _report.executionRenderViolations++;
            if (_stage == Stage.Normal)
            {
                if (camera.name == "MainCamera") _report.presentationMainFrames++;
                else if (camera.name == "TwinCamera") _report.presentationTwinFrames++;
                else _report.presentationViolations++;
                if (camera.clearFlags != CameraClearFlags.Skybox || !observation.skyboxAvailable
                    || observation.reflectionSourceCameraId != observation.cameraId
                    || observation.reflectionFrame != observation.unityFrameCount) _report.presentationViolations++;
            }
            if (_environmentSweep != null) observation.environment = _environmentSweep.Observe();
            observation.waterShaderParametersAvailable = _waterRenderer != null && _waterProperties != null;
            if (observation.waterShaderParametersAvailable)
            {
                _waterRenderer.GetPropertyBlock(_waterProperties);
                observation.waterShaderTime = _waterProperties.GetFloat("_SimTime");
                observation.waterShaderIntensity = _waterProperties.GetFloat("_Intensity");
                Vector4 flow = _waterProperties.GetVector("_FoamFlow");
                observation.waterShaderCurrentMps = new Vector2(flow.x, flow.y);
            }
            _writer.WriteLine(JsonUtility.ToJson(observation)); _report.queuedLines++;
        }
        void OnLog(string message, string stack, LogType type)
        {
            if (!Completed && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
                if (_report.errors.Count < 30) _report.errors.Add(message);
        }
        void Update()
        {
            if (Completed || _report == null) return;
            if (_finishing)
            {
                if (_writer == null || _writer.HasCompleted)
                {
                    LogLifecycle(_writer == null ? "WRITER_ABSENT" : "WRITER_COMPLETED", true);
                    CompleteFinish();
                }
                else if (WallTime - _started > OverallTimeoutSeconds)
                {
                    LogLifecycle("WRITER_DRAIN_DEADLINE", true);
                    _report.errors.Add("Durable evidence drain did not complete before the overall deadline."); CompleteFinish();
                }
                return;
            }
            try
            {
                if (WallTime - _started > OverallTimeoutSeconds) { Finish("Closed loop normal-player-loop timeout."); return; }
                if (_session == null)
                {
                    _session = FindObjectOfType<SimulationSession>();
                    if (_session == null) return;
                    _session.FrameCompleted += ObserveFrame;
                }
                if (_stage == Stage.Starting)
                {
                    if (_session.closedLoop == null || !_session.closedLoop.Active || !_session.closedLoop.Ready || _session.LastFrame == null) return;
                    _waterVisuals = _session.GetComponent<WaterInteractionVisuals>();
                    _audio = FindObjectOfType<RiverAudioController>();
                    var water = FindObjectOfType<WaterSurface>();
                    _waterRenderer = water != null ? water.GetComponent<Renderer>() : null;
                    _waterProperties = new MaterialPropertyBlock();
                    _foam = FindObjectOfType<InteractionFoamTrail>();
                    // Start only after the ordinary component callbacks have bound the
                    // effects. Subscribing now observes reset after their reset handlers.
                    if (_foam == null || _foam.ObservedFrames == 0 || _audio == null || !_audio.AudioReady || _waterVisuals == null) return;
                    var listener = FindObjectOfType<AudioListener>();
                    if (listener == null) throw new InvalidOperationException("Runtime audio listener missing.");
                    _audioCapture = listener.gameObject.AddComponent<RuntimeAudioCapture>();
                    _audioCapture.Begin(EnvironmentRuntimeSweep.Requested ? 60 : 24);
                    if (RuntimeMotionCapture.Requested)
                    {
                        _motionCapture = gameObject.AddComponent<RuntimeMotionCapture>();
                        _motionCapture.Begin(artifactsDirectory, _session, _audioCapture, () => _stage.ToString());
                    }
                    _session.TimelineReset += ObserveInteractionReset;
                    _body = _session.boat.GetComponent<Rigidbody>();
                    _report.initialBoatPosition = _session.InitialBoatPosition;
                    _report.initialBoatRotation = _session.InitialBoatRotation;
                    _report.initialWaveTime = _session.InitialWaveTime;
                    _report.inputTtlSeconds = _session.drivers[0].inputTimeoutSeconds;
                    _report.intentStrokesPerMinute = _session.drivers[0].strokesPerMinute;
                    _report.feedbackTtlSeconds = _session.closedLoop.configuration.feedbackTimeoutSeconds;
                    _report.countsPerMeter = _session.encoderPipelines[0].countsPerMeter;
                    _report.countsPerDegree = _session.encoderPipelines[0].countsPerDegree;
                    _report.normalPhysics = Physics.simulationMode == SimulationMode.FixedUpdate && !_body.isKinematic
                        && _session.GetComponents<SimulationPostStep>().Length == 1;
                    var platform = _session.platform;
                    _report.platformGeometry = new PlatformRigConfig { useExplicitJointGeometry = platform.useExplicitJointGeometry,
                        explicitBaseJointsLocal = platform.explicitBaseJointsLocal, explicitTopJointsLocal = platform.explicitTopJointsLocal,
                        baseRadius = platform.baseRadius, topRadius = platform.topRadius, neutralHeight = platform.neutralHeight,
                        minActuatorLengthsMm = platform.minActuatorLengthsMm, maxActuatorLengthsMm = platform.maxActuatorLengthsMm };
                    var telemetry = _session.closedLoop.Telemetry;
                    _report.initialSessionId = telemetry.sessionId; _report.initialControllerPid = telemetry.controllerProcessId;
                    ObserveProcess(telemetry);
                    _normalStartSimulation = _session.SimulationTime;
                    if (EnvironmentRuntimeSweep.Requested)
                    {
                        _environmentSweep = new EnvironmentRuntimeSweep(_session);
                        _report.environmentSweep = _environmentSweep.Evidence;
                    }
                    Begin(Stage.Normal);
                    ObserveInteractionReset();
                    return;
                }
                double elapsed = WallTime - _stageStarted;
                if (_stage == Stage.Normal)
                {
                    if (_environmentSweep != null) _environmentSweep.Update();
                    _report.normalSimulationSeconds = _session.SimulationTime - _normalStartSimulation;
                    _report.normalWallSeconds = elapsed;
                    if (!_screenshotRequested && _report.actualWaterEntries >= 1)
                    {
                        ScreenCapture.CaptureScreenshot(Path.Combine(artifactsDirectory, _report.screenshot));
                        _screenshotRequested = true;
                    }
                    if (_report.normalSimulationSeconds >= 1.5 && _session.drivers[0].Sample.valid)
                    {
                        int view = Mathf.Min(3, (int)(_session.drivers[0].Phase01 * 4f));
                        if ((_paddleViewsCaptured & (1 << view)) == 0)
                        {
                            ScreenCapture.CaptureScreenshot(Path.Combine(artifactsDirectory, "paddle_view_" + (view + 1) + ".png"));
                            _paddleViewsCaptured |= 1 << view;
                        }
                    }
                    double minimumNormal = _environmentSweep != null ? EnvironmentRuntimeSweep.NormalDurationSeconds : 8;
                    if (_report.normalSimulationSeconds >= minimumNormal && _report.actualWaterEntries >= 3)
                    { _session.closedLoop.SendCommands = false; Begin(Stage.Disconnected); }
                    else if (elapsed > (_environmentSweep != null ? 65 : 35)) Finish("Closed loop did not complete the requested runtime window and wet strokes before its deadline.");
                }
                else if (_stage == Stage.Disconnected && elapsed >= 0.7)
                { _session.closedLoop.SendCommands = true; Begin(Stage.Recovery); }
                else if (_stage == Stage.Recovery && elapsed >= 1.5 && _report.recoveredValidFrames >= 50)
                {
                    _oldSession = _session.closedLoop.Telemetry.sessionId;
                    _oldPid = _session.closedLoop.Telemetry.controllerProcessId;
                    if (!_session.EndExperience()) throw new InvalidOperationException("EndExperience was refused.");
                    Begin(Stage.Stopped);
                }
                else if (_stage == Stage.Recovery && elapsed > 10) Finish("UDP feedback failed to recover after commands resumed.");
                else if (_stage == Stage.Stopped)
                {
                    ObserveStopped();
                    if (elapsed >= 0.5 && !ProcessAlive(_oldPid, out _))
                    {
                        if (!_session.RepositionExperience()) throw new InvalidOperationException("RepositionExperience was refused.");
                        _report.restartResetImmediate = Math.Abs(_session.SimulationTime) < 1e-9 && _session.LastFrame == null
                            && Vector3.Distance(_body.position, _session.InitialBoatPosition) < 1e-5f;
                        WriteState("restart_requested");
                        _haveActualY = false;
                        Begin(Stage.Restarted);
                    }
                    else if (elapsed > 5) Finish("Stopped experience left the owned controller process alive.");
                }
                else if (_stage == Stage.Restarted)
                {
                    var telemetry = _session.closedLoop.Telemetry;
                    if (_session.closedLoop.Ready && string.IsNullOrEmpty(_report.restartedSessionId))
                    {
                        _report.restartedSessionId = telemetry.sessionId; _report.restartedControllerPid = telemetry.controllerProcessId;
                        _report.newControllerSession = !string.IsNullOrEmpty(telemetry.sessionId) && telemetry.sessionId != _oldSession
                            && telemetry.controllerProcessId > 0 && telemetry.controllerProcessId != _oldPid;
                        ObserveProcess(telemetry);
                    }
                    if (elapsed >= 3 && _report.restartedValidFrames >= 100 && _report.restartEntries >= 1) Finish(null);
                    else if (elapsed > 15) Finish("Restarted experience did not establish a fresh working controller session.");
                }
            }
            catch (Exception error) { Finish(error.ToString()); }
        }

        void Begin(Stage stage)
        {
            _stage = stage; _stageStarted = WallTime;
            Debug.Log("[CLOSEDLOOP_LIFECYCLE] stage=" + stage + " timeSeconds="
                + _stageStarted.ToString("F6", CultureInfo.InvariantCulture));
        }
        static bool ProcessAlive(int pid, out string processName)
        {
            processName = "";
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return false;
                    processName = p.ProcessName;
                    return !p.HasExited;
                }
            }
            catch (ArgumentException) { return false; }
            // The owned controller can exit between opening its handle and
            // reading ProcessName, especially during the tested stop boundary.
            catch (InvalidOperationException) { processName = ""; return false; }
        }
        void ObserveProcess(ClosedLoopTelemetry telemetry)
        {
            bool alive = ProcessAlive(telemetry.controllerProcessId, out string name);
            _report.processes.Add(new ProcessEvidence { stage = _stage.ToString(), sessionId = telemetry.sessionId,
                hostProcessId = telemetry.hostProcessId, controllerProcessId = telemetry.controllerProcessId,
                observedAlive = alive, processName = name });
        }

        bool ObserveCommand(ClosedLoopTelemetry t)
        {
            if (!t.commandSent) return !t.commandSourceFresh && string.IsNullOrEmpty(t.commandPacketBase64);
            _report.commandPackets++;
            try
            {
                if (!UdpClosedLoopClient.TryUnpack(Convert.FromBase64String(t.commandPacketBase64), ulong.Parse(t.sessionId),
                    out uint sequence, out string json) || sequence != t.commandSequence) return false;
                var command = JsonUtility.FromJson<ClosedLoopCommand>(json);
                if (command.commandSequence != sequence || command.sourceFeedbackSequence != t.feedbackSequence
                    || command.sourceSampleTick != (t.feedback != null ? t.feedback.plantSampleTick : 0)) return false;
                bool fresh = t.encoderConsumed && t.feedback != null && t.feedback.valid && !t.feedback.watchdog
                    && t.commandSourceReceivedAgeSeconds >= 0 && t.commandSourceReceivedAgeSeconds <= 0.1
                    && t.commandSourceAckAgeSeconds >= 0 && t.commandSourceAckAgeSeconds <= 0.1;
                if (fresh != t.commandSourceFresh) return false;
                if (!fresh)
                {
                    _report.commandSourceExpiredPackets++;
                    if (command.cableValid || command.platformValid || Vector(command.desiredGripVelocity).sqrMagnitude != 0
                        || Vector(command.desiredGripAngularVelocity).sqrMagnitude != 0) return false;
                }
                if (!command.cableValid)
                {
                    foreach (float value in command.tensionTargets) if (value != 0) return false;
                    if (Vector(command.requestForce).sqrMagnitude != 0 || Vector(command.requestTorque).sqrMagnitude != 0) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        void ObserveFrame(SimulationFrame frame)
        {
            if (Completed || _finishing || _stage == Stage.Starting || _stage == Stage.Finished) return;
            try
            {
                if (_stage == Stage.Stopped) { _report.stopViolations++; return; }
                if (frame == null || frame.closedLoop == null || frame.paddles == null || frame.paddles.Length != 1)
                    throw new InvalidOperationException("Closed-loop frame is missing the paired input/UDP snapshot.");
                var t = frame.closedLoop;
                var p = frame.paddles[0];
                if (!ObserveCommand(t)) _report.commandSourceViolations++;
                float intentPhase = _session.drivers[0].Phase01;
                double intentSampleTime = !t.intentPaused && t.feedback != null ? t.feedback.controllerTimeSeconds : double.NaN;
                if (_intentSession == t.sessionId)
                {
                    float advance = Mathf.DeltaAngle(_priorIntentPhase * 360f, intentPhase * 360f) / 360f;
                    double elapsed = intentSampleTime - _priorIntentSampleTime;
                    float expected = elapsed > 1e-6 && elapsed <= 0.1
                        ? _report.intentStrokesPerMinute / 60f * (float)elapsed : 0f;
                    _report.intentContinuityObservations++;
                    _report.maxIntentPhaseStep = Mathf.Max(_report.maxIntentPhaseStep, advance);
                    if (Mathf.Abs(advance - expected) > 1e-6f) _report.intentContinuityViolations++;
                }
                if (t.intentPaused) _report.intentPausedObservations++;
                _intentSession = t.sessionId; _priorIntentPhase = intentPhase; _priorIntentSampleTime = intentSampleTime;
                var encoder = frame.encoderTelemetry != null && frame.encoderTelemetry.Length == 1 ? frame.encoderTelemetry[0] : null;
                bool fresh = t.feedbackFresh && t.encoderConsumed && p.valid;
                _report.observedFrames++;
                if (_stage == Stage.Normal) { _report.normalFrames++; if (fresh) _report.normalValidFrames++; }
                if (_stage == Stage.Recovery && fresh) _report.recoveredValidFrames++;
                if (_stage == Stage.Restarted && fresh) _report.restartedValidFrames++;
                float waterForce = frame.rawWaterForceN[0].magnitude;
                _report.maxForceN = Mathf.Max(_report.maxForceN, waterForce);
                if (fresh)
                {
                    bool inputIdentity = encoder != null && encoder.packetAccepted && encoder.crcValid && encoder.packetBytes == 52
                        && encoder.rawCounts != null && encoder.rawCounts.Length == 6 && p.sequence == encoder.sourceSequence
                        && !string.IsNullOrEmpty(t.feedbackPacketBase64) && !string.IsNullOrEmpty(t.encoderPacketBase64)
                        && t.hostProcessId == _report.hostProcessId && t.controllerProcessId > 0 && t.controllerProcessId != _report.hostProcessId;
                    if (inputIdentity) _report.rawEncoderFrames++; else _report.identityFailures++;
                    if (t.feedback == null) throw new InvalidOperationException("Fresh telemetry has no received controller payload.");
                    bool motor = frame.cableFeedback != null && Same(frame.cableFeedback.tensionsN, t.feedback.tensionN, 0.001f)
                        && Same(frame.cableFeedback.lengthsMm, t.feedback.cableLengthsMm, 0.001f);
                    if (motor) _report.motorFeedbackFrames++; else _report.identityFailures++;
                    bool platform = frame.platformFeedback != null && Same(frame.platformFeedback.actuatorLengthsMm, t.feedback.platformLengthsMm, 0.001f)
                        && Vector3.Distance(frame.platformFeedback.translationM, Vector(t.feedback.platformPosition)) < 1e-5f
                        && Vector3.Distance(frame.platformFeedback.eulerDegrees, Vector(t.feedback.platformEuler)) < 1e-4f;
                    if (platform) _report.platformFeedbackFrames++; else _report.identityFailures++;
                    _report.maxPlatformFitResidualMm = Mathf.Max(_report.maxPlatformFitResidualMm, t.feedback.platformFitResidualMm);
                    float deviation = Vector3.Distance(t.desiredBladePositionLocal, t.actualBladePositionLocal);
                    _report.maxPoseDeviationM = Mathf.Max(_report.maxPoseDeviationM, deviation);
                    if (waterForce > 1 && Vector(t.feedback.forceLocal).magnitude > 0.1f && deviation > 0.0001f) _report.loadedDeviationFrames++;
                    bool air = t.actualBladePositionLocal.y > 0.1f && _session.hydrodynamics[0].WettedFraction < 0.01f;
                    bool entry = _haveActualY && t.actualBladePositionLocal.y < _lastActualY && t.actualBladePositionLocal.y < -0.1f
                        && _session.hydrodynamics[0].WettedFraction > 0.2f && waterForce > 1;
                    if (_stage == Stage.Normal)
                    { if (air) _entryArmed = true; if (_entryArmed && entry) { _report.actualWaterEntries++; _entryArmed = false; } }
                    if (_stage == Stage.Restarted)
                    { if (air) _restartEntryArmed = true; if (_restartEntryArmed && entry) { _report.restartEntries++; _restartEntryArmed = false; } }
                    _lastActualY = t.actualBladePositionLocal.y; _haveActualY = true;
                }
                if (_stage == Stage.Disconnected && (t.commandAckAgeSeconds > 0.1 || t.receivedAgeSeconds > 0.1 || (t.feedback != null && t.feedback.watchdog)))
                {
                    _report.disconnectedExpiredFrames++;
                    if (p.valid || t.feedbackFresh || waterForce > ZeroTolerance || frame.filteredFeedbackForceN[0].magnitude > ZeroTolerance
                        || frame.feedbackRequests[0].valid || frame.feedbackRequests[0].forceWorldN.magnitude > ZeroTolerance) _report.expiryViolations++;
                    if (t.feedback != null && t.feedback.watchdog && ArrayZero(t.feedback.tensionN)
                        && Vector(t.feedback.forceLocal).magnitude < ZeroTolerance) _report.watchdogZeroMotorFrames++;
                }
                Write(new Observation { stage = _stage.ToString(), realTimeSeconds = WallTime, fixedTimeSeconds = Time.fixedTimeAsDouble,
                    unityFrameCount = Time.frameCount, physicsAutomatic = Physics.simulationMode == SimulationMode.FixedUpdate,
                    experienceActive = _session.ExperienceActive, bodyKinematic = _body.isKinematic,
                    wettedFraction = _session.hydrodynamics[0].WettedFraction, driverPhase01 = _session.drivers[0].Phase01,
                    waterEntries = _waterVisuals != null ? _waterVisuals.EntryBursts : -1,
                    waterExits = _waterVisuals != null ? _waterVisuals.ExitEvents : -1,
                    bodyPosition = _body.position, bodyRotation = _body.rotation, bodyVelocity = _body.velocity, frame = frame,
                    environment = _environmentSweep != null ? _environmentSweep.Observe() : null });
            }
            catch (Exception error) { Finish(error.ToString()); }
        }
        void ObserveInteractionReset()
        {
            if (_finishing || Completed || _waterVisuals == null || _audio == null || _foam == null) return;
            _interactionBaseline = new InteractionResetObservation { stage = _stage.ToString(),
                realTimeSeconds = WallTime,
                waterEntries = _waterVisuals.EntryBursts, waterExits = _waterVisuals.ExitEvents,
                splashSounds = _audio.SplashSounds, dripSounds = _audio.DripSounds, foamEpoch = _foam.TimelineEpoch };
            _writer.WriteLine(JsonUtility.ToJson(_interactionBaseline)); _report.queuedLines++;
        }
        static Vector3 Vector(float[] value) => value != null && value.Length == 3 ? new Vector3(value[0], value[1], value[2]) : new Vector3(float.NaN, float.NaN, float.NaN);
        static bool Same(float[] a, float[] b, float tolerance)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!SimulationValue.Finite(a[i]) || !SimulationValue.Finite(b[i]) || Mathf.Abs(a[i] - b[i]) > tolerance) return false;
            return true;
        }
        static bool ArrayZero(float[] values)
        {
            if (values == null || values.Length == 0) return false;
            foreach (float value in values) if (!SimulationValue.Finite(value) || Mathf.Abs(value) > ZeroTolerance) return false;
            return true;
        }
        void ObserveStopped()
        {
            _report.stopObservations++;
            var hydro = _session.hydrodynamics[0];
            if (_session.ExperienceActive || _session.drivers[0].Sample.valid || hydro.RawWaterForceN.magnitude > ZeroTolerance
                || hydro.WaterForceN.magnitude > ZeroTolerance || hydro.FeedbackRequest.valid
                || hydro.FeedbackRequest.forceWorldN.magnitude > ZeroTolerance || !_body.isKinematic
                || _body.velocity.sqrMagnitude > ZeroTolerance || _session.LastFrame != null) _report.stopViolations++;
            WriteState("stopped");
        }
        void WriteState(string kind)
        {
            var hydro = _session.hydrodynamics[0];
            Write(new Observation { recordType = kind, stage = _stage.ToString(), realTimeSeconds = WallTime,
                fixedTimeSeconds = Time.fixedTimeAsDouble, unityFrameCount = Time.frameCount,
                experienceActive = _session.ExperienceActive, bodyKinematic = _body.isKinematic,
                bodyPosition = _body.position, bodyRotation = _body.rotation, bodyVelocity = _body.velocity,
                waveTimeSeconds = _session.wave.SimTime, lastFrameCleared = _session.LastFrame == null,
                controllerAlive = ProcessAlive(_oldPid, out _), paddle = _session.drivers[0].Sample,
                rawForceN = hydro.RawWaterForceN, filteredForceN = hydro.WaterForceN, request = hydro.FeedbackRequest,
                telemetry = _session.closedLoop.Telemetry });
        }
        void Write(Observation observation) { _writer.WriteLine(JsonUtility.ToJson(observation)); _report.queuedLines++; }

        void Check(string name, bool passed, string evidence) => _report.checks.Add(new CheckResult { name = name, passed = passed, evidence = evidence });
        void Finish(string error)
        {
            if (Completed || _finishing) return;
            _finishing = true;
            LogLifecycle("FINISH_REQUESTED", true);
            if (!string.IsNullOrEmpty(error)) _report.errors.Add(error);
            _motionCapture?.StopCapture();
            if (_session != null) { _session.FrameCompleted -= ObserveFrame; _session.EndExperience(); }
            LogLifecycle("END_EXPERIENCE_RETURNED");
            _writer?.BeginClose();
            LogLifecycle("WRITER_BEGIN_CLOSE_RETURNED", true);
        }
        void CompleteFinish()
        {
            LogLifecycle("COMPLETE_FINISH_ENTER", true);
            try
            {
                if (_audioCapture != null)
                {
                    string path = Path.Combine(artifactsDirectory, _report.audioCapture);
                    _audioCapture.Save(path);
                    _report.audioSampleFrames = _audioCapture.SampleFrames; _report.audioChannels = _audioCapture.Channels;
                    _report.audioSampleRate = _audioCapture.SampleRate; _report.audioPeak = _audioCapture.Peak;
                    _report.audioClippedSamples = _audioCapture.ClippedSamples;
                    using (var stream = File.OpenRead(path))
                    using (var sha = SHA256.Create()) _report.audioCaptureSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
                if (_writer != null)
                {
                    LogLifecycle("WRITER_DISPOSE_ENTER", true);
                    _writer.Dispose(); _report.writtenLines = _writer.WrittenLines;
                    LogLifecycle("WRITER_DISPOSE_RETURNED", true);
                    _report.sourceFramesComplete = _report.queuedLines > 0 && _report.writtenLines == _report.queuedLines;
                    LogLifecycle("OBSERVATIONS_HASH_BEGIN");
                    using (var stream = File.OpenRead(Path.Combine(artifactsDirectory, "observations.jsonl")))
                    using (var sha = SHA256.Create()) _report.observationsSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    LogLifecycle("OBSERVATIONS_HASH_END");
                }
                _motionCapture?.Complete();
            }
            catch (Exception failure)
            {
                _report.errors.Add("Cleanup/evidence completion failed: " + failure);
                LogLifecycle("CLEANUP_EVIDENCE_FAILED");
            }
            Check("normal_player_loop_three_actual_strokes", _report.normalPhysics && _report.normalFrames >= 300 && _report.actualWaterEntries >= 3,
                "normalFrames=" + _report.normalFrames + "; actual wet entries=" + _report.actualWaterEntries);
            Check("independent_udp_process_and_raw_encoder", _report.processes.Count >= 2 && _report.processes.TrueForAll(p => p.observedAlive && p.hostProcessId == _report.hostProcessId && p.controllerProcessId != p.hostProcessId)
                && _report.rawEncoderFrames >= 300 && _report.identityFailures == 0, "decoded raw encoder frames=" + _report.rawEncoderFrames);
            Check("received_motor_feedback_drives_frame", _report.motorFeedbackFrames >= 300 && _report.identityFailures == 0, "received tension/length matches=" + _report.motorFeedbackFrames);
            Check("loaded_actual_motion_differs_from_desired", _report.loadedDeviationFrames >= 20, "loaded deviation frames=" + _report.loadedDeviationFrames + "; maxM=" + _report.maxPoseDeviationM);
            Check("platform_received_pose_and_length_feedback", _report.platformFeedbackFrames >= 300 && _report.maxPlatformFitResidualMm <= 0.5f && _report.identityFailures == 0,
                "received platform frames=" + _report.platformFeedbackFrames + "; maxFitResidualMm=" + _report.maxPlatformFitResidualMm);
            Check("command_disconnect_100ms_zero_input_and_force", Mathf.Abs(_report.inputTtlSeconds - 0.1f) < 1e-6f && Mathf.Abs(_report.feedbackTtlSeconds - 0.1f) < 1e-6f
                && _report.disconnectedExpiredFrames >= 10 && _report.watchdogZeroMotorFrames >= 5 && _report.expiryViolations == 0,
                "expiredFrames=" + _report.disconnectedExpiredFrames + "; remote watchdog zero frames=" + _report.watchdogZeroMotorFrames);
            Check("commands_resume_recovers_feedback", _report.recoveredValidFrames >= 50, "valid recovery frames=" + _report.recoveredValidFrames);
            Check("outgoing_actuator_commands_use_fresh_observations", _report.commandPackets >= 100 && _report.commandSourceViolations == 0,
                "original command packets=" + _report.commandPackets + "; expired source rejected=" + _report.commandSourceExpiredPackets);
            Check("experience_stop_and_clean_restart", _report.stopObservations >= 3 && _report.stopViolations == 0 && _report.restartResetImmediate
                && _report.newControllerSession && _report.restartedValidFrames >= 100 && _report.restartEntries >= 1,
                "stopped observations=" + _report.stopObservations + "; restarted valid=" + _report.restartedValidFrames + "; fresh session=" + _report.newControllerSession);
            Check("normal_render_and_completed_evidence", _report.renderCallbacks >= 30 && _screenshotRequested
                && File.Exists(Path.Combine(artifactsDirectory, _report.screenshot)) && _report.sourceFramesComplete,
                "camera callbacks=" + _report.renderCallbacks + "; queued/written=" + _report.queuedLines + "/" + _report.writtenLines);
            Check("execution_pose_reaches_render", _report.executionRenderObservations >= 30 && _report.executionRenderViolations == 0,
                "observations=" + _report.executionRenderObservations + "; maxPositionM=" + _report.maxExecutionRenderPositionErrorM
                + "; maxAngleDeg=" + _report.maxExecutionRenderRotationErrorDeg);
            Check("presentation_views_use_sky_and_own_reflection", _report.presentationMainFrames >= 30
                && _report.presentationTwinFrames >= 30 && _report.presentationViolations == 0,
                "main/twin=" + _report.presentationMainFrames + "/" + _report.presentationTwinFrames
                + "; violations=" + _report.presentationViolations);
            Check("script_intent_continuity_on_recovery", _report.intentContinuityObservations >= 300
                && _report.intentPausedObservations >= 10 && _report.intentContinuityViolations == 0,
                "observations=" + _report.intentContinuityObservations + "; paused=" + _report.intentPausedObservations
                + "; maxPhaseStep=" + _report.maxIntentPhaseStep);
            Check("water_interaction_follows_completed_physics", _report.interactionObservations >= 30
                && _report.interactionViolations == 0 && _report.interactionNormalEntries >= 3
                && _report.interactionPaddlePaintEvents > 0 && _report.interactionWakePaintEvents > 0
                && _report.maxPaddleAudioRms > 1e-6f && _report.maxStrokeAudioRms > 1e-6f
                && _report.maxWoodAudioRms > 1e-6f && _report.normalWoodCreaks > 0 && _report.motionAudioViolations == 0
                && _report.audioSampleFrames >= _report.audioSampleRate * 2 && _report.audioSampleRate > 0
                && _report.audioClippedSamples == 0,
                "observations=" + _report.interactionObservations + "; entries=" + _report.interactionNormalEntries
                + "; paddle/wake=" + _report.interactionPaddlePaintEvents + "/" + _report.interactionWakePaintEvents
                + "; violations=" + _report.interactionViolations + "; paddleAudioRms=" + _report.maxPaddleAudioRms
                + "; stroke/woodRms=" + _report.maxStrokeAudioRms + "/" + _report.maxWoodAudioRms
                + "; motionAudioViolations=" + _report.motionAudioViolations + "; capturedAudioFrames=" + _report.audioSampleFrames);
            Passed = _report.errors.Count == 0 && _report.checks.TrueForAll(c => c.passed);
            _report.result = Passed ? "PASS" : "FAIL"; _report.completedUtc = DateTime.UtcNow.ToString("o");
            try
            {
                LogLifecycle("RESULT_WRITE_BEGIN");
                File.WriteAllText(Path.Combine(artifactsDirectory, "result.json"), JsonUtility.ToJson(_report, true) + "\n", new UTF8Encoding(false));
                LogLifecycle("RESULT_WRITE_END");
            }
            catch (Exception failure) { Passed = false; Debug.LogError("[CLOSEDLOOP] Could not complete report: " + failure); }
            Application.logMessageReceived -= OnLog;
            RenderPipelineManager.endCameraRendering -= OnCameraRendered;
            _stage = Stage.Finished; Completed = true;
            LogLifecycle("COMPLETE_FINISH_EXIT", true);
            Debug.Log("[CLOSEDLOOP] RESULT=" + (Passed ? "PASS" : "FAIL"));
            if (!Application.isEditor) Application.Quit(Passed ? 0 : 1);
        }
        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            RenderPipelineManager.endCameraRendering -= OnCameraRendered;
            if (_session != null) _session.FrameCompleted -= ObserveFrame;
            if (_session != null) _session.TimelineReset -= ObserveInteractionReset;
            if (!Completed && _writer != null) { try { _writer.Dispose(); } catch { } }
        }
    }
}
