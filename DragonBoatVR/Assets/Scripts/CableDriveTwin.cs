using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 多点拉索的软件模型；真实绳路、几何、电机对应关系和设备反馈仍需实测接入。
    ///
    /// ── 软件范围 ──
    /// 桨姿态来自统一输入，不假定真实桨固定不动。实际拉索连接桨的多个位置，
    /// 具体连接点和电机对应关系由配置提供。当前求解只支持独立张力控制的绳路，
    /// 共享电机耦合会明确拒绝；示例配置不代表已经确认的真实机构。
    ///
    /// ── 本脚本只仿三件事 ──
    ///   几何运动学（绳长逆解）+ 力分配（非负最小二乘，见 TendonRig）+ 传感器噪声。
    ///   不仿电机电磁场、不仿绳子弹性 —— 那部分不是我们负责，也没有数据。
    ///
    /// ── 为什么要 6 维而不是 3 维 ──
    /// 同点合力也会相对握把产生力矩 τ = p × F，但此时力与力矩不能独立指定。
    /// 分散拴点允许不同力臂；实际可实现范围仍受几何、只能拉、预紧及张力上限约束。
    /// 所以求解目标是 6 维力旋量；三根独立绳不能覆盖所有目标，当前模型需报告残差。
    ///
    /// ── 坐标系约定 ──
    ///   anchorLocal : 电机出绳口，船体坐标系 (m)
    ///   attachLocal : 桨上拴点，桨体坐标系 (m)。原点 = 桨叶中心，+Y 指向握把。
    ///                 握把在 (0, shaftToBlade, 0)，所以"距握把 d 米"的拴点 = (0, shaftToBlade − d, 0)。
    ///   力矩参考点 : 握把（人手上感到力矩的地方）
    /// </summary>
    public class CableDriveTwin : MonoBehaviour, IForceFeedbackDevice
    {
        // Unity's built-in Ignore Raycast layer is reserved for diagnostics.
        // SplitViewController removes it from the user-facing seat camera.
        const int DiagnosticVisualLayer = 2;
        [Header("数据源")]
        [Tooltip("桨（取桨叶中心世界坐标与水阻矢量）；留空自动找")]
        public PaddleHydrodynamics paddle;

        [Tooltip("船体根节点，锚点相对它定位；留空自动找")]
        public BoatBuoyancy boat;

        [Tooltip("由统一仿真循环调用 Receive，关闭本组件的独立 FixedUpdate 推进")]
        public bool externallyStepped;

        [Header("绳驱布置")]
        [Tooltip("完整的腱配置。为空则退化成下面的 3 点 anchorLocal 布置（拴点全在桨叶中心）")]
        public TendonRigConfig rig;

        [Tooltip("握把到桨叶中心的距离 (m)。力矩参考点用它定位")]
        public float shaftToBlade = 1.0f;

        [Header("三绳退化布置（rig 为空时使用）")]
        public Vector3[] anchorLocal = new Vector3[]
        {
            new Vector3(1.40f,  0.50f,  1.60f),   // 电机 1 前上外
            new Vector3(1.40f,  0.50f, -1.20f),   // 电机 2 后上外
            new Vector3(0.60f, -1.40f,  0.20f),   // 电机 3 内下（水下侧）
        };

        [Header("电机参数")]
        [Tooltip("单绳张力上限 (N)，保护电机")]
        public float maxTensionN = 300f;

        [Tooltip("预紧力 (N)，防止绳索松弛")]
        public float preTensionN = 5f;

        [Tooltip("力传感器高斯噪声标准差 (N)")]
        public float sensorNoiseN = 0.5f;

        [Header("可视化")]
        // The cables and motor housings are outside the user's physical view.
        // Keep the solver and telemetry active, but make the mechanism a
        // diagnostic-only visual that must be explicitly enabled.
        public bool showCables = false;
        public bool showMotorUnits = false;
        public Color cableLightColor = new Color(0.15f, 0.9f, 0.35f);
        public Color cableHeavyColor = new Color(0.85f, 0.10f, 0.10f);
        [Tooltip("绳子半径 (m)")]
        public float cableRadius = 0.012f;

        [Header("UDP 输出（发给电机上位机）")]
        public bool sendUdp = false;
        public string remoteHost = "127.0.0.1";
        public int remotePort = 9872;
        [Range(20f, 240f)] public float sendRate = 100f;

        // ---------------- 状态输出（只读） ----------------
        /// <summary>每根绳实时长度 (mm)，模拟编码器读数</summary>
        public float[] cableLengthsMm = new float[3];
        /// <summary>每根独立腱最终命令张力 (N)，已包含预紧和上限；不支持共享电机耦合绳路。</summary>
        public float[] motorTensionsN = new float[3];
        /// <summary>合力模长 (N)</summary>
        public float resultantTensionN;
        /// <summary>合力 / 水阻模长，理想 1.000</summary>
        public float allocationRatio;
        /// <summary>残差 (N)</summary>
        public float allocationErrorN;
        /// <summary>6 维还原率（力+力矩），理想 1.000</summary>
        public float restorationRate;
        /// <summary>3 维力的还原率</summary>
        public float forceRestorationRate;
        /// <summary>腱数量</summary>
        public int tendonCount;
        public float directionErrorDegrees;
        public Vector3 modeledForceWorldN;
        public Vector3 modeledTorqueAtGripWorldNm;
        public CableDeviceFeedback Feedback { get; private set; } = new CableDeviceFeedback
            { valid = false, status = "NOT_INITIALIZED" };

        public const uint MagicCable = 0x44424342u;   // 'DBCB'
        public const uint Version = 2u;
        public const int HeaderBytes = 20;
        /// <summary>每根绳在包里的字节数：长度 f32 + 张力 f32</summary>
        public const int BytesPerTendon = 8;
        /// <summary>当前包长</summary>
        public int packetSize => HeaderBytes + tendonCount * BytesPerTendon;

        LineRenderer[] _cables;
        Transform[] _motorBodies;
        Transform[] _motorSpools;
        Material _cableMat;
        Material _motorBodyMat;
        Material _motorSpoolMat;
        ReplayableRandom _rng;
        UdpClient _tx;
        float _sendTimer;
        uint _seq;

        // 求解缓存
        List<TendonSpec> _specs = new List<TendonSpec>();
        Vector3[] _dirs;      // 世界系单位方向（拴点 → 电机）
        Vector3[] _attachW;   // 拴点世界坐标
        Vector3[] _anchorW;   // 出绳口世界坐标
        Vector3[] _arms;      // 拴点相对握把的臂
        float[][] _cols;
        float[] _t, _sum, _target;
        float _charLen;
        float[] _lower, _upper;
        bool _initialized;
        bool _unsupportedCoupling;
        uint _standaloneSequence;

        void Start()
        {
            EnsureInitialized();
        }

        void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            if (paddle == null) paddle = FindObjectOfType<PaddleHydrodynamics>(true);
            if (boat == null) boat = FindObjectOfType<BoatBuoyancy>(true);

            if (paddle == null) Debug.LogWarning("[CableDriveTwin] 找不到桨，牵引绳不会动。");
            if (boat == null) Debug.LogWarning("[CableDriveTwin] 找不到船体，锚点无法定位。");

            BuildSpecs();

            _rng = new ReplayableRandom(20261020);

            if (showCables) BuildCableVisuals();
            if (showMotorUnits) BuildMotorVisuals();

            if (sendUdp)
            {
                _tx = new UdpClient();
                Debug.Log($"[CableDriveTwin] {tendonCount} 根绳状态 → {remoteHost}:{remotePort} @ {sendRate}Hz  " +
                          $"({packetSize} 字节/包)");
            }
        }

        void BuildSpecs()
        {
            _specs.Clear();

            if (rig != null)
            {
                if (rig.tendons != null)
                    foreach (var t in rig.tendons)
                        if (t != null && t.enabled)
                        {
                            if (rig.attachmentCoordinatesFromBladeTip)
                                t.attachLocal = rig.bladeTipLocal + t.attachmentFromBladeTip;
                            _specs.Add(t);
                        }
                _charLen = rig.charLengthM;
            }
            else
            {
                // 退化布置：绳全拴在桨叶中心，力矩由合力与握把臂决定，不能独立指定。
                _charLen = 0.60f;
                for (int i = 0; anchorLocal != null && i < anchorLocal.Length; i++)
                {
                    _specs.Add(new TendonSpec
                    {
                        name = $"T{i + 1}",
                        motorId = i + 1,
                        anchorLocal = anchorLocal[i],
                        attachLocal = Vector3.zero,
                        maxTensionN = maxTensionN,
                        preTensionN = preTensionN,
                    });
                }
            }

            tendonCount = _specs.Count;
            int n = tendonCount;

            cableLengthsMm = new float[n];
            motorTensionsN = new float[n];
            _dirs = new Vector3[n];
            _attachW = new Vector3[n];
            _anchorW = new Vector3[n];
            _arms = new Vector3[n];
            _t = new float[n];
            _lower = new float[n];
            _upper = new float[n];
            _sum = new float[TendonRig.Dim];
            _target = new float[TendonRig.Dim];
            _cols = new float[n][];
            for (int i = 0; i < n; i++) _cols[i] = new float[TendonRig.Dim];
            _unsupportedCoupling = MotorCount() != tendonCount;

            Debug.Log($"[CableDriveTwin] 腱数={tendonCount}  电机数={MotorCount()}  " +
                      $"力矩尺度={_charLen:F2}m  握把→桨叶={shaftToBlade:F2}m");
        }

        int MotorCount()
        {
            var ids = new HashSet<int>();
            foreach (var s in _specs) ids.Add(s.motorId);
            return ids.Count;
        }

        void BuildCableVisuals()
        {
            _cables = new LineRenderer[_specs.Count];

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            _cableMat = new Material(shader) { name = "CableMat" };

            for (int i = 0; i < _cables.Length; i++)
            {
                var go = new GameObject($"Cable_{_specs[i].name}");
                go.transform.SetParent(transform, false);
                go.layer = DiagnosticVisualLayer;
                var lr = go.AddComponent<LineRenderer>();
                lr.positionCount = 2;
                lr.useWorldSpace = true;
                lr.material = _cableMat;
                lr.widthMultiplier = cableRadius * 2f;
                lr.numCapVertices = 4;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.startColor = cableLightColor;
                lr.endColor = cableLightColor;
                _cables[i] = lr;
            }
        }

        void BuildMotorVisuals()
        {
            _motorBodies = new Transform[_specs.Count];
            _motorSpools = new Transform[_specs.Count];
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _motorBodyMat = new Material(shader) { name = "CableMotorHousingMat", color = new Color(0.075f, 0.10f, 0.13f) };
            _motorSpoolMat = new Material(shader) { name = "CableMotorSpoolMat", color = new Color(0.78f, 0.48f, 0.12f) };
            for (int i = 0; i < _specs.Count; i++)
            {
                var body = VisualPrimitive.Create(PrimitiveType.Cube);
                body.name = $"CableMotor_{_specs[i].motorId}_Housing";
                body.transform.SetParent(transform, false);
                body.layer = DiagnosticVisualLayer;
                body.transform.localScale = new Vector3(0.17f, 0.14f, 0.25f);
                body.GetComponent<Renderer>().sharedMaterial = _motorBodyMat;
                _motorBodies[i] = body.transform;

                var spool = VisualPrimitive.Create(PrimitiveType.Cylinder);
                spool.name = $"CableMotor_{_specs[i].motorId}_Spool";
                spool.transform.SetParent(transform, false);
                spool.layer = DiagnosticVisualLayer;
                spool.transform.localScale = new Vector3(0.105f, 0.035f, 0.105f);
                spool.GetComponent<Renderer>().sharedMaterial = _motorSpoolMat;
                _motorSpools[i] = spool.transform;
            }
            UpdateMotorVisuals();
        }

        void UpdateMotorVisuals()
        {
            if (!showMotorUnits || _motorBodies == null || boat == null) return;
            for (int i = 0; i < _motorBodies.Length && i < _specs.Count; i++)
            {
                if (_motorBodies[i] == null || _motorSpools[i] == null) continue;
                Vector3 anchor = boat.transform.TransformPoint(_specs[i].anchorLocal);
                Vector3 cableDirection = paddle != null ? (anchor - paddle.BladePosition).normalized : Vector3.forward;
                if (cableDirection.sqrMagnitude < 1e-6f) cableDirection = Vector3.forward;
                Quaternion housingRotation = Quaternion.LookRotation(cableDirection, Vector3.up);
                _motorBodies[i].SetPositionAndRotation(anchor + cableDirection * 0.20f, housingRotation);
                _motorSpools[i].SetPositionAndRotation(anchor, Quaternion.FromToRotation(Vector3.up, cableDirection));
            }
        }

        void FixedUpdate()
        {
            if (externallyStepped) return;
            EnsureInitialized();
            Vector3 blade = paddle != null ? paddle.BladePosition : Vector3.zero;
            Vector3 grip = paddle != null ? blade + paddle.transform.rotation * Vector3.up * shaftToBlade : Vector3.zero;
            Vector3 force = paddle != null ? paddle.WaterForceN : Vector3.zero;
            Receive(new ForceFeedbackRequest
            {
                commandSequence = ++_standaloneSequence,
                timeSeconds = Time.fixedTimeAsDouble,
                forceWorldN = force,
                torqueAtGripWorldNm = (paddle != null ? paddle.TorqueAtBladeNm : Vector3.zero) + Vector3.Cross(blade - grip, force),
                gripWorldPosition = grip,
                valid = paddle != null && boat != null
            }, Time.fixedDeltaTime);
        }

        public void Receive(ForceFeedbackRequest request, float dt)
        {
            ReceiveCore(request, dt, false, default, default, default);
        }

        /// <summary>Allocate against one input observation, even when scene transforms
        /// have advanced through physics or rendering. The request wrench and grip must
        /// use the same world frame and observation time as these explicit poses.</summary>
        public void ReceiveAtPose(ForceFeedbackRequest request, float dt, Matrix4x4 boatLocalToWorld,
            Vector3 bladeWorldPosition, Quaternion bladeWorldRotation)
        {
            ReceiveCore(request, dt, true, boatLocalToWorld, bladeWorldPosition, bladeWorldRotation);
        }

        void ReceiveCore(ForceFeedbackRequest request, float dt, bool useObservationPose,
            Matrix4x4 observationBoat, Vector3 observationBlade, Quaternion observationRotation)
        {
            EnsureInitialized();
            if (!request.valid || !SimulationValue.Finite(dt) || dt <= 0f ||
                double.IsNaN(request.timeSeconds) || double.IsInfinity(request.timeSeconds) ||
                !SimulationValue.Finite(request.forceWorldN) || !SimulationValue.Finite(request.torqueAtGripWorldNm) ||
                !SimulationValue.Finite(request.gripWorldPosition))
            {
                ZeroOutput(request, "INVALID_REQUEST", false, dt);
                return;
            }
            _unsupportedCoupling = MotorCount() != tendonCount;
            if (_unsupportedCoupling)
            {
                ZeroOutput(request, "UNSUPPORTED_SHARED_MOTOR_COUPLING", true, dt);
                return;
            }
            bool geometryFinite = useObservationPose
                ? RigidRegistration.IsRigid(observationBoat) && SimulationValue.Finite(observationBlade)
                    && SimulationValue.Finite(observationRotation)
                    && Mathf.Abs(Quaternion.Dot(observationRotation, observationRotation) - 1f) < 0.0001f
                : paddle != null && boat != null && SimulationValue.Finite(paddle.BladePosition)
                    && SimulationValue.Finite(paddle.transform.rotation) && SimulationValue.Finite(boat.transform.position)
                    && SimulationValue.Finite(boat.transform.rotation) && SimulationValue.Finite(boat.transform.lossyScale);
            if (paddle == null || boat == null || tendonCount == 0 ||
                !SimulationValue.Finite(_charLen) || _charLen <= 0f ||
                !geometryFinite)
            {
                ZeroOutput(request, "INVALID_OR_MISSING_GEOMETRY", true, dt);
                return;
            }
            Quaternion paddleRot = useObservationPose ? observationRotation : paddle.transform.rotation;
            Vector3 bladePos = useObservationPose ? observationBlade : paddle.BladePosition;

            for (int k = 0; k < tendonCount; k++)
            {
                var s = _specs[k];
                if (!s.enabled || !SimulationValue.Finite(s.anchorLocal) || !SimulationValue.Finite(s.attachLocal))
                {
                    ZeroOutput(request, "INVALID_TENDON_GEOMETRY_OR_LIMITS", true, dt);
                    return;
                }

                _anchorW[k] = useObservationPose ? observationBoat.MultiplyPoint3x4(s.anchorLocal)
                    : boat.transform.TransformPoint(s.anchorLocal);
                _attachW[k] = paddleRot * s.attachLocal + bladePos;

                Vector3 v = _anchorW[k] - _attachW[k];
                float dist = v.magnitude;
                if (!SimulationValue.Finite(dist) || dist <= 1e-4f ||
                    !SimulationValue.Finite(s.preTensionN) || !SimulationValue.Finite(s.maxTensionN) ||
                    s.preTensionN < 0f || s.maxTensionN < s.preTensionN || s.motorId <= 0)
                {
                    ZeroOutput(request, "INVALID_TENDON_GEOMETRY_OR_LIMITS", true, dt);
                    return;
                }
                cableLengthsMm[k] = dist * 1000f;
                _dirs[k] = v / dist;
                _arms[k] = _attachW[k] - request.gripWorldPosition;
                _lower[k] = s.preTensionN;
                _upper[k] = s.maxTensionN;

                TendonRig.BuildColumn(_dirs[k], _arms[k], _charLen, _cols[k]);
            }

            _target[0] = request.forceWorldN.x; _target[1] = request.forceWorldN.y; _target[2] = request.forceWorldN.z;
            Vector3 scaledTorque = request.torqueAtGripWorldNm / _charLen;
            _target[3] = scaledTorque.x; _target[4] = scaledTorque.y; _target[5] = scaledTorque.z;
            if (!TendonRig.SolveBounded(_cols, _target, _t, _lower, _upper))
            {
                ZeroOutput(request, "INVALID_ALLOCATION_INPUT", true, dt);
                return;
            }
            if (!TendonRig.TryCompose(_cols, _t, _sum))
            {
                ZeroOutput(request, "NONFINITE_ALLOCATION_OUTPUT", true, dt);
                return;
            }
            modeledForceWorldN = new Vector3(_sum[0], _sum[1], _sum[2]);
            modeledTorqueAtGripWorldNm = new Vector3(_sum[3], _sum[4], _sum[5]) * _charLen;
            if (!SimulationValue.Finite(modeledForceWorldN) || !SimulationValue.Finite(modeledTorqueAtGripWorldNm))
            {
                ZeroOutput(request, "NONFINITE_ALLOCATION_OUTPUT", true, dt);
                return;
            }
            var sensed = new float[tendonCount];
            for (int k = 0; k < tendonCount; k++)
            {
                motorTensionsN[k] = _t[k];
                float noise = NextGaussian() * (SimulationValue.Finite(sensorNoiseN) ? Mathf.Max(0f, sensorNoiseN) : 0f);
                sensed[k] = Mathf.Clamp(_t[k] + noise, 0f, _upper[k]);
            }
            resultantTensionN = modeledForceWorldN.magnitude;
            float wf = request.forceWorldN.magnitude;
            allocationRatio = wf > 1e-5f ? resultantTensionN / wf : 0f;
            allocationErrorN = (modeledForceWorldN - request.forceWorldN).magnitude;
            directionErrorDegrees = wf > 1e-5f ?
                (resultantTensionN > 1e-5f ? Vector3.Angle(modeledForceWorldN, request.forceWorldN) : 180f) : 0f;
            restorationRate = TendonRig.Restoration(_sum, _target);
            forceRestorationRate = TendonRig.ForceRestoration(_sum, _target);
            bool limited = allocationErrorN > 0.01f ||
                (modeledTorqueAtGripWorldNm - request.torqueAtGripWorldNm).magnitude > 0.01f;
            Feedback = new CableDeviceFeedback
            {
                commandSequence = request.commandSequence, timeSeconds = request.timeSeconds, simulated = true,
                valid = true, capabilityLimited = limited, status = limited ? "CAPABILITY_LIMITED_SIMULATION" : "SIMULATED",
                lengthsMm = (float[])cableLengthsMm.Clone(), tensionsN = sensed,
                forceWorldN = modeledForceWorldN, torqueAtGripWorldNm = modeledTorqueAtGripWorldNm,
                forceResidualN = allocationErrorN,
                torqueResidualNm = (modeledTorqueAtGripWorldNm - request.torqueAtGripWorldNm).magnitude
            };
            UpdateCableVisuals();
            SendIfDue(dt);
        }

        void ZeroOutput(ForceFeedbackRequest request, string status, bool limited, float dt)
        {
            Array.Clear(_t, 0, _t.Length);
            Array.Clear(motorTensionsN, 0, motorTensionsN.Length);
            Array.Clear(_sum, 0, _sum.Length);
            Array.Clear(cableLengthsMm, 0, cableLengthsMm.Length);
            modeledForceWorldN = modeledTorqueAtGripWorldNm = Vector3.zero;
            resultantTensionN = allocationRatio = 0f;
            allocationErrorN = SimulationValue.Finite(request.forceWorldN) ? request.forceWorldN.magnitude : 0f;
            directionErrorDegrees = allocationErrorN > 1e-5f ? 180f : 0f;
            restorationRate = forceRestorationRate = 0f;
            UpdateCableVisuals();
            Feedback = new CableDeviceFeedback
            {
                commandSequence = request.commandSequence,
                timeSeconds = double.IsNaN(request.timeSeconds) || double.IsInfinity(request.timeSeconds) ? 0d : request.timeSeconds,
                valid = false, simulated = true, capabilityLimited = limited, status = status,
                lengthsMm = (float[])cableLengthsMm.Clone(), tensionsN = new float[tendonCount],
                forceWorldN = Vector3.zero, torqueAtGripWorldNm = Vector3.zero,
                forceResidualN = allocationErrorN,
                torqueResidualNm = SimulationValue.Finite(request.torqueAtGripWorldNm) ? request.torqueAtGripWorldNm.magnitude : 0f
            };
            SendIfDue(dt, true);
        }

        /// <summary>Display and report received execution observations after allocation.
        /// The allocator is run again on the next tick; observation never changes its target.</summary>
        public void ApplyControllerObservation(CableDeviceFeedback observation)
        {
            // Metrics require a present observation and its own acknowledged command.
            allocationRatio = restorationRate = forceRestorationRate = directionErrorDegrees = 0f;
            if (observation == null) return;
            Feedback = DeviceReplayValue.Copy(observation);
            if (observation.tensionsN != null && observation.tensionsN.Length == tendonCount)
                Array.Copy(observation.tensionsN, motorTensionsN, tendonCount);
            if (observation.lengthsMm != null && observation.lengthsMm.Length == tendonCount)
                Array.Copy(observation.lengthsMm, cableLengthsMm, tendonCount);
            modeledForceWorldN = observation.forceWorldN;
            modeledTorqueAtGripWorldNm = observation.torqueAtGripWorldNm;
            allocationErrorN = observation.forceResidualN;
            resultantTensionN = modeledForceWorldN.magnitude;
            UpdateCableVisuals();
        }

        /// <summary>Evaluate execution against its acknowledged command, not the
        /// newest allocation. Keep solver caches untouched for the next allocation.</summary>
        public void ApplyControllerObservation(CableDeviceFeedback observation, uint requestSequence,
            Vector3 requestedForceWorldN, Vector3 requestedTorqueAtGripWorldNm)
        {
            ApplyControllerObservation(observation);
            if (observation == null || !observation.valid || observation.commandSequence != requestSequence
                || !SimulationValue.Finite(requestedForceWorldN) || !SimulationValue.Finite(requestedTorqueAtGripWorldNm)
                || !SimulationValue.Finite(_charLen) || _charLen <= 0f) return;
            float requestedMagnitude = requestedForceWorldN.magnitude;
            allocationRatio = requestedMagnitude > 1e-5f ? resultantTensionN / requestedMagnitude : 0f;
            directionErrorDegrees = requestedMagnitude > 1e-5f
                ? (resultantTensionN > 1e-5f ? Vector3.Angle(modeledForceWorldN, requestedForceWorldN) : 180f) : 0f;
            Vector3 actualTorque = modeledTorqueAtGripWorldNm / _charLen;
            Vector3 targetTorque = requestedTorqueAtGripWorldNm / _charLen;
            var actual = new[] { modeledForceWorldN.x, modeledForceWorldN.y, modeledForceWorldN.z,
                actualTorque.x, actualTorque.y, actualTorque.z };
            var target = new[] { requestedForceWorldN.x, requestedForceWorldN.y, requestedForceWorldN.z,
                targetTorque.x, targetTorque.y, targetTorque.z };
            restorationRate = TendonRig.Restoration(actual, target);
            forceRestorationRate = TendonRig.ForceRestoration(actual, target);
        }

        void UpdateCableVisuals()
        {
            UpdateMotorVisuals();
            if (!showCables || _cables == null) return;

            for (int k = 0; k < _cables.Length && k < tendonCount; k++)
            {
                if (_cables[k] == null) continue;
                _cables[k].SetPosition(0, _anchorW[k]);
                _cables[k].SetPosition(1, _attachW[k]);

                float lim = _specs[k].maxTensionN > 0f ? _specs[k].maxTensionN : maxTensionN;
                float t = Mathf.InverseLerp(_specs[k].preTensionN, Mathf.Max(_specs[k].preTensionN + 1f, lim * 0.6f), motorTensionsN[k]);
                Color c = Color.Lerp(cableLightColor, cableHeavyColor, t);
                _cables[k].startColor = c;
                _cables[k].endColor = c;
            }
        }

        void SendIfDue(float dt, bool immediate = false)
        {
            if (!sendUdp || _tx == null || tendonCount == 0) return;
            _sendTimer += SimulationValue.Finite(dt) ? Mathf.Max(0f, dt) : 0f;
            float interval = 1f / Mathf.Max(1f, sendRate);
            if (!immediate && _sendTimer < interval) return;
            _sendTimer -= interval;    // 减去间隔而非清零，避免实际频率被量化到物理步长整数倍
            if (_sendTimer > interval) _sendTimer = 0f;

            // 布局：magic(4) ver(4) ts(8) seq(4) | Len[n](4n) | Ten[n](4n)
            byte[] d = new byte[packetSize];
            BitConverter.GetBytes(MagicCable).CopyTo(d, 0);
            BitConverter.GetBytes(Version).CopyTo(d, 4);
            BitConverter.GetBytes((ulong)(Time.realtimeSinceStartupAsDouble * 1_000_000d)).CopyTo(d, 8);
            BitConverter.GetBytes(_seq++).CopyTo(d, 16);
            int off = HeaderBytes;
            for (int k = 0; k < tendonCount; k++, off += 4)
                BitConverter.GetBytes(cableLengthsMm[k]).CopyTo(d, off);
            for (int k = 0; k < tendonCount; k++, off += 4)
                BitConverter.GetBytes(motorTensionsN[k]).CopyTo(d, off);

            try { _tx.Send(d, d.Length, remoteHost, remotePort); }
            catch (Exception e) { Debug.LogWarning($"[CableDriveTwin] 发送失败: {e.Message}"); }
        }

        /// <summary>Box-Muller 生成标准正态分布随机数</summary>
        float NextGaussian()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return (float)z;
        }

        /// <summary>运行期换布置（诊断工具用）：传入新配置后立即重建缓存</summary>
        public void ApplyRig(TendonRigConfig cfg)
        {
            rig = cfg;
            if (!_initialized)
            {
                EnsureInitialized();
                ZeroOutput(new ForceFeedbackRequest(), "RIG_CHANGED_ZERO_OUTPUT", false, 0f);
                return;
            }
            if (_cables != null)
                foreach (var c in _cables)
                    if (c != null) DestroyVisual(c.gameObject);
            if (_cableMat != null) DestroyVisual(_cableMat);
            _cables = null;
            _cableMat = null;
            if (_motorBodies != null)
                foreach (var item in _motorBodies) if (item != null) DestroyVisual(item.gameObject);
            if (_motorSpools != null)
                foreach (var item in _motorSpools) if (item != null) DestroyVisual(item.gameObject);
            if (_motorBodyMat != null) DestroyVisual(_motorBodyMat);
            if (_motorSpoolMat != null) DestroyVisual(_motorSpoolMat);
            _motorBodies = _motorSpools = null;
            _motorBodyMat = _motorSpoolMat = null;
            BuildSpecs();
            if (showCables) BuildCableVisuals();
            if (showMotorUnits) BuildMotorVisuals();
            ZeroOutput(new ForceFeedbackRequest(), "RIG_CHANGED_ZERO_OUTPUT", false, 0f);
        }

        static void DestroyVisual(UnityEngine.Object item)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) { UnityEngine.Object.DestroyImmediate(item); return; }
