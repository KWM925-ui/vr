using UnityEngine;

namespace DragonBoatVR
{
    [System.Serializable]
    public class PlatformRigConfig
    {
        public string provenance = "Software example; platform geometry and limits are unmeasured";
        public bool measured;
        public bool useExplicitJointGeometry;
        public Vector3[] explicitBaseJointsLocal;
        public Vector3[] explicitTopJointsLocal;
        public bool geometryAssumed = true;
        public float baseRadius = 0.8f, topRadius = 0.55f, neutralHeight = 0.6f;
        public float[] minActuatorLengthsMm = { 300f, 300f, 300f, 300f, 300f, 300f };
        public float[] maxActuatorLengthsMm = { 1100f, 1100f, 1100f, 1100f, 1100f, 1100f };
        public bool lengthLimitsAssumed = true;
        public float maxHeaveMeters = 0.15f, maxTranslationMeters = 0.12f, maxTiltDegrees = 18f, maxYawDegrees = 8f;
        public float accelToTranslation = 0.02f, accelToYaw = 2f, servoLagTimeConstant = 0.025f;
    }

    /// <summary>
    /// 六轴运动平台（Stewart 并联机构）数字孪生。
    ///
    /// 使用假设几何和一阶响应检查软件的数据链，只仿三件事：
    ///   几何运动学（6 缸逆解）+ 力/行程分配（洗出限幅）+ 伺服响应滞后
    /// 不仿电动缸内部电磁场、不仿液压油路 —— 那部分没有数据，也不是我们负责。
    ///
    /// 三段逻辑：
    ///   1. 洗出与限幅（Washout & Clamp）
    ///      虚拟龙舟能在 1km 河道上一直往前划，但实验室的平台只有 ±12cm 行程、±18° 倾角。
    ///      所以要把船的状态映射成平台的"目标位姿"，并硬性限幅。
    ///      横滚/俯仰直接跟船（人感受到的是船在斜）；
    ///      纵向/横向加速度映射成短暂平移（模拟划桨推背感）；
    ///      偏航只跟加速度不跟绝对航向（否则平台会一直转，拧断电缆）。
    ///
    ///   2. 伺服滞后（Servo Lag）
    ///      用可配置的一阶惯性 τ 模拟响应；默认时间常数未经真实平台标定。
    ///      这样在电脑上就能提前演练"画面里的浪已经顶起虚拟船，实体平台慢半拍"，
    ///      并当场把同步补偿调好 —— 不用等真机到位才发现问题。
    ///
    ///   3. 六缸逆解
    ///      L_i = ‖ R·p_i + T − b_i ‖
    ///      上平台铰点 p_i 经旋转平移后与底座铰点 b_i 的距离，就是第 i 根缸的伸缩长度。
    ///      这是纯几何，不代表真实控制器、负载或稳定性已经验证。
    ///
    /// 输出：6 缸长度（mm）、平台实际姿态（滞后后）、同步误差（度）—— 全部进 HUD 与 UDP。
    /// </summary>
    public class StewartPlatformTwin : MonoBehaviour, IPlatformMotionDevice
    {
        [Header("数据源")]
        [Tooltip("水里的虚拟龙舟；留空自动找 BoatBuoyancy")]
        public BoatBuoyancy sourceBoat;
        public bool externallyStepped;
        public bool loadRuntimeConfiguration = true;
        public string runtimeConfigFileName = "platform_rig.json";
        public string configurationProvenance = "Inspector software geometry; unmeasured";
        public bool configurationMeasured;

        [Header("平台几何")]
        [Tooltip("底座铰点分布半径（m）")]
        public float baseRadius = 0.80f;
        [Tooltip("动平台铰点分布半径（m）")]
        public float topRadius = 0.55f;
        [Tooltip("中位高度：底座到动平台的静止距离（m）")]
        public float neutralHeight = 0.60f;
        [Tooltip("电动缸可视半径（m）")]
        public float actuatorRadius = 0.035f;

        [Tooltip("使用明确的六组底座/动平台铰点。动平台点相对其中心，米")]
        public bool useExplicitJointGeometry;
        public Vector3[] explicitBaseJointsLocal = new Vector3[6];
        public Vector3[] explicitTopJointsLocal = new Vector3[6];
        [Tooltip("几何是否为假设值。false 仅表示配置来源，不能证明真机验证")]
        public bool geometryAssumed = true;
        public float[] minActuatorLengthsMm = { 300f, 300f, 300f, 300f, 300f, 300f };
        public float[] maxActuatorLengthsMm = { 1100f, 1100f, 1100f, 1100f, 1100f, 1100f };
        public bool lengthLimitsAssumed = true;

        [Header("仿真映射限幅（默认值为假设）")]
        [Tooltip("最大垂直起伏行程 Heave（m）")]
        public float maxHeaveMeters = 0.15f;
        [Tooltip("最大纵向/横向平移行程 Surge/Sway（m）")]
        public float maxTranslationMeters = 0.12f;
        [Tooltip("最大俯仰/横滚倾角（度）")]
        public float maxTiltDegrees = 18f;
        [Tooltip("最大偏航角（度）。多数平台 yaw 行程很小")]
        public float maxYawDegrees = 8f;

