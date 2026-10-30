using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    // Observes the real PlayerLoop. Input is supplied only by the public Python CSV CLI.
    [InitializeOnLoad]
    public static class AcquisitionRuntimeSelfTest
    {
        const string ActiveKey = "DragonBoatVR.AdcRuntime.Active";
        const string PhaseKey = ActiveKey + ".Phase";
        const string ArtifactsKey = ActiveKey + ".Artifacts";
        const string ExitKey = ActiveKey + ".Exit";
        const string Source = "software-csv-runtime";
        const int PacketCount = 80;
        enum Stage { Starting, ProfileGui, Diagnostics, Settle, Receiving, Stopped, Replay, Eof, RestoreDiagnostics }
        [Serializable] sealed class Scenario { public string sourceId, rawUnits; public int packetCount; public double softwareSendIntervalSeconds; }
        [Serializable] sealed class Ready { public int port; public string sourceId; public double softwareSendIntervalSeconds; }
        [Serializable] sealed class Check { public string name, evidence; public bool passed; }
        [Serializable] sealed class Shot
        {
            public string file, stage;
            public string method = "ScreenCapture / normal Play replay of the actual CLI CSV recording";
            public bool completed, fileReady, hasSceneContent;
            public int width, height, requestedRender, completedRender, hudTextPixels, colorBins;
            public int requestedFrame, completedFrame;
            public AcquisitionSnapshot requestedSnapshot, firstRenderedSnapshot;
            public string requestedReception, requestedRejection;
            public float luminanceStdDev;
        }
        [Serializable] sealed class Report
        {
            public string result = "FAIL", scope = "DESKTOP_SOFTWARE_CSV_IMPORT_ONLY", artifacts;
            public string execution = "EnterPlaymode / normal FixedUpdate / PhysX / FrameCompleted / HUD / ScreenCapture; no manual stepping or Camera.Render";
            public string unityVersion, graphicsDevice, recordingPath, recordingSha256, hudFont;
            public double softwareSendIntervalSeconds;
            public bool physicsAutomatic, postChain, sequenceWrap, sourceIdentity = true, rawUnits = true;
            public int fixedFrames, acceptedPackets, rejectedPackets, observedPacketSequences, identityFailures;
            public int missingFrames, partialExpiredFrames, missingRawFrames, sourceInvalidFrames, saturatedFrames;
            public int isolationFrames, isolationFailures, senderStopExpiredFrames, senderStopFailures;
            public int hudFrames, hudFailures, normalRenderCallbacks, recordedFrames, replayFrames, replayFailures, eofFrames, eofFailures;
            public long recordingMaximumObservedQueuedBytes, recordingWrittenFrames;
            public double recordingMaximumWriteSeconds;
            public bool duplicatePreservedReceiveTicks;
            public bool rawProfileLoadedThroughHud, rawProfileRecordingRefused, rawProfileReplayRefused;
            public bool rawProfileGuiCompleted;
            public bool rawReplayLiveProfilePreserved, rawReplayListenerSuspended, rawReplayLiveListenerRestored;
            public int rawProfileGuiEvents;
            public int receptionDiagnosticFrames, receptionDiagnosticFailures;
            public bool diagnosticLegal, diagnosticDuplicate, diagnosticWrongSource, diagnosticMalformedJson, diagnosticUtf8;
            public bool diagnosticOldChannel, diagnosticNoRefresh, diagnosticExpired, diagnosticRecentRejectionPreserved;
            public bool diagnosticStopped, diagnosticReload, diagnosticReplayOwner, diagnosticRestored, diagnosticRestoreFailure;
            public List<Shot> diagnosticScreenshots = new List<Shot>();
            public string rawProfileGuiEnteredPath;
            public List<RawProfileGuiExercise.GuiEventEvidence> rawProfileGuiEventsEvidence
                = new List<RawProfileGuiExercise.GuiEventEvidence>();
            public List<Check> checks = new List<Check>();
            public List<Shot> screenshots = new List<Shot>();
            public List<string> errors = new List<string>();
        }
        static Report _report;
        static SimulationSession _session;
        static RideHud _hud;
        static Stage _stage;
        static double _stageTime, _startWall, _recordOrigin;
        static bool _done, _warmupRequested, _warmupCompleted;
        static int _warmupRender, _lastRequestFrame = -1;
        static int _diagnosticStep;
        static int _diagnosticFocusRepaint;
        static double _diagnosticWaitStarted;
        static long _diagnosticReceiveTicks;
        static AcquisitionPacket _diagnosticPacket;
        static UdpClient _diagnosticSender, _occupiedRestorePort;
        static RawProfileGuiExercise _profileGui;
        static long _lastReceivedTicks;
        static string _replayLiveSignature;
        static int _replayLivePort;
        static readonly HashSet<uint> ObservedSequences = new HashSet<uint>();
        static readonly Dictionary<uint, AcquisitionPacket> Packets = new Dictionary<uint, AcquisitionPacket>();
        static readonly List<SimulationFrame> Recorded = new List<SimulationFrame>();
        static readonly List<SimulationFrame> Observations = new List<SimulationFrame>();
        static EditorWindow _gameView;
        static object _gameViewGroup;
        static int _temporarySize = -1, _previousSize;
        static readonly FieldInfo HudStatus = typeof(RideHud).GetField("_acquisitionStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo HudRows = typeof(RideHud).GetField("_acquisitionRows", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo HudReception = typeof(RideHud).GetField("_acquisitionReception", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo HudRejection = typeof(RideHud).GetField("_acquisitionRejection", BindingFlags.Instance | BindingFlags.NonPublic);

        static AcquisitionRuntimeSelfTest() { if (SessionState.GetBool(ActiveKey, false)) Hooks(); }

        public static void Run()
        {
            string artifacts = Argument("-adcRuntimeArtifacts");
            if (string.IsNullOrWhiteSpace(artifacts)) throw new ArgumentException("-adcRuntimeArtifacts is required.");
            artifacts = Path.GetFullPath(artifacts);
            Directory.CreateDirectory(artifacts);
            if (File.Exists(Path.Combine(artifacts, "adc_runtime_result.json"))) throw new IOException("Use a new artifacts directory.");
            SessionState.SetString(ArtifactsKey, artifacts);
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetString(PhaseKey, "entering");
            SessionState.SetInt(ExitKey, 1);
            Hooks();
            try
            {
                if (!Application.isBatchMode || Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("Use a dedicated batch Editor starting in Edit mode.");
                if (Argument("-quit") != null || Argument("-nographics") != null)
                    throw new InvalidOperationException("This asynchronous normal-render entry owns exit; omit -quit and -nographics.");
                EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                var bridge = UnityEngine.Object.FindObjectOfType<HardwareUdpBridge>();
                if (bridge == null) throw new InvalidOperationException("Demo hardware bridge is missing.");
                bridge.listenPort = FreePort();
                do { bridge.remotePort = FreePort(); } while (bridge.remotePort == bridge.listenPort);
                bridge.remoteHost = "127.0.0.1";
                Debug.Log("[ADC_RUNTIME] Entering normal Play; CSV input only. Artifacts=" + artifacts);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { FailBeforePlay(error); }
        }
        static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++) if (args[i] == name) return i + 1 < args.Length ? args[i + 1] : "";
            return null;
        }
        static int FreePort()
        {
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        }
        static void Hooks()
        {
            EditorApplication.update -= Update; EditorApplication.update += Update;
            EditorApplication.playModeStateChanged -= PlayState; EditorApplication.playModeStateChanged += PlayState;
        }
        static void PlayState(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetString(PhaseKey, "") == "entering") Start();
        }
        static void Start()
        {
            if (_report != null) return;
            _report = new Report { artifacts = SessionState.GetString(ArtifactsKey, ""), unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName,
                physicsAutomatic = Physics.simulationMode == SimulationMode.FixedUpdate };
            _startWall = EditorApplication.timeSinceStartup;
            _stage = Stage.Starting;
            Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            SessionState.SetString(PhaseKey, "running");
            Application.logMessageReceived += OnLog;
            Camera.onPostRender += BuiltinRendered;
            RenderPipelineManager.endCameraRendering += PipelineRendered;
            try
            {
                var scenario = JsonUtility.FromJson<Scenario>(File.ReadAllText(Path.Combine(_report.artifacts, "scenario.json")));
                if (scenario.sourceId != Source || scenario.rawUnits != "example-count" || scenario.packetCount != PacketCount
                    || Math.Abs(scenario.softwareSendIntervalSeconds - .04) > 1e-9)
                    throw new InvalidDataException("Scenario identity, size or 40 ms software fixture cadence differs.");
                _report.softwareSendIntervalSeconds = scenario.softwareSendIntervalSeconds;
                foreach (string json in File.ReadLines(Path.Combine(_report.artifacts, "normalized.jsonl")))
                {
                    AcquisitionJsonEvidence.ValidatePacketJson(json);
                    var packet = JsonUtility.FromJson<AcquisitionPacket>(json);
                    if (Packets.ContainsKey(packet.sequence)) throw new InvalidDataException("Duplicate normalized packet sequence.");
                    Packets.Add(packet.sequence, packet);
                }
                if (Packets.Count != PacketCount) throw new InvalidDataException("Expected 80 fixture packets.");
                ConfigureGameView();
            }
            catch (Exception error) { Finish("Startup: " + error); }
        }
        static void ConfigureGameView()
        {
            Type type = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
            if (type == null) throw new InvalidOperationException("Game view is unavailable.");
            _gameView = EditorWindow.GetWindow(type, false, "Game", false);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type sizesType = type.Assembly.GetType("UnityEditor.GameViewSizes");
            Type sizeType = type.Assembly.GetType("UnityEditor.GameViewSize");
            Type kindType = type.Assembly.GetType("UnityEditor.GameViewSizeType");
            object sizes = sizesType.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            _gameViewGroup = sizesType.GetProperty("currentGroup", flags).GetValue(sizes);
            _temporarySize = (int)_gameViewGroup.GetType().GetMethod("GetTotalCount", flags).Invoke(_gameViewGroup, null);
            object fixedSize = Activator.CreateInstance(sizeType, flags, null,
                new object[] { Enum.ToObject(kindType, 1), 1280, 720, "CSV runtime temporary 1280x720" }, null);
            _gameViewGroup.GetType().GetMethod("AddCustomSize", flags).Invoke(_gameViewGroup, new[] { fixedSize });
            PropertyInfo selected = type.GetProperty("selectedSizeIndex", flags);
            _previousSize = (int)selected.GetValue(_gameView); selected.SetValue(_gameView, _temporarySize);
            _gameView.minSize = new Vector2(1280, 750); _gameView.position = new Rect(0, 0, 1280, 750); _gameView.Repaint();
        }
        static void RestoreGameView()
        {
            if (_temporarySize < 0 || _gameViewGroup == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (_gameView != null) _gameView.GetType().GetProperty("selectedSizeIndex", flags).SetValue(_gameView, _previousSize);
            _gameViewGroup.GetType().GetMethod("RemoveCustomSize", flags).Invoke(_gameViewGroup, new object[] { _temporarySize });
            _temporarySize = -1;
        }
        static void Update()
        {
            if (!SessionState.GetBool(ActiveKey, false)) return;
            if (SessionState.GetString(PhaseKey, "") == "finishing")
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    int code = SessionState.GetInt(ExitKey, 1);
                    SessionState.SetBool(ActiveKey, false);
                    EditorApplication.update -= Update; EditorApplication.playModeStateChanged -= PlayState;
                    EditorApplication.Exit(code);
                }
                return;
            }
            if (_report == null && Application.isPlaying) Start();
            if (_report == null || _done) return;
            try
            {
                if (!Application.isPlaying) { Finish("Play ended before verification."); return; }
                if (EditorApplication.timeSinceStartup - _startWall > 40) { Finish("40 second normal Play timeout."); return; }
                EditorApplication.QueuePlayerLoopUpdate(); _gameView?.Repaint(); PollShotFiles();
                if (_stage == Stage.Starting)
                {
                    _session = UnityEngine.Object.FindObjectOfType<SimulationSession>();
                    if (_session == null || _session.LastFrame == null) return;
                    _hud = UnityEngine.Object.FindObjectOfType<RideHud>();
                    if (_hud == null || !_hud.isActiveAndEnabled) throw new InvalidOperationException("Active normal HUD required.");
                    _session.drivers[0].SetMode(PaddleDriver.Mode.External);
                    string importedProfile = File.ReadAllText(Path.Combine(_report.artifacts, "raw_profile.json"));
                    AcquisitionJsonEvidence.ValidateProfileJson(importedProfile);
                    var profile = JsonUtility.FromJson<AcquisitionProfile>(importedProfile);
                    profile.loopbackPort = FreePort();
                    string configuredProfile = Path.Combine(_report.artifacts, "runtime_raw_profile.json");
                    File.WriteAllText(configuredProfile, JsonUtility.ToJson(profile));
                    _profileGui = new RawProfileGuiExercise(_hud, configuredProfile, Source, profile.loopbackPort);
                    _report.postChain = _session.GetComponents<SimulationPostStep>().Length == 1;
                    _session.FrameCompleted += OnFrame;
                    BeginStage(Stage.ProfileGui); return;
                }
                if (_stage == Stage.ProfileGui)
                {
                    _profileGui.Tick();
                    if (_profileGui.Failure != null) throw new InvalidOperationException("Raw profile GUI interaction: " + _profileGui.Failure);
                    if (!_profileGui.Completed) return;
                    _report.rawProfileGuiCompleted = true;
                    _report.rawProfileGuiEvents = _profileGui.EventsSent;
                    _report.rawProfileGuiEnteredPath = _profileGui.EnteredPath;
                    _report.rawProfileLoadedThroughHud = _session.acquisition.CurrentProfile.sourceId == Source
                        && _session.acquisition.LocalPort == _profileGui.LoadedPort;
                    BeginStage(Stage.Diagnostics);
                    _diagnosticSender = new UdpClient();
                    _diagnosticPacket = JsonUtility.FromJson<AcquisitionPacket>(JsonUtility.ToJson(Packets[uint.MaxValue - 1]));
                    _diagnosticPacket.sequence = 100;
                    foreach (var c in _diagnosticPacket.channels) c.sampleSequence = 100;
                    SendDiagnostic(JsonUtility.ToJson(_diagnosticPacket));
                    _diagnosticStep = 1; _diagnosticWaitStarted = EditorApplication.timeSinceStartup;
                    return;
                }
                double elapsed = Time.timeAsDouble - _stageTime;
                if (_stage == Stage.Diagnostics) { AdvanceDiagnostics(); return; }
                if (_stage == Stage.RestoreDiagnostics) { AdvanceRestoreDiagnostics(); return; }
                if (_stage == Stage.Settle && elapsed >= .2 && !_warmupRequested)
                {
                    _warmupRender = _report.normalRenderCallbacks;
                    ScreenCapture.CaptureScreenshot(Path.Combine(_report.artifacts, "warmup.png"));
                    _warmupRequested = true;
                }
                if (_stage == Stage.Settle && _warmupRequested && !_warmupCompleted)
                    _warmupCompleted = ScreenshotExists("warmup.png") && _report.normalRenderCallbacks > _warmupRender + 2;
                if (_stage == Stage.Settle && _warmupCompleted)
                {
                    _recordOrigin = _session.SimulationTime;
                    _report.recordingPath = Path.Combine(_report.artifacts, "csv_session.jsonl");
                    _session.BeginRecording(_report.recordingPath);
                    string recordingSignature = _session.acquisition.ConfigurationSignature;
                    _report.rawProfileRecordingRefused = !_hud.LoadRawAcquisitionProfile() && _session.IsRecording
                        && recordingSignature == _session.acquisition.ConfigurationSignature;
                    BeginStage(Stage.Receiving);
                    string pending = Path.Combine(_report.artifacts, "ready.pending");
                    File.WriteAllText(pending, JsonUtility.ToJson(new Ready { port = _session.acquisition.LocalPort,
                        sourceId = Source, softwareSendIntervalSeconds = _report.softwareSendIntervalSeconds }));
                    File.Move(pending, Path.Combine(_report.artifacts, "ready.json"));
                }
                else if (_stage == Stage.Receiving)
                {
                    if (File.Exists(Path.Combine(_report.artifacts, "sender_done.json"))) BeginStage(Stage.Stopped);
                }
                else if (_stage == Stage.Stopped && elapsed >= .35)
                {
                    _session.StopRecording();
                    if (!string.IsNullOrEmpty(_session.RecordingError)) throw new IOException(_session.RecordingError);
                    _report.recordedFrames = Recorded.Count;
                    _report.recordingWrittenFrames = _session.LastRecordingWrittenFrames;
                    _report.recordingMaximumWriteSeconds = _session.LastRecordingMaximumWriteSeconds;
                    VerifyDiskRecording();
                    _report.acceptedPackets = _session.acquisition.acceptedPackets;
                    _report.rejectedPackets = _session.acquisition.rejectedPackets;
                    // Recreate a different live acquisition state, as a fresh session
                    // has after restart. Historical raw identity must remain replay-only.
                    var liveProfile = AcquisitionProfile.Default();
                    liveProfile.enableLoopbackUdp = true; liveProfile.loopbackPort = FreePort();
                    _session.acquisition.ConfigureLoopback(liveProfile);
                    _replayLiveSignature = _session.acquisition.ConfigurationSignature;
                    _replayLivePort = _session.acquisition.LocalPort;
                    _session.BeginReplay(_report.recordingPath); _hud.ShowAcquisitionDetails();
                    _report.rawReplayLiveProfilePreserved = _session.acquisition.ConfigurationSignature == _replayLiveSignature;
                    _report.rawReplayListenerSuspended = _session.acquisition.LocalPort == 0;
                    string replaySignature = _session.acquisition.ConfigurationSignature;
                    _report.rawProfileReplayRefused = !_hud.LoadRawAcquisitionProfile() && _session.IsReplaying
                        && replaySignature == _session.acquisition.ConfigurationSignature;
                    BeginStage(Stage.Replay);
                }
                else if (_stage == Stage.Replay)
                {
                    QueueReplayShot();
                    if (_session.SimulationTime > Recorded[Recorded.Count - 1].timeSeconds - _recordOrigin + .02) BeginStage(Stage.Eof);
                }
                else if (_stage == Stage.Eof && elapsed >= .3 && AllShotFilesReady())
                {
                    _session.StopReplay(); _session.drivers[0].SetMode(PaddleDriver.Mode.External);
                    _hud.ShowAcquisitionDetails(); _diagnosticFocusRepaint = _hud.EditorGuiRepaintCount + 3;
                    _diagnosticStep = 0; _diagnosticWaitStarted = EditorApplication.timeSinceStartup;
                    BeginStage(Stage.RestoreDiagnostics);
                }
            }
            catch (Exception error) { Finish(error.ToString()); }
        }
        static void SendDiagnostic(string json) => SendDiagnostic(Encoding.UTF8.GetBytes(json));
        static void SendDiagnostic(byte[] bytes)
        {
            _diagnosticSender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, _session.acquisition.LocalPort));
            _diagnosticWaitStarted = EditorApplication.timeSinceStartup;
        }
        static void AdvanceDiagnostics()
        {
            if (EditorApplication.timeSinceStartup - _diagnosticWaitStarted > 5)
                throw new TimeoutException("Reception diagnostic step " + _diagnosticStep);
            var snapshot = _session.LastFrame?.acquisitionSnapshot;
            if (snapshot?.channels == null) return;
            var receiver = _session.acquisition.CaptureReceptionDiagnostics();
            string displayedReason = (string)HudRejection.GetValue(_hud);
            if (_diagnosticStep == 1 && receiver.acceptedPackets == 1 && snapshot.rawValidCount == 4)
            {
                _report.diagnosticLegal = true; _diagnosticReceiveTicks = snapshot.channels[0].receivedMonotonicTicks;
                SendDiagnostic(JsonUtility.ToJson(_diagnosticPacket)); _diagnosticStep = 2;
            }
            else if (_diagnosticStep == 2 && receiver.rejectedPackets == 1 && displayedReason.Contains("PACKET_DUPLICATE_OR_OLD"))
            {
                _report.diagnosticDuplicate = true;
                var wrong = JsonUtility.FromJson<AcquisitionPacket>(JsonUtility.ToJson(_diagnosticPacket));
                wrong.sourceId = "wrong-diagnostic-source"; wrong.sequence = 101;
                SendDiagnostic(JsonUtility.ToJson(wrong)); _diagnosticStep = 3;
            }
            else if (_diagnosticStep == 3 && receiver.rejectedPackets == 2 && displayedReason.Contains("INVALID_PACKET_OR_SOURCE"))
            { _report.diagnosticWrongSource = true; SendDiagnostic("{"); _diagnosticStep = 4; }
            else if (_diagnosticStep == 4 && receiver.rejectedPackets == 3 && displayedReason.Contains("INVALID_JSON_EVIDENCE"))
            { _report.diagnosticMalformedJson = true; SendDiagnostic(new byte[] { 0xc3, 0x28 }); _diagnosticStep = 5; }
            else if (_diagnosticStep == 5 && receiver.rejectedPackets == 4 && displayedReason.Contains("INVALID_UTF8"))
            {
                _report.diagnosticUtf8 = true;
                if (_report.diagnosticScreenshots.Count == 0) RequestDiagnosticShot("04_adc_invalid_utf8_hud.png");
                if (!AllDiagnosticShotFilesReady() || snapshot.channels[0].ageSeconds <= .1) return;
                _report.diagnosticNoRefresh = snapshot.channels[0].receivedMonotonicTicks == _diagnosticReceiveTicks;
                _report.diagnosticExpired = snapshot.rawValidCount == 0 && snapshot.channels[0].rawStatus == "STALE";
                _diagnosticPacket.sequence = 101;
                foreach (var c in _diagnosticPacket.channels) c.sampleSequence = c.channelId == "CH1" ? 100u : 101u;
                SendDiagnostic(JsonUtility.ToJson(_diagnosticPacket)); _diagnosticStep = 6;
            }
            else if (_diagnosticStep == 6 && receiver.acceptedPackets == 2 && receiver.rejectedChannels == 1
                && displayedReason.Contains("CHANNEL_DUPLICATE_OR_OLD:CH1"))
            {
                _report.diagnosticOldChannel = snapshot.channels[0].receivedMonotonicTicks == _diagnosticReceiveTicks
                    && snapshot.channels[1].packetSequence == 101 && snapshot.channels[1].sampleSequence == 101;
                if (_report.diagnosticScreenshots.Count == 1) RequestDiagnosticShot("05_adc_old_channel_hud.png");
                if (!AllDiagnosticShotFilesReady()) return;
                _diagnosticPacket.sequence = 102;
                foreach (var c in _diagnosticPacket.channels) c.sampleSequence = 102;
                SendDiagnostic(JsonUtility.ToJson(_diagnosticPacket)); _diagnosticStep = 7;
            }
            else if (_diagnosticStep == 7 && receiver.acceptedPackets == 3 && snapshot.rawValidCount == 4)
            {
                _report.diagnosticRecentRejectionPreserved = displayedReason.Contains("最近一次拒绝")
                    && displayedReason.Contains("CHANNEL_DUPLICATE_OR_OLD:CH1");
                _session.acquisition.StopListening(); _diagnosticStep = 8;
                _diagnosticWaitStarted = EditorApplication.timeSinceStartup;
            }
            else if (_diagnosticStep == 8 && receiver.listeningPort == 0
                && ((string)HudReception.GetValue(_hud)).Contains("当前未监听"))
            {
                _report.diagnosticStopped = receiver.loopbackConfigured && receiver.acceptedPackets == 3;
                if (_report.diagnosticScreenshots.Count == 2) RequestDiagnosticShot("06_adc_listener_stopped_hud.png");
                if (!AllDiagnosticShotFilesReady()) return;
                _report.diagnosticReload = _hud.LoadRawAcquisitionProfile()
                    && _session.acquisition.LocalPort == _profileGui.LoadedPort
                    && _session.acquisition.acceptedPackets == 0 && _session.acquisition.rejectedPackets == 0
                    && _session.acquisition.rejectedChannels == 0 && !_session.acquisition.Snapshot().channels[0].present;
                _diagnosticSender.Close(); _diagnosticSender = null;
                BeginStage(Stage.Settle);
            }
        }
        static void AdvanceRestoreDiagnostics()
        {
            if (EditorApplication.timeSinceStartup - _diagnosticWaitStarted > 5)
                throw new TimeoutException("Restore diagnostic step " + _diagnosticStep);
            var receiver = _session.acquisition.CaptureReceptionDiagnostics();
            string displayed = (string)HudReception.GetValue(_hud);
            if (_diagnosticStep == 0 && receiver.listeningPort == _replayLivePort
                && _hud.EditorGuiRepaintCount >= _diagnosticFocusRepaint
                && ReceptionControlVisible()
                && displayed.Contains("正在监听 127.0.0.1:" + _replayLivePort) && !displayed.Contains("回放中"))
            {
                _report.diagnosticRestored = _report.rawReplayLiveListenerRestored =
                    _session.acquisition.ConfigurationSignature == _replayLiveSignature
                    && !_session.acquisition.Snapshot().channels[0].present;
                RequestDiagnosticShot("07_adc_listener_restored_hud.png"); _diagnosticStep = 1;
            }
            else if (_diagnosticStep == 1 && AllDiagnosticShotFilesReady())
            {
                _session.BeginReplay(_report.recordingPath);
                _occupiedRestorePort = new UdpClient(new IPEndPoint(IPAddress.Loopback, _replayLivePort));
                _session.StopReplay(); _session.drivers[0].SetMode(PaddleDriver.Mode.External);
                _diagnosticStep = 2; _diagnosticWaitStarted = EditorApplication.timeSinceStartup;
            }
            else if (_diagnosticStep == 2 && receiver.listeningPort == 0 && displayed.Contains("当前未监听"))
            {
                _report.diagnosticRestoreFailure = receiver.loopbackConfigured && !displayed.Contains("回放中")
                    && _session.RawAcquisitionProfileStatus.Contains("未恢复") && !_session.IsReplaying;
                _occupiedRestorePort.Close(); _occupiedRestorePort = null;
                _session.acquisition.StartListening(); _diagnosticStep = 3;
                _diagnosticWaitStarted = EditorApplication.timeSinceStartup;
            }
            else if (_diagnosticStep == 3 && receiver.listeningPort == _replayLivePort
                && displayed.Contains("正在监听 127.0.0.1:" + _replayLivePort)) Finish(null);
        }
        static void QueueReplayShot()
        {
            var snapshot = _session.LastFrame?.acquisitionSnapshot;
            if (snapshot?.channels == null || !snapshot.replayed || _lastRequestFrame == Time.frameCount) return;
            if (_report.screenshots.Count > 0 && !_report.screenshots[_report.screenshots.Count - 1].fileReady) return;
            if (_report.screenshots.Count == 0 && snapshot.rawValidCount == 4
                && unchecked((uint)(snapshot.channels[0].packetSequence - 0xFFFFFFFEu)) >= 5)
                RequestShot("01_csv_recorded_valid_replay_hud.png");
            else if (_report.screenshots.Count == 1 && snapshot.channels[0].rawValid
                && snapshot.channels[1].rawStatus == "SOURCE_INVALID" && snapshot.channels[2].rawStatus == "STALE"
                && snapshot.channels[3].rawStatus == "RAW_MISSING") RequestShot("02_csv_recorded_partial_replay_hud.png");
            else if (_report.screenshots.Count == 2 && snapshot.channels[0].rawValid
                && snapshot.channels[2].rawStatus == "SATURATED") RequestShot("03_csv_recorded_saturated_replay_hud.png");
        }
        static void BeginStage(Stage stage) { _stage = stage; _stageTime = Time.timeAsDouble; }
        static void OnFrame(SimulationFrame frame)
        {
            if (_done) return;
            try
            {
                _report.fixedFrames++;
                if (_stage == Stage.Receiving || _stage == Stage.Stopped)
                {
                    Recorded.Add(frame);
                    _report.recordingMaximumObservedQueuedBytes = Math.Max(_report.recordingMaximumObservedQueuedBytes, _session.RecordingQueuedBytes);
                }
                Observations.Add(frame);
                var snapshot = frame.acquisitionSnapshot;
                if (!frame.hasAcquisitionSnapshot || snapshot?.channels == null || snapshot.channels.Length != 4)
                    throw new InvalidDataException("Four-channel completed snapshot missing.");
                bool replay = _stage == Stage.Replay || _stage == Stage.Eof;
                if (replay)
                {
                    _report.rawReplayLiveProfilePreserved &= _session.acquisition.ConfigurationSignature == _replayLiveSignature;
                    _report.rawReplayListenerSuspended &= _session.acquisition.LocalPort == 0;
                }
                if (!replay && _stage != Stage.Diagnostics && _stage != Stage.RestoreDiagnostics) ObserveLive(snapshot);
                if (_stage != Stage.Settle && _stage != Stage.ProfileGui) ObserveIsolation(frame, replay);
                if (_stage != Stage.ProfileGui && _stage != Stage.RestoreDiagnostics) ObserveHud(snapshot);
                if (_stage != Stage.ProfileGui) ObserveReceptionDiagnostics(replay);
                if (_stage == Stage.Replay) CompareReplay(frame);
                if (_stage == Stage.Eof)
                {
                    _report.eofFrames++;
                    if (!snapshot.replayed || snapshot.valid || snapshot.rawValidCount != 0 || snapshot.calibratedValidCount != 0
                        || snapshot.status != "REPLAY_ACQUISITION_EXPIRED") _report.eofFailures++;
                    foreach (var channel in snapshot.channels) if (channel.rawValid || channel.calibratedValid || channel.rawStatus != "REPLAY_EXPIRED") _report.eofFailures++;
                }
            }
            catch (Exception error) { _report.errors.Add("Frame observer: " + error); }
        }
        static void ObserveReceptionDiagnostics(bool replay)
        {
            _report.receptionDiagnosticFrames++;
            var captured = _session.acquisition.CaptureReceptionDiagnostics();
            string displayed = (string)HudReception.GetValue(_hud), rejected = (string)HudRejection.GetValue(_hud);
            bool correct = displayed.Contains("当前 PC 接收诊断") && displayed.Contains(captured.sourceId)
                && displayed.Contains(captured.profileVersion) && displayed.Contains("已接收包 " + captured.acceptedPackets)
                && displayed.Contains("拒绝包 " + captured.rejectedPackets) && displayed.Contains("拒绝通道样本 " + captured.rejectedChannels)
                && rejected.Contains(string.IsNullOrEmpty(captured.lastRejection) ? "尚无" : captured.lastRejection)
                && displayed.Contains(captured.listeningPort > 0 ? "正在监听 127.0.0.1:" + captured.listeningPort
                    : replay ? "实时监听已暂停" : "当前未监听");
            if (replay) { correct &= displayed.Contains("以下计数不属于录制"); _report.diagnosticReplayOwner |= correct; }
            else correct &= !displayed.Contains("回放中");
            if (!correct) _report.receptionDiagnosticFailures++;
        }
        static void ObserveLive(AcquisitionSnapshot snapshot)
        {
            if (_stage == Stage.ProfileGui) return;
            _report.sourceIdentity &= snapshot.sourceId == Source;
            _report.sourceIdentity &= snapshot.profileVersion == "pc-csv-raw-only-v1";
            bool missing = false;
            foreach (var channel in snapshot.channels)
            {
                if (!channel.present) { missing = true; continue; }
                _report.rawUnits &= channel.rawUnits == "example-count";
                if (!Packets.TryGetValue(channel.packetSequence, out var packet)) { _report.identityFailures++; continue; }
                var input = Array.Find(packet.channels, c => c.channelId == channel.channelId);
                if (input == null || channel.sourceId != packet.sourceId || channel.sourceKind != AcquisitionSourceKind.CsvImport
                    || channel.sampleSequence != input.sampleSequence || channel.deviceTimeUs != packet.deviceTimeUs
                    || channel.sampleTimeUs != input.sampleTimeUs || !channel.deviceTimeValid || !channel.sampleTimeValid
                    || channel.hasRaw != input.hasRaw || channel.sourceValid != input.valid || channel.saturated != input.saturated
                    || channel.rawText != input.rawText || (input.hasRaw && channel.raw != input.raw)) _report.identityFailures++;
                if (channel.channelId == "CH1") ObservedSequences.Add(channel.packetSequence);
            }
            if (missing) _report.missingFrames++;
            var c1 = snapshot.channels[0]; var c2 = snapshot.channels[1]; var c3 = snapshot.channels[2]; var c4 = snapshot.channels[3];
            if (c1.rawValid && c3.rawStatus == "STALE" && !c3.rawValid && !snapshot.samePacket) _report.partialExpiredFrames++;
            if (c1.rawValid && !c4.hasRaw && c4.rawStatus == "RAW_MISSING" && !c4.rawValid) _report.missingRawFrames++;
            if (c1.rawValid && !c2.sourceValid && c2.hasRaw && c2.rawStatus == "SOURCE_INVALID" && !c2.rawValid) _report.sourceInvalidFrames++;
            if (c1.rawValid && c3.saturated && c3.rawStatus == "SATURATED" && !c3.rawValid) _report.saturatedFrames++;
            if (c1.present && c1.packetSequence == 77)
            {
                if (_lastReceivedTicks == 0) _lastReceivedTicks = c1.receivedMonotonicTicks;
                else if (_lastReceivedTicks != c1.receivedMonotonicTicks) _report.senderStopFailures++;
                if (c1.ageSeconds > .1)
                {
                    _report.senderStopExpiredFrames++;
                    if (snapshot.rawValidCount != 0 || snapshot.valid || c1.rawValid || c2.rawValid || c3.rawValid || c4.rawValid)
                        _report.senderStopFailures++;
                }
            }
        }
        static void ObserveIsolation(SimulationFrame frame, bool replay)
        {
            _report.isolationFrames++;
            var paddle = frame.paddles[0]; var request = frame.feedbackRequests[0];
            if (paddle.valid || paddle.gripForceN != 0 || paddle.source != (replay ? PaddleInputKind.Replay : PaddleInputKind.External)
                || frame.rawWaterForceN[0].sqrMagnitude > 1e-12f || frame.boatPropulsionForceN[0].sqrMagnitude > 1e-12f
                || request.valid || request.forceWorldN.sqrMagnitude > 1e-12f || frame.filteredFeedbackForceN[0].sqrMagnitude > 1e-12f
                || frame.acquisitionSnapshot.calibratedValidCount != 0)
                _report.isolationFailures++;
            foreach (var channel in frame.acquisitionSnapshot.channels)
                if (channel.mapping != "Unassigned" || channel.calibrationMeasured || channel.calibratedValid || channel.forceN != 0)
                    _report.isolationFailures++;
        }
        static void ObserveHud(AcquisitionSnapshot snapshot)
        {
            _report.hudFrames++;
            string status = (string)HudStatus.GetValue(_hud);
            string[] rows = (string[])HudRows.GetValue(_hud);
            if (!_hud.visible || !_hud.detailsExpanded || !status.Contains(Source)
                || !status.Contains(snapshot.status) || !status.Contains(snapshot.rawValidCount + "/4")) _report.hudFailures++;
            for (int i = 0; i < 4; i++) if (!rows[i].Contains("CH" + (i + 1)) || !rows[i].Contains(snapshot.channels[i].rawStatus)) _report.hudFailures++;
            _report.hudFont = _hud.DisplayFontName;
        }
        static void CompareReplay(SimulationFrame actual)
        {
            int index = (int)Math.Round(actual.timeSeconds / Time.fixedDeltaTime) - 1;
            if (index < 0 || index >= Recorded.Count) return;
            var expected = Recorded[index];
            _report.replayFrames++;
            if (Math.Abs(actual.timeSeconds - (expected.timeSeconds - _recordOrigin)) > 1e-6
                || JsonUtility.ToJson(actual.acquisitionSnapshot) != JsonUtility.ToJson(AcquisitionSnapshot.ForReplay(expected.acquisitionSnapshot)))
                _report.replayFailures++;
        }
        static void VerifyDiskRecording()
        {
            int frames = 0; SimulationFrame header = null;
            RecordingCompletion completion = null;
            double lastIntegrityTime = double.NegativeInfinity;
            using (var hash = new RecordingContentHash())
            {
            foreach (string line in File.ReadLines(_report.recordingPath))
            {
                AcquisitionJsonEvidence.ValidateFrameJson(line);
                var frame = JsonUtility.FromJson<SimulationFrame>(line);
                if (completion != null) throw new InvalidDataException("Data after recording completion.");
                if (frame.recordType == "complete")
                { completion = JsonUtility.FromJson<RecordingCompletion>(line);
                    completion.lastFrameTimeSeconds = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(line, true); continue; }
                hash.Append(line);
                if (header == null) { header = frame; if (frame.recordType != "header") throw new InvalidDataException("Recording header missing."); continue; }
                if (frames >= Recorded.Count || JsonUtility.ToJson(frame) != JsonUtility.ToJson(Recorded[frames]))
                    throw new InvalidDataException("Disk recording differs from completed observation.");
                SensorAcquisitionBridge.ValidateRecordedSnapshot(frame.acquisitionSnapshot, header.acquisitionProfile);
                lastIntegrityTime = AcquisitionJsonEvidence.ReadRecordingTimeSeconds(line);
                frames++;
            }
            if (completion == null || completion.frameCount != frames || completion.contentSha256 != hash.FinishHex()
                || completion.configurationSignature != header.configurationSignature
                || completion.lastFrameTimeSeconds != lastIntegrityTime)
                throw new InvalidDataException("Recording completion evidence mismatch.");
            }
            if (frames != Recorded.Count || frames != _report.recordingWrittenFrames || frames == 0 || Math.Abs(header.timeSeconds - _recordOrigin) > 1e-6)
                throw new InvalidDataException("Recording count/origin mismatch.");
            using (var hash = System.Security.Cryptography.SHA256.Create())
                _report.recordingSha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(_report.recordingPath))).Replace("-", "").ToLowerInvariant();
        }
        static void RequestShot(string name)
        {
            _lastRequestFrame = Time.frameCount;
            _report.screenshots.Add(new Shot { file = name, stage = _stage.ToString(), requestedRender = _report.normalRenderCallbacks,
                requestedFrame = Time.frameCount, requestedSnapshot = _session.LastFrame.acquisitionSnapshot });
            ScreenCapture.CaptureScreenshot(Path.Combine(_report.artifacts, name));
        }
        static void RequestDiagnosticShot(string name)
        {
            _report.diagnosticScreenshots.Add(new Shot { file = name, stage = _stage.ToString(),
                requestedRender = _report.normalRenderCallbacks, requestedFrame = Time.frameCount,
                requestedSnapshot = _session.LastFrame.acquisitionSnapshot,
                requestedReception = (string)HudReception.GetValue(_hud), requestedRejection = (string)HudRejection.GetValue(_hud) });
            ScreenCapture.CaptureScreenshot(Path.Combine(_report.artifacts, name));
        }
        static bool ReceptionControlVisible()
        {
            if (!_hud.EditorControlScreenRects.TryGetValue("adcReception", out var rect)) return false;
            var viewport = _hud.EditorScrollViewportScreenRect;
            return rect.height > 0 && viewport.height > 0 && rect.yMin >= viewport.yMin && rect.yMax <= viewport.yMax;
        }
        static IEnumerable<Shot> AllShots()
        {
            foreach (var shot in _report.screenshots) yield return shot;
            foreach (var shot in _report.diagnosticScreenshots) yield return shot;
        }
        static bool AllDiagnosticShotFilesReady()
        {
            foreach (var shot in _report.diagnosticScreenshots) if (!shot.fileReady) return false;
            return true;
        }
        static bool ScreenshotExists(string file)
        {
            string path = Path.Combine(_report.artifacts, file);
            return File.Exists(path) && new FileInfo(path).Length >= 100;
        }
        static void PollShotFiles()
        {
            // Only a tiny readiness probe here. Decode/pixel analysis runs after acquisition/replay.
            foreach (var shot in AllShots())
                if (!shot.fileReady && _report.normalRenderCallbacks > shot.requestedRender && ScreenshotExists(shot.file))
                { shot.fileReady = true; shot.completedRender = _report.normalRenderCallbacks; shot.completedFrame = Time.frameCount; }
        }
        static void AnalyzeShots()
        {
            foreach (var shot in AllShots())
            {
                if (shot.completed) continue;
                string path = Path.Combine(_report.artifacts, shot.file);
                if (!File.Exists(path)) continue;
                byte[] bytes; try { bytes = File.ReadAllBytes(path); } catch (IOException) { continue; }
                if (bytes.Length < 100) continue;
                var image = new Texture2D(2, 2, TextureFormat.RGB24, false);
                try
                {
                    if (!image.LoadImage(bytes)) continue;
                    shot.width = image.width; shot.height = image.height;
                    Color32[] pixels = image.GetPixels32();
                    var bins = new HashSet<int>(); double sum = 0, squares = 0; int count = 0;
                    for (int y = 20; y < image.height - 20; y += 8) for (int x = image.width / 2; x < image.width - 20; x += 8)
                    {
                        Color32 color = pixels[y * image.width + x]; double luma = (77 * color.r + 150 * color.g + 29 * color.b) / 256d;
                        sum += luma; squares += luma * luma; count++;
                        bins.Add((color.r / 32) * 64 + (color.g / 32) * 8 + color.b / 32);
                    }
                    for (int y = 30; y < image.height - 30; y++) for (int x = 20; x < Math.Min(430, image.width); x++)
                    { Color32 color = pixels[y * image.width + x]; if (color.r > 150 && color.g > 150 && color.b > 150) shot.hudTextPixels++; }
                    shot.luminanceStdDev = (float)Math.Sqrt(Math.Max(0, squares / count - sum * sum / count / count));
                    shot.colorBins = bins.Count;
                    shot.hasSceneContent = shot.width == 1280 && shot.height == 720 && bins.Count >= 12
                        && shot.luminanceStdDev > 8 && shot.hudTextPixels > 1000 && shot.completedRender > shot.requestedRender;
                    shot.completed = true;
                }
                finally { UnityEngine.Object.DestroyImmediate(image); }
            }
        }
        static bool AllShotFilesReady()
        {
            foreach (var shot in _report.screenshots) if (!shot.fileReady) return false;
            return true;
        }
        static void BuiltinRendered(Camera camera) { if (GraphicsSettings.currentRenderPipeline == null) Rendered(camera); }
        static void PipelineRendered(ScriptableRenderContext context, Camera camera) { Rendered(camera); }
        static void Rendered(Camera camera)
        {
            if (!_done && _report != null && Application.isPlaying && camera != null && camera.cameraType == CameraType.Game && camera.targetTexture == null)
            {
                _report.normalRenderCallbacks++;
                if (_report.screenshots.Count > 0)
                {
                    var shot = _report.screenshots[_report.screenshots.Count - 1];
                    if (shot.firstRenderedSnapshot == null && _report.normalRenderCallbacks > shot.requestedRender)
                        shot.firstRenderedSnapshot = _session.LastFrame?.acquisitionSnapshot;
                }
            }
        }
        static void OnLog(string message, string stack, LogType type)
        {
            if (!_done && _report != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
                _report.errors.Add(message + "\n" + stack);
        }
        static void CheckResult(string name, bool pass, string evidence) { _report.checks.Add(new Check { name = name, passed = pass, evidence = evidence }); }
        static void Finish(string error)
        {
            if (_done) return; _done = true;
            if (error != null) _report.errors.Add(error);
            try
            {
                if (_session != null)
                {
                    _session.FrameCompleted -= OnFrame; _session.StopRecording();
                    if (_session.IsReplaying)
                    {
                        _session.StopReplay();
                        _report.rawReplayLiveListenerRestored = _session.acquisition.LocalPort == _replayLivePort
                            && _session.acquisition.ConfigurationSignature == _replayLiveSignature
                            && !_session.acquisition.Snapshot().channels[0].present;
                    }
                    _session.acquisition.StopLoopback();
                }
                _diagnosticSender?.Close(); _diagnosticSender = null;
                _occupiedRestorePort?.Close(); _occupiedRestorePort = null;
                PollShotFiles(); AnalyzeShots(); RestoreGameView();
                using (var writer = new StreamWriter(new FileStream(Path.Combine(_report.artifacts, "normal_observations.jsonl"), FileMode.CreateNew), new UTF8Encoding(false)))
                    foreach (var frame in Observations) writer.WriteLine(JsonUtility.ToJson(frame));
                _report.observedPacketSequences = ObservedSequences.Count;
                _report.sequenceWrap = ObservedSequences.Contains(uint.MaxValue - 1) && ObservedSequences.Contains(uint.MaxValue) && ObservedSequences.Contains(0);
                _report.duplicatePreservedReceiveTicks = _lastReceivedTicks > 0 && _report.senderStopFailures == 0;
                CheckResult("normal_play_fixed_chain", _report.physicsAutomatic && _report.postChain && _report.fixedFrames >= 200,
                    "automatic=" + _report.physicsAutomatic + "; onePostCompanion=" + _report.postChain + "; completedFixedFrames=" + _report.fixedFrames);
                CheckResult("csv_packet_identity_and_wrap", _report.acceptedPackets == PacketCount && _report.identityFailures == 0
                    && _report.sourceIdentity && _report.rawUnits && _report.sequenceWrap && ObservedSequences.Count >= 60,
                    "accepted=" + _report.acceptedPackets + "; observedSequences=" + ObservedSequences.Count + "; wrap=" + _report.sequenceWrap + "; fieldFailures=" + _report.identityFailures);
                CheckResult("independent_channel_states", _report.missingFrames > 0 && _report.partialExpiredFrames > 0 && _report.missingRawFrames > 0
                    && _report.sourceInvalidFrames > 0 && _report.saturatedFrames > 0,
                    "initialMissing/partialStale/missingRaw/sourceInvalid/saturated=" + _report.missingFrames + "/" + _report.partialExpiredFrames + "/" + _report.missingRawFrames + "/" + _report.sourceInvalidFrames + "/" + _report.saturatedFrames);
                CheckResult("adc_does_not_drive_paddle_or_forces", _report.isolationFrames >= 200 && _report.isolationFailures == 0,
                    "raw counts retain units and uncalibrated state; isolated frames=" + _report.isolationFrames + "; violations=" + _report.isolationFailures);
                CheckResult("sender_stop_and_duplicate_do_not_refresh", _report.rejectedPackets == 1 && _report.senderStopExpiredFrames > 10
                    && _report.senderStopFailures == 0 && _report.duplicatePreservedReceiveTicks,
                    "TTL=100ms; duplicateRejected=" + _report.rejectedPackets + "; expiredFrames=" + _report.senderStopExpiredFrames + "; freshnessViolations=" + _report.senderStopFailures);
                bool rendering = _report.screenshots.Count == 3 && _report.normalRenderCallbacks > 50;
                foreach (var shot in _report.screenshots) rendering &= shot.completed && shot.hasSceneContent;
                if (_report.screenshots.Count == 3)
                {
                    var partial = _report.screenshots[1].firstRenderedSnapshot;
                    rendering &= partial != null && partial.replayed && partial.channels[0].rawValid
                        && partial.channels[1].rawStatus == "SOURCE_INVALID" && partial.channels[2].rawStatus == "STALE"
                        && partial.channels[3].rawStatus == "RAW_MISSING";
                }
                CheckResult("hud_observes_csv_and_normal_render", _report.hudFrames >= 200 && _report.hudFailures == 0 && rendering,
                    "HUD field matches=" + _report.hudFrames + "; failures=" + _report.hudFailures + "; normalCameraCallbacks=" + _report.normalRenderCallbacks + "; screenshots=" + _report.screenshots.Count + "; font=" + _report.hudFont);
                CheckResult("complete_acquisition_record_replay", _report.recordedFrames > 100 && _report.replayFrames == _report.recordedFrames
                    && _report.replayFailures == 0 && !string.IsNullOrEmpty(_report.recordingSha256),
                    "recorded/compared=" + _report.recordedFrames + "/" + _report.replayFrames + "; snapshotFailures=" + _report.replayFailures + "; diskRecordingSHA256=" + _report.recordingSha256);
                CheckResult("replay_eof_invalidates_acquisition", _report.eofFrames > 10 && _report.eofFailures == 0,
                    "EOF frames=" + _report.eofFrames + "; violations=" + _report.eofFailures);
                CheckResult("explicit_raw_profile_hud_load_and_timeline_guards", _report.rawProfileLoadedThroughHud
                    && _report.rawProfileRecordingRefused && _report.rawProfileReplayRefused,
                    "public CSV profile CLI / HUD loader=" + _report.rawProfileLoadedThroughHud
                    + "; recording/replay refusal=" + _report.rawProfileRecordingRefused + "/" + _report.rawProfileReplayRefused);
                CheckResult("raw_profile_normal_gui_keyboard_and_click", _report.rawProfileGuiCompleted
                    && _report.rawProfileGuiEvents >= 10 && _report.rawProfileGuiEnteredPath == Path.Combine(_report.artifacts, "runtime_raw_profile.json")
                    && _report.rawProfileLoadedThroughHud,
                    "QueueGameViewInputEvent / normal IMGUI; completed=" + _report.rawProfileGuiCompleted
                    + "; events=" + _report.rawProfileGuiEvents + "; exactPath="
                    + (_report.rawProfileGuiEnteredPath == Path.Combine(_report.artifacts, "runtime_raw_profile.json")));
                CheckResult("raw_replay_isolates_different_live_profile_and_restores_listener",
                    _report.rawReplayLiveProfilePreserved && _report.rawReplayListenerSuspended && _report.rawReplayLiveListenerRestored,
                    "recorded CSV identity replayed with default live profile; livePreserved=" + _report.rawReplayLiveProfilePreserved
                    + "; listenerSuspended=" + _report.rawReplayListenerSuspended + "; originalListenerRestoredAndCleared=" + _report.rawReplayLiveListenerRestored);
                bool diagnosticsVisible = _report.diagnosticScreenshots.Count == 4;
                foreach (var shot in _report.diagnosticScreenshots) diagnosticsVisible &= shot.completed && shot.hasSceneContent;
                CheckResult("adc_receive_diagnostics_rejections_and_freshness", _report.receptionDiagnosticFrames >= 200
                    && _report.receptionDiagnosticFailures == 0 && _report.diagnosticLegal && _report.diagnosticDuplicate
                    && _report.diagnosticWrongSource && _report.diagnosticMalformedJson && _report.diagnosticUtf8
                    && _report.diagnosticOldChannel && _report.diagnosticNoRefresh && _report.diagnosticExpired
                    && _report.diagnosticRecentRejectionPreserved && diagnosticsVisible,
                    "normal UDP legal/duplicate/source/JSON/UTF8/channel rejection; HUD=" + _report.receptionDiagnosticFrames
                    + "; failures=" + _report.receptionDiagnosticFailures + "; receive ticks unchanged=" + _report.diagnosticNoRefresh
                    + "; raw100ms expiry=" + _report.diagnosticExpired + "; screenshots=" + _report.diagnosticScreenshots.Count);
                CheckResult("adc_receive_diagnostics_listener_and_replay_ownership", _report.diagnosticStopped
                    && _report.diagnosticReload && _report.diagnosticReplayOwner && _report.diagnosticRestored
                    && _report.diagnosticRestoreFailure,
                    "actual stopped/reload/replay-current-owner/restored/occupied-restore-failed=" + _report.diagnosticStopped
                    + "/" + _report.diagnosticReload + "/" + _report.diagnosticReplayOwner + "/" + _report.diagnosticRestored
                    + "/" + _report.diagnosticRestoreFailure + "; diagnostics are not recording fields");
                bool passed = _report.errors.Count == 0 && _report.checks.Count == 13;
                foreach (var check in _report.checks) passed &= check.passed;
                if (_profileGui != null) _report.rawProfileGuiEventsEvidence = _profileGui.Events;
                _report.result = passed ? "PASS" : "FAIL";
                File.WriteAllText(Path.Combine(_report.artifacts, "adc_runtime_result.json"), JsonUtility.ToJson(_report, true));
                SessionState.SetInt(ExitKey, passed ? 0 : 1);
                Debug.Log("[ADC_RUNTIME] RESULT=" + _report.result + " desktop software CSV import; hardware unverified");
                foreach (var check in _report.checks) Debug.Log("[ADC_RUNTIME] " + check.name + "=" + (check.passed ? "PASS" : "FAIL") + " " + check.evidence);
            }
            catch (Exception exception) { SessionState.SetInt(ExitKey, 1); Debug.Log("[ADC_RUNTIME] RESULT=FAIL evidence completion: " + exception); }
            Application.logMessageReceived -= OnLog; Camera.onPostRender -= BuiltinRendered;
            RenderPipelineManager.endCameraRendering -= PipelineRendered;
            SessionState.SetString(PhaseKey, "finishing"); if (Application.isPlaying) EditorApplication.ExitPlaymode();
        }
        static void FailBeforePlay(Exception error)
        {
            string artifacts = SessionState.GetString(ArtifactsKey, "");
            var report = new Report { artifacts = artifacts }; report.errors.Add(error.ToString());
            File.WriteAllText(Path.Combine(artifacts, "adc_runtime_result.json"), JsonUtility.ToJson(report, true));
            SessionState.SetString(PhaseKey, "finishing"); Debug.Log("[ADC_RUNTIME] RESULT=FAIL " + error);
        }
    }
}
