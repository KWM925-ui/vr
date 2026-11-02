using System;
using System.Globalization;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Physics completion events update statistics; rendering only reads them.
    /// F1 toggles a compact speed/distance readout; F9 toggles desktop diagnostics.</summary>
    public class RideHud : MonoBehaviour
    {
        [Header("数据源（留空自动查找）")]
        public PaddleHydrodynamics paddle;
        public BoatBuoyancy boat;
        public HardwareUdpBridge bridge;
        public PaddleDriver driver;
        public CableDriveTwin cable;
        public StewartPlatformTwin stewart;
        public SimulationSession session;
        public MotionTelemetry motionTelemetry;

        [Header("技术参数")]
        public float idealAttackAngle = 60f;
        public float attackTolerance = 8f;

        [Header("显示")]
        // Start with a clean scene. The operator may enable the compact speed/distance
        // readout with F1 or the technical diagnostics with F9.
        public bool visible;
        public bool compactMetricsVisible;
        public bool detailsExpanded;
        public KeyCode toggleKey = KeyCode.F1;
        public KeyCode toggleDiagnosticsKey = KeyCode.F9;
        public KeyCode modeSwitchKey = KeyCode.F4;

        public float StrokeRateSpm => (float)_metrics.StrokeRateSpm;
        public float LastImpulse => (float)_metrics.LastImpulse;
        public float AvgImpulse => (float)_metrics.AverageImpulse;
        public int StrokeCount => _metrics.StrokeCount;
        public float Distance => (float)_metrics.DistanceMeters;
        public float BoatSpeedMetersPerSecond => _frame == null ? 0f : HorizontalSpeed(_frame.boatVelocity);
        public string DisplayFontName { get; private set; }
        public static bool HasActivePanel => _activeHud != null && _activeHud.isActiveAndEnabled;
        public static bool DiagnosticsVisible => HasActivePanel && _activeHud.visible;

        static RideHud _activeHud;
        readonly StrokeMetrics _metrics = new StrokeMetrics();
        SimulationSession _subscribedSession;
        SimulationFrame _frame;
        GameObject _headsetMetricsObject;
        TextMesh _headsetMetricsText;
        int _paddlerIndex;
        PaddleInputKind _lastSource;
        bool _hasSource, _paused, _inputValid, _bladeWet;
        float _depth, _attackAngle, _relativeSpeed, _draft, _waterElevation, _strokeUsage;
        int _submergedCount, _probeCount;
        Vector2 _scroll;
        bool _focusAcquisitionPending, _focusAcquisitionLayoutReady, _acquisitionMetadataExpanded;
        bool _rawProfileExpanded;
        public string rawAcquisitionProfilePath = "";
        float _acquisitionSectionY;
#if UNITY_EDITOR
        // Observe actual IMGUI layout for the dedicated Editor interaction check.
        // These rectangles do not invoke actions or change runtime state.
        public readonly System.Collections.Generic.Dictionary<string, Rect> EditorControlScreenRects
            = new System.Collections.Generic.Dictionary<string, Rect>();
        public Rect EditorScrollViewportScreenRect { get; private set; }
        public Rect EditorGameScreenRect { get; private set; }
        public int EditorGuiRepaintCount { get; private set; }
        public bool EditorRawProfileExpanded => _rawProfileExpanded;
        public string EditorHeadsetMetricsText => _headsetMetricsText != null ? _headsetMetricsText.text : "";
        public bool EditorHeadsetMetricsBoundToMainCamera => _headsetMetricsText != null
            && Camera.main != null && _headsetMetricsText.transform.parent == Camera.main.transform;
        static Rect EditorScreenRect(Rect rect)
        {
            Vector2 minimum = GUIUtility.GUIToScreenPoint(rect.min);
            Vector2 maximum = GUIUtility.GUIToScreenPoint(rect.max);
            return Rect.MinMaxRect(minimum.x, minimum.y, maximum.x, maximum.y);
        }
        void EditorObserveControl(string name)
        {
            if (Event.current.type == EventType.Repaint)
                EditorControlScreenRects[name] = EditorScreenRect(GUILayoutUtility.GetLastRect());
        }
#endif
        GUIStyle _label, _big, _title, _muted, _button, _input;
        Font _font;
        string _acquisitionStatus = "旁路原始采集：尚未收到";
        readonly string[] _acquisitionRows = { "CH1 --", "CH2 --", "CH3 --", "CH4 --" };
        readonly string[] _acquisitionMetadataRows = new string[4];
        AcquisitionReceptionDiagnostics _receptionDiagnostics;
        bool _hasReceptionDiagnostics, _receptionReplay;
        string _acquisitionReception = "当前 PC 接收诊断：等待固定帧";
        string _acquisitionRejection = "最近一次拒绝：尚无";
        readonly CableCommandReadout _sentCableTargets = new CableCommandReadout();
        readonly CableCommandReadout _acknowledgedCableTargets = new CableCommandReadout();

        // Decode only while the existing details panel is drawn, and reuse each
        // packet's text across IMGUI passes. Read the captured frame, never poll
        // the controller or substitute its observed tensions for command targets.
        sealed class CableCommandReadout
        {
            string _packet, _session, _text;
            uint _sequence;

            public string Read(ClosedLoopTelemetry loop, string packet, uint expectedSequence)
            {
                if (_text != null && _packet == packet && _session == loop.sessionId && _sequence == expectedSequence)
                    return _text;
                _packet = packet; _session = loop.sessionId; _sequence = expectedSequence;
                _text = "未取得可核验的目标张力";
                if (string.IsNullOrEmpty(packet) || packet.Length > 80040
                    || !ulong.TryParse(loop.sessionId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong sessionId))
                    return _text;
                try
                {
                    if (!UdpClosedLoopClient.TryUnpack(Convert.FromBase64String(packet), sessionId, out uint sequence, out string json)
                        || sequence != expectedSequence) return _text;
                    var command = JsonUtility.FromJson<ClosedLoopCommand>(json);
                    if (command == null || command.kind != "step" || command.commandSequence != sequence
                        || command.tensionTargets == null || command.tensionTargets.Length == 0 || command.tensionTargets.Length > 64)
                        return _text;
                    var values = new string[command.tensionTargets.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        float tension = command.tensionTargets[i];
                        if (!SimulationValue.Finite(tension) || tension < 0f) return _text;
                        values[i] = tension.ToString("F2", CultureInfo.InvariantCulture);
                    }
                    _text = $"命令 {sequence} · [{string.Join(", ", values)}] N"
                        + (command.active && command.cableValid ? "" : " · 绳路目标停用");
                }
                catch (Exception error) when (error is FormatException || error is ArgumentException)
                {
                    // A malformed recorded packet must not interrupt the GUI.
                }
                return _text;
            }
        }

        void OnEnable()
        {
            _activeHud = this;
            TrySubscribe();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged += EditorPauseChanged;
#endif
        }
        void Start()
        {
            if (paddle == null) paddle = FindObjectOfType<PaddleHydrodynamics>(true);
            if (boat == null) boat = FindObjectOfType<BoatBuoyancy>(true);
            if (bridge == null) bridge = FindObjectOfType<HardwareUdpBridge>(true);
            if (driver == null) driver = paddle != null ? paddle.input : null;
            if (driver == null) driver = FindObjectOfType<PaddleDriver>(true);
            if (cable == null) cable = FindObjectOfType<CableDriveTwin>(true);
            if (stewart == null) stewart = FindObjectOfType<StewartPlatformTwin>(true);
            if (motionTelemetry == null) motionTelemetry = FindObjectOfType<MotionTelemetry>(true);
            EnsureDisplayFont();
            TrySubscribe();
            EnsureHeadsetMetricsObject();
            if (_headsetMetricsObject != null) _headsetMetricsObject.SetActive(compactMetricsVisible);
        }
        void TrySubscribe()
        {
            if (session == null) session = FindObjectOfType<SimulationSession>();
            if (session != null && ReferenceEquals(session, _subscribedSession)) return;
            Unsubscribe();
            ResetStatistics();
            if (session == null) return;
            _subscribedSession = session;
            _subscribedSession.FrameCompleted += OnFrameCompleted;
            _subscribedSession.TimelineReset += ResetStatistics;
        }
        void Unsubscribe()
        {
            if (_subscribedSession != null)
            {
                _subscribedSession.FrameCompleted -= OnFrameCompleted;
                _subscribedSession.TimelineReset -= ResetStatistics;
            }
            _subscribedSession = null;
        }
        void OnDisable()
        {
            Unsubscribe();
            if (_activeHud == this) _activeHud = null;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged -= EditorPauseChanged;
#endif
        }
        void OnDestroy()
        {
            if (_headsetMetricsObject != null) Destroy(_headsetMetricsObject);
            if (_font != null) Destroy(_font);
        }
        void Update()
        {
            EnsureHeadsetMetricsObject();
            if (Input.GetKeyDown(toggleKey)) ToggleCompactMetrics();
            if (Input.GetKeyDown(toggleDiagnosticsKey)) ToggleDiagnosticsPanel();
            if (Input.GetKeyDown(modeSwitchKey)) CycleInputMode();
            bool paused = Time.timeScale <= 0f;
            if (paused != _paused) { ResetStatistics(); _paused = paused; }
            TrySubscribe();
        }
        public void CycleInputMode()
        {
            TrySubscribe();
            if (session != null && session.drivers != null && session.drivers.Length == 1)
                driver = session.drivers[0];
            // Replay owns the paddle and the acquisition timeline. Exit through the session
            // so a keyboard action cannot mix live paddle input with recorded ADC observations.
            if (session != null && session.IsReplaying)
            {
                session.StopReplay();
                return;
            }
            if (driver == null) return;
            driver.SetMode(driver.mode == PaddleDriver.Mode.VirtualPaddler
                ? PaddleDriver.Mode.UdpDirect : PaddleDriver.Mode.VirtualPaddler);
            ResetStatistics();
            Debug.Log($"[HUD] 桨驱动切换为 {driver.mode}");
        }
        void OnApplicationPause(bool paused) { ResetStatistics(); _paused = paused; }
#if UNITY_EDITOR
        void EditorPauseChanged(UnityEditor.PauseState state) { ResetStatistics(); }
#endif
        public void ResetStatistics()
        {
            _metrics.Reset();
            _frame = null;
            _hasSource = false;
            _inputValid = _bladeWet = false;
            UpdateHeadsetMetricsText();
            UpdateAcquisitionDisplay(null);
        }

        /// <summary>Open the panel and scroll to the acquisition heading after its next real
        /// GUILayout pass. The target follows the current font, window size and device rows.</summary>
        public void ShowAcquisitionDetails()
        {
            visible = detailsExpanded = true;
            _acquisitionMetadataExpanded = false;
            _focusAcquisitionPending = true;
            _focusAcquisitionLayoutReady = false;
        }

        public bool LoadRawAcquisitionProfile()
        {
            TrySubscribe();
            bool loaded = session != null && session.LoadRawAcquisitionProfile(rawAcquisitionProfilePath);
            if (loaded) { UpdateAcquisitionDisplay(null); ShowAcquisitionDetails(); }
            return loaded;
        }

        // Read the captured physics frame, including its recorded validity/age decisions during
        // replay. Rendering never polls the live bridge or turns an ADC value into model force.
        void UpdateAcquisitionDisplay(SimulationFrame frame)
        {
            AcquisitionSnapshot snapshot = frame != null && frame.hasAcquisitionSnapshot ? frame.acquisitionSnapshot : null;
            _acquisitionStatus = snapshot == null ? "旁路原始采集：此帧没有采集快照"
                : $"{(snapshot.replayed ? "回放采集快照" : "PC 采集快照")} · {snapshot.sourceId ?? "来源未指定"} · {snapshot.status ?? "状态未知"}\n"
                    + $"原始有效 {snapshot.rawValidCount}/4 · 标定有效 {snapshot.calibratedValidCount}/4 · {(snapshot.samePacket ? "同包" : "分包 / 缺路")}";
            for (int i = 0; i < 4; i++)
            {
                var channel = snapshot?.channels != null && i < snapshot.channels.Length ? snapshot.channels[i] : null;
                if (channel == null)
                {
                    _acquisitionRows[i] = $"CH{i + 1} · 未收到";
                    _acquisitionMetadataRows[i] = null;
                    continue;
                }
                string raw = channel.hasRaw && channel.rawNumericFinite ? channel.raw.ToString("G9", CultureInfo.InvariantCulture)
                    : !string.IsNullOrEmpty(channel.rawText) ? channel.rawText : "--";
                string age = channel.present ? $"本地年龄 {channel.ageSeconds * 1000d:F1} ms" : "未收到";
                string mapped = string.IsNullOrEmpty(channel.mapping) || channel.mapping == "Unassigned" ? "映射未指定" : channel.mapping;
                string force = channel.calibratedValid && channel.calibrationMeasured && Finite(channel.forceN)
                    ? $"已标定换算 {channel.forceN:F2} N · {channel.calibrationSourceId}/{channel.calibrationVersion}"
                    : $"力换算不可用 · {channel.forceStatus ?? "UNCALIBRATED"}";
                string id = channel.channelId ?? ("CH" + (i + 1));
                _acquisitionRows[i] = $"{id} · 原始 {raw} {channel.rawUnits ?? "单位未确认"} · {(channel.rawValid ? "有效" : "无效")} ({channel.rawStatus ?? "UNKNOWN"})\n"
                    + $"{age} · 包/样本序号 {channel.packetSequence}/{channel.sampleSequence}\n{force}";
                _acquisitionMetadataRows[i] = $"{id} · {SourceName(channel.sourceKind)} · {mapped}\n"
                    + $"设备时钟 {(channel.deviceTimeValid ? channel.deviceTimeUs.ToString() + " us" : "未提供")} · "
                    + $"样本时钟 {(channel.sampleTimeValid ? channel.sampleTimeUs.ToString() + " us" : "未提供")}";
            }
        }

        void OnFrameCompleted(SimulationFrame frame)
        {
            if (_paused || frame == null) return;
            UpdateAcquisitionDisplay(frame);
            UpdateReceptionDiagnostics();
            if (session == null || session.drivers == null || session.drivers.Length == 0) return;
            _paddlerIndex = Array.IndexOf(session.drivers, driver);
            if (_paddlerIndex < 0) { _paddlerIndex = 0; driver = session.drivers[0]; }
            if (session.hydrodynamics == null || _paddlerIndex >= session.hydrodynamics.Length ||
                frame.paddles == null || _paddlerIndex >= frame.paddles.Length ||
                frame.feedbackRequests == null || _paddlerIndex >= frame.feedbackRequests.Length ||
                frame.rawWaterForceN == null || _paddlerIndex >= frame.rawWaterForceN.Length) return;
            paddle = session.hydrodynamics[_paddlerIndex];
            if (paddle == null) return;
            var sample = frame.paddles[_paddlerIndex];
            if (_hasSource && sample.source != _lastSource) { ResetStatistics(); UpdateAcquisitionDisplay(frame); }
            _lastSource = sample.source;
            _hasSource = true;
            bool inputValid = sample.valid && frame.feedbackRequests[_paddlerIndex].valid && paddle.InputValid;
            if (!_metrics.Observe(new StrokeObservation
            {
                timeSeconds = frame.timeSeconds, fixedStepSeconds = frame.fixedStepSeconds,
                inputValid = inputValid, bladeInWater = paddle.BladeInWater,
                waveInducedEntry = paddle.WaveInducedEntry, modelForceN = frame.rawWaterForceN[_paddlerIndex].magnitude,
                boatPosition = frame.boatPosition
            })) return;
            // Snapshot the completed physics step before any next step or GUI pass.
            _frame = frame;
            UpdateHeadsetMetricsText();
            _inputValid = inputValid;
            _bladeWet = inputValid && paddle.BladeInWater;
            _depth = paddle.EffectiveDepth;
            _attackAngle = paddle.AttackAngleDeg;
            _relativeSpeed = paddle.RelativeSpeed;
            if (boat != null)
            {
                _draft = boat.draft;
                _waterElevation = boat.waterElevation;
                _submergedCount = boat.submergedCount;
                _probeCount = boat.points != null ? boat.points.Length : 0;
            }
            if (stewart != null) _strokeUsage = stewart.strokeUsage;
        }

        void UpdateReceptionDiagnostics()
        {
            var receiver = session != null ? session.acquisition : null;
            var current = receiver != null ? receiver.CaptureReceptionDiagnostics() : default(AcquisitionReceptionDiagnostics);
            bool replay = session != null && session.IsReplaying;
            if (_hasReceptionDiagnostics && current.Matches(_receptionDiagnostics) && replay == _receptionReplay) return;
            _receptionDiagnostics = current; _receptionReplay = replay; _hasReceptionDiagnostics = true;
            string owner = replay ? "当前 PC 接收诊断（回放中，以下计数不属于录制）" : "当前 PC 接收诊断 · 固定帧更新";
            string listener = current.listeningPort > 0 ? "正在监听 127.0.0.1:" + current.listeningPort
                : replay ? "实时监听已暂停" : "当前未监听";
            _acquisitionReception = owner + "\n" + listener + " · 配置 "
                + (current.loopbackConfigured ? "已启用" : "未启用") + "\n来源 "
                + (current.sourceId ?? "未配置") + " · 版本 " + (current.profileVersion ?? "未配置")
                + "\n已接收包 " + current.acceptedPackets + " · 拒绝包 " + current.rejectedPackets
                + " · 拒绝通道样本 " + current.rejectedChannels;
            _acquisitionRejection = "最近一次拒绝：" + (string.IsNullOrEmpty(current.lastRejection) ? "尚无" : current.lastRejection);
        }

        void EnsureStyles()
        {
            if (_label != null) return;
            EnsureDisplayFont();
            DisplayFontName = DisplayFontName ?? "Unity default font";
            // Use the installed regular face: synthesized bold loses Chinese glyphs in the old HUD.
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Normal, wordWrap = true };
            _big = new GUIStyle(_label) { fontSize = 20, wordWrap = false };
            _title = new GUIStyle(_label) { fontSize = 14 };
            _muted = new GUIStyle(_label);
            _muted.normal.textColor = new Color(0.74f, 0.84f, 0.92f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Normal };
            _input = new GUIStyle(GUI.skin.textField) { fontSize = 13, fontStyle = FontStyle.Normal };
            if (_font != null)
                foreach (var style in new[] { _label, _big, _title, _muted, _button, _input }) style.font = _font;
            if (_headsetMetricsText != null && _font != null) _headsetMetricsText.font = _font;
        }

        void EnsureDisplayFont()
        {
            if (_font != null) return;
            string[] installed = Font.GetOSInstalledFontNames();
            string[] preferred = { "Microsoft YaHei", "微软雅黑", "Noto Sans SC", "SimHei", "黑体", "SimSun", "宋体" };
            foreach (string candidate in preferred)
                if (Array.Exists(installed, name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    _font = Font.CreateDynamicFontFromOSFont(candidate, 14);
                    DisplayFontName = candidate;
                    break;
                }
        }

        void OnGUI()
        {
            if (!Application.isPlaying) return;
            EnsureStyles();
            EnsureHeadsetMetricsObject();
#if UNITY_EDITOR
            if (Event.current.type == EventType.Repaint)
            {
                EditorControlScreenRects.Clear();
                EditorGameScreenRect = EditorScreenRect(new Rect(0, 0, Screen.width, Screen.height));
                EditorGuiRepaintCount++;
            }
#endif
            // Screen IMGUI is the operator/debug view. The optional HMD readout is a
            // separate camera-relative TextMesh, so technical diagnostics never enter VR.
            if (UnityEngine.XR.XRSettings.enabled) return;
            if (compactMetricsVisible && !DemoPresentationUi.Active) DrawCompactMetrics();
            if (!visible)
                return;
            float top = Screen.height >= 480 ? 104f : 12f;
            float width = Mathf.Min(356f, Screen.width - 24f);
            float availableHeight = Mathf.Max(140f, Screen.height - top - 12f);
            float height = detailsExpanded ? availableHeight : Mathf.Min(294f, availableHeight);
            // Explicit heading instead of a BeginArea box title that overlaps the first row.
            GUILayout.BeginArea(new Rect(12f, top, width, height), GUIContent.none,
                DemoPresentationUi.Active ? DemoPresentationUi.LegacyPanelStyle(_font) : GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("划桨与设备指标", _title);
            if (GUILayout.Button("F9 隐藏", _button, GUILayout.Width(76))) visible = false;
            GUILayout.EndHorizontal();
            GUILayout.Label(_metrics.HasStrokeRate ? $"{StrokeRateSpm:F1} spm   {StrokeCount} 桨" : $"-- spm   {StrokeCount} 桨", _big);
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUILayout.ExpandHeight(true));
            DrawSummary();
            if (detailsExpanded) DrawDetails();
            GUILayout.EndScrollView();
#if UNITY_EDITOR
            if (Event.current.type == EventType.Repaint)
                EditorScrollViewportScreenRect = EditorScreenRect(GUILayoutUtility.GetLastRect());
#endif
            // Use the content-space heading rect measured during Repaint, then let the next
            // BeginScrollView apply it. Never query dummy Layout rectangles or change the
            // scroll transform while the current content is still being drawn.
            // TimelineReset temporarily clears _frame. Keep the navigation request until
            // the full completed-frame page is present, so its final heading is targeted.
            if (_focusAcquisitionPending && _focusAcquisitionLayoutReady && _frame != null && Event.current.type == EventType.Repaint)
            {
                _scroll.y = Mathf.Max(0f, _acquisitionSectionY - 4f);
                _focusAcquisitionPending = _focusAcquisitionLayoutReady = false;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(detailsExpanded ? "收起详情" : "展开桨 / 设备详情", _button))
            {
                detailsExpanded = !detailsExpanded;
                _scroll = Vector2.zero;
                _focusAcquisitionPending = _focusAcquisitionLayoutReady = false;
            }
            if (GUILayout.Button("四路采集", _button, GUILayout.Width(86f))) ShowAcquisitionDetails();
#if UNITY_EDITOR
            EditorObserveControl("acquisition");
#endif
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        public void ToggleCompactMetrics()
        {
            compactMetricsVisible = !compactMetricsVisible;
            if (Application.isPlaying)
            {
                EnsureHeadsetMetricsObject();
                if (_headsetMetricsObject != null)
                    _headsetMetricsObject.SetActive(compactMetricsVisible && UnityEngine.XR.XRSettings.enabled);
            }
        }

        public void ToggleDiagnosticsPanel() => visible = !visible;

        void EnsureHeadsetMetricsObject()
        {
            Camera headCamera = Camera.main;
            if (_headsetMetricsObject != null)
            {
                if (headCamera != null && _headsetMetricsObject.transform.parent != headCamera.transform)
                    _headsetMetricsObject.transform.SetParent(headCamera.transform, false);
                _headsetMetricsObject.SetActive(compactMetricsVisible && UnityEngine.XR.XRSettings.enabled);
                return;
            }
            if (headCamera == null) return;

            _headsetMetricsObject = new GameObject("OptionalHeadsetMetrics");
            _headsetMetricsObject.transform.SetParent(headCamera.transform, false);
            _headsetMetricsObject.transform.localPosition = new Vector3(0f, -0.34f, 1.45f);
            _headsetMetricsObject.transform.localRotation = Quaternion.identity;
            _headsetMetricsText = _headsetMetricsObject.AddComponent<TextMesh>();
            _headsetMetricsText.anchor = TextAnchor.MiddleCenter;
            _headsetMetricsText.alignment = TextAlignment.Center;
            _headsetMetricsText.fontSize = 48;
            _headsetMetricsText.characterSize = 0.003f;
            _headsetMetricsText.fontStyle = FontStyle.Normal;
            _headsetMetricsText.color = Color.white;
            if (_font != null) _headsetMetricsText.font = _font;
            var renderer = _headsetMetricsObject.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            UpdateHeadsetMetricsText();
            _headsetMetricsObject.SetActive(compactMetricsVisible && UnityEngine.XR.XRSettings.enabled);
        }

        void UpdateHeadsetMetricsText()
        {
            if (_headsetMetricsText == null) return;
            _headsetMetricsText.text = _frame == null
                ? "速度 -- m/s   距离 -- m"
                : $"速度 {BoatSpeedMetersPerSecond.ToString("F2", CultureInfo.InvariantCulture)} m/s   距离 {Distance.ToString("F1", CultureInfo.InvariantCulture)} m";
        }

        void DrawCompactMetrics()
        {
            const float margin = 12f;
            float width = Mathf.Min(310f, Screen.width - margin * 2f);
            const float height = 34f;
            string readout = _frame == null
                ? "航速 -- m/s   航程 -- m"
                : $"航速 {HorizontalSpeed(_frame.boatVelocity):F2} m/s   航程 {Distance:F1} m";
            GUI.Box(new Rect(margin, margin, width, height), GUIContent.none);
            GUI.Label(new Rect(margin + 8f, margin + 4f, width - 16f, height - 8f), readout, _title);
        }

        static float HorizontalSpeed(Vector3 velocity)
        {
            velocity.y = 0f;
            return velocity.magnitude;
        }

        void DrawSummary()
        {
            if (_frame == null)
            {
                GUILayout.Label(_paused ? "仿真暂停，统计已重置" : "等待物理帧；统计在完整入水、出水后更新", _muted);
                return;
            }
            var sample = _frame.paddles[_paddlerIndex];
            var request = _frame.feedbackRequests[_paddlerIndex];
            var feedback = _frame.cableFeedback;
            GUILayout.Label($"上桨模型冲量 {LastImpulse:F2} N·s   均值 {AvgImpulse:F2}", _label);
            GUILayout.Label($"船速 {_frame.boatVelocity.magnitude * 3.6f:F2} km/h   航程 {Distance:F1} m", _label);
            GUILayout.Label($"输入 {SourceName(sample.source)} · {(_inputValid ? (_bladeWet ? "入水" : "出水") : "无效 / 过期")}", _label);
            GUILayout.Label($"模型水动力 {_frame.rawWaterForceN[_paddlerIndex].magnitude:F1} N   请求 {request.forceWorldN.magnitude:F1} N", _label);
            GUILayout.Label(CurrentFeedback(feedback, request)
                ? HasIndependentLoop
                    ? $"最近有效执行回报（模拟） {feedback.forceWorldN.magnitude:F1} N · 回执对应命令 {feedback.commandSequence}"
                    : $"{(feedback.simulated ? "模拟设备反馈" : "设备报告反馈")} {feedback.forceWorldN.magnitude:F1} N"
                : HasIndependentLoop ? "执行回报：无效或等待新鲜回执" : "设备反馈：无效或等待当前请求响应", _muted);
            var platformFeedback = _frame.platformFeedback;
            if (platformFeedback != null)
                GUILayout.Label(CurrentFeedback(platformFeedback, _frame.platformRequest)
                    ? HasIndependentLoop
                        ? $"平台最近有效执行回报 P {platformFeedback.eulerDegrees.x:F1}°  R {platformFeedback.eulerDegrees.z:F1}° · 命令 {platformFeedback.commandSequence}"
                        : $"平台{(platformFeedback.simulated ? "模拟" : "报告")} P {platformFeedback.eulerDegrees.x:F1}°  R {platformFeedback.eulerDegrees.z:F1}°"
                    : HasIndependentLoop ? "平台执行回报：无效或等待新鲜回执" : "平台反馈：无效或等待当前请求响应", _muted);
        }

        void DrawDetails()
        {
            if (_frame == null) { DrawAcquisitionDetails(); return; }
            var request = _frame.feedbackRequests[_paddlerIndex];
            var sample = _frame.paddles[_paddlerIndex];
            Section("划桨统计 · 模型水动力积分");
            GUILayout.Label($"当前冲量 {_metrics.CurrentImpulse:F2} N·s   上桨峰值 {_metrics.LastPeakForceN:F1} N", _label);
            GUILayout.Label("桨频 = 相邻有效入水的完整周期；均值为已完成桨的算术平均。失效期间中断统计。", _muted);
            float deviation = _attackAngle - idealAttackAngle;
            var previousColor = GUI.color;
            if (_bladeWet) GUI.color = Mathf.Abs(deviation) <= attackTolerance
                ? new Color(0.45f, 1f, 0.58f) : new Color(1f, 0.8f, 0.42f);
            GUILayout.Label($"攻角 {_attackAngle:F1}°   偏差 {deviation:+0.0;-0.0;0.0}°", _label);
            GUI.color = previousColor;
            GUILayout.Label($"入水深度 {_depth * 100f:F1} cm   相对流速 {_relativeSpeed:F2} m/s", _label);
            GUILayout.Label($"输入握力报告 {sample.gripForceN:F1} N · {SourceName(sample.source)}", _muted);
            if (_frame.encoderTelemetry != null && _paddlerIndex < _frame.encoderTelemetry.Length
                && _frame.encoderTelemetry[_paddlerIndex] != null)
            {
                var encoder = _frame.encoderTelemetry[_paddlerIndex];
                GUILayout.Label($"编码器包 {encoder.status} · {encoder.packetBytes} B · 计数序号 {encoder.sequence} / 输入序号 {encoder.sourceSequence} · CRC {(encoder.crcValid ? "OK" : "FAIL")}", _muted);
                GUILayout.Label($"原始计数 {(encoder.rawCounts == null ? "--" : string.Join(", ", encoder.rawCounts))}", _muted);
                GUILayout.Label($"独立握力通道 {encoder.gripForceN:F2} N · {encoder.gripForceSource}（未实测）", _muted);
            }
            var euler = _frame.boatRotation.eulerAngles;
            Section("船体模型");
            GUILayout.Label($"横摇 {BoatBuoyancy.NormalizeAngle(euler.z):F2}°   纵摇 {BoatBuoyancy.NormalizeAngle(euler.x):F2}°", _label);
            GUILayout.Label($"吃水 {_draft * 100f:F1} cm   浸没点 {_submergedCount}/{_probeCount}   浪高 {_waterElevation * 100f:+0.0;-0.0;0.0} cm", _label);

            Section("拉索请求与设备反馈");
            GUILayout.Label($"请求力 {request.forceWorldN.magnitude:F2} N   力矩 {request.torqueAtGripWorldNm.magnitude:F2} N·m", _label);
            var cableFeedback = _frame.cableFeedback;
            var protocol = _frame.deviceProtocol;
            if (HasIndependentLoop)
            {
                GUILayout.Label("独立 UDP 执行回报（未实测模型）；回执对应已发送的历史命令。", _muted);
                var loop = _frame.closedLoop;
                GUILayout.Label(loop.commandSent
                    ? "本步发送目标 · " + _sentCableTargets.Read(loop, loop.commandPacketBase64, loop.commandSequence)
                    : "本步未发送三绳执行目标", _label);
                GUILayout.Label("回执源目标 · " + _acknowledgedCableTargets.Read(loop,
                    loop.acknowledgedCommandPacketBase64, loop.acknowledgedCommandSequence), _label);
                GUILayout.Label("目标数组按绳编号顺序排列；下方反馈张力与残差对应回执源命令。", _muted);
            }
            else if (protocol != null)
            {
                GUILayout.Label($"样机协议（未实测）· 三绳命令 {protocol.cableCommandBytes} B CRC {protocol.cableCommandCrcValid} / 解析 {protocol.cableCommandParsed} 有效 {protocol.cableCommandValid} / 反馈 {protocol.cableFeedbackValid}：{protocol.cableStatus}", _muted);
                GUILayout.Label($"六轴命令 {protocol.platformCommandBytes} B CRC {protocol.platformCommandCrcValid} / 解析 {protocol.platformCommandParsed} 有效 {protocol.platformCommandValid} / 反馈 {protocol.platformFeedbackValid}：{protocol.platformStatus}", _muted);
            }
            if (cableFeedback != null)
            {
                GUILayout.Label(cableFeedback.simulated ? "以下为软件模拟反馈" : "以下为设备报告反馈", _muted);
                GUILayout.Label(HasIndependentLoop
                    ? $"回执对应命令 {cableFeedback.commandSequence} · 主机命令时刻 {cableFeedback.timeSeconds:F3} s · 最近执行回报有效 {CurrentFeedback(cableFeedback, request)}"
                    : $"请求/反馈序号 {request.commandSequence}/{cableFeedback.commandSequence}   当前响应 {CurrentFeedback(cableFeedback, request)}", _muted);
                int count = Math.Min(cableFeedback.lengthsMm?.Length ?? 0, cableFeedback.tensionsN?.Length ?? 0);
                for (int i = 0; i < count; i++)
                    GUILayout.Label($"绳 {i + 1}   {cableFeedback.lengthsMm[i]:F1} mm   反馈张力 {cableFeedback.tensionsN[i]:F1} N", _label);
                GUILayout.Label($"合力残差 {cableFeedback.forceResidualN:F2} N   力矩残差 {cableFeedback.torqueResidualNm:F2} N·m", _label);
                GUILayout.Label($"有效 {CurrentFeedback(cableFeedback, request)}   能力受限 {cableFeedback.capabilityLimited}", _muted);
                GUILayout.Label(cableFeedback.status ?? "--", _muted);
            }

            Section("六轴平台请求与反馈");
            var platformRequest = _frame.platformRequest;
            GUILayout.Label($"请求 P {platformRequest.eulerDegrees.x:F2}°   R {platformRequest.eulerDegrees.z:F2}°", _label);
            GUILayout.Label($"请求位移 {platformRequest.translationM.ToString("F3")} m", _label);
            var platformFeedback = _frame.platformFeedback;
            if (platformFeedback != null)
            {
                GUILayout.Label(platformFeedback.simulated ? "以下为软件模拟反馈" : "以下为设备报告反馈", _muted);
                if (HasIndependentLoop)
                    GUILayout.Label($"回执对应命令 {platformFeedback.commandSequence} · 主机命令时刻 {platformFeedback.timeSeconds:F3} s", _muted);
                GUILayout.Label($"反馈 P {platformFeedback.eulerDegrees.x:F2}°   R {platformFeedback.eulerDegrees.z:F2}°", _label);
                GUILayout.Label($"反馈位移 {platformFeedback.translationM.ToString("F3")} m", _label);
                if (platformFeedback.actuatorLengthsMm != null)
                    for (int i = 0; i < platformFeedback.actuatorLengthsMm.Length; i++)
                        GUILayout.Label($"缸 {i + 1}   长度 {platformFeedback.actuatorLengthsMm[i]:F1} mm", _label);
                GUILayout.Label($"有效 {CurrentFeedback(platformFeedback, platformRequest)}   能力受限 {platformFeedback.capabilityLimited}", _muted);
                GUILayout.Label(platformFeedback.status ?? "--", _muted);
            }
            if (stewart != null) GUILayout.Label($"模拟行程占用 {_strokeUsage * 100f:F0}%   配置伺服时间常数 {stewart.servoLagTimeConstant * 1000f:F0} ms", _muted);

            DrawAcquisitionDetails();
            Section("通信状态");
            if (HasIndependentLoop)
                GUILayout.Label(IndependentCommunicationSummary(), _label);
            else if (bridge != null)
            {
                GUILayout.Label($"UDP 输入 {(bridge.linkAlive ? "近期有包" : "离线 / 过期")}   {bridge.rxRateHz:F0} Hz", _label);
                GUILayout.Label($"接收 {bridge.rxCount}   序号缺口 {bridge.rxLost}   拒绝 {bridge.rxRejected}", _label);
            }
            if (!HasIndependentLoop && motionTelemetry != null)
            {
                GUILayout.Label($"平台遥测发送 {motionTelemetry.remoteHost}:{motionTelemetry.remotePort}", _label);
                GUILayout.Label($"{motionTelemetry.txRateHz:F0} Hz   序号 {motionTelemetry.txSeq}   {(motionTelemetry.limitClamped ? "软限位生效" : "未触发软限位")}", _label);
            }
            GUILayout.Label($"物理时间 {_frame.timeSeconds:F3} s   步长 {_frame.fixedStepSeconds * 1000f:F1} ms   统计 {_metrics.ObservedSteps} 步", _muted);
        }

        void DrawAcquisitionDetails()
        {
            Section("四路原始采集旁路");
            if (_focusAcquisitionPending)
            {
                if (Event.current.type == EventType.Layout) _focusAcquisitionLayoutReady = true;
                else if (_focusAcquisitionLayoutReady && Event.current.type == EventType.Repaint)
                    _acquisitionSectionY = GUILayoutUtility.GetLastRect().y;
            }
            GUILayout.Label(_acquisitionStatus, _muted);
            GUILayout.Label(_acquisitionReception, _muted);
#if UNITY_EDITOR
            EditorObserveControl("adcReception");
#endif
            GUILayout.Label(_acquisitionRejection, _muted);
            foreach (string row in _acquisitionRows) GUILayout.Label(row, _label);
            if (_acquisitionMetadataExpanded)
                foreach (string row in _acquisitionMetadataRows)
                    if (!string.IsNullOrEmpty(row)) GUILayout.Label(row, _muted);
            if (GUILayout.Button(_acquisitionMetadataExpanded ? "收起来源 / 映射 / 时钟" : "来源 / 映射 / 时钟详情", _button))
                _acquisitionMetadataExpanded = !_acquisitionMetadataExpanded;
            if (GUILayout.Button(_rawProfileExpanded ? "收起原始数据接入" : "接入 CSV 原始数据", _button))
                _rawProfileExpanded = !_rawProfileExpanded;
#if UNITY_EDITOR
            EditorObserveControl("rawExpand");
#endif
            if (_rawProfileExpanded)
            {
                GUILayout.Label("采集配置 JSON 路径（相对项目根目录或完整路径）", _muted);
                // Long absolute paths scroll inside the editor instead of widening
                // every row and hiding the load button beyond the panel.
                rawAcquisitionProfilePath = GUILayout.TextField(rawAcquisitionProfilePath ?? "", _input,
                    GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
#if UNITY_EDITOR
                EditorObserveControl("rawPath");
#endif
                if (GUILayout.Button("载入原始采集配置", _button)) LoadRawAcquisitionProfile();
#if UNITY_EDITOR
                EditorObserveControl("rawLoad");
#endif
                GUILayout.Label(session != null ? session.RawAcquisitionProfileStatus : "等待仿真会话", _muted);
                GUILayout.Label("原始值保持原单位，未标定数据不换算为力。配置加载后不可热切换。", _muted);
            }
        }

        void Section(string title) { GUILayout.Space(9); GUILayout.Label(title, _title); }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        bool HasIndependentLoop => _frame?.closedLoop != null && _frame.closedLoop.active;
        bool ConsumedIndependentFeedback()
        {
            var loop = _frame?.closedLoop;
            return loop != null && loop.active && loop.ready && loop.feedbackFresh && loop.encoderConsumed
                && Finite(loop.receivedAgeSeconds) && loop.receivedAgeSeconds >= 0d && loop.receivedAgeSeconds <= 0.1d
                && Finite(loop.commandAckAgeSeconds) && loop.commandAckAgeSeconds >= 0d && loop.commandAckAgeSeconds <= 0.1d
                && loop.feedback != null && loop.feedback.kind == "feedback" && loop.feedback.valid && !loop.feedback.watchdog
                && loop.feedback.commandSequence == loop.acknowledgedCommandSequence
                && _frame.paddles != null && _paddlerIndex >= 0 && _paddlerIndex < _frame.paddles.Length
                && _frame.paddles[_paddlerIndex].valid && _frame.paddles[_paddlerIndex].sequence == loop.feedbackSequence;
        }
        bool IndependentFeedbackMatches(uint commandSequence, double commandTime) =>
            ConsumedIndependentFeedback() && commandSequence == _frame.closedLoop.acknowledgedCommandSequence
            && Finite(commandTime) && commandTime >= 0d && commandTime <= _frame.timeSeconds;
        bool CurrentFeedback(CableDeviceFeedback feedback, ForceFeedbackRequest request) =>
            feedback != null && feedback.valid && (HasIndependentLoop
                ? IndependentFeedbackMatches(feedback.commandSequence, feedback.timeSeconds)
                : feedback.commandSequence == request.commandSequence && feedback.timeSeconds == request.timeSeconds);
        bool CurrentFeedback(PlatformDeviceFeedback feedback, PlatformMotionRequest request) =>
            feedback != null && feedback.valid && (HasIndependentLoop
                ? IndependentFeedbackMatches(feedback.commandSequence, feedback.timeSeconds)
                : feedback.commandSequence == request.commandSequence && feedback.timeSeconds == request.timeSeconds);
        string IndependentCommunicationSummary()
        {
            var loop = _frame.closedLoop;
            return $"独立 UDP · {(ConsumedIndependentFeedback() ? "本帧已消费有效执行回报" : "等待有效回报 / 回报过期")}\n"
                + $"宿主 PID {loop.hostProcessId} · 控制器 PID {loop.controllerProcessId}\n"
                + $"已发送命令 {loop.commandSequence} · 回执对应命令 {loop.acknowledgedCommandSequence} · 回包序号 {loop.feedbackSequence}\n"
                + $"接收年龄 {loop.receivedAgeSeconds * 1000d:F1} ms · 命令确认年龄 {loop.commandAckAgeSeconds * 1000d:F1} ms\n"
                + $"发送 {loop.sentPackets} · 接收 {loop.receivedPackets} · 拒绝 {loop.rejectedPackets}\n{loop.status}";
        }
        static string SourceName(PaddleInputKind source)
        {
            switch (source)
            {
                case PaddleInputKind.Synthetic: return "合成划桨";
                case PaddleInputKind.Udp: return "UDP";
                case PaddleInputKind.Mouse: return "鼠标";
                case PaddleInputKind.Replay: return "回放";
                case PaddleInputKind.Encoder: return "编码器解码";
                default: return "外部输入";
            }
        }
        static string SourceName(AcquisitionSourceKind source)
        {
            switch (source)
            {
                case AcquisitionSourceKind.Fixture: return "软件样例";
                case AcquisitionSourceKind.CsvImport: return "CSV 导入";
                case AcquisitionSourceKind.DeviceAdapter: return "设备适配器";
                default: return "来源未验证";
            }
        }
    }
}