        [Header("映射增益")]
        [Tooltip("加速度 → 平移量（m per m/s²）。0.02 表示 1m/s² 换 2cm 平移")]
        public float accelToTranslation = 0.02f;
        [Tooltip("横向加速度 → 偏航角（度 per m/s²）")]
        public float accelToYaw = 2.0f;

        [Header("伺服滞后")]
        [Tooltip("仿真响应时间常数 τ（秒），默认值未经真机标定")]
        [Range(0.001f, 0.2f)] public float servoLagTimeConstant = 0.025f;

        [Header("场景模型")]
        [Tooltip("自动生成底座/动平台/6 根电动缸的 3D 模型")]
        public bool autoBuildModel = true;
        [Tooltip("整机装配局部偏移：平台顶板中位应贴在船底；尺寸未实测")]
        public Vector3 standPosition = new Vector3(12f, 0f, 0f);
        [Tooltip("为船外整机视图让平台底座跟随样机船的水平位置与航向。仅用于装配可视化；不改变船体物理积分。")]
        public bool followBoatForAssemblyDisplay;

        // ---------------- 状态输出（只读，进 HUD 与 UDP） ----------------
        /// <summary>6 根电动缸实时长度（mm）</summary>
        public float[] actuatorLengthsMm = new float[6];
        /// <summary>平台目标姿态（度）：x=Pitch, y=Yaw, z=Roll</summary>
        public Vector3 targetEuler;
        /// <summary>平台实际姿态（滞后后，度）</summary>
        public Vector3 actualEuler;
        public Vector3 targetTranslationM;
        public Vector3 actualTranslationM;
        public Vector3 rawTargetEuler;
        public Vector3 rawTargetTranslationM;
        public float[] targetActuatorLengthsMm = new float[6];
        public int kinematicRank;
        public PlatformMotionRequest LastRequest { get; private set; }
        public PlatformMotionRequest Request => LastRequest;
        public uint CommandSequence => _sequence;
        public PlatformDeviceFeedback Feedback { get; private set; } = new PlatformDeviceFeedback
            { valid = false, status = "NOT_INITIALIZED" };
        /// <summary>视觉船体倾角与平台实际倾角的实时滞后偏差（度）</summary>
        public float pitchSyncErrorDeg;
        public float rollSyncErrorDeg;
        /// <summary>相对各缸真实几何中位长度的行程占比，1 到配置上/下限，超限时大于1。</summary>
        public float strokeUsage;

        Transform _basePlate, _topPlate;
        Transform[] _cylinders = new Transform[6];
        Vector3[] _baseJointsLocal = new Vector3[6];
        Vector3[] _topJointsLocal = new Vector3[6];

        Vector3 _currentPos;          // 动平台相对中位的偏移
        Vector3 _currentEuler;        // 平台实际姿态（度）
        float _restBoatY;
        bool _hasRestY;
        bool _initialized;
        float[] _neutralLengthsMm = new float[6];
        uint _sequence;
        double _simulationTime;
        bool _configurationValid = true;

        [System.Serializable]
        public class RuntimeConfiguration : PlatformRigConfig { }

        void Start()
        {
            EnsureInitialized();
        }

        void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            LoadRuntimeConfiguration();
            if (sourceBoat == null) sourceBoat = FindObjectOfType<BoatBuoyancy>(true);
            if (sourceBoat == null)
            {
                Debug.LogWarning("[StewartPlatformTwin] 找不到船体；仍可接收显式平台请求。");
            }

            BuildJoints();

            if (autoBuildModel && GeometryValid()) BuildModel();

            if (sourceBoat != null)
            {
                _restBoatY = sourceBoat.transform.position.y;
                _hasRestY = true;
            }
            if (GeometryValid())
            {
                ComputeLengths(Vector3.zero, Vector3.zero, _neutralLengthsMm);
                if (LengthsWithinLimits(_neutralLengthsMm)) UpdateActuators();
                else _configurationValid = false;
            }
        }

