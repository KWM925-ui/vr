using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 桨驱动器：把"桨该怎么动"的四种来源统一成同一套 transform 输出。
    ///
    /// 四种模式（对应工程规划里"无硬件也能完整演示"的要求）：
    ///   UdpDirect       —— 真硬件 / mock 发的位姿直接覆盖（验数据链路用，会跳变）
    ///   UdpInterpolated —— 真硬件位姿 + 平滑（正式联调用）
    ///   VirtualPaddler  —— 参数化虚拟划桨手：椭圆轨迹自动完成
    ///                      入水(Catch) → 拉桨(Drive) → 出水(Finish) → 回桨(Recovery)
    ///                      没有真桨时用它，整个系统照样能跑、能演示、能答辩
    ///   Mouse           —— 鼠标拖拽，手动找手感
    ///
    /// 为什么虚拟划桨手必须有：
    ///   硬件 12 月 1 日才到位。若桨不动，"桨水动力→推进力→船前进→平台姿态"这条链路
    ///   一个环节都验证不了。虚拟划桨手就是这条链路的信号源，且参数（桨频/入水深度/摆幅）
    ///   可调，正好用来标定"多大桨频对应多快船速"。
    ///
    /// 桨叶朝向的处理（决定攻角大小，进而决定阻力）：
    ///   桨杆方向 = 支点 → 桨叶中心。
    ///   桨叶面法线 = 划水方向（船尾方向）在垂直于桨杆平面上的投影。
    ///   这样拉桨时桨面正对水流（α≈90°，阻力大），回桨时桨面顺势切水（α小）。
    /// </summary>
    public class PaddleDriver : MonoBehaviour
    {
        public enum Mode { UdpDirect, UdpInterpolated, VirtualPaddler, Mouse, Replay, External }
        [HideInInspector] public bool externallyStepped;
        public PaddleSample Sample { get; private set; }
        public bool ExecutionFeedbackOwnsPose { get; private set; }
        bool _hasExecutionRenderPose;
        Vector3 _executionBladeLocal;
        Quaternion _executionRotationLocal;

        public void SetExecutionPoseOwner(bool enabled)
        {
            if (ExecutionFeedbackOwnsPose == enabled) return;
            ExecutionFeedbackOwnsPose = enabled;
            _hasExecutionRenderPose = false;
        }
        public bool leftSide;
        public float inputTimeoutSeconds = 0.10f;
        double _sampleTime;
        uint _sampleSequence;
        Vector3 _previousPosition;
        Quaternion _previousRotation;
        double _previousPoseTime;
        bool _hasUdpPose;
        uint _udpPoseSequence;
        long _udpPoseReceivedTicks;
        Vector3 _udpBladeInStage, _udpRelativeVelocity, _udpRelativeAngularVelocity;
        Vector3 _udpClampedAxes;
        Quaternion _udpRotationInStage;
        bool _hasPreviousPosition;
        PaddleSample _externalSample;
        bool _hasExternalSample;
        bool _hasReplayFrame;
        double _lastReplayFrameTime = double.NegativeInfinity;

        [Header("数据源")]
        public HardwareUdpBridge bridge;

        [Header("模式")]
        public Mode mode = Mode.VirtualPaddler;

        [Header("空间关系（虚拟划桨手 / 鼠标模式用）")]
        [Tooltip("船的根节点；留空自动找 BoatBuoyancy")]
        public BoatBuoyancy boat;

        [Tooltip("桨支点在船体本地坐标的位置（船舷右上）")]
        public Vector3 pivotLocalPos = new Vector3(0.448f, 0.20f, 0f);

        [Header("虚拟划桨手参数")]
        [Tooltip("划桨频率（桨/分钟，SPM）。龙舟比赛 60~90，休闲 30~45")]
        [Range(0f, 120f)] public float strokesPerMinute = 45f;

        [Tooltip("前后划桨行程（米，桨叶中心相对船体从最前到最后的半幅）")]
        [Range(0.1f, 1.2f)] public float sweepAmplitude = 0.45f;

        [Tooltip("划水时桨叶中心的下沉量（米，相对船体坐标系）。0.75 对应实际浸没约 0.43m")]
        [Range(0.05f, 1.0f)] public float bladeDepth = 0.75f;

        [Tooltip("回桨时桨叶中心的船体本地 y 坐标（米），不再叠加船舷工位高度")]
        [Range(0.02f, 0.6f)] public float bladeAirHeight = 0.20f;

        [Tooltip("虚拟手持桨的横向内倾角（度）；样机假设，须通过船体间隙和握把可达范围校验")]
        [Range(0f, 35f)] public float inboardShaftLeanDeg = 15f;

        [Tooltip("上握把随桨叶前后行程移动的比例；样机假设。0 表示固定的前后支点")]
        [Range(0f, 0.5f)] public float upperGripTravelFraction = 0.25f;

        [Tooltip("相对船舷支点的额外横向偏移（米）。按当前导入船宽预留桨叶边缘间隙；未实测，可调")]
        [Range(0f, 1.5f)] public float sideOffset = 0.16f;

        [Header("UDP 平滑（Interpolated 模式）")]
        [Tooltip("跟随时间常数（秒）。0.05≈跟手，0.2≈绵软")]
        public float smoothing = 0.05f;

        [Header("坐标修正")]
        public bool applyCalibration = false;
        public SpatialCalibrator calibrator;

        [Tooltip("硬件发来的桨位姿是否按船体局部坐标解释。" +
                 "true（推荐）= 桨跟着船走，人坐在船上划，船前进桨也前进；" +
                 "false = 直接当世界坐标，只在船固定不动的展台模式用")]
        public bool paddleInBoatSpace = true;

        [Header("桨柄 → 桨叶 映射（关键）")]
        [Tooltip("握把中心到桨叶中心的距离（米）。硬件力传感器/编码器测的是握把位置，" +
                 "而水动力采样点在桨叶中心，两者差整整一截桨杆。" +
                 "不补这一截，桨叶会被摆到水面之上，人怎么划都没有水阻。")]
        [Range(0f, 2.5f)] public float shaftToBlade = 1.0f;
        [Tooltip("传感器坐标系中指定桨刚体参考点的偏移（米），不是限制人手的固定支点")]
        public Vector3 sensorToReferencePosition;
        [Tooltip("从桨模型轴到传感器安装轴的固定旋转（度），需实测替换")]
        public Vector3 sensorToReferenceEulerDeg;

        [Header("鼠标模式")]
        public float mouseSensitivity = 6f;

        float _phase;
        float _mouseSweep, _mousePlunge;

        void Start()
        {
            if (bridge == null) bridge = FindObjectOfType<HardwareUdpBridge>(true);
            if (calibrator == null) calibrator = FindObjectOfType<SpatialCalibrator>(true);
            if (boat == null) boat = FindObjectOfType<BoatBuoyancy>(true);

            if (mode == Mode.UdpDirect || mode == Mode.UdpInterpolated)
            {
                if (bridge == null)
                    Debug.LogWarning("[PaddleDriver] UDP 模式下场景里没有 HardwareUdpBridge，桨不会动。");
            }
            if (boat == null && mode != Mode.UdpDirect && mode != Mode.UdpInterpolated)
                Debug.LogWarning("[PaddleDriver] 找不到船体，虚拟划桨手无法定位支点。");
        }

        /// <summary>切换驱动模式（HUD/按键用）</summary>
        public void SetMode(Mode m)
        {
            SetExecutionPoseOwner(false);
            _hasPreviousPosition = false;
            _hasUdpPose = false;
            _hasExternalSample = false;
            _hasReplayFrame = false;
            _lastReplayFrameTime = double.NegativeInfinity;
            mode = m;
            Debug.Log($"[PaddleDriver] 切换到 {m} 模式");
        }

        void Update()
        {
            if (!externallyStepped) Tick(Time.deltaTime);
        }

        // Rigidbody interpolation can move the boat after the fixed-step input
        // was sampled. Re-apply the same local trajectory at render time so the
        // visible paddle follows the interpolated hull instead of appearing to
        // slide away from the gunwale. This does not advance phase or alter the
        // sampled physics pose.
        void LateUpdate()
        {
            if (ExecutionFeedbackOwnsPose)
            {
                if (_hasExecutionRenderPose && boat != null)
                    transform.SetPositionAndRotation(boat.transform.TransformPoint(_executionBladeLocal),
                        boat.transform.rotation * _executionRotationLocal);
                return;
            }
            if (mode == Mode.VirtualPaddler && boat != null)
                ApplyBladeTrajectory(strokesPerMinute <= 0.1f ? 1.8f * Mathf.PI : _phase);
        }

        /// <summary>
        /// 单步驱动。Update 里用 Time.deltaTime；自动化自检（batchmode 非 Play 模式）
        /// 里 Time.deltaTime 恒为 0，必须由外部注入 dt 才能推进相位。
        /// 这是"自检里桨不动"的唯一原因，别在别处乱找。
        /// </summary>
        public void Tick(float dt)
        {
            if (!SimulationValue.Finite(dt) || dt <= 0f) return;
            _sampleTime += dt;
            bool valid = true;
            ulong deviceTime = 0;
            uint sequence = ++_sampleSequence;
            float grip = 0f;
            PaddleInputKind source = PaddleInputKind.Synthetic;

            switch (mode)
            {
                case Mode.UdpDirect:
                case Mode.UdpInterpolated:
                    source = PaddleInputKind.Udp;
                    HardwareUdpBridge.PaddleState udp = default;
                    long udpReceivedTicks = 0;
                    valid = bridge != null && bridge.TryGetPaddle(inputTimeoutSeconds, out udp, out udpReceivedTicks)
                        && (!applyCalibration || (calibrator != null && calibrator.HasUsableCalibration));
                    if (valid)
                    {
                        DriveByUdp(dt, udp, udpReceivedTicks);
                        sequence = udp.seq;
                        deviceTime = udp.timestampUs;
                        grip = udp.gripForce;
                    }
                    break;
                case Mode.VirtualPaddler:
                    StepPhase(dt);
                    valid = boat != null;
                    if (valid && !ExecutionFeedbackOwnsPose)
                        ApplyBladeTrajectory(strokesPerMinute <= 0.1f ? 1.8f * Mathf.PI : _phase);
                    break;
                case Mode.Mouse:
                    source = PaddleInputKind.Mouse;
                    valid = boat != null;
                    DriveMouse();
                    break;
                case Mode.Replay:
                    if (_hasReplayFrame)
                    {
                        var recorded = _externalSample;
                        recorded.valid &= _sampleTime + 1e-6 >= recorded.timeSeconds
                            && _sampleTime - recorded.timeSeconds <= inputTimeoutSeconds;
                        transform.SetPositionAndRotation(recorded.bladePosition, recorded.bladeRotation);
                        Sample = recorded;
                        _previousPosition = recorded.bladePosition;
                        _previousRotation = recorded.bladeRotation;
                        _previousPoseTime = recorded.timeSeconds;
                        _hasPreviousPosition = recorded.valid;
                        return;
                    }
                    valid = false; source = PaddleInputKind.Replay;
                    break;
                case Mode.External:
                    var sample = _externalSample;
                    valid = _hasExternalSample && sample.valid && !double.IsNaN(sample.timeSeconds) && !double.IsInfinity(sample.timeSeconds)
                        && _sampleTime + 1e-6 >= sample.timeSeconds
                        && _sampleTime - sample.timeSeconds <= inputTimeoutSeconds
                        && SimulationValue.Finite(sample.bladePosition)
                        && SimulationValue.Finite(sample.bladeRotation)
                        && SimulationValue.Finite(sample.bladeVelocity) && SimulationValue.Finite(sample.bladeAngularVelocity)
                        && SimulationValue.Finite(sample.gripForceN);
                    if (valid)
                    {
                        transform.SetPositionAndRotation(sample.bladePosition, sample.bladeRotation);
                        if (!sample.hasAngularVelocity)
                        {
                            float poseDt = (float)(sample.timeSeconds - _previousPoseTime);
                            sample.bladeAngularVelocity = _hasPreviousPosition && poseDt > 0f
                                ? SimulationValue.AngularVelocity(_previousRotation, transform.rotation, poseDt) : Vector3.zero;
                            sample.hasAngularVelocity = true;
                            // Cache the derivative for this accepted pose. A lower-rate
                            // source can be held for several physics steps without losing
                            // its angular velocity; the unchanged freshness gate still applies.
                            _externalSample = sample;
                        }
                        Sample = sample;
                        _previousPosition = sample.bladePosition;
                        _previousRotation = transform.rotation;
                        _previousPoseTime = sample.timeSeconds;
                        _hasPreviousPosition = true;
                        return;
                    }
                    source = PaddleInputKind.External;
                    break;
            }
            valid &= SimulationValue.Finite(transform.position) && SimulationValue.Finite(transform.rotation);
            Vector3 velocity = valid && _hasPreviousPosition ? (transform.position - _previousPosition) / dt : Vector3.zero;
            Vector3 angularVelocity = valid && _hasPreviousPosition
                ? SimulationValue.AngularVelocity(_previousRotation, transform.rotation, dt) : Vector3.zero;
            if (valid && mode == Mode.UdpDirect)
            {
                velocity = _udpRelativeVelocity;
                angularVelocity = _udpRelativeAngularVelocity;
                if (paddleInBoatSpace && !applyCalibration && boat != null)
                {
                    velocity = boat.transform.rotation * velocity;
                    angularVelocity = boat.transform.rotation * angularVelocity;
                    if (boat.BoatRb != null)
                    {
                        velocity += boat.BoatRb.GetPointVelocity(transform.position);
                        angularVelocity += boat.BoatRb.angularVelocity;
                    }
                }
                else if (applyCalibration && calibrator != null && calibrator.HasUsableCalibration)
                {
                    velocity = calibrator.PlatformRotationToWorld(Quaternion.identity) * velocity;
                    angularVelocity = calibrator.PlatformRotationToWorld(Quaternion.identity) * angularVelocity;
                }
                if (_udpClampedAxes != Vector3.zero && boat != null)
                {
                    // A clipped coordinate is fixed to the moving hull. Remove its
                    // relative motion, while retaining tangential and hull point velocity.
                    Vector3 hullVelocity = boat.BoatRb != null
                        ? boat.BoatRb.GetPointVelocity(transform.position) : Vector3.zero;
                    Vector3 relative = boat.transform.InverseTransformDirection(velocity - hullVelocity);
                    if (_udpClampedAxes.x != 0f) relative.x = 0f;
                    if (_udpClampedAxes.y != 0f) relative.y = 0f;
                    if (_udpClampedAxes.z != 0f) relative.z = 0f;
                    velocity = boat.transform.TransformDirection(relative) + hullVelocity;
                }
            }
            valid &= SimulationValue.Finite(velocity) && SimulationValue.Finite(angularVelocity);
            if (!valid)
            {
                velocity = angularVelocity = Vector3.zero;
                _hasUdpPose = false;
            }
            Sample = new PaddleSample { sequence = sequence, timeSeconds = _sampleTime, deviceTimeUs = deviceTime,
                bladePosition = transform.position, bladeRotation = transform.rotation,
                bladeVelocity = velocity, bladeAngularVelocity = angularVelocity, hasAngularVelocity = true,
                gripForceN = grip, source = source, originalSource = source, valid = valid };
            _previousPosition = transform.position;
            _previousRotation = transform.rotation;
            _previousPoseTime = _sampleTime;
            _hasPreviousPosition = valid;
        }

        public bool SetExternalSample(PaddleSample sample)
        {
            if (mode == Mode.Replay) return false;
            if (double.IsNaN(sample.timeSeconds) || double.IsInfinity(sample.timeSeconds)
                || !SimulationValue.Finite(sample.bladePosition) || !SimulationValue.Finite(sample.bladeRotation)
                || !SimulationValue.Finite(sample.bladeVelocity) || !SimulationValue.Finite(sample.bladeAngularVelocity)
                || !SimulationValue.Finite(sample.gripForceN)) return false;
            if (_hasExternalSample && (unchecked((int)(sample.sequence - _externalSample.sequence)) <= 0
                || sample.timeSeconds < _externalSample.timeSeconds)) return false;
            sample.source = sample.originalSource = PaddleInputKind.External;
            _externalSample = sample; _hasExternalSample = true;
            return true;
        }

        /// <summary>
        /// 供编码器/协议解码层把“已接收并校验”的姿态交回驱动器。
        /// 该入口不会绕过输入有效性检查；它只更新当前显示姿态和水动力要消费的样本。
        /// </summary>
        public bool ApplyDecodedSample(PaddleSample sample)
        {
            PaddleSample prior = Sample;
            bool newObservation = !prior.valid || sample.sequence != prior.sequence;
            if (!sample.valid || !SimulationValue.Finite(sample.bladePosition) ||
                !SimulationValue.Finite(sample.bladeRotation) || !SimulationValue.Finite(sample.bladeVelocity) ||
                !SimulationValue.Finite(sample.bladeAngularVelocity) || !SimulationValue.Finite(sample.gripForceN)
                || double.IsNaN(sample.timeSeconds) || double.IsInfinity(sample.timeSeconds))
            {
                sample.valid = false;
                sample.bladeVelocity = sample.bladeAngularVelocity = Vector3.zero;
                sample.hasAngularVelocity = true;
                sample.gripForceN = 0f;
                sample.bladePosition = SimulationValue.Finite(transform.position) ? transform.position : Vector3.zero;
                sample.bladeRotation = SimulationValue.Finite(transform.rotation) ? transform.rotation : Quaternion.identity;
                sample.timeSeconds = _sampleTime;
                Sample = sample;
                _hasPreviousPosition = false;
                _hasUdpPose = false;
                _udpRelativeVelocity = _udpRelativeAngularVelocity = Vector3.zero;
                return false;
            }
            Sample = sample;
            transform.SetPositionAndRotation(sample.bladePosition, sample.bladeRotation);
            if (ExecutionFeedbackOwnsPose && boat != null)
            {
                _executionBladeLocal = boat.transform.InverseTransformPoint(sample.bladePosition);
                _executionRotationLocal = Quaternion.Inverse(boat.transform.rotation) * sample.bladeRotation;
                _hasExecutionRenderPose = true;
            }
            // A lower-rate packet is deliberately replayed on several fixed steps.
            // Keep the derivative anchor at the last distinct packet; advancing it on
            // every held frame would halve the interval and double the next pose rate.
            if (newObservation)
            {
                _previousPosition = sample.bladePosition;
                _previousRotation = sample.bladeRotation;
                _previousPoseTime = sample.timeSeconds;
            }
            _hasPreviousPosition = true;
            return true;
        }
        // A recorded fixed frame is an observation, not a new device packet. Repeated packet
        // sequences can have different interpolated poses/velocities, and invalid observations
        // must also be restored. This path cannot be selected by forging a sample.source tag.
        internal void RestoreReplayFrame(PaddleSample sample, double frameTimeSeconds)
        {
            if (mode != Mode.Replay || double.IsNaN(frameTimeSeconds) || double.IsInfinity(frameTimeSeconds)
                || frameTimeSeconds <= _lastReplayFrameTime
                || double.IsNaN(sample.timeSeconds) || double.IsInfinity(sample.timeSeconds)
                || sample.timeSeconds > frameTimeSeconds + 1e-6
                || !SimulationValue.Finite(sample.bladePosition) || !SimulationValue.Finite(sample.bladeRotation)
                || !SimulationValue.Finite(sample.bladeVelocity) || !SimulationValue.Finite(sample.bladeAngularVelocity)
                || !SimulationValue.Finite(sample.gripForceN)
                || !System.Enum.IsDefined(typeof(PaddleInputKind), sample.source) || sample.source == PaddleInputKind.Replay)
                throw new System.InvalidOperationException("Invalid or unordered replay observation.");
            sample.originalSource = sample.source;
            sample.source = PaddleInputKind.Replay;
            _externalSample = sample;
            _hasReplayFrame = true;
            _lastReplayFrameTime = frameTimeSeconds;
        }
        public void SetPhase01(float phase) { _phase = Mathf.Repeat(phase, 1f) * 2f * Mathf.PI; }
        public void ResetInputTimeline()
        { SetExecutionPoseOwner(false);
            _sampleTime = 0; _hasPreviousPosition = false; _hasUdpPose = false; _hasExternalSample = false; _hasReplayFrame = false;
            _lastReplayFrameTime = double.NegativeInfinity; _externalSample = default; _phase = 0; Sample = default; }

        void StepPhase(float dt)
        {
            if (strokesPerMinute <= 0.1f) return;
            _phase += (strokesPerMinute / 60f) * 2f * Mathf.PI * dt;
            _phase = Mathf.Repeat(_phase, 2f * Mathf.PI);
        }

        // ---------------- UDP 直驱 ----------------
        Quaternion PlatformRotToWorld(Quaternion localRot)
        {
            return calibrator != null && calibrator.HasUsableCalibration
                ? calibrator.PlatformRotationToWorld(localRot) : localRot;
        }

        void DriveByUdp(float dt, HardwareUdpBridge.PaddleState state, long receivedTicks)
        {
            if (bridge == null) return;

            Quaternion sensorRotation = Quaternion.Euler(state.eulerDeg);
            Vector3 pos = state.position + sensorRotation * sensorToReferencePosition;
            Quaternion rot = sensorRotation * Quaternion.Euler(sensorToReferenceEulerDeg);

            // 桨柄 → 桨叶：硬件测的是握把，水动力要的是桨叶。
            // 桨对象约定 local +Y = 桨杆（指向握把），所以桨叶 = 握把 − 桨杆方向 × 杆长。
            // 漏掉这一步，桨叶会一直停在水面之上，划起来毫无阻力 —— 手上没手感，
            // 那整个力反馈闭环就只是"电机在动"，不是"划龙舟"。
            if (shaftToBlade > 0.01f)
                pos -= (rot * Vector3.up) * shaftToBlade;

            Vector3 acceptedStagePosition = pos;
            Quaternion stageRotation = rot;

            // 桨位姿的参考系。
            // 硬件测的是"桨在平台上的位置"，平台就是船。船在虚拟河道里会一直往前跑，
            // 若把桨位姿当世界坐标用，划十几秒后桨就留在原地、人和桨分离了 ——
            // 这是虚实同步里最容易被忽略的一处断裂。所以默认按船体局部坐标解释。
            // Calibration maps platform to the scene stage. Apply it before interpreting
            // stage coordinates; never transform an already world-space point a second time.
            if (applyCalibration && calibrator != null && calibrator.HasUsableCalibration)
            {
                pos = calibrator.PlatformToWorld(pos);
                rot = PlatformRotToWorld(rot);
            }
            else if (paddleInBoatSpace && boat != null)
            {
                pos = boat.transform.TransformPoint(pos);
                rot = boat.transform.rotation * rot;
            }

            // 物理合理域保护:本船半宽 0.51m,任何来源(UDP/配置错位/坐标解释
            // 错误)的桨位姿超出船体可达范围时钳回包络并告警——不静默吞掉,
            // 也不让桨飞出船体(2026-10-24 桨逃逸 BUG 的教训)。真实传感器的
            // 正常数据远小于该包络,不会被触及。
            _udpClampedAxes = Vector3.zero;
            if (boat != null && paddleInBoatSpace)
            {
                Vector3 local = boat.transform.InverseTransformPoint(pos);
                Vector3 clamped = local;
                clamped.x = Mathf.Clamp(local.x, -1.4f, 1.4f);   // 已锁定的船体局部工作包络
                clamped.z = Mathf.Clamp(local.z, -4.6f, 4.6f);
                clamped.y = Mathf.Clamp(local.y, -1.2f, 2.4f);
                _udpClampedAxes = new Vector3(local.x != clamped.x ? 1f : 0f,
                    local.y != clamped.y ? 1f : 0f, local.z != clamped.z ? 1f : 0f);
                if (_udpClampedAxes != Vector3.zero)
                {
                    if ((clamped - local).sqrMagnitude > 1e-6f)
                        Debug.LogWarning($"[PADDLE] 外部桨位姿超出物理包络,已钳制 {local} → {clamped}");
                    Vector3 constrainedWorldPosition = boat.transform.TransformPoint(clamped);
                    if (!applyCalibration)
                        acceptedStagePosition = clamped;
                    else if (calibrator != null && calibrator.HasUsableCalibration)
                        acceptedStagePosition += Quaternion.Inverse(PlatformRotToWorld(Quaternion.identity))
                            * (constrainedWorldPosition - pos);
                    pos = constrainedWorldPosition;
                }
            }

            // Derive motion from the accepted, bounded position in the source frame.
            // Only a new UDP pose advances this anchor, so sparse/held input retains
            // its measured derivative. Hull motion is added once in Tick.
            if (!_hasUdpPose || state.seq != _udpPoseSequence)
            {
                float poseDt = (float)((receivedTicks - _udpPoseReceivedTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
                _udpRelativeVelocity = _hasUdpPose && poseDt > 0f
                    ? (acceptedStagePosition - _udpBladeInStage) / poseDt : Vector3.zero;
                _udpRelativeAngularVelocity = _hasUdpPose && poseDt > 0f
                    ? SimulationValue.AngularVelocity(_udpRotationInStage, stageRotation, poseDt) : Vector3.zero;
                _udpBladeInStage = acceptedStagePosition; _udpRotationInStage = stageRotation;
                _udpPoseSequence = state.seq; _udpPoseReceivedTicks = receivedTicks; _hasUdpPose = true;
            }

            if (mode == Mode.UdpDirect)
            {
                transform.position = pos;
                transform.rotation = rot;
            }
            else
            {
                float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, smoothing));
                transform.position = Vector3.Lerp(transform.position, pos, k);
                transform.rotation = Quaternion.Slerp(transform.rotation, rot, k);
            }
        }

        // ---------------- 虚拟划桨手 ----------------
        // 相位推进已合并到 Tick → StepPhase，这里只保留轨迹计算

        // ---------------- 鼠标 ----------------
        void DriveMouse()
        {
            if (boat == null) return;
            _mouseSweep = Mathf.Clamp(_mouseSweep + Input.GetAxis("Mouse X") * mouseSensitivity * 0.02f, -1f, 1f);
            _mousePlunge = Mathf.Clamp(_mousePlunge + Input.GetAxis("Mouse Y") * mouseSensitivity * 0.02f, -1f, 1f);

            // 鼠标模式下：横向拖 = 前后扫，纵向拖 = 深浅
            float reach = _mouseSweep * sweepAmplitude;
            Vector3 localBlade = new Vector3(
                (leftSide ? -1f : 1f) * (Mathf.Abs(pivotLocalPos.x) + sideOffset),
                Mathf.Lerp(bladeAirHeight, LowestHandheldBladeY(reach), (_mousePlunge + 1f) * 0.5f),
                pivotLocalPos.z + reach);
            // 保留原最小工位；完整桨杆/桨叶的余量由当前船型的 HalfBeam/RailTop 约束。
            if (Mathf.Abs(localBlade.x) < 0.43f)
                localBlade.x = Mathf.Sign(localBlade.x) * 0.43f;
            PlacePaddle(localBlade);
        }

        /// <summary>
        /// 虚拟划桨手的桨叶轨迹：严格按真实划桨四段式走
        ///   u∈[0, 0.55)   拉桨 Drive：桨在水中，沿船身向后扫过 2A 行程
        ///   u∈[0.55, 1)   回桨 Recovery：桨抬出水面，空中回到起点
        ///
        /// 两个关键设计：
        ///
        /// 1) 水平位移用 cos(πt)，不是线性 —— 让桨叶在**两端速度为 0**。
        ///    入水/出水正好发生在两端，这样出水的垂直运动不会和水平速度叠加，
        ///    避免"桨叶以 5m/s 斜着穿出水面、把推力全耗在拍水上"。
        ///
        /// 2) 入水/出水各留 10% 的过渡带，过渡带内 y 快速变化、而水平速度≈0，
        ///    此时桨面法线（朝船尾）与竖直速度近似垂直 ⇒ 攻角≈0 ⇒ Cd 只有 0.05，
        ///    正好等价于真实桨叶"侧立切水"，入水不会产生一记闷响。
        ///
        /// 之前用 y = yMid ± yAmp·tanh(k·sin) 是错的：tanh 在 sin 过零处导数极大，
        /// 桨速里垂直分量能冲到 5m/s 以上，水阻方向几乎全是竖直的，
        /// 再被 propulsionVerticalDamp 衰减掉 80%，于是"桨在划、船不走"。
        /// </summary>
        void ApplyBladeTrajectory(float phase)
        {
            if (boat == null) return;
            float u = phase / (2f * Mathf.PI);      // 0..1

            const float drive = 0.55f;               // 拉桨占周期比例（比赛约 0.5~0.6）
            const float edge = 0.10f;                // 入水/出水过渡带占本段比例

            bool inDrive = u < drive;
            float t = inDrive ? u / drive : (u - drive) / (1f - drive);

            // 水平扫动：拉桨段 +A → −A（船头 → 船尾），回桨段反向
            float zOff = inDrive
                ? sweepAmplitude * Mathf.Cos(Mathf.PI * t)
                : -sweepAmplitude * Mathf.Cos(Mathf.PI * t);

            // 垂直：拉桨中段沉到水下，两端（出入水）快速过渡；回桨全程在空中
            float wet = 0f;
            if (inDrive)
            {
                wet = Mathf.Clamp01(Mathf.Min(t / edge, (1f - t) / edge));
                wet = wet * wet * (3f - 2f * wet);   // smoothstep，过渡带两端导数为 0
            }
            float bladeY = Mathf.Lerp(bladeAirHeight, LowestHandheldBladeY(zOff), wet);

            Vector3 localBlade = new Vector3(
                (leftSide ? -1f : 1f) * (Mathf.Abs(pivotLocalPos.x) + sideOffset),
                bladeY,
                pivotLocalPos.z + zOff);

            // 防穿舷保险:同上;真实数据模式(UDP/回放/外部采样)不做此钳制
            if (Mathf.Abs(localBlade.x) < 0.43f)
                localBlade.x = Mathf.Sign(localBlade.x) * 0.43f;

            PlacePaddle(localBlade);
        }

        /// <summary>
        /// 将未受力的手持桨意图放到船体坐标系，并保持整根桨与船舷的几何间隙。
        /// </summary>
        void PlacePaddle(Vector3 requestedBladeLocal)
        {
            ResolveHandheldPose(requestedBladeLocal, out Vector3 localBlade, out Quaternion rotation);
            transform.SetPositionAndRotation(boat.transform.TransformPoint(localBlade), boat.transform.rotation * rotation);
        }

        // grip.y = blade.y + (R * up).y * shaft length. Apply the existing
        // upper-hand floor at THIS pose: a worst-case lean from another phase
        // needlessly prevented the upright middle of a stroke from immersing.
        // Actual encoder poses are never constrained by this human stroke model.
        public float LowestHandheldBladeY(float foreAftReach)
        {
            float verticalReach = shaftToBlade * HandheldShaftDirection(foreAftReach).y;
            return Mathf.Max(pivotLocalPos.y - bladeDepth, pivotLocalPos.y + 0.35f - verticalReach);
        }

        Vector3 HandheldShaftDirection(float foreAftReach)
        {
            float side = leftSide ? -1f : 1f;
            // A handheld paddle has a travelling upper hand. From rigid shaft
            // closure, grip.z = blade.z + up.z * L; solve the fore/aft component
            // for that hand track, then use the remaining unit-vector length
            // for the declared lateral lean. This is only the human intention;
            // the loaded encoder pose remains unconstrained by this geometry.
            float along = -Mathf.Clamp((1f - upperGripTravelFraction) * foreAftReach
                / Mathf.Max(0.1f, shaftToBlade), -0.6f, 0.6f);
            float crossSection = Mathf.Sqrt(1f - along * along);
            float lean = inboardShaftLeanDeg * Mathf.Deg2Rad;
            return new Vector3(-side * Mathf.Sin(lean) * crossSection,
                Mathf.Cos(lean) * crossSection, along);
        }

        public void ResolveHandheldPose(Vector3 requestedBlade, out Vector3 blade, out Quaternion rotation)
        {
            float side = leftSide ? -1f : 1f;
            float reach = requestedBlade.z - pivotLocalPos.z;
            Vector3 localUp = HandheldShaftDirection(reach);
            rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(Vector3.back, localUp).normalized, localUp);
            var hull = BoatHullGeometry.FindOrCreate(boat);
            float halfBeam = hull != null ? hull.HalfBeam : Mathf.Abs(pivotLocalPos.x) + 0.06f;
            float railTop = hull != null ? Mathf.Max(pivotLocalPos.y, hull.RailTop(pivotLocalPos.z - sweepAmplitude, pivotLocalPos.z + sweepAmplitude)) : pivotLocalPos.y;
            var hydro = GetComponent<PaddleHydrodynamics>();
            float halfWidth = hydro != null ? hydro.BladeWidth * 0.5f : 0.085f;
            float halfLength = hydro != null ? hydro.bladeLength * 0.5f : 0.21f;
            float halfThickness = hydro != null ? hydro.bladeThickness * 0.5f : 0.0125f;
            float edge = Mathf.Abs((rotation * Vector3.right).x) * halfWidth
                + Mathf.Abs(localUp.x) * halfLength + Mathf.Abs((rotation * Vector3.forward).x) * halfThickness;
            // Keep the whole blade outside, and the complete shaft outside until
            // it crosses ABOVE the rail. A centre-only x>=0.43 check cannot do this.
            float bladeClear = halfBeam + edge + 0.02f;
            float shaftClear = halfBeam + 0.0225f + 0.02f
                + Mathf.Max(0f, railTop + 0.0225f - requestedBlade.y) * Mathf.Abs(localUp.x) / localUp.y;
            // The upper hand belongs at the paddle station beside the torso.
            // A blade-only lateral path pulled that hand into the middle of the
            // boat, leaving no clearance for the real compliant angular motion.
            float handTrack = Mathf.Abs(pivotLocalPos.x) + Mathf.Abs(localUp.x) * shaftToBlade;
            float required = SmoothMaximum(SmoothMaximum(bladeClear, shaftClear, 0.04f), handTrack, 0.04f);
            blade = requestedBlade;
            blade.x = side * SmoothMaximum(Mathf.Max(0.43f, Mathf.Abs(requestedBlade.x)), required, 0.04f);
        }

        static float SmoothMaximum(float a, float b, float width)
        {
            float h = Mathf.Max(0f, 1f - Mathf.Abs(a - b) / width);
            return Mathf.Max(a, b) + width * h * h * 0.25f;
        }

        /// <summary>当前桨相位（0~2π），HUD 画相位环用</summary>
        public float Phase01 => _phase / (2f * Mathf.PI);
    }
}
