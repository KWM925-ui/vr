using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.LowLevel;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// Normal Play-mode desktop acceptance. The harness never calls a simulation Step/Tick,
    /// Physics.Simulate, a component lifecycle method, or Camera.Render. A coroutine hosted by
    /// the already-running session observes each real WaitForFixedUpdate after its companion.
    /// UDP uses real loopback sockets. Screenshots use the normally rendered Game view.
    /// Run without -quit or -nographics; this entry exits the Editor after saving evidence.
    /// </summary>
    [InitializeOnLoad]
    public static class RuntimeSmokeSelfTest
    {
        const string ActiveKey = "DragonBoatVR.RuntimeSmoke.Active";
        const string PhaseKey = "DragonBoatVR.RuntimeSmoke.Phase";
        const string ArtifactsKey = "DragonBoatVR.RuntimeSmoke.Artifacts";
        const string ExitKey = "DragonBoatVR.RuntimeSmoke.Exit";
        const float DefaultTtl = 0.10f;
        const float InputToleranceM = 1e-5f;
        const float ForceToleranceN = 1e-3f;
        const float BoatToleranceM = 0.005f;
        enum Stage
        {
            Starting, Synthetic, Udp, Coast, RecordSettle, Record, Replay, ReplayEof,
            ControlsEofSynthetic, ControlsMiddleReplay, ControlsMiddleSynthetic,
            ControlsMutationReplay, ControlsMutationStopped, ControlsRecovered
        }

        [Serializable] public class Check
        {
            public string name;
            public bool passed;
            public string evidence;
        }
        [Serializable] public class StageEvidence
        {
            public string name;
            public int fixedFrames, validInputs, invalidInputs, nonzeroForceFrames, inputChanges;
            public double startTime, endTime, waveStart, waveEnd;
            public Vector3 startPosition, endPosition;
            public float startSpeed, endSpeed, maximumRawForceN, maximumFeedbackForceN;
            public float minimumY, maximumY, minimumProbeHeight, maximumProbeHeight;
        }
        [Serializable] public class ScreenshotEvidence
        {
            public string stage, file, method = "ScreenCapture.CaptureScreenshot / normal Game view";
            public string purpose = "scene";
            public double sessionTime;
            public int width, height, renderCountRequested, renderCountCompleted, frameCountRequested;
            public float sceneLuminanceStdDev, sceneChangedFraction;
            public int sceneColorBins;
            public int hudTextPixels;
            public bool completed, hasSceneContent;
            [NonSerialized] public double requestedAt;
            [NonSerialized] public byte[] sceneSamples;
        }
        [Serializable] public class FrameEvidence
        {
            public string stage;
            public string inputMode, replayError;
            public bool sessionReplaying;
            public int unityFrameCount;
            public double realTimeSeconds, fixedTimeSeconds;
            public float postPhysicsPositionErrorM, postPhysicsRotationErrorDeg, postPhysicsVelocityError;
            public long fixedInputObservationTicks, fixedPostObservationTicks, acceptedReceiveTicks;
            public uint acceptedInputSequence;
            public double acceptedInputAgeAtFixedStartSeconds;
            public bool coastTimingWindow;
            public bool acquisitionTimingWindow;
            public long frameObserverBeginTicks, previousFrameObserverEndTicks;
            public long lastCameraRenderBeginTicks, lastCameraRenderEndTicks;
            public int fixedStartGc0 = -1, fixedStartGc1 = -1, fixedStartGc2 = -1;
            public int observerPostGc0 = -1, observerPostGc1 = -1, observerPostGc2 = -1;
            public SimulationFrame frame;
        }
        enum TimingEventKind
        {
            SenderStopRequested, FixedInputStart, FrameObserverBegin, FrameObserverPost,
            FrameObserverEnd, EditorUpdateBegin, EditorUpdateEnd, CameraRenderBegin,
            CameraRenderEnd, RenderObserverEnd, AcquisitionStopRequested,
            FrameEvidenceWriteBegin, FrameEvidenceWriteEnd, ReturnEvidenceWriteBegin, ReturnEvidenceWriteEnd,
            ScreenshotPollBegin, ScreenshotPollEnd, ReturnDrainBegin, ReturnDrainEnd
        }
        [Serializable] public struct TimingEvent
        {
            public int kind, unityFrameCount, cameraInstanceId, gc0, gc1, gc2;
            public long ticks;
        }
        [Serializable] public class TimingEvidence
        {
            public string scope = "First 0.5 wall seconds after sender stop; diagnostic observations only, unchanged acceptance checks.";
            public string[] eventKinds;
            public long stopwatchFrequency, windowStartTicks, windowEndTicks;
            public int droppedEvents;
            public TimingEvent[] events;
        }
        [Serializable] public class ReplayControlEvidence
        {
            public string action, stage, inputModeBefore, inputModeAfter, replayError, status;
            public string recordingSha256Before, recordingSha256After, detail;
            public int unityFrameCount;
            public double sessionTime;
            public bool replayingBefore, replayingAfter, recordingAfter, actionAccepted, fileUnchanged;
        }
        [Serializable] public class SentPacket
        {
            public uint sequence;
            public ulong timestampUs;
            public double senderSeconds;
            public long actualSendTicks;
            public float gripX, gripY, gripZ, gripForceN;
        }
        [Serializable] public class BoatPacket
        {
            public ulong timestampUs;
            public uint receiveSequence, echoedTimestampLow;
            public float roll, pitch, yaw, dragN, speedMps, waveHeightM;
            public double receivedRealTimeSeconds;
            public bool echoMatchesSentPacket;
            public string stage;
        }
        [Serializable] public class TrackingObservation
        {
            public string kind, failureReason = "", poseStatus;
            public int frame, updateFrame, evaluationFrame;
            public double observerTimeSeconds, updateTimeSeconds, lastSubmitTimeSeconds, attemptedSubmitTimeSeconds;
            public double evaluationTimeSeconds, reportedAgeSeconds, independentAgeSeconds, maximumTrackingAgeSeconds;
            public bool feedTracking, hasSubmitted, submissionAccepted, evaluationClockValid;
            public bool reportedAgeFinite, independentAgeValid, poseApplied, poseChecked, recoveredAfterExpiry;
            public float positionErrorM, rotationErrorDeg;
        }
        [Serializable] public class Report
        {
            public string result = "FAIL", unityVersion, artifacts, graphicsDevice, configurationProvenance;
            public string scope = "DESKTOP_SOFTWARE_FIXTURE_ONLY: simulated paddler, localhost fake hardware, software cable/platform models. No headset, physical motor, platform, measured geometry or human haptic acceptance.";
            public string execution = "EditorApplication.EnterPlaymode; normal lifecycle/FixedUpdate/PhysX/WaitForFixedUpdate/LateUpdate/render; observer only, no manual stepping or Camera.Render";
            public string startedUtc, finishedUtc;
            public bool batchmode, physicsAutomatic, measuredConfiguration;
            public float fixedDeltaTime, inputTtlSeconds, bridgeTtlSeconds;
            public int inputPort, returnPort, observedFixedFrames, postCompanionCount, renderCallbacks;
            public int renderWaveSamples, postFrameIdentityFailures, postPhysicsFailures;
            public int fixedStartObserversBeforeInstall = -1, fixedStartObserversInstalled = -1;
            public int fixedStartObserversBeforeRestore = -1, fixedStartObserversRestored = -1;
            public bool fixedStartOriginalLoopUnchanged, fixedStartInstalledLoopMatches, fixedStartRestoredLoopMatches;
            public float maximumPostPhysicsPositionErrorM, maximumPostPhysicsRotationErrorDeg;
            public float maximumShaderWaveTimeError;
            public int udpSent, udpAccepted, udpInputSequences, udpIdentityFailures;
            public int boatPackets, boatEchoMatches, boatNonzeroDragPackets, boatExpiredZeroPackets;
            public float maximumBoatPacketDragN, maximumBoatPacketSpeedMps;
            public double firstExpiredAfterSenderStopSeconds;
            public long senderStopRequestTicks, senderLastActualSendTicks, senderStoppedTicks, finalAcceptedReceiveTicks;
            public double firstExpiredAcceptedAgeSeconds, maximumCoastFixedObserverGapSeconds, maximumCoastFixedStartGapSeconds;
            public long timingStopwatchFrequency, timingWindowStartTicks, timingWindowEndTicks;
            public long acquisitionTimingWindowStartTicks, acquisitionTimingWindowEndTicks;
            public int acquisitionTimingEvents, acquisitionTimingDroppedEvents;
            public long frameEvidenceQueuedLines, frameEvidenceWrittenLines, returnEvidenceQueuedLines, returnEvidenceWrittenLines;
            public long frameEvidenceMaximumQueuedBytes, returnEvidenceMaximumQueuedBytes;
            public double frameEvidenceMaximumBackgroundWriteSeconds, returnEvidenceMaximumBackgroundWriteSeconds;
            public bool evidenceWritersDrainedAndCountVerified;
            public string frameEvidenceSha256, returnEvidenceSha256;
            public int timingEvents, timingDroppedEvents;
            public int observedTargetFrameRate, observedVSyncCount;
            public bool observedRunInBackground, observedFocusedAtStart;
            public double maximumCoastFrameObserverSeconds;
            public int fixedStartExpiredInputChecks;
            public int expiryZeroFrames, expiryViolations;
            public double recordingOriginSeconds, recordingDurationSeconds;
            public float recordingFixedDeltaTime;
            public int recordedFrames, replayComparedFrames, replayMissingFrames, replayIdentityFailures;
            public int recordedUdpFrames, recordedRepeatedSequences, recordedChangingRepeatedSequences;
            public int recordUdpSent;
            public float replayInputErrorM, replayForceErrorN, replayBoatErrorM, replayWaveErrorSeconds;
            public float replayPlatformRequestErrorM, replayPlatformFeedbackErrorM, replayPlatformAngleErrorDeg;
            public float replayCableForceErrorN, replayCableTensionErrorN;
            public int replayDeviceStateFailures;
            public int replayEofZeroFrames;
            public bool replayF5EofRefused, replayF5MiddleRefused, replayEofHudExit, replayMiddleHudExit;
            public int replayEofSyntheticFrames, replayMiddleSyntheticFrames, replayControlFailures;
            public int replayMutationWarmupForceFrames, replayMutationZeroFrames, replayMutationFailures, replayRecoveryFrames;
            public float replayMutationOriginalBladeArea, replayMutationChangedBladeArea;
            public string replayMutationReason;
            public int acquisitionPort, acquisitionSent, acquisitionAccepted, acquisitionRejected;
            public int acquisitionValidFrames, acquisitionIdentityFailures, acquisitionIndependentExpiryFrames;
            public int acquisitionPaddleIsolationFrames, acquisitionPaddleIsolationFailures;
            public int acquisitionExpiredFrames, acquisitionExpiryViolations, acquisitionRecordedFrames;
            public int acquisitionReplayComparedFrames, acquisitionReplayFieldFailures, acquisitionReplayCloneFailures, acquisitionReplayEofFrames;
            public double acquisitionTtlSeconds, acquisitionFirstExpiredAgeSeconds = -1;
            public long acquisitionStopRequestTicks, acquisitionLastActualSendTicks, acquisitionStoppedTicks;
            public bool acquisitionHeaderProfileMatches;
            public string hudFontName;
            public bool hudDefaultSceneOnly;
            public int hudPhysicsSamples, hudMaximumStrokeCount;
            public float hudMaximumImpulseNs, hudMaximumDistanceM;
            public int trackingSubmittedFrames, trackingAppliedFrames, trackingStaleFrames, trackingFailures;
            public int trackingRecoveredAppliedFrames;
            public float trackingMaximumPositionErrorM, trackingMaximumRotationErrorDeg;
            public bool trackingDefaultDisabled, trackingMainCameraUnchanged;
            public List<TrackingObservation> trackingObservations = new List<TrackingObservation>();
            public List<RuntimeCounterSample> runtimeCounters = new List<RuntimeCounterSample>();
            public List<CameraCostSample> cameraCosts = new List<CameraCostSample>();
            public string cameraGeometryCounts;
            public bool hudOptionalMetricsToggledAndFormatted;
            public List<string> errors = new List<string>();
            public List<Check> checks = new List<Check>();
            public List<StageEvidence> stages = new List<StageEvidence>();
            public List<ScreenshotEvidence> screenshots = new List<ScreenshotEvidence>();
            public List<ReplayControlEvidence> replayControls = new List<ReplayControlEvidence>();
        }

        static Report _report;
        static Stage _stage;
        static double _stagePlayTime, _enteredWallTime, _senderStopWallTime;
        static SimulationSession _session;
        static Rigidbody _body;
        static Renderer _water;
        static MaterialPropertyBlock _renderObservationBlock;
        static EditorWindow _gameView;
        static StageEvidence _metrics;
        static BackgroundRecordingWriter _frameWriter, _returnWriter;
        static UdpClient _boatListener;
        static FakeHardwareSender _sender;
        static FakeHardwareSender _recordSender;
        static FakeAcquisitionSender _acquisitionSender;
        static FakeAcquisitionSender _initialAcquisitionSender;
        static AcquisitionProfile _acquisitionProfile;
        static AcquisitionSnapshot _previousReplayAcquisition;
        static RideHud _hud;
        static RuntimeTrackingFixture _trackingFixture;
        static bool _stoppedFirstAcquisitionChannel, _captureHud, _captureAcquisitionHud, _focusAcquisitionHud;
        static readonly HashSet<uint> UdpSequences = new HashSet<uint>();
        static List<SimulationFrame> _reference;
        static readonly Dictionary<long, SimulationFrame> ReferenceByTick = new Dictionary<long, SimulationFrame>();
        static double _lastFrameTime = -1;
        static Vector3 _lastInputPosition;
        static bool _haveInput, _done, _captureSynthetic, _captureUdp, _captureCoast;
        static Coroutine _observer;
        static object _bridgeLock, _gameViewSizeGroup;
        static FieldInfo _receivedTicksField, _acceptedSequenceField;
        static long _fixedInputObservationTicks, _acceptedReceiveTicks, _previousCoastPostTicks, _previousCoastFixedStartTicks;
        static uint _acceptedInputSequence;
        static PlayerLoopSystem _originalLoop;
        static bool _loopObserved;
        static int _temporaryGameViewSizeIndex = -1, _previousGameViewSizeIndex;
        static string _controlRecordingHash;
        static bool _controlBladeAreaMutated;
        // Preallocated value events: no JSON, file IO, lists or strings in the timing hooks.
        static readonly TimingEvent[] CoastTiming = new TimingEvent[4096];
        static readonly TimingEvent[] AcquisitionTiming = new TimingEvent[4096];
        static int _timingCount, _timingDropped;
        static int _acquisitionTimingCount, _acquisitionTimingDropped;
        static long _previousFrameObserverEndTicks, _lastCameraRenderBeginTicks, _lastCameraRenderEndTicks;
        static int _fixedStartGc0 = -1, _fixedStartGc1 = -1, _fixedStartGc2 = -1;
        static int _timingGc0 = -1, _timingGc1 = -1, _timingGc2 = -1;

        static bool InTimingWindow(long ticks)
        {
            return _report != null && _report.timingWindowStartTicks > 0
                && ticks >= _report.timingWindowStartTicks && ticks <= _report.timingWindowEndTicks;
        }
        static bool InAcquisitionTimingWindow(long ticks)
        {
            return _report != null && _report.acquisitionTimingWindowStartTicks > 0
                && ticks >= _report.acquisitionTimingWindowStartTicks && ticks <= _report.acquisitionTimingWindowEndTicks;
        }
        static bool InAnyTimingWindow(long ticks) => InTimingWindow(ticks) || InAcquisitionTimingWindow(ticks);
        static void TraceTiming(TimingEventKind kind, long ticks = 0, int cameraInstanceId = 0)
        {
            if (_done || _report == null) return;
            if (ticks == 0) ticks = Stopwatch.GetTimestamp();
            if (!InAnyTimingWindow(ticks)) return;
            _timingGc0 = GC.CollectionCount(0);
            _timingGc1 = GC.CollectionCount(1);
            _timingGc2 = GC.CollectionCount(2);
            if (kind == TimingEventKind.FixedInputStart)
            {
                _fixedStartGc0 = _timingGc0; _fixedStartGc1 = _timingGc1; _fixedStartGc2 = _timingGc2;
            }
            if (kind == TimingEventKind.CameraRenderBegin) _lastCameraRenderBeginTicks = ticks;
            if (kind == TimingEventKind.CameraRenderEnd) _lastCameraRenderEndTicks = ticks;
            var observation = new TimingEvent
            {
                kind = (int)kind, ticks = ticks, unityFrameCount = Time.frameCount,
                cameraInstanceId = cameraInstanceId, gc0 = _timingGc0, gc1 = _timingGc1, gc2 = _timingGc2
            };
            if (InTimingWindow(ticks))
            {
                if (_timingCount == CoastTiming.Length) _timingDropped++;
                else CoastTiming[_timingCount++] = observation;
            }
            if (InAcquisitionTimingWindow(ticks))
            {
                if (_acquisitionTimingCount == AcquisitionTiming.Length) _acquisitionTimingDropped++;
                else AcquisitionTiming[_acquisitionTimingCount++] = observation;
            }
        }
        static void SaveTimingEvidence()
        {
            var events = new TimingEvent[_timingCount];
            Array.Copy(CoastTiming, events, _timingCount);
            _report.timingEvents = _timingCount;
            _report.timingDroppedEvents = _timingDropped;
            File.WriteAllText(Path.Combine(_report.artifacts, "coast_timing.json"), JsonUtility.ToJson(new TimingEvidence
            {
                eventKinds = Enum.GetNames(typeof(TimingEventKind)), stopwatchFrequency = Stopwatch.Frequency,
                windowStartTicks = _report.timingWindowStartTicks, windowEndTicks = _report.timingWindowEndTicks,
                droppedEvents = _timingDropped, events = events
            }, true));
            var adcEvents = new TimingEvent[_acquisitionTimingCount];
            Array.Copy(AcquisitionTiming, adcEvents, _acquisitionTimingCount);
            _report.acquisitionTimingEvents = _acquisitionTimingCount;
            _report.acquisitionTimingDroppedEvents = _acquisitionTimingDropped;
            File.WriteAllText(Path.Combine(_report.artifacts, "adc_timing.json"), JsonUtility.ToJson(new TimingEvidence
            {
                scope = "First 0.5 wall seconds after ADC sender stop; diagnostic observations only, unchanged acceptance checks.",
                eventKinds = Enum.GetNames(typeof(TimingEventKind)), stopwatchFrequency = Stopwatch.Frequency,
                windowStartTicks = _report.acquisitionTimingWindowStartTicks, windowEndTicks = _report.acquisitionTimingWindowEndTicks,
                droppedEvents = _acquisitionTimingDropped, events = adcEvents
            }, true));
        }

        static RuntimeSmokeSelfTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) RegisterEditorHooks();
        }

        public static void Run()
        {
            string artifacts = Argument("-runtimeArtifacts");
            if (string.IsNullOrWhiteSpace(artifacts))
                artifacts = Path.GetFullPath(Path.Combine(Application.dataPath, "../../tools/runtime_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            artifacts = Path.GetFullPath(artifacts);
            Directory.CreateDirectory(artifacts);
            SessionState.SetString(ArtifactsKey, artifacts);
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetString(PhaseKey, "entering");
            SessionState.SetInt(ExitKey, 1);
            RegisterEditorHooks();
            try
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("Start this entry from Edit mode in a dedicated Editor process.");
                if (HasArgument("-quit") || HasArgument("-nographics"))
                    throw new InvalidOperationException("Normal Play-mode screenshots require graphics and this asynchronous entry owns process exit: omit -quit and -nographics.");
                if (File.Exists(Path.Combine(artifacts, "runtime_result.json")))
                    throw new IOException("Use a new independent artifacts directory; an existing result will not be overwritten.");
                EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                var bridge = UnityEngine.Object.FindObjectOfType<HardwareUdpBridge>();
                if (bridge == null) throw new InvalidOperationException("Demo scene has no hardware UDP bridge.");
                bridge.listenPort = AllocatePort();
                do { bridge.remotePort = AllocatePort(); } while (bridge.listenPort == bridge.remotePort);
                bridge.remoteHost = "127.0.0.1";
                SessionState.SetInt(ActiveKey + ".InputPort", bridge.listenPort);
                SessionState.SetInt(ActiveKey + ".ReturnPort", bridge.remotePort);
                // Scene changes remain in this dedicated process and are never saved.
                Debug.Log("[RUNTIME] Entering normal Play mode. Artifacts=" + artifacts);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception ex) { FailBeforePlay(ex); }
        }

        static void RegisterEditorHooks()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged -= PlayStateChanged;
            EditorApplication.playModeStateChanged += PlayStateChanged;
        }

        static void ConfigureDedicatedGameView(Type type)
        {
            if (_gameView == null || type == null || !Application.isBatchMode)
                throw new InvalidOperationException("Runtime acceptance requires its dedicated batch Editor Game view.");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type sizesType = type.Assembly.GetType("UnityEditor.GameViewSizes");
            Type sizeType = type.Assembly.GetType("UnityEditor.GameViewSize");
            Type kindType = type.Assembly.GetType("UnityEditor.GameViewSizeType");
            object sizes = sizesType.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            _gameViewSizeGroup = sizesType.GetProperty("currentGroup", flags).GetValue(sizes);
            Type groupType = _gameViewSizeGroup.GetType();
            _temporaryGameViewSizeIndex = (int)groupType.GetMethod("GetTotalCount", flags).Invoke(_gameViewSizeGroup, null);
            object fixedSize = Activator.CreateInstance(sizeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { Enum.ToObject(kindType, 1), 1280, 720, "Runtime smoke temporary 1280x720" }, null);
            groupType.GetMethod("AddCustomSize", flags).Invoke(_gameViewSizeGroup, new[] { fixedSize });
            PropertyInfo selected = type.GetProperty("selectedSizeIndex", flags);
            _previousGameViewSizeIndex = (int)selected.GetValue(_gameView);
            selected.SetValue(_gameView, _temporaryGameViewSizeIndex);
            // Avoid SetCustomResolution: that Unity helper calls SaveToHDD and would persist
            // a preference in the user's global Editor settings. This temporary size is
            // removed before exit and only changes this invisible dedicated batch process.
            _gameView.minSize = new Vector2(1280, 750);
            _gameView.position = new Rect(0, 0, 1280, 750);
            _gameView.Repaint();
        }

        static void RestoreDedicatedGameView()
        {
            if (_gameViewSizeGroup == null || _temporaryGameViewSizeIndex < 0) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (_gameView != null) _gameView.GetType().GetProperty("selectedSizeIndex", flags).SetValue(_gameView, _previousGameViewSizeIndex);
            _gameViewSizeGroup.GetType().GetMethod("RemoveCustomSize", flags).Invoke(_gameViewSizeGroup, new object[] { _temporaryGameViewSizeIndex });
            _temporaryGameViewSizeIndex = -1;
        }

        // Adds only a read-only timestamp marker immediately before normal script
        // FixedUpdate in the existing PlayerLoop. Every original Unity loop entry remains.
        // It never advances session time or calls input, physics or device functions.
        sealed class FixedStartObservationMarker { }
        static void InstallFixedStartObservation()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _bridgeLock = typeof(HardwareUdpBridge).GetField("_latestLock", flags).GetValue(_session.bridge);
            _receivedTicksField = typeof(HardwareUdpBridge).GetField("_receivedTicks", flags);
            _acceptedSequenceField = typeof(HardwareUdpBridge).GetField("_acceptedSequence", flags);
            PlayerLoopSystem currentLoop = PlayerLoop.GetCurrentPlayerLoop();
            _report.fixedStartObserversBeforeInstall = CountFixedStartObservers(currentLoop);
            if (_loopObserved || _report.fixedStartObserversBeforeInstall != 0)
                throw new InvalidOperationException("Fixed-start observer is already installed.");
            // PlayerLoopSystem is a struct, but all descendant arrays are references.
            // Keep both the restore tree and the edited tree independent at every level.
            _originalLoop = CopyPlayerLoop(currentLoop);
            PlayerLoopSystem observedLoop = CopyPlayerLoop(_originalLoop);
            if (!InsertFixedStartMarker(ref observedLoop)) throw new InvalidOperationException("Normal script FixedUpdate PlayerLoop entry was not found.");
            _report.fixedStartOriginalLoopUnchanged = PlayerLoopTreesEqual(currentLoop, _originalLoop);
            if (!_report.fixedStartOriginalLoopUnchanged)
                throw new InvalidOperationException("Installing the fixed-start observer mutated the original PlayerLoop tree.");
            PlayerLoop.SetPlayerLoop(observedLoop);
            _loopObserved = true;
            PlayerLoopSystem installedLoop = PlayerLoop.GetCurrentPlayerLoop();
            _report.fixedStartObserversInstalled = CountFixedStartObservers(installedLoop);
            _report.fixedStartInstalledLoopMatches = PlayerLoopTreesEqual(observedLoop, installedLoop);
            if (_report.fixedStartObserversInstalled != 1 || !_report.fixedStartInstalledLoopMatches)
                throw new InvalidOperationException("The active PlayerLoop does not match the single-observer installation.");
        }
        static PlayerLoopSystem CopyPlayerLoop(PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return system;
            var children = new PlayerLoopSystem[system.subSystemList.Length];
            for (int i = 0; i < children.Length; i++) children[i] = CopyPlayerLoop(system.subSystemList[i]);
            system.subSystemList = children;
            return system;
        }
        static bool PlayerLoopTreesEqual(PlayerLoopSystem left, PlayerLoopSystem right)
        {
            if (left.type != right.type || left.updateDelegate != right.updateDelegate
                || left.updateFunction != right.updateFunction || left.loopConditionFunction != right.loopConditionFunction)
                return false;
            if (left.subSystemList == null || right.subSystemList == null)
                return left.subSystemList == null && right.subSystemList == null;
            if (left.subSystemList.Length != right.subSystemList.Length) return false;
            for (int i = 0; i < left.subSystemList.Length; i++)
                if (!PlayerLoopTreesEqual(left.subSystemList[i], right.subSystemList[i])) return false;
            return true;
        }
        static int CountFixedStartObservers(PlayerLoopSystem system)
        {
            int count = system.type == typeof(FixedStartObservationMarker) ? 1 : 0;
            if (system.subSystemList != null)
                foreach (PlayerLoopSystem child in system.subSystemList) count += CountFixedStartObservers(child);
            return count;
        }
        static void RestoreFixedStartObservation()
        {
            if (!_loopObserved) return;
            try { _report.fixedStartObserversBeforeRestore = CountFixedStartObservers(PlayerLoop.GetCurrentPlayerLoop()); }
            catch (Exception ex) { _report.errors.Add("PlayerLoop pre-restore inspection failed: " + ex); }
            // A failed diagnostic must not prevent the actual restoration attempt.
            PlayerLoop.SetPlayerLoop(_originalLoop);
            _loopObserved = false;
            PlayerLoopSystem restoredLoop = PlayerLoop.GetCurrentPlayerLoop();
            _report.fixedStartObserversRestored = CountFixedStartObservers(restoredLoop);
            _report.fixedStartRestoredLoopMatches = PlayerLoopTreesEqual(_originalLoop, restoredLoop);
        }
        static bool InsertFixedStartMarker(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return false;
            for (int i = 0; i < system.subSystemList.Length; i++)
            {
                if (system.subSystemList[i].type == typeof(UnityEngine.PlayerLoop.FixedUpdate.ScriptRunBehaviourFixedUpdate))
                {
                    var entries = new List<PlayerLoopSystem>(system.subSystemList);
                    entries.Insert(i, new PlayerLoopSystem { type = typeof(FixedStartObservationMarker), updateDelegate = ObserveFixedInputAge });
                    system.subSystemList = entries.ToArray();
                    return true;
                }
                PlayerLoopSystem child = system.subSystemList[i];
                if (InsertFixedStartMarker(ref child)) { system.subSystemList[i] = child; return true; }
            }
            return false;
        }
        static void ObserveFixedInputAge()
        {
            if (_done || _bridgeLock == null) return;
            lock (_bridgeLock)
            {
                _fixedInputObservationTicks = Stopwatch.GetTimestamp();
                _acceptedReceiveTicks = (long)_receivedTicksField.GetValue(_session.bridge);
                _acceptedInputSequence = (uint)_acceptedSequenceField.GetValue(_session.bridge);
            }
            TraceTiming(TimingEventKind.FixedInputStart, _fixedInputObservationTicks);
        }
        static bool AcceptedSequenceUnchanged()
        {
            lock (_bridgeLock) return _acceptedInputSequence == (uint)_acceptedSequenceField.GetValue(_session.bridge);
        }

        static void PlayStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetString(PhaseKey, "") == "entering")
                StartObservation();
        }

        static void StartObservation()
        {
            if (_report != null) return;
            _report = new Report
            {
                unityVersion = Application.unityVersion, batchmode = Application.isBatchMode,
                artifacts = SessionState.GetString(ArtifactsKey, ""), startedUtc = DateTime.UtcNow.ToString("O"),
                graphicsDevice = SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName,
                fixedDeltaTime = Time.fixedDeltaTime, physicsAutomatic = Physics.simulationMode == SimulationMode.FixedUpdate,
                inputPort = SessionState.GetInt(ActiveKey + ".InputPort", 0),
                returnPort = SessionState.GetInt(ActiveKey + ".ReturnPort", 0),
                firstExpiredAfterSenderStopSeconds = -1
            };
            _previousCoastFixedStartTicks = 0;
            _stage = Stage.Starting;
            _enteredWallTime = WallTime();
            SessionState.SetString(PhaseKey, "running");
            Application.logMessageReceived += OnLog;
            Camera.onPostRender += OnBuiltinRendered;
            Camera.onPreRender += OnBuiltinRenderBegin;
            RenderPipelineManager.endCameraRendering += OnPipelineRendered;
            RenderPipelineManager.beginCameraRendering += OnPipelineRenderBegin;
            _renderObservationBlock = new MaterialPropertyBlock();
            // Do not add a 60 Hz render throttle to a 100 Hz physics acceptance run.
            // FixedUpdate and rendering still run through the real Unity player loop.
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            // This isolated batch Editor intentionally has no foreground focus.
            // Keep its ordinary player loop running while the user works elsewhere;
            // otherwise background pausing/throttling contaminates wall-time checks.
            Application.runInBackground = true;
            _report.timingStopwatchFrequency = Stopwatch.Frequency;
            _report.observedTargetFrameRate = Application.targetFrameRate;
            _report.observedVSyncCount = QualitySettings.vSyncCount;
            _report.observedRunInBackground = Application.runInBackground;
            _report.observedFocusedAtStart = Application.isFocused;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-profileRuntime") >= 0)
                new GameObject("Runtime counter observer").AddComponent<RuntimeCounterProbe>().Initialize(_report);
            _timingCount = 0; _timingDropped = 0;
            _acquisitionTimingCount = 0; _acquisitionTimingDropped = 0;
            try
            {
                _boatListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, _report.returnPort));
                _frameWriter = CreateEvidenceWriter("normal_fixed_frames.jsonl");
                _returnWriter = CreateEvidenceWriter("boatstate_loopback.jsonl");
                // This is the dedicated batch process's Game view, with no new OS window or visible focus.
                Type gameViewType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType != null) _gameView = EditorWindow.GetWindow(gameViewType, false, "Game", false);
                ConfigureDedicatedGameView(gameViewType);
            }
            catch (Exception ex) { Finish("Startup failed: " + ex); }
        }

        static void Update()
        {
            if (!SessionState.GetBool(ActiveKey, false)) return;
            string phase = SessionState.GetString(PhaseKey, "");
            if (phase == "finishing")
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    int code = SessionState.GetInt(ExitKey, 1);
                    SessionState.SetBool(ActiveKey, false);
                    EditorApplication.update -= Update;
                    EditorApplication.playModeStateChanged -= PlayStateChanged;
                    EditorApplication.Exit(code);
                }
                return;
            }
            if (_report == null && Application.isPlaying && phase == "entering") StartObservation();
            if (_report == null || _done) return;
            TraceTiming(TimingEventKind.EditorUpdateBegin);
            try
            {
                if (!Application.isPlaying) { Finish("Play mode ended before acceptance completed."); return; }
                EditorApplication.QueuePlayerLoopUpdate();
                if (_gameView != null) _gameView.Repaint();
                if (WallTime() - _enteredWallTime > 55) { Finish("Normal Play-mode timeout (55 s). No manual stepping or camera rendering fallback was used."); return; }
                TraceTiming(TimingEventKind.ReturnDrainBegin);
                try { DrainBoatPackets(); }
                finally { TraceTiming(TimingEventKind.ReturnDrainEnd); }
                TraceTiming(TimingEventKind.ScreenshotPollBegin);
                try { PollScreenshots(); }
                finally { TraceTiming(TimingEventKind.ScreenshotPollEnd); }
                if (_sender != null && _sender.Error != null) throw new IOException("Fake hardware sender failed: " + _sender.Error);
                if (_recordSender != null && _recordSender.Error != null) throw new IOException("Recording hardware sender failed: " + _recordSender.Error);
                if (_acquisitionSender != null && _acquisitionSender.Error != null) throw new IOException("Acquisition sender failed: " + _acquisitionSender.Error);
                if (_stage == Stage.Starting)
                {
                    _session = UnityEngine.Object.FindObjectOfType<SimulationSession>();
                    if (_session == null || _session.LastFrame == null) return;
                    _body = _session.boat.GetComponent<Rigidbody>();
                    var surface = UnityEngine.Object.FindObjectOfType<WaterSurface>();
                    _water = surface != null ? surface.GetComponent<Renderer>() : null;
                    _report.postCompanionCount = _session.GetComponents<SimulationPostStep>().Length;
                    if (_session.drivers.Length != 1 || _session.bridge == null || _session.cable == null || _session.platform == null)
                        throw new InvalidOperationException("Desktop Demo acceptance expects one configured paddler and paired cable/platform software models.");
                    _report.inputTtlSeconds = _session.drivers[0].inputTimeoutSeconds;
                    _report.bridgeTtlSeconds = _session.bridge.maximumInputAgeSeconds;
                    _report.configurationProvenance = _session.configuration.provenance;
                    _report.measuredConfiguration = _session.configuration.measured;
                    var hull = _session.boat.GetComponent<BoatHullCollisionShape>();
                    bool contactReady = hull != null && hull.Ready && !_body.isKinematic;
                    if (contactReady) foreach (var part in hull.Colliders)
                        contactReady &= part != null && part.enabled && part.convex && !part.isTrigger && part.attachedRigidbody == _body;
                    if (!contactReady)
                        throw new InvalidOperationException("Normal Demo requires every generated contact part on its dynamic Rigidbody.");
                    if (_session.drivers[0].mode != PaddleDriver.Mode.VirtualPaddler)
                        throw new InvalidOperationException("Demo must start with its configured synthetic paddler.");
                    ConfigureAcquisitionFixture();
                    _hud = UnityEngine.Object.FindObjectOfType<RideHud>();
                    if (_hud == null || !_hud.isActiveAndEnabled)
                        throw new InvalidOperationException("Normal Demo requires its active HUD component.");
                    _report.hudDefaultSceneOnly = !_hud.visible && !_hud.compactMetricsVisible;
                    if (!_report.hudDefaultSceneOnly)
                        throw new InvalidOperationException("Normal Demo must start with scene-only display and no compact metrics.");
                    _hud.ToggleCompactMetrics();
                    _report.hudOptionalMetricsToggledAndFormatted = _hud.compactMetricsVisible
                        && _hud.EditorHeadsetMetricsBoundToMainCamera
                        && _hud.EditorHeadsetMetricsText.Contains("速度")
                        && _hud.EditorHeadsetMetricsText.Contains("距离");
                    _hud.ToggleCompactMetrics();
                    if (!_report.hudOptionalMetricsToggledAndFormatted || _hud.compactMetricsVisible)
                        throw new InvalidOperationException("Optional camera-relative speed/distance display could not be enabled and hidden.");
                    // Enable the separate desktop diagnostic panel explicitly for the
                    // technical rendering checks; the product-facing default remains clean.
                    _hud.ToggleDiagnosticsPanel();
                    if (!_hud.visible)
                        throw new InvalidOperationException("Diagnostic panel could not be enabled for the runtime HUD check.");
                    var trackingCamera = new GameObject("Runtime acceptance independent disabled tracking camera");
                    trackingCamera.AddComponent<Camera>().enabled = false;
                    _trackingFixture = trackingCamera.AddComponent<RuntimeTrackingFixture>();
                    _trackingFixture.Configure(_session.boat.transform, _report);
                    _observer = _session.StartCoroutine(ObserveNormalFixedFrames());
                    InstallFixedStartObservation();
                    BeginStage(Stage.Synthetic);
                    return;
                }
                double elapsed = Time.timeAsDouble - _stagePlayTime;
                switch (_stage)
                {
                    case Stage.Synthetic:
                        if (elapsed >= 1.0 && !_captureSynthetic) { RequestScreenshot("01_synthetic"); _captureSynthetic = true; }
                        if (elapsed >= 1.8 && !_stoppedFirstAcquisitionChannel)
                        { _acquisitionSender.OmitFirstChannel = true; _stoppedFirstAcquisitionChannel = true; }
                        if (elapsed >= 4.0)
                        {
                            _acquisitionSender.OmitFirstChannel = false;
                            _session.drivers[0].mode = PaddleDriver.Mode.UdpDirect;
                            _sender = new FakeHardwareSender(_report.inputPort, _session.drivers[0].shaftToBlade);
                            _sender.Start();
                            BeginStage(Stage.Udp);
                        }
                        break;
                    case Stage.Udp:
                        if (elapsed >= 2.0 && !_captureUdp) { RequestScreenshot("02_udp_fake_hardware"); _captureUdp = true; }
                        if (elapsed >= 2.5 && !_captureHud)
                        {
                            _hud.detailsExpanded = true;
                            RequestScreenshot("04_hud_details", "hud_details"); _captureHud = true;
                        }
                        if (elapsed >= 2.8 && !_focusAcquisitionHud)
                        { _hud.ShowAcquisitionDetails(); _focusAcquisitionHud = true; }
                        if (elapsed >= 3.05 && !_captureAcquisitionHud)
                        { RequestScreenshot("05_hud_acquisition", "hud_acquisition"); _captureAcquisitionHud = true; }
                        if (elapsed >= 3.15 && _captureAcquisitionHud) _hud.detailsExpanded = false;
                        if (elapsed >= 3.2 && _report.acquisitionStopRequestTicks == 0)
                        {
                            _report.acquisitionStopRequestTicks = Stopwatch.GetTimestamp();
                            _report.acquisitionTimingWindowStartTicks = _report.acquisitionStopRequestTicks;
                            _report.acquisitionTimingWindowEndTicks = _report.acquisitionStopRequestTicks + (long)(0.5 * Stopwatch.Frequency);
                            TraceTiming(TimingEventKind.AcquisitionStopRequested, _report.acquisitionStopRequestTicks);
                            _acquisitionSender.RequestStop();
                        }
                        if (elapsed >= 4.0)
                        {
                            // Finish stage logging while the sender is still running. Stop is
                            // nonblocking: no Join, JSON serialization or synchronous log may
                            // delay the next real fixed tick during the TTL measurement.
                            BeginStage(Stage.Coast);
                            _report.senderStopRequestTicks = Stopwatch.GetTimestamp();
                            _report.timingWindowStartTicks = _report.senderStopRequestTicks;
                            _report.timingWindowEndTicks = _report.senderStopRequestTicks + (long)(0.5 * Stopwatch.Frequency);
                            TraceTiming(TimingEventKind.SenderStopRequested, _report.senderStopRequestTicks);
                            _senderStopWallTime = _report.senderStopRequestTicks / (double)Stopwatch.Frequency;
                            _sender.RequestStop();
                        }
                        break;
                    case Stage.Coast:
                        if (elapsed >= 0.8 && !_captureCoast) { RequestScreenshot("03_expired_coasting"); _captureCoast = true; }
                        if (elapsed >= 0.2) _trackingFixture.feedTracking = false;
                        if (elapsed >= 2.0)
                        {
                            SaveAcquisitionSentPackets("acquisition_initial_sent.jsonl");
                            _acquisitionSender.Stop();
                            _report.acquisitionLastActualSendTicks = _acquisitionSender.LastActualSendTicks;
                            _report.acquisitionStoppedTicks = _acquisitionSender.StoppedTicks;
                            // Retained stale observations still belong to a real sent packet until
                            // the new sender's first packet is polled in a later normal fixed tick.
                            _initialAcquisitionSender = _acquisitionSender;
                            _acquisitionSender = new FakeAcquisitionSender(_report.acquisitionPort, _acquisitionProfile.sourceId, 100000);
                            _acquisitionSender.Start();
                            _trackingFixture.feedTracking = true;
                            _report.udpAccepted = _session.bridge.rxCount;
                            _session.drivers[0].SetMode(PaddleDriver.Mode.UdpInterpolated);
                            // 50 Hz packets feed 100 Hz physics, so replay must preserve each
                            // interpolated frame even when its source packet sequence repeats.
                            _recordSender = new FakeHardwareSender(_report.inputPort, _session.drivers[0].shaftToBlade, 100000, 0.02);
                            _recordSender.Start();
                            BeginStage(Stage.RecordSettle);
                        }
                        break;
                    case Stage.RecordSettle:
                        if (elapsed >= 0.5)
                        {
                            _session.BeginRecording(RecordingPath());
                            _report.recordingOriginSeconds = _session.SimulationTime;
                            _report.recordingFixedDeltaTime = Time.fixedDeltaTime;
                            BeginStage(Stage.Record);
                        }
                        break;
                    case Stage.Record:
                        if (elapsed >= 3.0)
                        {
                            _session.StopRecording();
                            _recordSender.Stop();
                            _acquisitionSender.Stop();
                            LoadReference();
                            _session.BeginReplay(RecordingPath());
                            _lastFrameTime = -1;
                            BeginStage(Stage.Replay);
                        }
                        break;
                    case Stage.Replay:
                        if (_session.SimulationTime >= _report.recordingDurationSeconds - 1e-6)
                            BeginStage(Stage.ReplayEof);
                        break;
                    case Stage.ReplayEof:
                        if (elapsed >= 0.5)
                        {
                            _controlRecordingHash = RecordingHash();
                            _report.replayF5EofRefused = ObserveReplayRecordingRefusal("F5_at_EOF");
                            _report.replayEofHudExit = ObserveHudReplayExit("F4_exit_EOF");
                            BeginStage(Stage.ControlsEofSynthetic);
                        }
                        break;
                    case Stage.ControlsEofSynthetic:
                        if (elapsed >= 0.3 && _metrics.fixedFrames >= 20)
                            BeginControlReplay(Stage.ControlsMiddleReplay);
                        break;
                    case Stage.ControlsMiddleReplay:
                        if (elapsed >= 0.2 && _metrics.fixedFrames >= 10)
                        {
                            _report.replayF5MiddleRefused = ObserveReplayRecordingRefusal("F5_during_replay");
                            _report.replayMiddleHudExit = ObserveHudReplayExit("F4_exit_during_replay");
                            BeginStage(Stage.ControlsMiddleSynthetic);
                        }
                        break;
                    case Stage.ControlsMiddleSynthetic:
                        if (elapsed >= 0.3 && _metrics.fixedFrames >= 20)
                            BeginControlReplay(Stage.ControlsMutationReplay);
                        break;
                    case Stage.ControlsMutationReplay:
                        var live = _session.LastFrame;
                        if (elapsed >= 0.1 && live != null && live.paddles[0].valid && live.rawWaterForceN[0].magnitude > 1f)
                        {
                            _report.replayMutationOriginalBladeArea = _session.hydrodynamics[0].bladeArea;
                            _report.replayMutationChangedBladeArea = _report.replayMutationOriginalBladeArea * 2f;
                            _session.hydrodynamics[0].bladeArea = _report.replayMutationChangedBladeArea;
                            _controlBladeAreaMutated = true;
                            RecordControlAction("change_bladeArea", true, true, PaddleDriver.Mode.Replay.ToString(),
                                "bladeArea=" + _report.replayMutationOriginalBladeArea + " -> " + _report.replayMutationChangedBladeArea);
                            BeginStage(Stage.ControlsMutationStopped);
                        }
                        else if (elapsed > _report.recordingDurationSeconds)
                            throw new InvalidOperationException("No valid nonzero-force replay frame was observed before the configuration mutation.");
                        break;
                    case Stage.ControlsMutationStopped:
                        if (elapsed >= 0.3 && _metrics.fixedFrames >= 20)
                        {
                            _session.hydrodynamics[0].bladeArea = _report.replayMutationOriginalBladeArea;
                            _controlBladeAreaMutated = false;
                            bool wasReplaying = _session.IsReplaying;
                            string previousMode = _session.drivers[0].mode.ToString();
                            _session.StopReplay();
                            RecordControlAction("F7_explicit_synthetic_recovery", wasReplaying, true, previousMode, "original bladeArea restored");
                            BeginStage(Stage.ControlsRecovered);
                        }
                        break;
                    case Stage.ControlsRecovered:
                        if (elapsed >= 0.3 && _metrics.fixedFrames >= 20) Finish(null);
                        break;
                }
            }
            catch (Exception ex) { Finish("Harness/runtime error: " + ex); }
            finally { TraceTiming(TimingEventKind.EditorUpdateEnd); }
        }

        static IEnumerator ObserveNormalFixedFrames()
        {
            while (!_done)
            {
                yield return new WaitForFixedUpdate();
                if (_done) yield break;
                try { ObserveFrame(); }
                catch (Exception ex) { Finish("Post-physics observation failed: " + ex); }
            }
        }

        static void ObserveFrame()
        {
            long observerBeginTicks = Stopwatch.GetTimestamp();
            TraceTiming(TimingEventKind.FrameObserverBegin, observerBeginTicks);
            try
            {
            SimulationFrame f = _session.LastFrame;
            if (f == null || _metrics == null) return;
            if (Math.Abs(f.timeSeconds - _lastFrameTime) < 1e-8) return;
            _lastFrameTime = f.timeSeconds;
            _report.observedFixedFrames++;
            _metrics.fixedFrames++;
            _metrics.endTime = f.timeSeconds;
            _metrics.waveEnd = f.waveTimeSeconds;
            _metrics.endPosition = f.boatPosition;
            _metrics.endSpeed = f.boatVelocity.magnitude;
            _metrics.minimumY = Mathf.Min(_metrics.minimumY, f.boatPosition.y);
            _metrics.maximumY = Mathf.Max(_metrics.maximumY, f.boatPosition.y);
            float probeHeight = _session.wave.GetHeight(3.25f, 4.5f);
            _metrics.minimumProbeHeight = Mathf.Min(_metrics.minimumProbeHeight, probeHeight);
            _metrics.maximumProbeHeight = Mathf.Max(_metrics.maximumProbeHeight, probeHeight);
            if (f.paddles == null || f.paddles.Length != 1 || f.rawWaterForceN == null || f.rawWaterForceN.Length != 1
                || f.boatPropulsionForceN == null || f.boatPropulsionForceN.Length != 1 || f.feedbackRequests == null || f.feedbackRequests.Length != 1)
                throw new InvalidDataException("A real post frame is missing expected input/water/propulsion/request data.");
            if (!SimulationValue.Finite(f.boatPosition) || !SimulationValue.Finite(f.boatVelocity)
                || !SimulationValue.Finite(f.boatRotation) || Mathf.Abs(f.boatPosition.y) > 1.5f
                || f.boatVelocity.magnitude > 20f || f.boatAngularVelocity.magnitude > 20f
                || Mathf.Abs(f.boatPosition.x) > 50f || Mathf.Abs(f.boatPosition.z) > 50f)
                throw new InvalidDataException("Normal desktop fixture boat left its physical acceptance bounds (|height| <= 1.5 m, horizontal coordinates <= 50 m, speed/angular speed <= 20).");
            PaddleSample p = f.paddles[0];
            ForceFeedbackRequest request = f.feedbackRequests[0];
            float raw = f.rawWaterForceN[0].magnitude;
            _metrics.maximumRawForceN = Mathf.Max(_metrics.maximumRawForceN, raw);
            _metrics.maximumFeedbackForceN = Mathf.Max(_metrics.maximumFeedbackForceN, request.forceWorldN.magnitude);
            if (raw > 1f) _metrics.nonzeroForceFrames++;
            if (p.valid) _metrics.validInputs++; else _metrics.invalidInputs++;
            if (_haveInput && Vector3.Distance(p.bladePosition, _lastInputPosition) > 0.001f) _metrics.inputChanges++;
            _lastInputPosition = p.bladePosition;
            _haveInput = true;
            float positionError = Vector3.Distance(f.boatPosition, _body.position);
            float rotationError = Quaternion.Angle(f.boatRotation, _body.rotation);
            float velocityError = Vector3.Distance(f.boatVelocity, _body.velocity);
            _report.maximumPostPhysicsPositionErrorM = Mathf.Max(_report.maximumPostPhysicsPositionErrorM, positionError);
            _report.maximumPostPhysicsRotationErrorDeg = Mathf.Max(_report.maximumPostPhysicsRotationErrorDeg, rotationError);
            if (positionError > 1e-5f || rotationError > 0.02f || velocityError > 1e-5f) _report.postPhysicsFailures++;
            bool identity = Math.Abs(f.timeSeconds - _session.SimulationTime) < 1e-6
                && Math.Abs(f.waveTimeSeconds - _session.wave.SimTime) < 1e-5
                && SimulationValue.Finite(f.boatPosition) && SimulationValue.Finite(f.boatVelocity)
                && SimulationValue.Finite(f.boatRotation) && SimulationValue.Finite(f.rawWaterForceN[0])
                && SimulationValue.Finite(f.boatPropulsionForceN[0]) && SimulationValue.Finite(request.forceWorldN)
                && f.cableFeedback != null && f.platformFeedback != null
                && f.cableFeedback.simulated && f.platformFeedback.simulated && f.platformFeedback.valid && f.platformRequest.valid
                && f.cableFeedback.commandSequence == request.commandSequence
                && f.platformFeedback.commandSequence == f.platformRequest.commandSequence
                && Math.Abs(f.cableFeedback.timeSeconds - request.timeSeconds) < 1e-6
                && Math.Abs(f.platformFeedback.timeSeconds - f.platformRequest.timeSeconds) < 1e-6;
            if (p.valid) identity &= request.valid && request.inputSequence == p.sequence
                && Math.Abs(request.timeSeconds - p.timeSeconds) < 1e-6;
            if (!identity) _report.postFrameIdentityFailures++;
            ObserveAcquisition(f);
            ObserveHud(f);
            long postTicks = Stopwatch.GetTimestamp();
            TraceTiming(TimingEventKind.FrameObserverPost, postTicks);
            bool timingWindow = InAnyTimingWindow(postTicks);
            TraceTiming(TimingEventKind.FrameEvidenceWriteBegin);
            try { _frameWriter.WriteLine(JsonUtility.ToJson(new FrameEvidence
            {
                stage = _stage.ToString(), unityFrameCount = Time.frameCount,
                inputMode = _session.drivers[0].mode.ToString(), sessionReplaying = _session.IsReplaying,
                replayError = _session.ReplayError,
                realTimeSeconds = Time.realtimeSinceStartupAsDouble, fixedTimeSeconds = Time.fixedTimeAsDouble,
                postPhysicsPositionErrorM = positionError, postPhysicsRotationErrorDeg = rotationError,
                postPhysicsVelocityError = velocityError, frame = f,
                fixedInputObservationTicks = _fixedInputObservationTicks, fixedPostObservationTicks = postTicks,
                acceptedReceiveTicks = _acceptedReceiveTicks, acceptedInputSequence = _acceptedInputSequence,
                acceptedInputAgeAtFixedStartSeconds = (_fixedInputObservationTicks - _acceptedReceiveTicks) / (double)Stopwatch.Frequency,
                coastTimingWindow = InTimingWindow(postTicks), acquisitionTimingWindow = InAcquisitionTimingWindow(postTicks),
                frameObserverBeginTicks = observerBeginTicks,
                previousFrameObserverEndTicks = _previousFrameObserverEndTicks,
                lastCameraRenderBeginTicks = timingWindow ? _lastCameraRenderBeginTicks : 0,
                lastCameraRenderEndTicks = timingWindow ? _lastCameraRenderEndTicks : 0,
                fixedStartGc0 = timingWindow ? _fixedStartGc0 : -1,
                fixedStartGc1 = timingWindow ? _fixedStartGc1 : -1,
                fixedStartGc2 = timingWindow ? _fixedStartGc2 : -1,
                observerPostGc0 = timingWindow ? _timingGc0 : -1,
                observerPostGc1 = timingWindow ? _timingGc1 : -1,
                observerPostGc2 = timingWindow ? _timingGc2 : -1
            }));
                _report.frameEvidenceQueuedLines++;
                _report.frameEvidenceMaximumQueuedBytes = Math.Max(_report.frameEvidenceMaximumQueuedBytes, _frameWriter.QueuedBytes);
            }
            finally { TraceTiming(TimingEventKind.FrameEvidenceWriteEnd); }
            if (_stage == Stage.Udp && p.valid)
            {
                UdpSequences.Add(p.sequence);
                if (p.source != PaddleInputKind.Udp || !_sender.TryGet(p.sequence, out SentPacket sent)
                    || p.deviceTimeUs != sent.timestampUs || Mathf.Abs(p.gripForceN - sent.gripForceN) > 1e-5f)
                    _report.udpIdentityFailures++;
            }
            if (_stage == Stage.Coast)
            {
                double stoppedFor = (postTicks - _report.senderStopRequestTicks) / (double)Stopwatch.Frequency;
                double ageAtFixedStart = (_fixedInputObservationTicks - _acceptedReceiveTicks) / (double)Stopwatch.Frequency;
                if (_previousCoastFixedStartTicks > 0)
                    _report.maximumCoastFixedStartGapSeconds = Math.Max(_report.maximumCoastFixedStartGapSeconds,
                        (_fixedInputObservationTicks - _previousCoastFixedStartTicks) / (double)Stopwatch.Frequency);
                _previousCoastFixedStartTicks = _fixedInputObservationTicks;
                if (_previousCoastPostTicks > 0) _report.maximumCoastFixedObserverGapSeconds = Math.Max(_report.maximumCoastFixedObserverGapSeconds,
                    (postTicks - _previousCoastPostTicks) / (double)Stopwatch.Frequency);
                _previousCoastPostTicks = postTicks;
                if (!p.valid && _report.firstExpiredAfterSenderStopSeconds < 0)
                {
                    _report.firstExpiredAfterSenderStopSeconds = stoppedFor;
                    // Measure at the fixed-start observer where the driver's freshness
                    // decision is made. Post-physics time also includes an unrelated,
                    // variable main-thread/physics gap and can overstate observed TTL.
                    _report.firstExpiredAcceptedAgeSeconds = (_fixedInputObservationTicks - _acceptedReceiveTicks) / (double)Stopwatch.Frequency;
                    _report.finalAcceptedReceiveTicks = _acceptedReceiveTicks;
                }
                // At this marker the accepted sample's exact local age already exceeds
                // the configured 100 ms. All normal input callbacks execute afterwards.
                // If the accepted sequence did not change, a still-valid force is a real
                // freshness violation. There is no extra age tolerance in this assertion.
                if (ageAtFixedStart > _session.drivers[0].inputTimeoutSeconds && AcceptedSequenceUnchanged())
                {
                    _report.fixedStartExpiredInputChecks++;
                    if (ZeroExpired(f)) _report.expiryZeroFrames++; else _report.expiryViolations++;
                }
            }
            if (_stage == Stage.Replay) CompareReplay(f);
            if (_stage == Stage.ReplayEof && f.timeSeconds > _report.recordingDurationSeconds + DefaultTtl + Time.fixedDeltaTime)
                if (ZeroExpired(f)) _report.replayEofZeroFrames++; else _report.expiryViolations++;
            if (IsControlStage()) ObserveReplayControls(f);
            }
            finally
            {
                _previousFrameObserverEndTicks = Stopwatch.GetTimestamp();
                TraceTiming(TimingEventKind.FrameObserverEnd, _previousFrameObserverEndTicks);
                if (InTimingWindow(observerBeginTicks)) _report.maximumCoastFrameObserverSeconds = Math.Max(
                    _report.maximumCoastFrameObserverSeconds,
                    (_previousFrameObserverEndTicks - observerBeginTicks) / (double)Stopwatch.Frequency);
            }
        }

        static bool IsControlStage()
        {
            return _stage == Stage.ControlsEofSynthetic || _stage == Stage.ControlsMiddleReplay
                || _stage == Stage.ControlsMiddleSynthetic || _stage == Stage.ControlsMutationReplay
                || _stage == Stage.ControlsMutationStopped || _stage == Stage.ControlsRecovered;
        }

        static string RecordingHash()
        {
            using (var algorithm = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(RecordingPath()))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        static bool ObserveReplayRecordingRefusal(string action)
        {
            bool wasReplaying = _session.IsReplaying;
            string previousMode = _session.drivers[0].mode.ToString();
            string before = RecordingHash();
            bool accepted = _session.ToggleRecording(RecordingPath());
            string after = RecordingHash();
            bool unchanged = before == _controlRecordingHash && after == _controlRecordingHash;
            RecordControlAction(action, wasReplaying, accepted, previousMode, "UI recording control", before, after, unchanged);
            return wasReplaying && !accepted && _session.IsReplaying && !_session.IsRecording && unchanged;
        }

        static bool ObserveHudReplayExit(string action)
        {
            bool wasReplaying = _session.IsReplaying;
            string previousMode = _session.drivers[0].mode.ToString();
            _hud.CycleInputMode();
            bool exited = !_session.IsReplaying && _session.drivers[0].mode == PaddleDriver.Mode.VirtualPaddler;
            RecordControlAction(action, wasReplaying, exited, previousMode, "HUD input-mode control");
            return wasReplaying && exited;
        }

        static void BeginControlReplay(Stage stage)
        {
            bool wasReplaying = _session.IsReplaying;
            string previousMode = _session.drivers[0].mode.ToString();
            _session.BeginReplay(RecordingPath());
            _lastFrameTime = -1;
            BeginStage(stage);
            RecordControlAction("BeginReplay", wasReplaying, true, previousMode, "independent control observation; original replay comparison already complete");
        }

        static void RecordControlAction(string action, bool wasReplaying, bool accepted, string previousMode, string detail,
            string beforeHash = null, string afterHash = null, bool unchanged = false)
        {
            _report.replayControls.Add(new ReplayControlEvidence
            {
                action = action, stage = _stage.ToString(), unityFrameCount = Time.frameCount,
                sessionTime = _session.SimulationTime, replayingBefore = wasReplaying, replayingAfter = _session.IsReplaying,
                inputModeBefore = previousMode, inputModeAfter = _session.drivers[0].mode.ToString(),
                recordingAfter = _session.IsRecording, actionAccepted = accepted, replayError = _session.ReplayError,
                status = _session.Status, detail = detail, recordingSha256Before = beforeHash,
                recordingSha256After = afterHash, fileUnchanged = unchanged
            });
        }

        static void ObserveReplayControls(SimulationFrame frame)
        {
            var snapshot = frame.acquisitionSnapshot;
            var paddle = frame.paddles[0];
            if (_stage == Stage.ControlsMiddleReplay || _stage == Stage.ControlsMutationReplay)
            {
                if (!_session.IsReplaying || _session.drivers[0].mode != PaddleDriver.Mode.Replay
                    || paddle.source != PaddleInputKind.Replay || !snapshot.replayed || _session.ReplayError != null)
                    _report.replayControlFailures++;
                if (_stage == Stage.ControlsMutationReplay && paddle.valid && frame.rawWaterForceN[0].magnitude > 1f)
                    _report.replayMutationWarmupForceFrames++;
                return;
            }
            if (_stage == Stage.ControlsMutationStopped)
            {
                bool stopped = !_session.IsReplaying && _session.drivers[0].mode == PaddleDriver.Mode.External
                    && !string.IsNullOrEmpty(_session.ReplayError) && ZeroExpired(frame)
                    && !snapshot.replayed && snapshot.rawValidCount == 0 && snapshot.calibratedValidCount == 0;
                if (frame.cableFeedback?.tensionsN == null) stopped = false;
                else foreach (float tension in frame.cableFeedback.tensionsN) stopped &= tension == 0f;
                if (stopped) _report.replayMutationZeroFrames++; else _report.replayMutationFailures++;
                _report.replayMutationReason = _session.ReplayError;
                return;
            }
            bool synthetic = !_session.IsReplaying && _session.drivers[0].mode == PaddleDriver.Mode.VirtualPaddler
                && paddle.valid && paddle.source == PaddleInputKind.Synthetic && frame.feedbackRequests[0].valid
                && !snapshot.replayed && _session.ReplayError == null;
            if (!synthetic) { _report.replayControlFailures++; return; }
            if (_stage == Stage.ControlsEofSynthetic) _report.replayEofSyntheticFrames++;
            if (_stage == Stage.ControlsMiddleSynthetic) _report.replayMiddleSyntheticFrames++;
            if (_stage == Stage.ControlsRecovered) _report.replayRecoveryFrames++;
        }

        static void ConfigureAcquisitionFixture()
        {
            if (_session.acquisition == null) throw new InvalidOperationException("Session acquisition bridge missing.");
            _acquisitionProfile = AcquisitionProfile.Default();
            _acquisitionProfile.version = "normal-play-software-adc-fixture-v1";
            _acquisitionProfile.sourceId = "runtime-localhost-software-fixture";
            _acquisitionProfile.enableLoopbackUdp = true;
            _acquisitionProfile.loopbackPort = AllocatePort();
            while (_acquisitionProfile.loopbackPort == _report.inputPort || _acquisitionProfile.loopbackPort == _report.returnPort)
                _acquisitionProfile.loopbackPort = AllocatePort();
            // Explicit software fixture bounds, not an inferred PCB ADC encoding or calibration.
            foreach (var channel in _acquisitionProfile.channels)
            {
                channel.maximumAgeSeconds = DefaultTtl;
                channel.rawUnits = "fixture-count";
                channel.rawRangeConfigured = true;
                channel.minimumRaw = 0; channel.maximumRaw = 10000;
            }
            _session.acquisition.Configure(_acquisitionProfile);
            if (!_session.acquisition.StartLoopback()) throw new InvalidOperationException("Explicit ADC localhost listener failed to start.");
            _report.acquisitionPort = _session.acquisition.LocalPort;
            _report.acquisitionTtlSeconds = _acquisitionProfile.channels[0].maximumAgeSeconds;
            _acquisitionSender = new FakeAcquisitionSender(_report.acquisitionPort, _acquisitionProfile.sourceId);
            _acquisitionSender.Start();
            // Compare against the configuration actually cloned and installed by the bridge.
            // JSON serialization normalizes absent strings, including calibration provenance.
            _acquisitionProfile = _session.acquisition.CurrentProfile;
        }

        static void ObserveAcquisition(SimulationFrame frame)
        {
            if (!frame.hasAcquisitionSnapshot || frame.acquisitionSnapshot == null || frame.acquisitionSnapshot.channels == null
                || frame.acquisitionSnapshot.channels.Length != 4)
                throw new InvalidDataException("Normal post-physics frame lacks four-channel ADC snapshot.");
            var snapshot = frame.acquisitionSnapshot;
            if (_stage == Stage.ReplayEof)
            {
                if (frame.timeSeconds > _report.recordingDurationSeconds + Time.fixedDeltaTime)
                {
                    bool expired = snapshot.replayed && !snapshot.valid && snapshot.rawValidCount == 0 && snapshot.calibratedValidCount == 0;
                    foreach (var channel in snapshot.channels)
                        expired &= !channel.valid && !channel.rawValid && !channel.calibratedValid && channel.rawStatus == "REPLAY_EXPIRED";
                    if (expired) _report.acquisitionReplayEofFrames++; else _report.acquisitionExpiryViolations++;
                }
                return;
            }
            if (_stage == Stage.Replay || IsControlStage()) return;
            SensorAcquisitionBridge.ValidateRecordedSnapshot(snapshot, _acquisitionProfile);
            if (snapshot.valid) _report.acquisitionValidFrames++;
            foreach (var channel in snapshot.channels)
            {
                if (!channel.present) continue;
                if (!_acquisitionSender.TryGet(channel.packetSequence, out AcquisitionPacket packet)
                    && (_initialAcquisitionSender == null || !_initialAcquisitionSender.TryGet(channel.packetSequence, out packet)))
                { _report.acquisitionIdentityFailures++; continue; }
                var sent = Array.Find(packet.channels, c => c.channelId == channel.channelId);
                bool same = sent != null && snapshot.sourceId == packet.sourceId && channel.sourceId == packet.sourceId
                    && channel.sourceKind == packet.sourceKind && channel.raw == sent.raw && channel.rawText == sent.rawText
                    && channel.hasRaw == sent.hasRaw && channel.sourceValid == sent.valid && channel.saturated == sent.saturated
                    && channel.rawUnits == sent.rawUnits && channel.sampleSequence == sent.sampleSequence
                    && channel.deviceTimeValid == packet.deviceTimeValid && channel.deviceTimeUs == packet.deviceTimeUs
                    && channel.sampleTimeValid == sent.sampleTimeValid && channel.sampleTimeUs == sent.sampleTimeUs
                    && channel.receivedMonotonicTicks > 0 && snapshot.capturedMonotonicTicks >= channel.receivedMonotonicTicks
                    && !channel.calibratedValid && !channel.calibrationMeasured && channel.forceN == 0;
                if (!same) _report.acquisitionIdentityFailures++;
            }
            if (_stage == Stage.Synthetic && _stoppedFirstAcquisitionChannel && snapshot.channels[0].ageSeconds > DefaultTtl
                && snapshot.channels[1].rawValid && snapshot.channels[2].rawValid && snapshot.channels[3].rawValid)
            {
                bool independentlyExpired = !snapshot.channels[0].rawValid && snapshot.channels[0].rawStatus == "STALE"
                    && snapshot.rawValidCount == 3 && !snapshot.samePacket && frame.paddles[0].valid;
                if (independentlyExpired) _report.acquisitionIndependentExpiryFrames++; else _report.acquisitionExpiryViolations++;
            }
            if (_report.acquisitionStopRequestTicks > 0 && (_stage == Stage.Udp || _stage == Stage.Coast))
            {
                var first = snapshot.channels[0];
                if (!first.rawValid && first.rawStatus == "STALE" && _report.acquisitionFirstExpiredAgeSeconds < 0)
                    _report.acquisitionFirstExpiredAgeSeconds = first.ageSeconds;
                bool everyExpired = true;
                foreach (var channel in snapshot.channels) everyExpired &= channel.ageSeconds > DefaultTtl;
                if (everyExpired)
                {
                    bool valid = !snapshot.valid && snapshot.rawValidCount == 0 && snapshot.calibratedValidCount == 0;
                    foreach (var channel in snapshot.channels) valid &= !channel.valid && !channel.rawValid && !channel.calibratedValid;
                    if (valid) _report.acquisitionExpiredFrames++; else _report.acquisitionExpiryViolations++;
                }
            }
            if ((_stage == Stage.Synthetic || _stage == Stage.Udp) && snapshot.rawValidCount > 0 && frame.paddles[0].valid)
            {
                var paddle = frame.paddles[0];
                bool isolated = _stage == Stage.Synthetic ? paddle.source == PaddleInputKind.Synthetic
                    : paddle.source == PaddleInputKind.Udp && _sender.TryGet(paddle.sequence, out SentPacket sent)
                        && paddle.deviceTimeUs == sent.timestampUs && Mathf.Abs(paddle.gripForceN - sent.gripForceN) < 1e-5f;
                foreach (var channel in snapshot.channels) if (channel.hasRaw) isolated &= Math.Abs(channel.raw - paddle.gripForceN) > 100;
                _report.acquisitionPaddleIsolationFrames++;
                if (!isolated) _report.acquisitionPaddleIsolationFailures++;
            }
        }

        static void ObserveHud(SimulationFrame frame)
        {
            if (_hud == null || !_hud.isActiveAndEnabled || !RideHud.HasActivePanel) return;
            if (!SimulationValue.Finite(_hud.StrokeRateSpm) || !SimulationValue.Finite(_hud.LastImpulse)
                || !SimulationValue.Finite(_hud.AvgImpulse) || !SimulationValue.Finite(_hud.Distance))
                throw new InvalidDataException("HUD physics statistics are non-finite.");
            _report.hudPhysicsSamples++;
            _report.hudMaximumStrokeCount = Math.Max(_report.hudMaximumStrokeCount, _hud.StrokeCount);
            _report.hudMaximumImpulseNs = Mathf.Max(_report.hudMaximumImpulseNs, _hud.LastImpulse);
            _report.hudMaximumDistanceM = Mathf.Max(_report.hudMaximumDistanceM, _hud.Distance);
            _report.hudFontName = _hud.DisplayFontName;
        }

        static bool ZeroExpired(SimulationFrame f)
        {
            return !f.paddles[0].valid && !f.feedbackRequests[0].valid
                && f.rawWaterForceN[0].sqrMagnitude <= 1e-12f
                && f.boatPropulsionForceN[0].sqrMagnitude <= 1e-12f
                && f.feedbackRequests[0].forceWorldN.sqrMagnitude <= 1e-12f
                && !_session.hydrodynamics[0].InputValid
                && _session.hydrodynamics[0].WaterForceN.sqrMagnitude <= 1e-12f
                && f.cableFeedback != null && !f.cableFeedback.valid
                && f.cableFeedback.forceWorldN.sqrMagnitude <= 1e-12f;
        }

        static void BeginStage(Stage stage)
        {
            _stage = stage;
            _stagePlayTime = Time.timeAsDouble;
            SimulationFrame f = _session.LastFrame;
            float probe = _session.wave.GetHeight(3.25f, 4.5f);
            _metrics = new StageEvidence
            {
                name = stage.ToString(), startTime = _session.SimulationTime, endTime = _session.SimulationTime,
                waveStart = _session.wave.SimTime, waveEnd = _session.wave.SimTime,
                startPosition = _body.position, endPosition = _body.position,
                startSpeed = _body.velocity.magnitude, endSpeed = _body.velocity.magnitude,
                minimumY = _body.position.y, maximumY = _body.position.y,
                minimumProbeHeight = probe, maximumProbeHeight = probe
            };
            _report.stages.Add(_metrics);
            _haveInput = false;
            Debug.Log("[RUNTIME] Stage=" + stage + " session=" + _session.SimulationTime.ToString("F3") + " wave=" + _session.wave.SimTime.ToString("F3"));
        }

        static void LoadReference()
        {
            _reference = new List<SimulationFrame>();
            ReferenceByTick.Clear();
            SimulationFrame header = null;
            RecordingCompletion completion = null;
            double lastIntegrityTime = double.NegativeInfinity;
            using (var contentHash = new RecordingContentHash())
            {
            foreach (string line in File.ReadLines(RecordingPath()))
            {
                AcquisitionJsonEvidence.ValidateFrameJson(line);
                if (completion != null) throw new InvalidDataException("Data after recording completion.");
                SimulationFrame f = JsonUtility.FromJson<SimulationFrame>(line);
                if (f.recordType == "complete")
                { completion = JsonUtility.FromJson<RecordingCompletion>(line);
                    completion.lastFrameTimeSeconds = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(line, true); continue; }
                contentHash.Append(line);
                if (f.recordType == "header") header = f;
                else if (f.recordType == "frame")
                { _reference.Add(f); lastIntegrityTime = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(line); }
                else throw new InvalidDataException("Unexpected record type in real recording.");
            }
            if (completion == null || completion.frameCount != _reference.Count
                || completion.contentSha256 != contentHash.FinishHex()
                || completion.configurationSignature != header.configurationSignature
                || completion.lastFrameTimeSeconds != lastIntegrityTime)
                throw new InvalidDataException("Recording completion evidence mismatch.");
            }
            if (header == null || _reference.Count < 100 || header.timeSeconds <= 1)
                throw new InvalidDataException("Recording must contain real fixed frames and start from a nonzero session time.");
            if (Math.Abs(header.fixedStepSeconds - Time.fixedDeltaTime) > 1e-6)
                throw new InvalidDataException("Recording header does not describe the actual current fixed timestep.");
            _report.acquisitionHeaderProfileMatches = header.hasAcquisitionSnapshot
                && JsonUtility.ToJson(header.acquisitionProfile) == _session.acquisition.ConfigurationSignature;
            SensorAcquisitionBridge.ValidateRecordedSnapshot(header.acquisitionSnapshot, header.acquisitionProfile);
            _report.recordingOriginSeconds = header.timeSeconds;
            _report.recordedFrames = _reference.Count;
            _report.recordingDurationSeconds = _reference[_reference.Count - 1].timeSeconds - header.timeSeconds;
            double previous = header.timeSeconds;
            PaddleSample previousInput = header.paddles[0];
            foreach (SimulationFrame f in _reference)
            {
                if (f.hasAcquisitionSnapshot && f.acquisitionSnapshot != null) _report.acquisitionRecordedFrames++;
                if (Math.Abs(f.timeSeconds - previous - Time.fixedDeltaTime) > 1e-6)
                    throw new InvalidDataException("The normal post recorder missed/duplicated a fixed step or changed its timestep.");
                long tick = (long)Math.Round((f.timeSeconds - header.timeSeconds) / Time.fixedDeltaTime);
                ReferenceByTick.Add(tick, f);
                PaddleSample input = f.paddles[0];
                if (input.valid && input.source == PaddleInputKind.Udp) _report.recordedUdpFrames++;
                if (input.sequence == previousInput.sequence && input.valid && previousInput.valid)
                {
                    _report.recordedRepeatedSequences++;
                    if (Vector3.Distance(input.bladePosition, previousInput.bladePosition) > 1e-5f
                        || Vector3.Distance(input.bladeVelocity, previousInput.bladeVelocity) > 1e-5f)
                        _report.recordedChangingRepeatedSequences++;
                }
                previousInput = input;
                previous = f.timeSeconds;
            }
        }

        static void CompareReplay(SimulationFrame actual)
        {
            // Editor updates may observe several normal catch-up fixed ticks at once. Ticks
            // beyond the recorded duration belong to the separately checked EOF expiry phase.
            if (actual.timeSeconds > _report.recordingDurationSeconds + 1e-6) return;
            long tick = (long)Math.Round(actual.timeSeconds / _report.recordingFixedDeltaTime);
            if (!ReferenceByTick.TryGetValue(tick, out SimulationFrame expected)) { _report.replayMissingFrames++; return; }
            _report.replayComparedFrames++;
            var replaySnapshot = actual.acquisitionSnapshot;
            var expectedSnapshot = AcquisitionSnapshot.ForReplay(expected.acquisitionSnapshot);
            _report.acquisitionReplayComparedFrames++;
            if (!actual.hasAcquisitionSnapshot || replaySnapshot == null || !replaySnapshot.replayed
                || JsonUtility.ToJson(replaySnapshot) != JsonUtility.ToJson(expectedSnapshot))
                _report.acquisitionReplayFieldFailures++;
            if (ReferenceEquals(replaySnapshot, _previousReplayAcquisition)
                || (replaySnapshot != null && _previousReplayAcquisition != null
                    && ReferenceEquals(replaySnapshot.channels, _previousReplayAcquisition.channels)))
                _report.acquisitionReplayCloneFailures++;
            _previousReplayAcquisition = replaySnapshot;
            _report.replayInputErrorM = Mathf.Max(_report.replayInputErrorM,
                Vector3.Distance(actual.paddles[0].bladePosition, expected.paddles[0].bladePosition));
            _report.replayForceErrorN = Mathf.Max(_report.replayForceErrorN,
                Vector3.Distance(actual.rawWaterForceN[0], expected.rawWaterForceN[0]));
            _report.replayBoatErrorM = Mathf.Max(_report.replayBoatErrorM, Vector3.Distance(actual.boatPosition, expected.boatPosition));
            _report.replayWaveErrorSeconds = Mathf.Max(_report.replayWaveErrorSeconds, Mathf.Abs(actual.waveTimeSeconds - expected.waveTimeSeconds));
            PaddleSample p = actual.paddles[0];
            if (p.source != PaddleInputKind.Replay || p.originalSource != expected.paddles[0].source
                || p.valid != expected.paddles[0].valid || p.sequence != expected.paddles[0].sequence
                || p.deviceTimeUs != expected.paddles[0].deviceTimeUs
                || Vector3.Distance(p.bladeVelocity, expected.paddles[0].bladeVelocity) > 1e-5f
                || Math.Abs(p.timeSeconds - (expected.paddles[0].timeSeconds - _report.recordingOriginSeconds)) > 1e-6
                || actual.feedbackRequests[0].inputSequence != p.sequence)
                _report.replayIdentityFailures++;
            _report.replayPlatformRequestErrorM = Mathf.Max(_report.replayPlatformRequestErrorM,
                Vector3.Distance(actual.platformRequest.translationM, expected.platformRequest.translationM),
                Vector3.Distance(actual.platformRequest.rawTranslationM, expected.platformRequest.rawTranslationM));
            if (actual.platformFeedback == null || expected.platformFeedback == null
                || actual.cableFeedback == null || expected.cableFeedback == null)
            { _report.replayDeviceStateFailures++; return; }
            _report.replayPlatformFeedbackErrorM = Mathf.Max(_report.replayPlatformFeedbackErrorM,
                Vector3.Distance(actual.platformFeedback.translationM, expected.platformFeedback.translationM));
            _report.replayPlatformAngleErrorDeg = Mathf.Max(_report.replayPlatformAngleErrorDeg,
                EulerError(actual.platformRequest.eulerDegrees, expected.platformRequest.eulerDegrees),
                EulerError(actual.platformRequest.rawEulerDegrees, expected.platformRequest.rawEulerDegrees),
                EulerError(actual.platformFeedback.eulerDegrees, expected.platformFeedback.eulerDegrees));
            _report.replayCableForceErrorN = Mathf.Max(_report.replayCableForceErrorN,
                Vector3.Distance(actual.cableFeedback.forceWorldN, expected.cableFeedback.forceWorldN));
            if (actual.cableFeedback.tensionsN == null || expected.cableFeedback.tensionsN == null
                || actual.cableFeedback.tensionsN.Length != expected.cableFeedback.tensionsN.Length)
                _report.replayDeviceStateFailures++;
            else for (int i = 0; i < actual.cableFeedback.tensionsN.Length; i++)
                _report.replayCableTensionErrorN = Mathf.Max(_report.replayCableTensionErrorN,
                    Mathf.Abs(actual.cableFeedback.tensionsN[i] - expected.cableFeedback.tensionsN[i]));
            if (actual.platformRequest.valid != expected.platformRequest.valid || actual.platformRequest.clamped != expected.platformRequest.clamped
                || actual.platformFeedback.valid != expected.platformFeedback.valid
                || actual.platformFeedback.capabilityLimited != expected.platformFeedback.capabilityLimited
                || actual.cableFeedback.valid != expected.cableFeedback.valid
                || actual.cableFeedback.capabilityLimited != expected.cableFeedback.capabilityLimited
                || actual.cableFeedback.status != expected.cableFeedback.status)
                _report.replayDeviceStateFailures++;
        }

        static float EulerError(Vector3 actual, Vector3 expected)
            => Mathf.Max(Mathf.Abs(Mathf.DeltaAngle(actual.x, expected.x)),
                Mathf.Abs(Mathf.DeltaAngle(actual.y, expected.y)), Mathf.Abs(Mathf.DeltaAngle(actual.z, expected.z)));

        static void RequestScreenshot(string name, string purpose = "scene")
        {
            var shot = new ScreenshotEvidence
            {
                stage = _stage.ToString(), file = name + ".png", purpose = purpose, sessionTime = _session.SimulationTime,
                frameCountRequested = Time.frameCount, renderCountRequested = _report.renderCallbacks, requestedAt = WallTime()
            };
            _report.screenshots.Add(shot);
            ScreenCapture.CaptureScreenshot(Path.Combine(_report.artifacts, shot.file));
        }

        static void PollScreenshots()
        {
            foreach (ScreenshotEvidence shot in _report.screenshots)
            {
                if (shot.completed) continue;
                string path = Path.Combine(_report.artifacts, shot.file);
                if (!File.Exists(path)) continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); }
                catch (IOException) { continue; }
                if (bytes.Length < 100) continue;
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                try
                {
                    if (!texture.LoadImage(bytes)) continue;
                    shot.width = texture.width; shot.height = texture.height;
                    Color32[] pixels = texture.GetPixels32();
                    for (int y = texture.height - 390; y < texture.height - 105; y++)
                        for (int x = 20; x < 340; x++)
                        {
                            Color32 c = pixels[y * texture.width + x];
                            if (c.r > 150 && c.g > 150 && c.b > 150) shot.hudTextPixels++;
                        }
                    // Lower main-view region excludes every changing text HUD. A changed clock
                    // label cannot satisfy scene motion or rescue an otherwise blank screenshot.
                    var samples = new List<byte>();
                    var bins = new HashSet<int>();
                    double sum = 0, squares = 0;
                    int count = 0;
                    for (int iy = 0; iy < 60; iy++)
                        for (int ix = 0; ix < 80; ix++)
                        {
                            int x = Mathf.Clamp(Mathf.RoundToInt(texture.width * (0.05f + 0.55f * ix / 79f)), 0, texture.width - 1);
                            int y = Mathf.Clamp(Mathf.RoundToInt(texture.height * (0.06f + 0.60f * iy / 59f)), 0, texture.height - 1);
                            Color32 c = pixels[y * texture.width + x];
                            byte luma = (byte)((77 * c.r + 150 * c.g + 29 * c.b) >> 8);
                            samples.Add(luma); sum += luma; squares += luma * luma; count++;
                            bins.Add((c.r / 32) * 64 + (c.g / 32) * 8 + c.b / 32);
                        }
                    shot.sceneSamples = samples.ToArray();
                    shot.sceneLuminanceStdDev = (float)Math.Sqrt(Math.Max(0, squares / count - sum * sum / count / count));
                    shot.sceneColorBins = bins.Count;
                    shot.renderCountCompleted = _report.renderCallbacks;
                    shot.hasSceneContent = texture.width == 1280 && texture.height == 720
                        && shot.sceneLuminanceStdDev > 8 && bins.Count >= 12
                        && shot.renderCountCompleted > shot.renderCountRequested;
                    shot.completed = true;
                    if (_report.screenshots.Count > 1 && _report.screenshots[0].sceneSamples != null && shot != _report.screenshots[0])
                    {
                        byte[] first = _report.screenshots[0].sceneSamples;
                        int changed = 0;
                        for (int i = 0; i < first.Length; i++) if (Math.Abs(first[i] - shot.sceneSamples[i]) > 6) changed++;
                        shot.sceneChangedFraction = changed / (float)first.Length;
                    }
                    Debug.Log("[RUNTIME] Normal screenshot=" + shot.file + " " + shot.width + "x" + shot.height + " content=" + shot.hasSceneContent);
                }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
            }
        }

        static void TraceCameraTiming(TimingEventKind kind, Camera camera)
        {
            if (_report == null) return;
            long ticks = Stopwatch.GetTimestamp();
            if (!InAnyTimingWindow(ticks) || camera == null || camera.cameraType != CameraType.Game
                || camera.targetTexture != null) return;
            TraceTiming(kind, ticks, camera.GetInstanceID());
        }
        static void OnBuiltinRenderBegin(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null) TraceCameraTiming(TimingEventKind.CameraRenderBegin, camera);
        }
        static void OnPipelineRenderBegin(ScriptableRenderContext context, Camera camera)
        {
            TraceCameraTiming(TimingEventKind.CameraRenderBegin, camera);
        }
        static void OnBuiltinRendered(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null) ObserveRenderedCamera(camera);
        }
        static void OnPipelineRendered(ScriptableRenderContext context, Camera camera) { ObserveRenderedCamera(camera); }
        static void ObserveRenderedCamera(Camera camera)
        {
            TraceCameraTiming(TimingEventKind.CameraRenderEnd, camera);
            try { OnRendered(camera); }
            finally { TraceCameraTiming(TimingEventKind.RenderObserverEnd, camera); }
        }
        static void OnRendered(Camera camera)
        {
            if (_done || _report == null || !Application.isPlaying || camera == null
                || camera.cameraType != CameraType.Game || camera.targetTexture != null) return;
            _report.renderCallbacks++;
            if (_water == null || _session == null) return;
            // Callbacks run sequentially on the main thread and each read fully refreshes the block.
            _water.GetPropertyBlock(_renderObservationBlock);
            _report.renderWaveSamples++;
            _report.maximumShaderWaveTimeError = Mathf.Max(_report.maximumShaderWaveTimeError,
                Mathf.Abs(_renderObservationBlock.GetFloat("_SimTime") - _session.wave.SimTime));
        }

        static void DrainBoatPackets()
        {
            if (_boatListener == null) return;
            IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
            for (int i = 0; i < 1000 && _boatListener.Available > 0; i++)
            {
                byte[] data = _boatListener.Receive(ref source);
                if (data.Length != HardwareUdpBridge.PacketSize || BitConverter.ToUInt32(data, 0) != HardwareUdpBridge.MagicBoatState
                    || BitConverter.ToUInt32(data, 4) != HardwareUdpBridge.ProtoVersion)
                    throw new InvalidDataException("Invalid BoatState received through the loopback return socket.");
                BoatPacket p = new BoatPacket
                {
                    timestampUs = BitConverter.ToUInt64(data, 8), receiveSequence = BitConverter.ToUInt32(data, 16),
                    roll = BitConverter.ToSingle(data, 20), pitch = BitConverter.ToSingle(data, 24), yaw = BitConverter.ToSingle(data, 28),
                    dragN = BitConverter.ToSingle(data, 32), speedMps = BitConverter.ToSingle(data, 36),
                    waveHeightM = BitConverter.ToSingle(data, 40), echoedTimestampLow = BitConverter.ToUInt32(data, 44),
                    stage = _stage.ToString(), receivedRealTimeSeconds = Time.realtimeSinceStartupAsDouble
                };
                if (!SimulationValue.Finite(p.roll) || !SimulationValue.Finite(p.pitch) || !SimulationValue.Finite(p.yaw)
                    || !SimulationValue.Finite(p.dragN) || !SimulationValue.Finite(p.speedMps) || !SimulationValue.Finite(p.waveHeightM))
                    throw new InvalidDataException("Non-finite BoatState returned during normal Play mode.");
                p.echoMatchesSentPacket = _sender != null && _sender.HasTimestampLow(p.echoedTimestampLow);
                if (_stage == Stage.Udp)
                {
                    _report.boatPackets++;
                    if (p.echoMatchesSentPacket) _report.boatEchoMatches++;
                    if (p.dragN > 1) _report.boatNonzeroDragPackets++;
                    _report.maximumBoatPacketDragN = Mathf.Max(_report.maximumBoatPacketDragN, p.dragN);
                    _report.maximumBoatPacketSpeedMps = Mathf.Max(_report.maximumBoatPacketSpeedMps, p.speedMps);
                }
                if (_stage == Stage.Coast && WallTime() - _senderStopWallTime > 0.3 && p.dragN <= 1e-6f)
                    _report.boatExpiredZeroPackets++;
                TraceTiming(TimingEventKind.ReturnEvidenceWriteBegin);
                try
                {
                    _returnWriter.WriteLine(JsonUtility.ToJson(p));
                    _report.returnEvidenceQueuedLines++;
                    _report.returnEvidenceMaximumQueuedBytes = Math.Max(_report.returnEvidenceMaximumQueuedBytes, _returnWriter.QueuedBytes);
                }
                finally { TraceTiming(TimingEventKind.ReturnEvidenceWriteEnd); }
            }
        }

        static void SaveSentPackets()
        {
            if (_sender == null) return;
            SentPacket[] sent = _sender.Snapshot();
            _report.udpSent = sent.Length;
            using (var writer = new StreamWriter(Path.Combine(_report.artifacts, "fake_hardware_sent.jsonl"), false))
                foreach (SentPacket p in sent) writer.WriteLine(JsonUtility.ToJson(p));
        }

        static BackgroundRecordingWriter CreateEvidenceWriter(string file)
        {
            var sink = new StreamWriter(new FileStream(Path.Combine(_report.artifacts, file), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read), new UTF8Encoding(false, true));
            try { return new BackgroundRecordingWriter(sink); }
            catch { sink.Dispose(); throw; }
        }
        static string VerifyDrainedEvidenceFile(string file, long expectedLines)
        {
            if (expectedLines <= 0) throw new InvalidDataException("Empty normal-runtime evidence: " + file);
            long lines = 0;
            using (var reader = new StreamReader(Path.Combine(_report.artifacts, file), new UTF8Encoding(false, true), false))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                { if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Empty evidence line: " + file); lines++; }
            }
            if (lines != expectedLines) throw new InvalidDataException("Drained evidence line count differs: " + file);
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (var input = File.OpenRead(Path.Combine(_report.artifacts, file)))
                return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        static void CloseEvidenceWriters()
        {
            Exception failure = null;
            foreach (var writer in new[] { _frameWriter, _returnWriter })
                try { writer?.Dispose(); } catch (Exception error) { failure = failure ?? error; }
            if (failure != null) throw new IOException("Normal-runtime evidence did not drain and close.", failure);
            if (_frameWriter == null || _returnWriter == null) throw new InvalidDataException("Normal-runtime evidence writer missing.");
            _report.frameEvidenceWrittenLines = _frameWriter.WrittenLines;
            _report.returnEvidenceWrittenLines = _returnWriter.WrittenLines;
            _report.frameEvidenceMaximumBackgroundWriteSeconds = _frameWriter.MaximumWriteSeconds;
            _report.returnEvidenceMaximumBackgroundWriteSeconds = _returnWriter.MaximumWriteSeconds;
            if (_report.frameEvidenceWrittenLines != _report.frameEvidenceQueuedLines
                || _report.returnEvidenceWrittenLines != _report.returnEvidenceQueuedLines)
                throw new InvalidDataException("Normal-runtime evidence queue/drain counts differ.");
            _report.frameEvidenceSha256 = VerifyDrainedEvidenceFile("normal_fixed_frames.jsonl", _report.frameEvidenceQueuedLines);
            _report.returnEvidenceSha256 = VerifyDrainedEvidenceFile("boatstate_loopback.jsonl", _report.returnEvidenceQueuedLines);
            _report.evidenceWritersDrainedAndCountVerified = true;
        }

        static void SaveAcquisitionSentPackets(string file)
        {
            if (_acquisitionSender == null) return;
            var packets = _acquisitionSender.Snapshot();
            _report.acquisitionSent += packets.Length;
            using (var writer = new StreamWriter(Path.Combine(_report.artifacts, file), false))
                foreach (var packet in packets) writer.WriteLine(JsonUtility.ToJson(packet));
        }

        static void Finish(string error)
        {
            if (_done) return;
            _done = true;
            if (_report == null) { FailBeforePlay(new Exception(error ?? "No report")); return; }
            if (!string.IsNullOrEmpty(error)) _report.errors.Add(error);
            // Restore and inspect the actual loop before deciding or writing the result.
            // Do this independently of later sender/file cleanup, which can also fail.
            try { RestoreFixedStartObservation(); }
            catch (Exception ex) { _report.errors.Add("PlayerLoop restoration failed: " + ex); }
            try { RestoreDedicatedGameView(); }
            catch (Exception ex) { _report.errors.Add("Game view restoration failed: " + ex); }
            try
            {
                _sender?.Stop();
                _recordSender?.Stop();
                _acquisitionSender?.Stop();
                _initialAcquisitionSender?.Stop();
                if (_controlBladeAreaMutated && _session != null)
                {
                    _session.hydrodynamics[0].bladeArea = _report.replayMutationOriginalBladeArea;
                    _controlBladeAreaMutated = false;
                }
                SaveAcquisitionSentPackets("acquisition_record_sent.jsonl");
                if (_session != null && _session.acquisition != null)
                {
                    _report.acquisitionAccepted = _session.acquisition.acceptedPackets;
                    _report.acquisitionRejected = _session.acquisition.rejectedPackets;
                }
                if (_sender != null) SaveSentPackets();
                if (_recordSender != null)
                {
                    SentPacket[] sent = _recordSender.Snapshot();
                    _report.recordUdpSent = sent.Length;
                    using (var writer = new StreamWriter(Path.Combine(_report.artifacts, "record_hardware_sent.jsonl"), false))
                        foreach (SentPacket p in sent) writer.WriteLine(JsonUtility.ToJson(p));
                }
                if (_sender != null)
                {
                    _report.senderLastActualSendTicks = _sender.LastActualSendTicks;
                    _report.senderStoppedTicks = _sender.StoppedTicks;
                }
                _session?.StopRecording();
                _boatListener?.Close(); CloseEvidenceWriters();
                SaveTimingEvidence();
                _report.udpInputSequences = UdpSequences.Count;
                _report.finishedUtc = DateTime.UtcNow.ToString("O");
                AddChecks();
                bool passed = _report.checks.Count >= 8 && _report.errors.Count == 0;
                foreach (Check check in _report.checks) passed &= check.passed;
                _report.result = passed ? "PASS" : "FAIL";
                File.WriteAllText(Path.Combine(_report.artifacts, "runtime_result.json"), JsonUtility.ToJson(_report, true));
                SessionState.SetInt(ExitKey, passed ? 0 : 1);
                Debug.Log("[RUNTIME] RESULT=" + _report.result + " desktop software only; artifacts=" + _report.artifacts);
                foreach (Check check in _report.checks) Debug.Log("[RUNTIME] " + check.name + "=" + (check.passed ? "PASS" : "FAIL") + " " + check.evidence);
            }
            catch (Exception ex)
            {
                SessionState.SetInt(ExitKey, 1);
                Debug.Log("[RUNTIME] RESULT=FAIL Could not complete evidence: " + ex);
            }
            Application.logMessageReceived -= OnLog;
            Camera.onPostRender -= OnBuiltinRendered;
            Camera.onPreRender -= OnBuiltinRenderBegin;
            RenderPipelineManager.endCameraRendering -= OnPipelineRendered;
            RenderPipelineManager.beginCameraRendering -= OnPipelineRenderBegin;
            SessionState.SetString(PhaseKey, "finishing");
            if (Application.isPlaying) EditorApplication.ExitPlaymode();
        }

        static void AddChecks()
        {
            CheckResult("normal_runtime_evidence_background_writer_complete", _report.evidenceWritersDrainedAndCountVerified
                && _report.frameEvidenceWrittenLines == _report.observedFixedFrames && _report.returnEvidenceWrittenLines >= 100
                && _report.frameEvidenceMaximumQueuedBytes <= 4 * 1024 * 1024
                && _report.returnEvidenceMaximumQueuedBytes <= 4 * 1024 * 1024,
                "bounded background IO; durable drain/line counts verified; frameQueued/written=" + _report.frameEvidenceQueuedLines
                + "/" + _report.frameEvidenceWrittenLines + "; returnQueued/written=" + _report.returnEvidenceQueuedLines
                + "/" + _report.returnEvidenceWrittenLines + "; frame/returnSHA256=" + _report.frameEvidenceSha256 + "/" + _report.returnEvidenceSha256);
            StageEvidence synthetic = GetStage(Stage.Synthetic), udp = GetStage(Stage.Udp), coast = GetStage(Stage.Coast), eof = GetStage(Stage.ReplayEof);
            CheckResult("normal_fixed_post_chain", _report.physicsAutomatic && _report.postCompanionCount == 1
                && _report.observedFixedFrames >= 1000 && _report.postFrameIdentityFailures == 0 && _report.postPhysicsFailures == 0
                && _report.fixedStartObserversBeforeInstall == 0 && _report.fixedStartObserversInstalled == 1
                && _report.fixedStartObserversBeforeRestore == 1 && _report.fixedStartObserversRestored == 0
                && _report.fixedStartOriginalLoopUnchanged && _report.fixedStartInstalledLoopMatches && _report.fixedStartRestoredLoopMatches,
                "fixedFrames=" + _report.observedFixedFrames + "; companions=" + _report.postCompanionCount + "; identityFailures=" + _report.postFrameIdentityFailures
                + "; postPositionErrorM=" + _report.maximumPostPhysicsPositionErrorM
                + "; fixedStartObserversBefore/installed/beforeRestore/restored=" + _report.fixedStartObserversBeforeInstall
                + "/" + _report.fixedStartObserversInstalled + "/" + _report.fixedStartObserversBeforeRestore + "/" + _report.fixedStartObserversRestored
                + "; originalLoopUnchanged=" + _report.fixedStartOriginalLoopUnchanged
                + "; installed/restoredLoopMatches=" + _report.fixedStartInstalledLoopMatches + "/" + _report.fixedStartRestoredLoopMatches);
            CheckResult("synthetic_propulsion_and_independent_wave_clock", synthetic != null && synthetic.fixedFrames >= 200
                && synthetic.validInputs >= 200 && synthetic.nonzeroForceFrames >= 20 && synthetic.inputChanges >= 100
                && synthetic.endPosition.z - synthetic.startPosition.z > 0.1f && synthetic.waveEnd - synthetic.waveStart > 3.8
                && synthetic.maximumProbeHeight - synthetic.minimumProbeHeight > 0.02f,
                StageText(synthetic));
            CheckResult("software_udp_input_identity_and_force", udp != null && _report.udpSent >= 200 && _report.udpAccepted >= 150
                && UdpSequences.Count >= 100 && udp.validInputs >= 150 && udp.nonzeroForceFrames >= 20 && udp.inputChanges >= 100
                && _report.udpIdentityFailures == 0, StageText(udp) + "; sent/accepted=" + _report.udpSent + "/" + _report.udpAccepted
                + "; uniqueInputSeq=" + UdpSequences.Count + "; identityFailures=" + _report.udpIdentityFailures);
            CheckResult("actual_boatstate_loopback_return", _report.boatPackets >= 100 && _report.boatEchoMatches >= 100
                && _report.boatNonzeroDragPackets >= 20 && _report.maximumBoatPacketDragN > 1 && _report.maximumBoatPacketSpeedMps > 0.05f,
                "packets=" + _report.boatPackets + "; echoMatches=" + _report.boatEchoMatches + "; nonzeroDrag=" + _report.boatNonzeroDragPackets);
            CheckResult("100ms_ttl_zero_force_after_sender_stops", Math.Abs(_report.inputTtlSeconds - DefaultTtl) < 1e-6
                && Math.Abs(_report.bridgeTtlSeconds - DefaultTtl) < 1e-6 && _report.firstExpiredAfterSenderStopSeconds >= 0
                && _report.firstExpiredAcceptedAgeSeconds >= DefaultTtl
                && _report.firstExpiredAcceptedAgeSeconds - DefaultTtl <= _report.maximumCoastFixedStartGapSeconds + 1e-4
                && _report.fixedStartExpiredInputChecks >= 100 && _report.expiryZeroFrames >= 100
                && _report.expiryViolations == 0 && _report.boatExpiredZeroPackets >= 50,
                "configuredTTL=" + _report.inputTtlSeconds + "; firstObservedExpiryAfterStop=" + _report.firstExpiredAfterSenderStopSeconds
                + "; firstExpiredAcceptedAge=" + _report.firstExpiredAcceptedAgeSeconds
                + "; maxFixedStartGap=" + _report.maximumCoastFixedStartGapSeconds
                + "; fixedStartAgeExpiredChecks=" + _report.fixedStartExpiredInputChecks
                + "; zeroFrames=" + _report.expiryZeroFrames + "; violations=" + _report.expiryViolations + "; zeroReturnPackets=" + _report.boatExpiredZeroPackets);
            CheckResult("expired_input_boat_coasts_and_waves_continue", coast != null && coast.fixedFrames >= 150
                && coast.waveEnd - coast.waveStart > 1.8 && coast.maximumProbeHeight - coast.minimumProbeHeight > 0.02f
                && coast.startSpeed > 0.05f && Vector3.Distance(coast.endPosition, coast.startPosition) > 0.05f,
                StageText(coast));
            CheckResult("real_nonzero_origin_record_replay", _report.recordingOriginSeconds > 1 && _report.recordedFrames >= 250
                && _report.recordedUdpFrames >= 250 && _report.recordedRepeatedSequences >= 20
                && _report.recordedChangingRepeatedSequences >= 20 && _report.recordUdpSent >= 100
                && Math.Abs(_report.recordingFixedDeltaTime - _report.fixedDeltaTime) < 1e-8
                && _report.replayComparedFrames == _report.recordedFrames && _report.replayMissingFrames == 0 && _report.replayIdentityFailures == 0
                && _report.replayInputErrorM < InputToleranceM && _report.replayForceErrorN < ForceToleranceN
                && _report.replayBoatErrorM < BoatToleranceM && _report.replayWaveErrorSeconds < 1e-5f,
                "origin=" + _report.recordingOriginSeconds + "; dt=" + _report.recordingFixedDeltaTime + "; recorded/compared="
                + _report.recordedFrames + "/" + _report.replayComparedFrames + "; input/force/boat errors="
                + _report.replayInputErrorM + "/" + _report.replayForceErrorN + "/" + _report.replayBoatErrorM
                + "; UDP/repeated/changingRepeated=" + _report.recordedUdpFrames + "/" + _report.recordedRepeatedSequences
                + "/" + _report.recordedChangingRepeatedSequences);
            CheckResult("paired_device_record_replay", _report.replayComparedFrames >= 250
                && _report.replayDeviceStateFailures == 0 && _report.replayPlatformRequestErrorM < 0.001f
                && _report.replayPlatformFeedbackErrorM < 0.001f && _report.replayPlatformAngleErrorDeg < 0.02f
                && _report.replayCableForceErrorN < 0.001f && _report.replayCableTensionErrorN < 0.002f,
                "platform request/feedback errors M=" + _report.replayPlatformRequestErrorM + "/" + _report.replayPlatformFeedbackErrorM
                + "; angleDeg=" + _report.replayPlatformAngleErrorDeg + "; cable force/tension errors N="
                + _report.replayCableForceErrorN + "/" + _report.replayCableTensionErrorN + "; stateFailures=" + _report.replayDeviceStateFailures);
            CheckResult("replay_eof_expires_and_wave_continues", eof != null && _report.replayEofZeroFrames >= 20
                && eof.waveEnd - eof.waveStart > 0.35 && _report.expiryViolations == 0,
                StageText(eof) + "; expiredFrames=" + _report.replayEofZeroFrames);
            var scenes = _report.screenshots.FindAll(s => s.purpose == "scene");
            bool images = scenes.Count == 3;
            foreach (ScreenshotEvidence shot in scenes) images &= shot.completed && shot.hasSceneContent;
            if (scenes.Count == 3) images &= scenes[1].sceneChangedFraction > 0.01f && scenes[2].sceneChangedFraction > 0.01f;
            CheckResult("normal_rendering_three_scene_screenshots", images && _report.renderCallbacks >= 100
                && _report.renderWaveSamples >= 100 && _report.maximumShaderWaveTimeError < 1e-4f,
                "normalCameraCallbacks=" + _report.renderCallbacks + "; shaderWaveSamples=" + _report.renderWaveSamples
                + "; shaderTimeError=" + _report.maximumShaderWaveTimeError + "; screenshots=" + _report.screenshots.Count
                + "; missing/blank screenshots are FAIL; no manual-render substitute");
            CheckResult("normalized_adc_four_channel_loopback_and_isolation", _report.acquisitionSent >= 300
                && _report.acquisitionAccepted >= 200 && _report.acquisitionRejected == 0
                && _report.acquisitionValidFrames >= 300 && _report.acquisitionIdentityFailures == 0
                && _report.acquisitionPaddleIsolationFrames >= 200 && _report.acquisitionPaddleIsolationFailures == 0,
                "sent/accepted=" + _report.acquisitionSent + "/" + _report.acquisitionAccepted
                + "; validFrames=" + _report.acquisitionValidFrames + "; identityFailures=" + _report.acquisitionIdentityFailures
                + "; paddleIsolationFailures=" + _report.acquisitionPaddleIsolationFailures);
            CheckResult("independent_adc_ttl_and_sender_stop", Math.Abs(_report.acquisitionTtlSeconds - DefaultTtl) < 1e-6
                && _report.acquisitionIndependentExpiryFrames >= 30 && _report.acquisitionExpiredFrames >= 100
                && _report.acquisitionFirstExpiredAgeSeconds >= DefaultTtl
                && _report.acquisitionFirstExpiredAgeSeconds <= DefaultTtl + Time.fixedDeltaTime
                && _report.acquisitionExpiryViolations == 0,
                "partialExpiryFrames=" + _report.acquisitionIndependentExpiryFrames + "; expiredFrames=" + _report.acquisitionExpiredFrames
                + "; firstExpiredAge=" + _report.acquisitionFirstExpiredAgeSeconds + "; violations=" + _report.acquisitionExpiryViolations);
            CheckResult("adc_record_replay_profile_and_eof", _report.acquisitionHeaderProfileMatches
                && _report.acquisitionRecordedFrames == _report.recordedFrames && _report.recordedFrames >= 250
                && _report.acquisitionReplayComparedFrames == _report.recordedFrames
                && _report.acquisitionReplayFieldFailures == 0 && _report.acquisitionReplayCloneFailures == 0
                && _report.acquisitionReplayEofFrames >= 20,
                "recorded/compared=" + _report.acquisitionRecordedFrames + "/" + _report.acquisitionReplayComparedFrames
                + "; fieldFailures=" + _report.acquisitionReplayFieldFailures + "; cloneFailures=" + _report.acquisitionReplayCloneFailures
                + "; eofFrames=" + _report.acquisitionReplayEofFrames);
            var detail = _report.screenshots.Find(s => s.purpose == "hud_details");
            var acquisitionDetail = _report.screenshots.Find(s => s.purpose == "hud_acquisition");
            CheckResult("hud_physics_statistics_and_rendered_details", _report.hudDefaultSceneOnly && _report.hudOptionalMetricsToggledAndFormatted
                && _report.hudPhysicsSamples >= 1000
                && _report.hudMaximumStrokeCount >= 1 && _report.hudMaximumImpulseNs > 0
                && _report.hudMaximumDistanceM > 0.1f && !string.IsNullOrWhiteSpace(_report.hudFontName)
                && detail != null && detail.completed && detail.hudTextPixels >= 1000,
                "defaultSceneOnly=" + _report.hudDefaultSceneOnly + "; optionalMetrics=" + _report.hudOptionalMetricsToggledAndFormatted
                + "; physicsSamples=" + _report.hudPhysicsSamples + "; strokes=" + _report.hudMaximumStrokeCount
                + "; impulseNs=" + _report.hudMaximumImpulseNs + "; font=" + _report.hudFontName
                + "; detailTextPixels=" + (detail == null ? 0 : detail.hudTextPixels));
            CheckResult("hud_acquisition_navigation_and_render", _focusAcquisitionHud
                && acquisitionDetail != null && acquisitionDetail.completed && acquisitionDetail.hudTextPixels >= 1000,
                "navigationInvoked=" + _focusAcquisitionHud + "; acquisitionTextPixels="
                + (acquisitionDetail == null ? 0 : acquisitionDetail.hudTextPixels));
            CheckResult("tracking_adapter_normal_lateupdate_and_stale_gate", _report.trackingDefaultDisabled
                && _report.trackingMainCameraUnchanged && _report.trackingAppliedFrames >= 100
                && _report.trackingStaleFrames >= 20 && _report.trackingFailures == 0
                && _report.trackingRecoveredAppliedFrames >= 100
                && _report.trackingMaximumPositionErrorM < 1e-4f && _report.trackingMaximumRotationErrorDeg < 0.02f,
                "submitted/applied=" + _report.trackingSubmittedFrames + "/" + _report.trackingAppliedFrames
                + "; staleFrames=" + _report.trackingStaleFrames + "; recoveredApplied=" + _report.trackingRecoveredAppliedFrames
                + "; position/rotationErrors="
                + _report.trackingMaximumPositionErrorM + "/" + _report.trackingMaximumRotationErrorDeg
                + "; failures=" + _report.trackingFailures);
            CheckResult("replay_controls_normal_player_loop", _report.replayF5EofRefused && _report.replayF5MiddleRefused
                && _report.replayEofHudExit && _report.replayMiddleHudExit && _report.replayEofSyntheticFrames >= 20
                && _report.replayMiddleSyntheticFrames >= 20 && _report.replayControlFailures == 0,
                "F5refusedEOF/middle=" + _report.replayF5EofRefused + "/" + _report.replayF5MiddleRefused
                + "; HUDexitedEOF/middle=" + _report.replayEofHudExit + "/" + _report.replayMiddleHudExit
                + "; syntheticFramesEOF/middle=" + _report.replayEofSyntheticFrames + "/" + _report.replayMiddleSyntheticFrames
                + "; controlFailures=" + _report.replayControlFailures + "; recording SHA256 and actions in replayControls");
            var aborted = GetStage(Stage.ControlsMutationStopped);
            CheckResult("replay_configuration_abort_and_explicit_recovery", _report.replayMutationWarmupForceFrames >= 1
                && _report.replayMutationZeroFrames >= 20 && _report.replayMutationFailures == 0
                && !string.IsNullOrEmpty(_report.replayMutationReason) && _report.replayRecoveryFrames >= 20
                && _report.replayControlFailures == 0 && aborted != null && aborted.waveEnd - aborted.waveStart > 0.2,
                "validNonzeroReplayFrames=" + _report.replayMutationWarmupForceFrames
                + "; bladeArea=" + _report.replayMutationOriginalBladeArea + " -> " + _report.replayMutationChangedBladeArea
                + "; immediateZeroFrames=" + _report.replayMutationZeroFrames + "; violations=" + _report.replayMutationFailures
                + "; syntheticRecoveryFrames=" + _report.replayRecoveryFrames + "; reason=" + _report.replayMutationReason
                + "; abortStage=" + StageText(aborted));
        }

        static StageEvidence GetStage(Stage stage) { return _report.stages.Find(s => s.name == stage.ToString()); }
        static string StageText(StageEvidence s)
        {
            return s == null ? "stage did not execute" : "frames=" + s.fixedFrames + "; valid=" + s.validInputs
                + "; forceFrames=" + s.nonzeroForceFrames + "; distanceM=" + Vector3.Distance(s.endPosition, s.startPosition)
                + "; waveElapsed=" + (s.waveEnd - s.waveStart);
        }
        static void CheckResult(string name, bool passed, string evidence) { _report.checks.Add(new Check { name = name, passed = passed, evidence = evidence }); }
        static void OnLog(string condition, string stack, LogType type)
        {
            if (!_done && _report != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
                _report.errors.Add(condition + "\n" + stack);
        }
        static void FailBeforePlay(Exception ex)
        {
            string directory = SessionState.GetString(ArtifactsKey, "");
            var report = new Report { artifacts = directory, unityVersion = Application.unityVersion, batchmode = Application.isBatchMode };
            report.errors.Add(ex.ToString());
            try { File.WriteAllText(Path.Combine(directory, "runtime_result.json"), JsonUtility.ToJson(report, true)); }
            catch (Exception writeError) { Debug.Log("[RUNTIME] Evidence write failed: " + writeError.Message); }
            Debug.Log("[RUNTIME] RESULT=FAIL " + ex.Message);
            SessionState.SetInt(ExitKey, 1);
            SessionState.SetString(PhaseKey, "finishing");
            if (Application.isPlaying) EditorApplication.ExitPlaymode();
        }
        static string RecordingPath() { return Path.Combine(_report.artifacts, "normal_play_recording.jsonl"); }
        static double WallTime() { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
        static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
        static bool HasArgument(string name) { return Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0; }
        static int AllocatePort()
        {
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        }

        sealed class FakeAcquisitionSender
        {
            readonly int _port;
            readonly string _source;
            readonly uint _first;
            readonly object _gate = new object();
            readonly Dictionary<uint, AcquisitionPacket> _sent = new Dictionary<uint, AcquisitionPacket>();
            volatile bool _running;
            public volatile bool OmitFirstChannel;
            Thread _thread;
            public string Error { get; private set; }
            public long LastActualSendTicks { get; private set; }
            public long StoppedTicks { get; private set; }
            public FakeAcquisitionSender(int port, string source, uint first = 1000) { _port = port; _source = source; _first = first; }
            public void Start()
            {
                _running = true;
                _thread = new Thread(SendLoop) { IsBackground = true, Name = "RuntimeNormalizedAdcFixture" };
                _thread.Start();
            }
            void SendLoop()
            {
                try
                {
                    using (var socket = new UdpClient())
                    {
                        var endpoint = new IPEndPoint(IPAddress.Loopback, _port);
                        uint sequence = _first;
                        var watch = Stopwatch.StartNew(); double next = 0;
                        while (_running)
                        {
                            double now = watch.Elapsed.TotalSeconds;
                            if (now < next) { Thread.Sleep(1); continue; }
                            next = now + .02;
                            bool omit = OmitFirstChannel;
                            var packet = new AcquisitionPacket { format = AcquisitionPacket.Format, sourceId = _source,
                                sourceKind = AcquisitionSourceKind.Fixture, sequenceValid = true, sequence = sequence++,
                                deviceTimeValid = true, deviceTimeUs = 123000000UL + sequence,
                                channels = new AcquisitionChannelInput[omit ? 3 : 4] };
                            for (int j = 0; j < packet.channels.Length; j++)
                            {
                                int i = j + (omit ? 1 : 0);
                                packet.channels[j] = new AcquisitionChannelInput { channelId = "CH" + (i + 1),
                                    sampleSequenceValid = true, sampleSequence = packet.sequence,
                                    sampleTimeValid = true, sampleTimeUs = packet.deviceTimeUs + (ulong)(i * 31),
                                    rawUnits = "fixture-count", hasRaw = true, raw = 1000 + 200 * i + packet.sequence % 100,
                                    rawText = "pc-fixture", valid = true };
                            }
                            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(packet));
                            lock (_gate) _sent.Add(packet.sequence, packet);
                            socket.Send(bytes, bytes.Length, endpoint); LastActualSendTicks = Stopwatch.GetTimestamp();
                        }
                    }
                }
                catch (Exception e) { Error = e.ToString(); }
                finally { StoppedTicks = Stopwatch.GetTimestamp(); }
            }
            public bool TryGet(uint sequence, out AcquisitionPacket packet) { lock (_gate) return _sent.TryGetValue(sequence, out packet); }
            public AcquisitionPacket[] Snapshot() { lock (_gate) { var result = new AcquisitionPacket[_sent.Count]; _sent.Values.CopyTo(result, 0); return result; } }
            public void RequestStop() { _running = false; }
            public void Stop() { RequestStop(); _thread?.Join(1000); }
        }

        [Serializable] public class RuntimeCounterSample
        {
            public int observedFrame;
            public double observedTimeSeconds;
            public string marker;
            public long previousFrameValue;
        }

        [Serializable] public class CameraCostSample
        {
            public int observedFrame, lastOcclusionQueries;
            public float lastOcclusionMilliseconds;
        }

        public class RuntimeCounterProbe : MonoBehaviour
        {
            sealed class Counter { public string name; public ProfilerRecorder recorder; }
            readonly List<Counter> _counters = new List<Counter>();
            Report _evidence;
            DebugFollowCamera _follow;
            public void Initialize(Report report)
            {
                _evidence = report;
                var handles = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(handles);
                foreach (var handle in handles)
                {
                    var description = ProfilerRecorderHandle.GetDescription(handle);
                    string name = description.Name;
                    if (name == "Main Thread" || name == "PlayerLoop" || name == "EditorLoop"
                        || name.Contains("WaitFor") || name.Contains("BehaviourUpdate") || name.Contains("BehaviourLateUpdate")
                        || name == "GUI.Repaint" || name == "Physics.Simulate" || name == "Camera.Render")
                    {
                        var recorder = ProfilerRecorder.StartNew(description.Category, name, 1,
                            ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
                        if (recorder.Valid) _counters.Add(new Counter { name = name, recorder = recorder });
                        else recorder.Dispose();
                    }
                }
            }
            void Update()
            {
                if (_follow == null) _follow = UnityEngine.Object.FindObjectOfType<DebugFollowCamera>();
                if (_evidence == null || !InAcquisitionTimingWindow(Stopwatch.GetTimestamp())) return;
                if (_evidence != null && _follow != null && _evidence.cameraGeometryCounts == null)
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var occlusion = typeof(DebugFollowCamera).GetField("_occlusion", flags)?.GetValue(_follow);
                    if (occlusion != null)
                    {
                        var counts = new List<string>();
                        foreach (string name in new[] { "_localSurface", "_localWitnesses", "_surfaceGroups", "_surfaceRegions", "_solids" })
                            if (typeof(BoatViewOcclusion).GetField(name, flags)?.GetValue(occlusion) is ICollection collection)
                                counts.Add(name + "=" + collection.Count);
                        if (typeof(BoatViewOcclusion).GetField("_solids", flags)?.GetValue(occlusion) is List<Collider> solids)
                            foreach (Collider solid in solids)
                                if (solid != null && (solid.name == "Bank" || solid.name == "Walkway" || solid.name == "Railing"))
                                {
                                    var mesh = (solid as MeshCollider)?.sharedMesh;
                                    counts.Add(solid.name + ":mesh=" + (mesh == null ? "null" : mesh.name)
                                        + ",readable=" + (mesh != null && mesh.isReadable)
                                        + ",vertices=" + (mesh != null ? mesh.vertexCount : 0));
                                }
                        _evidence.cameraGeometryCounts = string.Join("; ", counts);
                    }
                }
                // These public counters were measured by the previous normal
                // LateUpdate. This probe never invokes or skips a camera query.
                if (_follow != null) _evidence.cameraCosts.Add(new CameraCostSample {
                    observedFrame = Time.frameCount, lastOcclusionQueries = _follow.OcclusionQueries,
                    lastOcclusionMilliseconds = _follow.OcclusionMilliseconds });
                foreach (var counter in _counters)
                    _evidence.runtimeCounters.Add(new RuntimeCounterSample { observedFrame = Time.frameCount,
                        observedTimeSeconds = HeadPoseAttachment.MonotonicSeconds, marker = counter.name,
                        previousFrameValue = counter.recorder.LastValue });
            }
            void OnDisable()
            {
                foreach (var counter in _counters) counter.recorder.Dispose();
                _counters.Clear();
            }
        }

        [DefaultExecutionOrder(1100)]
        public class RuntimeTrackingFixture : MonoBehaviour
        {
            public bool feedTracking = true;
            Transform _boat;
            HeadPoseAttachment _attachment;
            Report _evidence;
            double _lastSubmit, _updateTime;
            int _updateFrame = -1;
            bool _hasSubmitted, _observedExpiredTracking, _submittedAfterExpiry;
            readonly Vector3 _offset = new Vector3(.13f, 1.2f, -.07f);
            readonly Quaternion _relativeRotation = Quaternion.Euler(7, 11, -3);
            public void Configure(Transform boat, Report evidence)
            {
                _boat = boat; _evidence = evidence;
                _attachment = gameObject.AddComponent<HeadPoseAttachment>();
                evidence.trackingDefaultDisabled = !_attachment.useTrackedPose;
                var mainAttachment = Camera.main.GetComponent<HeadPoseAttachment>();
                evidence.trackingMainCameraUnchanged = mainAttachment != null && !mainAttachment.useTrackedPose;
                _attachment.virtualBoat = boat;
                _attachment.allowUnmeasuredDesktopPreview = true;
                _attachment.useTrackedPose = true;
            }
            void Update()
            {
                if (_attachment == null) return;
                double now = HeadPoseAttachment.MonotonicSeconds;
                _updateTime = now; _updateFrame = Time.frameCount;
                if (!feedTracking) return;
                float t = (float)Time.timeAsDouble;
                var physical = Matrix4x4.TRS(new Vector3(Mathf.Sin(t), .02f, -.3f), Quaternion.Euler(4, t * 9, -5), Vector3.one);
                var head = physical * Matrix4x4.TRS(_offset, _relativeRotation, Vector3.one);
                bool accepted = _attachment.SubmitTrackingSample(head, physical, now, HeadTrackingSource.DesktopSimulation, true, true);
                if (accepted)
                {
                    _lastSubmit = now; _hasSubmitted = true;
                    if (_observedExpiredTracking) _submittedAfterExpiry = true;
                }
                _evidence.trackingSubmittedFrames++;
                TrackingObservation observation = Observe("submission");
                observation.attemptedSubmitTimeSeconds = now; observation.submissionAccepted = accepted;
                if (!accepted) FailTracking(observation, "SUBMISSION_REJECTED");
                _evidence.trackingObservations.Add(observation);
            }
            void LateUpdate()
            {
                if (_attachment == null) return;
                TrackingObservation observation = Observe("late_update");
                _evidence.trackingObservations.Add(observation);
                if (!_hasSubmitted) return;
                // Compare against the adapter's evaluation instant, not this later observer's
                // clock. Crossing 100 ms between these ordered callbacks is not a stale apply.
                // The current-frame and Update/observer bounds independently bind that instant
                // to the real lifecycle; reported age is checked but is not the expiry oracle.
                if (!observation.evaluationClockValid || observation.evaluationFrame != observation.frame
                    || observation.updateFrame != observation.frame
                    || observation.evaluationTimeSeconds < observation.updateTimeSeconds
                    || observation.evaluationTimeSeconds > observation.observerTimeSeconds)
                {
                    FailTracking(observation, "EVALUATION_FRAME_OR_CLOCK_MISMATCH"); return;
                }
                if (!observation.independentAgeValid || observation.independentAgeSeconds < 0
                    || !observation.reportedAgeFinite
                    || Math.Abs(observation.reportedAgeSeconds - observation.independentAgeSeconds) > 1e-9)
                {
                    FailTracking(observation, "EVALUATION_AGE_MISMATCH"); return;
                }
                if (observation.independentAgeSeconds > _attachment.maximumTrackingAgeSeconds)
                {
                    if (!feedTracking) _observedExpiredTracking = true;
                    if (!_attachment.PoseApplied && _attachment.PoseStatus == "TRACKING_STALE_OR_INVALID_CLOCK")
                    {
                        if (!feedTracking) _evidence.trackingStaleFrames++;
                    }
                    else FailTracking(observation, "EXPIRED_EVALUATION_APPLIED_OR_WRONG_STATUS");
                    return;
                }
                if (_attachment.PoseApplied)
                {
                    _evidence.trackingAppliedFrames++;
                    observation.recoveredAfterExpiry = feedTracking && _submittedAfterExpiry;
                    if (observation.recoveredAfterExpiry) _evidence.trackingRecoveredAppliedFrames++;
                    float error = Vector3.Distance(transform.position, _boat.position + _boat.rotation * _offset);
                    Quaternion delta = Quaternion.Inverse(_boat.rotation * _relativeRotation) * transform.rotation;
                    float angle = 2f * Mathf.Atan2(new Vector3(delta.x, delta.y, delta.z).magnitude, Mathf.Abs(delta.w)) * Mathf.Rad2Deg;
                    observation.poseChecked = true; observation.positionErrorM = error; observation.rotationErrorDeg = angle;
                    _evidence.trackingMaximumPositionErrorM = Mathf.Max(_evidence.trackingMaximumPositionErrorM, error);
                    _evidence.trackingMaximumRotationErrorDeg = Mathf.Max(_evidence.trackingMaximumRotationErrorDeg, angle);
                }
                else FailTracking(observation, "FRESH_EVALUATION_NOT_APPLIED");
            }

            TrackingObservation Observe(string kind)
            {
                double evaluated = _attachment.PoseEvaluationTimeSeconds, reportedAge = _attachment.TrackingAgeSeconds;
                bool clockValid = !double.IsNaN(evaluated) && !double.IsInfinity(evaluated)
                    && evaluated >= 0 && _attachment.PoseEvaluationFrame >= 0;
                bool ageFinite = !double.IsNaN(reportedAge) && !double.IsInfinity(reportedAge);
                return new TrackingObservation
                {
                    kind = kind, frame = Time.frameCount, observerTimeSeconds = HeadPoseAttachment.MonotonicSeconds,
                    feedTracking = feedTracking, updateFrame = _updateFrame, updateTimeSeconds = _updateTime,
                    hasSubmitted = _hasSubmitted, lastSubmitTimeSeconds = _lastSubmit,
                    evaluationFrame = _attachment.PoseEvaluationFrame, evaluationClockValid = clockValid,
                    evaluationTimeSeconds = clockValid ? evaluated : 0d,
                    reportedAgeFinite = ageFinite, reportedAgeSeconds = ageFinite ? reportedAge : 0d,
                    independentAgeValid = clockValid && _hasSubmitted,
                    independentAgeSeconds = clockValid && _hasSubmitted ? evaluated - _lastSubmit : 0d,
                    maximumTrackingAgeSeconds = _attachment.maximumTrackingAgeSeconds,
                    poseApplied = _attachment.PoseApplied, poseStatus = _attachment.PoseStatus
                };
            }

            void FailTracking(TrackingObservation observation, string reason)
            {
                observation.failureReason = reason;
                _evidence.trackingFailures++;
                Debug.Log("[RUNTIME_TRACKING] " + JsonUtility.ToJson(observation));
            }
        }

        sealed class FakeHardwareSender
        {
            readonly int _port;
            readonly float _shaft;
            readonly uint _firstSequence;
            readonly double _sendInterval;
            readonly object _lock = new object();
            readonly Dictionary<uint, SentPacket> _packets = new Dictionary<uint, SentPacket>();
            readonly HashSet<uint> _timestamps = new HashSet<uint>();
            Thread _thread;
            volatile bool _running;
            public string Error { get; private set; }
            public long LastActualSendTicks { get; private set; }
            public long StoppedTicks { get; private set; }
            public FakeHardwareSender(int port, float shaft, uint firstSequence = 1000, double sendInterval = 0.01)
            { _port = port; _shaft = shaft; _firstSequence = firstSequence; _sendInterval = sendInterval; }
            public void Start()
            {
                _running = true;
                _thread = new Thread(SendLoop) { IsBackground = true, Name = "RuntimeFakeHardwareUdp" };
                _thread.Start();
            }
            void SendLoop()
            {
                try
                {
                  using (var socket = new UdpClient())
                  {
                    var endpoint = new IPEndPoint(IPAddress.Loopback, _port);
                    var watch = Stopwatch.StartNew();
                    uint sequence = _firstSequence;
                    double next = 0;
                    while (_running)
                    {
                        double now = watch.Elapsed.TotalSeconds;
                        if (now < next) { Thread.Sleep(1); continue; }
                        next = now + _sendInterval;
                        double u = now / (60.0 / 45.0) % 1.0;
                        bool drive = u < 0.55;
                        double t = drive ? u / 0.55 : (u - 0.55) / 0.45;
                        double wet = drive ? Math.Max(0, Math.Min(1, Math.Min(t / 0.1, (1 - t) / 0.1))) : 0;
                        wet = wet * wet * (3 - 2 * wet);
                        // 真实划桨几何:握把在舷内(约 0.28m),桨杆外倾 25°,
                        // 桨叶落点 = 握把 + (sin25°·杆长, −cos25°·杆长) ≈ (0.70, ...)。
                        // 旧值 gripX=1.55 是宽船时代的占位数——新龙舟半宽 0.51m,
                        // 1.55m 的握把等于人坐在船外一米划水,桨视觉上直接飞出船体。
                        double tiltX = Math.Sin(25.0 * Math.PI / 180.0) * _shaft;
                        double tiltY = Math.Cos(25.0 * Math.PI / 180.0) * _shaft;
                        SentPacket sent = new SentPacket
                        {
                            sequence = sequence++, timestampUs = (ulong)(Stopwatch.GetTimestamp() * 1000000.0 / Stopwatch.Frequency),
                            senderSeconds = now,
                            gripX = (float)(0.70 - tiltX),
                            gripY = (float)(0.45 - wet) + (float)tiltY,
                            gripZ = 0.2f + (float)((drive ? 0.45 : -0.45) * Math.Cos(Math.PI * t)), gripForceN = 23.5f
                        };
                        byte[] packet = new byte[48];
                        BitConverter.GetBytes(HardwareUdpBridge.MagicPaddleState).CopyTo(packet, 0);
                        BitConverter.GetBytes(HardwareUdpBridge.ProtoVersion).CopyTo(packet, 4);
                        BitConverter.GetBytes(sent.timestampUs).CopyTo(packet, 8);
                        BitConverter.GetBytes(sent.sequence).CopyTo(packet, 16);
                        BitConverter.GetBytes(sent.gripX).CopyTo(packet, 20);
                        BitConverter.GetBytes(sent.gripY).CopyTo(packet, 24);
                        BitConverter.GetBytes(sent.gripZ).CopyTo(packet, 28);
                        // Euler:绕 Z +25° 让 +Y 桨杆朝舷外倾斜,握把舷内/桨叶舷外
                        // (HardwareUdpBridge 按角度解析,不是弧度)
                        BitConverter.GetBytes(0f).CopyTo(packet, 32);
                        BitConverter.GetBytes(0f).CopyTo(packet, 36);
                        BitConverter.GetBytes(25f).CopyTo(packet, 40);
                        // Euler angles 0/0/0: +Y shaft, +Z blade normal. v1 is the existing 48-byte contract.
                        BitConverter.GetBytes(sent.gripForceN).CopyTo(packet, 44);
                        lock (_lock) { _packets.Add(sent.sequence, sent); _timestamps.Add((uint)sent.timestampUs); }
                        socket.Send(packet, packet.Length, endpoint);
                        sent.actualSendTicks = Stopwatch.GetTimestamp();
                        LastActualSendTicks = sent.actualSendTicks;
                    }
                  }
                }
                catch (Exception ex) { Error = ex.ToString(); _running = false; }
                finally { StoppedTicks = Stopwatch.GetTimestamp(); }
            }
            public void RequestStop() { _running = false; }
            public void Stop() { RequestStop(); _thread?.Join(500); }
            public bool TryGet(uint sequence, out SentPacket sent) { lock (_lock) return _packets.TryGetValue(sequence, out sent); }
            public bool HasTimestampLow(uint value) { lock (_lock) return _timestamps.Contains(value); }
            public SentPacket[] Snapshot()
            {
                lock (_lock)
                {
                    var result = new List<SentPacket>(_packets.Values);
                    result.Sort((a, b) => a.sequence.CompareTo(b.sequence));
                    return result.ToArray();
                }
            }
        }
    }
}