        void LoadRuntimeConfiguration()
        {
            if (!loadRuntimeConfiguration) return;
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, runtimeConfigFileName);
            if (!System.IO.File.Exists(path)) return;
            try
            {
                PlatformRigConfig config = JsonUtility.FromJson<PlatformRigConfig>(System.IO.File.ReadAllText(path));
                if (config == null || config.minActuatorLengthsMm == null || config.maxActuatorLengthsMm == null ||
                    config.minActuatorLengthsMm.Length != 6 || config.maxActuatorLengthsMm.Length != 6 ||
                    (config.useExplicitJointGeometry && (config.explicitBaseJointsLocal == null || config.explicitTopJointsLocal == null ||
                    config.explicitBaseJointsLocal.Length != 6 || config.explicitTopJointsLocal.Length != 6)))
                    throw new System.FormatException("平台配置需六组长度范围与显式几何的六组铰点。");
                AssignConfiguration(config);
            }
            catch (System.Exception error)
            {
                _configurationValid = false;
                Debug.LogError("[StewartPlatformTwin] 平台配置无效：" + error.Message);
            }
        }

        /// <summary>Apply independent geometry, limits and response settings. A measured label is provenance only;
        /// this component's feedback always remains a software simulation.</summary>
        public bool ApplyConfiguration(PlatformRigConfig config)
        {
            loadRuntimeConfiguration = false;
            if (config == null || config.minActuatorLengthsMm == null || config.maxActuatorLengthsMm == null ||
                config.minActuatorLengthsMm.Length != 6 || config.maxActuatorLengthsMm.Length != 6)
            {
                _configurationValid = false;
                SetFeedback(new PlatformMotionRequest(), false, true, "INVALID_CONFIGURATION_HOLD");
                return false;
            }
            AssignConfiguration(config);
            BuildJoints();
            _configurationValid = true;
            if (!GeometryValid() || !MappingValid())
            {
                _configurationValid = false;
                SetFeedback(new PlatformMotionRequest(), false, true, "INVALID_CONFIGURATION_HOLD");
                return false;
            }
            ComputeLengths(Vector3.zero, Vector3.zero, _neutralLengthsMm);
            if (!LengthsWithinLimits(_neutralLengthsMm))
            {
                _configurationValid = false;
                SetFeedback(new PlatformMotionRequest(), false, true, "NEUTRAL_ACTUATOR_LIMIT_CONFIGURATION_REJECTED_HOLD");
                return false;
            }
            ResetModel();
            return true;
        }

        void AssignConfiguration(PlatformRigConfig config)
        {
            configurationProvenance = config.provenance ?? "Unspecified configuration source";
            configurationMeasured = config.measured;
            useExplicitJointGeometry = config.useExplicitJointGeometry;
            explicitBaseJointsLocal = config.explicitBaseJointsLocal == null ? null : (Vector3[])config.explicitBaseJointsLocal.Clone();
            explicitTopJointsLocal = config.explicitTopJointsLocal == null ? null : (Vector3[])config.explicitTopJointsLocal.Clone();
            geometryAssumed = config.geometryAssumed;
            baseRadius = config.baseRadius; topRadius = config.topRadius; neutralHeight = config.neutralHeight;
            minActuatorLengthsMm = (float[])config.minActuatorLengthsMm.Clone();
            maxActuatorLengthsMm = (float[])config.maxActuatorLengthsMm.Clone();
            lengthLimitsAssumed = config.lengthLimitsAssumed;
            maxHeaveMeters = config.maxHeaveMeters; maxTranslationMeters = config.maxTranslationMeters;
            maxTiltDegrees = config.maxTiltDegrees; maxYawDegrees = config.maxYawDegrees;
            accelToTranslation = config.accelToTranslation; accelToYaw = config.accelToYaw;
            servoLagTimeConstant = config.servoLagTimeConstant;
        }

        /// <summary>
        /// 铰点布置：底座 6 点每 60° 一个，动平台错开 30°。
        /// 默认均匀环形只是可视化假设，并不保证满秩、静力稳定或六自由度能力。
        /// </summary>
        void BuildJoints()
        {
            if (useExplicitJointGeometry && explicitBaseJointsLocal != null && explicitTopJointsLocal != null &&
                explicitBaseJointsLocal.Length == 6 && explicitTopJointsLocal.Length == 6)
            {
                System.Array.Copy(explicitBaseJointsLocal, _baseJointsLocal, 6);
                System.Array.Copy(explicitTopJointsLocal, _topJointsLocal, 6);
                return;
            }
            for (int i = 0; i < 6; i++)
            {
                float aBase = i * 60f * Mathf.Deg2Rad;
                float aTop = (i * 60f + 30f) * Mathf.Deg2Rad;

                _baseJointsLocal[i] = new Vector3(
                    Mathf.Cos(aBase) * baseRadius, 0f, Mathf.Sin(aBase) * baseRadius);
                // 动平台铰点的 y 必须是 0：平台高度已经由 trans 里的 neutralHeight 给出，
                // 这里再写一次 neutralHeight 会让每个缸凭空多出 0.6m（实测行程 1073~1461mm，
                // 而几何上应该是 600~800mm）。铰点只在平台平面内定义，高度由平台位姿决定。
                _topJointsLocal[i] = new Vector3(
                    Mathf.Cos(aTop) * topRadius, 0f, Mathf.Sin(aTop) * topRadius);
            }
        }

        void BuildModel()
        {
            // 底座
            var baseGo = VisualPrimitive.Create(PrimitiveType.Cylinder);
            baseGo.name = "PlatformBase";
            baseGo.transform.SetParent(transform, false);
            baseGo.transform.localPosition = standPosition + Vector3.down * 0.05f;
            baseGo.transform.localScale = new Vector3(baseRadius * 2.4f, 0.05f, baseRadius * 2.4f);
            _basePlate = baseGo.transform;

            // 动平台（自己带一个坐标系，绕它算逆解）
            var topGo = new GameObject("PlatformTop");
            topGo.transform.SetParent(transform, false);
            topGo.transform.localPosition = standPosition + Vector3.up * neutralHeight;
            _topPlate = topGo.transform;

            var topVisible = VisualPrimitive.Create(PrimitiveType.Cylinder);
            topVisible.name = "PlatformTopMesh";
            topVisible.transform.SetParent(_topPlate, false);
            topVisible.transform.localPosition = Vector3.zero;
            topVisible.transform.localScale = new Vector3(topRadius * 2.4f, 0.04f, topRadius * 2.4f);

            // 6 根电动缸
            for (int i = 0; i < 6; i++)
            {
                var cyl = VisualPrimitive.Create(PrimitiveType.Cylinder);
                cyl.name = $"Actuator{i + 1}";
                cyl.transform.SetParent(transform, false);
                _cylinders[i] = cyl.transform;
                // 缸体和平台区分色，答辩时一眼能看出这是机械结构
                var r = cyl.GetComponent<Renderer>();
                if (r != null)
                {
                    var m = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                    m.color = new Color(0.85f, 0.75f, 0.25f);
                    r.material = m;
                }
            }
        }

        void Update()
        {
            if (!externallyStepped) Pump(Time.deltaTime);
        }

        /// <summary>
        /// 单步推进。抽出来的原因：batchmode 非 Play 模式下 Time.deltaTime 恒为 0，
        /// 原写法会直接在 `if (dt <= 0f) return;` 处返回，六缸长度永远不动，
        /// 自检里平台等于"死"的。
        /// </summary>
        public void Pump(float dt)
        {
            Receive(CreateRequest(dt), dt);
        }

        /// <summary>从船体状态生成平台运动控制器的输入请求，但不执行任何电缸命令。</summary>
        public PlatformMotionRequest CreateRequest(float dt)
        {
            EnsureInitialized();
            if (SimulationValue.Finite(dt) && dt > 0f) _simulationTime += dt;
            if (sourceBoat == null)
            {
                return new PlatformMotionRequest { commandSequence = ++_sequence, timeSeconds = _simulationTime, valid = false };
            }
            if (followBoatForAssemblyDisplay)
            {
                // The virtual river boat can travel far beyond a lab-sized platform.
                // Keep the illustrative hardware stack directly beneath the same boat
                // for the external view; actuator motion remains relative to this root.
                Vector3 p = sourceBoat.transform.position;
                transform.SetPositionAndRotation(new Vector3(p.x, 0f, p.z),
                    Quaternion.Euler(0f, sourceBoat.transform.eulerAngles.y, 0f));
            }
            Vector3 boatPos = sourceBoat.transform.position;
            Vector3 boatEuler = sourceBoat.currentEulerAngles;
            Vector3 boatAcc = sourceBoat.localLinearAcceleration;

            if (!_hasRestY) { _restBoatY = boatPos.y; _hasRestY = true; }

            Vector3 rawEuler = new Vector3(boatEuler.x, boatAcc.x * accelToYaw, boatEuler.z);
            Vector3 rawPosition = new Vector3(boatAcc.x * accelToTranslation, boatPos.y - _restBoatY, boatAcc.z * accelToTranslation);
            return new PlatformMotionRequest
            {
                commandSequence = ++_sequence, timeSeconds = WaveField.Instance != null ? WaveField.Instance.SimTime : _simulationTime,
                rawEulerDegrees = rawEuler, rawTranslationM = rawPosition,
                eulerDegrees = rawEuler, translationM = rawPosition,
                valid = SimulationValue.Finite(boatPos) && SimulationValue.Finite(boatEuler) && SimulationValue.Finite(boatAcc)
            };
        }

        public void Receive(PlatformMotionRequest request, float dt)
            => ReceiveCore(request, dt, true);

        /// <summary>Validate and map a target for the independent controller. Its
        /// received observation remains the only owner of actual pose and feedback.</summary>
        public void PrepareControllerRequest(PlatformMotionRequest request, float dt)
        {
            PlatformDeviceFeedback observation = Feedback;
            try { ReceiveCore(request, dt, false); }
            finally { Feedback = observation; }
        }

        void ReceiveCore(PlatformMotionRequest request, float dt, bool simulateResponse)
        {
            EnsureInitialized();
            LastRequest = request;
            if (!request.valid || !SimulationValue.Finite(dt) || dt <= 0f ||
                double.IsNaN(request.timeSeconds) || double.IsInfinity(request.timeSeconds) ||
                !SimulationValue.Finite(request.eulerDegrees) || !SimulationValue.Finite(request.translationM) ||
                !SimulationValue.Finite(request.rawEulerDegrees) || !SimulationValue.Finite(request.rawTranslationM))
            {
                request.valid = false;
                request.timeSeconds = double.IsNaN(request.timeSeconds) || double.IsInfinity(request.timeSeconds) ? 0d : request.timeSeconds;
                request.eulerDegrees = SimulationValue.Finite(request.eulerDegrees) ? request.eulerDegrees : Vector3.zero;
                request.translationM = SimulationValue.Finite(request.translationM) ? request.translationM : Vector3.zero;
                request.rawEulerDegrees = SimulationValue.Finite(request.rawEulerDegrees) ? request.rawEulerDegrees : Vector3.zero;
                request.rawTranslationM = SimulationValue.Finite(request.rawTranslationM) ? request.rawTranslationM : Vector3.zero;
                LastRequest = request;
                SetFeedback(request, false, true, "INVALID_REQUEST_HOLD");
                return;
            }
            BuildJoints();
            if (!GeometryValid() || !MappingValid())
            {
                SetFeedback(request, false, true, "INVALID_GEOMETRY_OR_LIMITS_HOLD");
                return;
            }
            ComputeLengths(Vector3.zero, Vector3.zero, _neutralLengthsMm);
            rawTargetEuler = request.rawEulerDegrees;
            rawTargetTranslationM = request.rawTranslationM;
            targetEuler = new Vector3(Mathf.Clamp(request.eulerDegrees.x, -maxTiltDegrees, maxTiltDegrees),
                Mathf.Clamp(request.eulerDegrees.y, -maxYawDegrees, maxYawDegrees),
                Mathf.Clamp(request.eulerDegrees.z, -maxTiltDegrees, maxTiltDegrees));
            targetTranslationM = new Vector3(Mathf.Clamp(request.translationM.x, -maxTranslationMeters, maxTranslationMeters),
                Mathf.Clamp(request.translationM.y, -maxHeaveMeters, maxHeaveMeters),
                Mathf.Clamp(request.translationM.z, -maxTranslationMeters, maxTranslationMeters));
            request.clamped |= (targetEuler - request.eulerDegrees).sqrMagnitude > 1e-10f ||
                (targetTranslationM - request.translationM).sqrMagnitude > 1e-10f;
            request.eulerDegrees = targetEuler;
            request.translationM = targetTranslationM;
            LastRequest = request;
            ComputeLengths(targetTranslationM, targetEuler, targetActuatorLengthsMm);
            if (!LengthsWithinLimits(targetActuatorLengthsMm))
            {
                SetFeedback(request, false, true, "ACTUATOR_LIMIT_REQUEST_REJECTED_HOLD");
                return;
            }
            float alpha = dt / (servoLagTimeConstant + dt);
            Vector3 nextPosition = Vector3.Lerp(_currentPos, targetTranslationM, alpha);
            Vector3 nextEuler = Vector3.Lerp(_currentEuler, targetEuler, alpha);
            var nextLengths = new float[6];
            ComputeLengths(nextPosition, nextEuler, nextLengths);
            if (!LengthsWithinLimits(nextLengths))
            {
                SetFeedback(request, false, true, "ACTUATOR_LIMIT_RESPONSE_REJECTED_HOLD");
                return;
            }
            if (!simulateResponse) return;
            _currentPos = nextPosition;
            _currentEuler = nextEuler;
            if (_topPlate != null)
            {
                _topPlate.localPosition = standPosition + Vector3.up * neutralHeight + _currentPos;
                _topPlate.localRotation = Quaternion.Euler(_currentEuler);
            }
            UpdateActuators();
            pitchSyncErrorDeg = Mathf.Abs(targetEuler.x - _currentEuler.x);
            rollSyncErrorDeg = Mathf.Abs(targetEuler.z - _currentEuler.z);
            actualEuler = _currentEuler;
            actualTranslationM = _currentPos;
            kinematicRank = JacobianRank(_currentPos, _currentEuler);
            bool limited = request.clamped || kinematicRank < 6;
            SetFeedback(request, true, limited,
                kinematicRank < 6 ? "CAPABILITY_LIMITED_KINEMATIC_SIMULATION" :
                request.clamped ? "CLAMPED_KINEMATIC_SIMULATION" : "KINEMATIC_SIMULATION");
        }

        bool MappingValid()
        {
            return SimulationValue.Finite(maxHeaveMeters) && maxHeaveMeters >= 0f &&
                SimulationValue.Finite(maxTranslationMeters) && maxTranslationMeters >= 0f &&
                SimulationValue.Finite(maxTiltDegrees) && maxTiltDegrees >= 0f &&
                SimulationValue.Finite(maxYawDegrees) && maxYawDegrees >= 0f &&
                SimulationValue.Finite(accelToTranslation) && SimulationValue.Finite(accelToYaw) &&
                SimulationValue.Finite(servoLagTimeConstant) && servoLagTimeConstant > 0f;
        }

        bool GeometryValid()
        {
            if (!_configurationValid || !SimulationValue.Finite(neutralHeight) || neutralHeight <= 0f ||
                !SimulationValue.Finite(standPosition) || minActuatorLengthsMm == null || maxActuatorLengthsMm == null ||
                minActuatorLengthsMm.Length != 6 || maxActuatorLengthsMm.Length != 6) return false;
            if (useExplicitJointGeometry && (explicitBaseJointsLocal == null || explicitTopJointsLocal == null ||
                explicitBaseJointsLocal.Length != 6 || explicitTopJointsLocal.Length != 6)) return false;
            if (!useExplicitJointGeometry && (!SimulationValue.Finite(baseRadius) || baseRadius <= 0f ||
                !SimulationValue.Finite(topRadius) || topRadius <= 0f)) return false;
            for (int i = 0; i < 6; i++)
                if (!SimulationValue.Finite(_baseJointsLocal[i]) || !SimulationValue.Finite(_topJointsLocal[i]) ||
                    !SimulationValue.Finite(minActuatorLengthsMm[i]) || !SimulationValue.Finite(maxActuatorLengthsMm[i]) ||
                    minActuatorLengthsMm[i] <= 0f || maxActuatorLengthsMm[i] <= minActuatorLengthsMm[i]) return false;
            return true;
        }

        /// <summary>Use actual length-servo/FK observations for the diagnostic model.
        /// Invalid or stale observations never move the platform.</summary>
        public void ApplyControllerObservation(PlatformDeviceFeedback observation)
        {
            if (observation == null) return;
            Feedback = DeviceReplayValue.Copy(observation);
            if (!observation.valid || !SimulationValue.Finite(observation.translationM)
                || !SimulationValue.Finite(observation.eulerDegrees)) return;
            _currentPos = actualTranslationM = observation.translationM;
            _currentEuler = actualEuler = observation.eulerDegrees;
            if (_topPlate != null)
            {
                _topPlate.localPosition = standPosition + Vector3.up * neutralHeight + _currentPos;
                _topPlate.localRotation = Quaternion.Euler(_currentEuler);
            }
            UpdateActuators();
            if (observation.actuatorLengthsMm != null && observation.actuatorLengthsMm.Length == 6)
                System.Array.Copy(observation.actuatorLengthsMm, actuatorLengthsMm, 6);
        }

        void ComputeLengths(Vector3 position, Vector3 euler, float[] lengths)
        {
            Quaternion rotation = Quaternion.Euler(euler);
            Vector3 translation = Vector3.up * neutralHeight + position;
            for (int i = 0; i < 6; i++) lengths[i] = (translation + rotation * _topJointsLocal[i] - _baseJointsLocal[i]).magnitude * 1000f;
        }

        bool LengthsWithinLimits(float[] lengths)
        {
            for (int i = 0; i < 6; i++)
                if (!SimulationValue.Finite(lengths[i]) || lengths[i] < minActuatorLengthsMm[i] || lengths[i] > maxActuatorLengthsMm[i]) return false;
            return true;
        }

        void SetFeedback(PlatformMotionRequest request, bool valid, bool limited, string status)
        {
            // Adapters forward Request, so a rejected motion must invalidate the command
            // as well as its feedback. Preserve the rejected target for diagnostics.
            request.valid &= valid;
            LastRequest = request;
            string assumptions = (geometryAssumed || !useExplicitJointGeometry ? ";ASSUMED_GEOMETRY" : ";CONFIGURED_GEOMETRY") +
                (lengthLimitsAssumed ? ";ASSUMED_LENGTH_LIMITS" : ";CONFIGURED_LENGTH_LIMITS") +
                (configurationMeasured ? ";MEASURED_CONFIGURATION_SOURCE" : ";UNMEASURED_CONFIGURATION_SOURCE");
            Feedback = new PlatformDeviceFeedback
            {
                commandSequence = request.commandSequence,
                timeSeconds = double.IsNaN(request.timeSeconds) || double.IsInfinity(request.timeSeconds) ? 0d : request.timeSeconds,
                simulated = true, valid = valid, capabilityLimited = limited, status = status + assumptions,
                eulerDegrees = _currentEuler, translationM = _currentPos,
                actuatorLengthsMm = (float[])actuatorLengthsMm.Clone()
            };
        }

        public StewartPlatformReplayState CaptureReplayState()
        {
            EnsureInitialized();
            var state = new StewartPlatformReplayState
            {
                currentPosition = _currentPos, currentEuler = _currentEuler,
                targetEuler = targetEuler, targetTranslationM = targetTranslationM,
                actualEuler = actualEuler, actualTranslationM = actualTranslationM,
                rawTargetEuler = rawTargetEuler, rawTargetTranslationM = rawTargetTranslationM,
                restBoatY = _restBoatY, hasRestY = _hasRestY, sequence = _sequence, simulationTime = _simulationTime,
                actuatorLengthsMm = (float[])actuatorLengthsMm.Clone(),
                targetActuatorLengthsMm = (float[])targetActuatorLengthsMm.Clone(),
                neutralLengthsMm = (float[])_neutralLengthsMm.Clone(),
                kinematicRank = kinematicRank, pitchSyncErrorDeg = pitchSyncErrorDeg,
                rollSyncErrorDeg = rollSyncErrorDeg, strokeUsage = strokeUsage,
                lastRequest = LastRequest, feedback = DeviceReplayValue.Copy(Feedback)
            };
            ValidateReplayState(state);
            return state;
        }

        public void ValidateReplayState(StewartPlatformReplayState state)
        {
            DeviceReplayValue.Require(_initialized && GeometryValid() && MappingValid() && state != null && state.version == 1,
                "Invalid initial platform model or configuration.");
            DeviceReplayValue.Require(SimulationValue.Finite(state.currentPosition) && SimulationValue.Finite(state.currentEuler) &&
                SimulationValue.Finite(state.targetEuler) && SimulationValue.Finite(state.targetTranslationM) &&
                SimulationValue.Finite(state.actualEuler) && SimulationValue.Finite(state.actualTranslationM) &&
                SimulationValue.Finite(state.rawTargetEuler) && SimulationValue.Finite(state.rawTargetTranslationM) &&
                SimulationValue.Finite(state.restBoatY) && DeviceReplayValue.Finite(state.simulationTime) &&
                state.simulationTime >= 0d && SimulationValue.Finite(state.pitchSyncErrorDeg) && state.pitchSyncErrorDeg >= 0f &&
                SimulationValue.Finite(state.rollSyncErrorDeg) && state.rollSyncErrorDeg >= 0f &&
                SimulationValue.Finite(state.strokeUsage) && state.strokeUsage >= 0f &&
                state.kinematicRank >= 0 && state.kinematicRank <= 6,
                "Nonfinite or malformed initial platform state.");
            DeviceReplayValue.Require(state.currentPosition == state.actualTranslationM && state.currentEuler == state.actualEuler &&
                ReplayPoseWithinMapping(state.currentPosition, state.currentEuler) &&
                ReplayPoseWithinMapping(state.targetTranslationM, state.targetEuler), "Invalid restored platform pose.");
            DeviceReplayValue.Array(state.actuatorLengthsMm, 6, "platform actual lengths");
            DeviceReplayValue.Array(state.targetActuatorLengthsMm, 6, "platform target lengths");
            DeviceReplayValue.Array(state.neutralLengthsMm, 6, "platform neutral lengths");
            DeviceReplayValue.Require(LengthsWithinLimits(state.actuatorLengthsMm) && LengthsWithinLimits(state.neutralLengthsMm),
                "Recorded platform pose violates current actuator limits.");
            var calculated = new float[6];
            ComputeLengths(Vector3.zero, Vector3.zero, calculated);
            RequireReplayLengths(calculated, state.neutralLengthsMm);
            ComputeLengths(state.currentPosition, state.currentEuler, calculated);
            DeviceReplayValue.Require(LengthsWithinLimits(calculated), "Recorded platform pose violates current geometry limits.");
            RequireReplayLengths(calculated, state.actuatorLengthsMm);
            // An initialized component can have no requested target yet. Rejected targets can
            // exceed actuator length limits, but must still describe the recorded target pose.
            bool noTarget = !state.lastRequest.valid && state.lastRequest.commandSequence == 0 &&
                state.targetEuler == Vector3.zero && state.targetTranslationM == Vector3.zero;
            for (int i = 0; i < 6; i++) noTarget &= state.targetActuatorLengthsMm[i] == 0f;
            if (!noTarget)
            {
                ComputeLengths(state.targetTranslationM, state.targetEuler, calculated);
                RequireReplayLengths(calculated, state.targetActuatorLengthsMm);
            }
            var request = state.lastRequest;
            DeviceReplayValue.Require(DeviceReplayValue.Finite(request.timeSeconds) &&
                SimulationValue.Finite(request.rawEulerDegrees) && SimulationValue.Finite(request.rawTranslationM) &&
                SimulationValue.Finite(request.eulerDegrees) && SimulationValue.Finite(request.translationM),
                "Invalid initial platform request.");
            var feedback = state.feedback;
            DeviceReplayValue.Require(feedback != null && feedback.simulated && !string.IsNullOrEmpty(feedback.status) &&
                DeviceReplayValue.Finite(feedback.timeSeconds) && SimulationValue.Finite(feedback.eulerDegrees) &&
                SimulationValue.Finite(feedback.translationM), "Invalid initial platform feedback.");
            if (feedback.actuatorLengthsMm != null)
            {
                DeviceReplayValue.Array(feedback.actuatorLengthsMm, 6, "platform feedback lengths");
                RequireReplayLengths(state.actuatorLengthsMm, feedback.actuatorLengthsMm);
            }
            else DeviceReplayValue.Require(!feedback.valid && feedback.status == "NOT_INITIALIZED",
                "Missing initial platform feedback lengths.");
            DeviceReplayValue.Require(feedback.eulerDegrees == state.currentEuler && feedback.translationM == state.currentPosition,
                "Platform feedback does not match its internal pose.");
        }

        bool ReplayPoseWithinMapping(Vector3 position, Vector3 euler) =>
            Mathf.Abs(position.x) <= maxTranslationMeters && Mathf.Abs(position.z) <= maxTranslationMeters &&
            Mathf.Abs(position.y) <= maxHeaveMeters && Mathf.Abs(euler.x) <= maxTiltDegrees &&
            Mathf.Abs(euler.z) <= maxTiltDegrees && Mathf.Abs(euler.y) <= maxYawDegrees;

        static void RequireReplayLengths(float[] expected, float[] actual)
        {
            // UpdateActuators includes a world stand offset before subtraction; allow only
            // its float roundoff here. Actual pose and lengths still obey the unchanged limits.
            for (int i = 0; i < 6; i++)
                DeviceReplayValue.Require(Mathf.Abs(expected[i] - actual[i]) <= 0.05f,
                    "Recorded platform lengths do not match their geometry.");
        }

        /// <summary>Restore the recorded neutral reference and servo state. Request time follows
        /// WaveField.SimTime when present, so its clock is preserved with the recorded wave phase.</summary>
        public void RestoreReplayState(StewartPlatformReplayState state)
        {
            ValidateReplayState(state);
            _currentPos = state.currentPosition;
            _currentEuler = state.currentEuler;
            targetEuler = state.targetEuler;
            targetTranslationM = state.targetTranslationM;
            actualEuler = state.actualEuler;
            actualTranslationM = state.actualTranslationM;
            rawTargetEuler = state.rawTargetEuler;
            rawTargetTranslationM = state.rawTargetTranslationM;
            _restBoatY = state.restBoatY;
            _hasRestY = state.hasRestY;
            _sequence = state.sequence;
            _simulationTime = state.simulationTime;
            System.Array.Copy(state.neutralLengthsMm, _neutralLengthsMm, 6);
            if (_topPlate != null)
            {
                _topPlate.localPosition = standPosition + Vector3.up * neutralHeight + _currentPos;
                _topPlate.localRotation = Quaternion.Euler(_currentEuler);
            }
            UpdateActuators();
            System.Array.Copy(state.actuatorLengthsMm, actuatorLengthsMm, 6);
            System.Array.Copy(state.targetActuatorLengthsMm, targetActuatorLengthsMm, 6);
            kinematicRank = state.kinematicRank;
            pitchSyncErrorDeg = state.pitchSyncErrorDeg;
            rollSyncErrorDeg = state.rollSyncErrorDeg;
            strokeUsage = state.strokeUsage;
            LastRequest = state.lastRequest;
            Feedback = DeviceReplayValue.Copy(state.feedback);
        }

        public void ResetModel()
        {
            EnsureInitialized();
            _currentPos = _currentEuler = targetEuler = actualEuler = Vector3.zero;
            targetTranslationM = actualTranslationM = rawTargetEuler = rawTargetTranslationM = Vector3.zero;
            pitchSyncErrorDeg = rollSyncErrorDeg = 0f;
            _sequence = 0u;
            _simulationTime = 0d;
            _hasRestY = false;
            LastRequest = new PlatformMotionRequest();
            BuildJoints();
            if (GeometryValid() && MappingValid())
            {
                ComputeLengths(Vector3.zero, Vector3.zero, _neutralLengthsMm);
                if (LengthsWithinLimits(_neutralLengthsMm))
                {
                    System.Array.Copy(_neutralLengthsMm, targetActuatorLengthsMm, 6);
                    UpdateActuators();
                    kinematicRank = JacobianRank(Vector3.zero, Vector3.zero);
                }
                else _configurationValid = false;
            }
            if (!GeometryValid() || !MappingValid())
            {
                System.Array.Clear(actuatorLengthsMm, 0, actuatorLengthsMm.Length);
                System.Array.Clear(targetActuatorLengthsMm, 0, targetActuatorLengthsMm.Length);
                kinematicRank = 0;
                SetFeedback(LastRequest, false, true, "INVALID_NEUTRAL_CONFIGURATION");
                return;
            }
            if (_topPlate != null)
            {
                _topPlate.localPosition = standPosition + Vector3.up * neutralHeight;
                _topPlate.localRotation = Quaternion.identity;
            }
            SetFeedback(LastRequest, false, kinematicRank < 6, "RESET_NEUTRAL_SIMULATION");
        }

        int JacobianRank(Vector3 position, Vector3 euler)
        {
            Quaternion rotation = Quaternion.Euler(euler);
            Vector3 translation = Vector3.up * neutralHeight + position;
            var basis = new float[6][];
            int rank = 0;
            for (int i = 0; i < 6; i++)
            {
                Vector3 arm = rotation * _topJointsLocal[i];
                Vector3 direction = (translation + arm - _baseJointsLocal[i]).normalized;
                var row = new float[6];
                TendonRig.BuildColumn(direction, arm, Mathf.Max(0.01f, neutralHeight), row);
                for (int j = 0; j < rank; j++)
                {
                    float dot = 0f;
                    for (int k = 0; k < 6; k++) dot += row[k] * basis[j][k];
                    for (int k = 0; k < 6; k++) row[k] -= dot * basis[j][k];
                }
                float norm = 0f;
                for (int k = 0; k < 6; k++) norm += row[k] * row[k];
                if (norm < 1e-6f) continue;
                norm = Mathf.Sqrt(norm);
                for (int k = 0; k < 6; k++) row[k] /= norm;
                basis[rank++] = row;
            }
            return rank;
        }

        void UpdateActuators()
        {
            Quaternion rot = Quaternion.Euler(_currentEuler);
            Vector3 trans = standPosition + Vector3.up * neutralHeight + _currentPos;

            float maxUsage = 0f;

            for (int i = 0; i < 6; i++)
            {
                // 动平台铰点世界坐标 = T + R · p_i
                Vector3 topWorld = trans + rot * _topJointsLocal[i];
                Vector3 baseWorld = standPosition + _baseJointsLocal[i];

                Vector3 vec = topWorld - baseWorld;
                float len = vec.magnitude;
                actuatorLengthsMm[i] = len * 1000f;

                // 行程使用各缸几何中位长度和明确的长度上/下限，未配置有效范围时报告未知。
                float neutralMm = _neutralLengthsMm[i];
                float margin = GeometryValid() ? (actuatorLengthsMm[i] >= neutralMm ?
                    maxActuatorLengthsMm[i] - neutralMm : neutralMm - minActuatorLengthsMm[i]) : 0f;
                float usage = margin > 0f ? Mathf.Abs(actuatorLengthsMm[i] - neutralMm) / margin : float.PositiveInfinity;
                if (usage > maxUsage) maxUsage = usage;

                var cyl = _cylinders[i];
                if (cyl == null) continue;

                Vector3 dir = len > 1e-4f ? vec / len : Vector3.up;
                cyl.position = transform.TransformPoint((topWorld + baseWorld) * 0.5f);
                cyl.rotation = transform.rotation * Quaternion.FromToRotation(Vector3.up, dir);
                // Cylinder 原始高度 2，缩放到实际长度
                cyl.localScale = new Vector3(actuatorRadius * 2f, len * 0.5f, actuatorRadius * 2f);
            }

            strokeUsage = maxUsage;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (_baseJointsLocal == null) return;
            Gizmos.color = Color.yellow;
            for (int i = 0; i < 6; i++)
            {
                Vector3 b = standPosition + _baseJointsLocal[i];
                Gizmos.DrawWireSphere(b, 0.04f);
            }
        }
#endif
    }
}