#endif
            UnityEngine.Object.Destroy(item);
        }

        public CableDriveReplayState CaptureReplayState()
        {
            EnsureInitialized();
            var columns = new float[tendonCount * TendonRig.Dim];
            for (int i = 0; i < tendonCount; i++) Array.Copy(_cols[i], 0, columns, i * TendonRig.Dim, TendonRig.Dim);
            var state = new CableDriveReplayState
            {
                random = _rng.CaptureState(), sendTimer = _sendTimer,
                packetSequence = _seq, standaloneSequence = _standaloneSequence,
                cableLengthsMm = (float[])cableLengthsMm.Clone(), motorTensionsN = (float[])motorTensionsN.Clone(),
                directions = (Vector3[])_dirs.Clone(), attachmentsWorld = (Vector3[])_attachW.Clone(),
                anchorsWorld = (Vector3[])_anchorW.Clone(), arms = (Vector3[])_arms.Clone(),
                solverTensions = (float[])_t.Clone(), solverSum = (float[])_sum.Clone(), solverTarget = (float[])_target.Clone(),
                lower = (float[])_lower.Clone(), upper = (float[])_upper.Clone(), solverColumns = columns,
                resultantTensionN = resultantTensionN, allocationRatio = allocationRatio, allocationErrorN = allocationErrorN,
                restorationRate = restorationRate, forceRestorationRate = forceRestorationRate,
                directionErrorDegrees = directionErrorDegrees, modeledForceWorldN = modeledForceWorldN,
                modeledTorqueAtGripWorldNm = modeledTorqueAtGripWorldNm, feedback = DeviceReplayValue.Copy(Feedback)
            };
            ValidateReplayState(state);
            return state;
        }

        public void ValidateReplayState(CableDriveReplayState state)
        {
            DeviceReplayValue.Require(_initialized && state != null && state.version == 1 && tendonCount == _specs.Count,
                "Invalid initial cable model state.");
            ReplayableRandom.ValidateState(state.random);
            DeviceReplayValue.Require(SimulationValue.Finite(state.sendTimer) &&
                SimulationValue.Finite(state.resultantTensionN) && state.resultantTensionN >= 0f &&
                SimulationValue.Finite(state.allocationRatio) && state.allocationRatio >= 0f &&
                SimulationValue.Finite(state.allocationErrorN) && state.allocationErrorN >= 0f &&
                SimulationValue.Finite(state.restorationRate) && SimulationValue.Finite(state.forceRestorationRate) &&
                SimulationValue.Finite(state.directionErrorDegrees) && state.directionErrorDegrees >= 0f &&
                state.directionErrorDegrees <= 180f && SimulationValue.Finite(state.modeledForceWorldN) &&
                SimulationValue.Finite(state.modeledTorqueAtGripWorldNm), "Nonfinite or malformed initial cable state.");
            DeviceReplayValue.Array(state.cableLengthsMm, tendonCount, "cable lengths");
            DeviceReplayValue.Array(state.motorTensionsN, tendonCount, "cable motor tensions");
            DeviceReplayValue.Array(state.directions, tendonCount, "cable directions");
            DeviceReplayValue.Array(state.attachmentsWorld, tendonCount, "cable attachments");
            DeviceReplayValue.Array(state.anchorsWorld, tendonCount, "cable anchors");
            DeviceReplayValue.Array(state.arms, tendonCount, "cable arms");
            DeviceReplayValue.Array(state.solverTensions, tendonCount, "cable solver tensions");
            DeviceReplayValue.Array(state.solverSum, TendonRig.Dim, "cable solver sum");
            DeviceReplayValue.Array(state.solverTarget, TendonRig.Dim, "cable solver target");
            DeviceReplayValue.Array(state.lower, tendonCount, "cable lower bounds");
            DeviceReplayValue.Array(state.upper, tendonCount, "cable upper bounds");
            DeviceReplayValue.Array(state.solverColumns, tendonCount * TendonRig.Dim, "cable solver columns");
            var feedback = state.feedback;
            DeviceReplayValue.Require(feedback != null && feedback.simulated && !string.IsNullOrEmpty(feedback.status) &&
                DeviceReplayValue.Finite(feedback.timeSeconds) && SimulationValue.Finite(feedback.forceWorldN) &&
                SimulationValue.Finite(feedback.torqueAtGripWorldNm) && SimulationValue.Finite(feedback.forceResidualN) &&
                feedback.forceResidualN >= 0f && SimulationValue.Finite(feedback.torqueResidualNm) && feedback.torqueResidualNm >= 0f,
                "Invalid initial cable feedback.");
            bool noFeedback = !feedback.valid && feedback.status == "NOT_INITIALIZED" &&
                feedback.lengthsMm == null && feedback.tensionsN == null;
            if (!noFeedback)
            {
                DeviceReplayValue.Array(feedback.lengthsMm, tendonCount, "cable feedback lengths");
                DeviceReplayValue.Array(feedback.tensionsN, tendonCount, "cable feedback tensions");
            }
            DeviceReplayValue.Require(feedback.forceWorldN == state.modeledForceWorldN &&
                feedback.torqueAtGripWorldNm == state.modeledTorqueAtGripWorldNm,
                "Cable feedback does not match its internal wrench.");
            for (int i = 0; i < tendonCount; i++)
            {
                var spec = _specs[i];
                DeviceReplayValue.Require(spec != null && SimulationValue.Finite(spec.preTensionN) &&
                    SimulationValue.Finite(spec.maxTensionN) && spec.preTensionN >= 0f && spec.maxTensionN >= spec.preTensionN &&
                    state.cableLengthsMm[i] >= 0f && state.motorTensionsN[i] >= 0f && state.motorTensionsN[i] <= spec.maxTensionN &&
                    state.solverTensions[i] == state.motorTensionsN[i] && state.lower[i] >= 0f &&
                    state.upper[i] >= state.lower[i] && state.upper[i] <= spec.maxTensionN,
                    "Recorded cable state violates tendon limits.");
                if (feedback.valid)
                    DeviceReplayValue.Require(state.motorTensionsN[i] >= spec.preTensionN && state.cableLengthsMm[i] > 0f,
                        "Valid cable feedback lacks a valid tension or length.");
                if (!noFeedback)
                    DeviceReplayValue.Require(feedback.lengthsMm[i] == state.cableLengthsMm[i] &&
                        feedback.tensionsN[i] >= 0f && feedback.tensionsN[i] <= spec.maxTensionN,
                        "Recorded cable feedback violates tendon limits.");
            }
        }

        /// <summary>Restore without transmitting a command. The noise state is restored exactly;
        /// a session may rebase the last feedback's local input time when its replay starts at zero.</summary>
        public void RestoreReplayState(CableDriveReplayState state, double timeOriginSeconds = 0d)
        {
            ValidateReplayState(state);
            DeviceReplayValue.Require(DeviceReplayValue.Finite(timeOriginSeconds) &&
                DeviceReplayValue.Finite(state.feedback.timeSeconds - timeOriginSeconds), "Invalid cable replay time origin.");
            _rng.RestoreState(state.random);
            _sendTimer = state.sendTimer;
            _seq = state.packetSequence;
            _standaloneSequence = state.standaloneSequence;
            Array.Copy(state.cableLengthsMm, cableLengthsMm, tendonCount);
            Array.Copy(state.motorTensionsN, motorTensionsN, tendonCount);
            Array.Copy(state.directions, _dirs, tendonCount);
            Array.Copy(state.attachmentsWorld, _attachW, tendonCount);
            Array.Copy(state.anchorsWorld, _anchorW, tendonCount);
            Array.Copy(state.arms, _arms, tendonCount);
            Array.Copy(state.solverTensions, _t, tendonCount);
            Array.Copy(state.solverSum, _sum, TendonRig.Dim);
            Array.Copy(state.solverTarget, _target, TendonRig.Dim);
            Array.Copy(state.lower, _lower, tendonCount);
            Array.Copy(state.upper, _upper, tendonCount);
            for (int i = 0; i < tendonCount; i++) Array.Copy(state.solverColumns, i * TendonRig.Dim, _cols[i], 0, TendonRig.Dim);
            resultantTensionN = state.resultantTensionN;
            allocationRatio = state.allocationRatio;
            allocationErrorN = state.allocationErrorN;
            restorationRate = state.restorationRate;
            forceRestorationRate = state.forceRestorationRate;
            directionErrorDegrees = state.directionErrorDegrees;
            modeledForceWorldN = state.modeledForceWorldN;
            modeledTorqueAtGripWorldNm = state.modeledTorqueAtGripWorldNm;
            Feedback = DeviceReplayValue.Copy(state.feedback);
            Feedback.timeSeconds -= timeOriginSeconds;
            UpdateCableVisuals();
        }

        public void ResetModel()
        {
            EnsureInitialized();
            _rng = new ReplayableRandom(20261020);
            _sendTimer = 0f;
            _seq = _standaloneSequence = 0u;
            ZeroOutput(new ForceFeedbackRequest(), "RESET_ZERO_OUTPUT", false, 0f);
        }

        void OnDestroy()
        {
            try { _tx?.Close(); } catch { }
            if (_cableMat != null) Destroy(_cableMat);
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (boat == null || anchorLocal == null) return;
            Gizmos.color = Color.magenta;
            for (int k = 0; k < anchorLocal.Length; k++)
                Gizmos.DrawWireSphere(boat.transform.TransformPoint(anchorLocal[k]), 0.06f);

            Gizmos.color = Color.cyan;
            for (int k = 0; k < _specs.Count; k++)
            {
                Gizmos.DrawWireSphere(boat.transform.TransformPoint(_specs[k].anchorLocal), 0.05f);
                if (paddle != null)
                    Gizmos.DrawWireSphere(paddle.transform.rotation * _specs[k].attachLocal + paddle.transform.position, 0.04f);
            }
        }
#endif
    }
}
