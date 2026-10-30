using UnityEngine;

namespace DragonBoatVR
{
    [System.Serializable]
    public class PaddleGeometryConfiguration
    {
        public string provenance = "UNMEASURED_RECTANGULAR_PROTOTYPE";
        public bool measured;
        public float bladeAreaM2 = 0.0714f;
        public float bladeLengthM = 0.42f;
        public float bladeThicknessM = 0.025f;
        public float referenceToBladeCentreM = 1.0f;
        public Vector3 sensorToReferencePositionM;
        public Vector3 sensorToReferenceEulerDeg;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(provenance) || !SimulationValue.Finite(bladeAreaM2) || bladeAreaM2 <= 0f
                || !SimulationValue.Finite(bladeLengthM) || bladeLengthM <= 0f
                || !SimulationValue.Finite(bladeThicknessM) || bladeThicknessM <= 0f
                || !SimulationValue.Finite(referenceToBladeCentreM) || referenceToBladeCentreM <= bladeLengthM * 0.5f
                || !SimulationValue.Finite(sensorToReferencePositionM) || !SimulationValue.Finite(sensorToReferenceEulerDeg))
                throw new System.ArgumentException("Invalid paddle geometry configuration.");
        }
    }
    /// <summary>
    /// 桨叶水动力解算 + 推进反作用力 + 三电机牵引绳数字孪生数据源。
    ///
    /// 核心公式（与工程规划的物理式一致）：
    ///     h_eff = max(0, y_water − y_blade)              动态入水深度（浪打过来即使手不动也会入水）
    ///     v_rel = v_blade − v_water                      相对流速（水质点速度来自 WaveField）
    ///     Cd(α) = Cd_flat·sin²α + Cd_edge·cos²α          攻角修正（桨面立/平阻力差一个量级）
    ///     F_water = −½·ρ·Cd(α)·A(h_eff)·|v_rel|·v_rel    水对桨叶的阻力矢量
    ///
    /// 然后做两件事：
    ///   1. 把 F_water 作为"电机期望阻力"交给力反馈链路（缆绳张力分配见 CableDriveTwin）；
    ///   2. 把 **F_water 本身** 沿桨杆传给船体，作为推进力（牛顿第三定律）。
    ///
    /// 第 2 点的符号容易搞反，这里写清楚：
    ///   桨叶向后（−Z）划水 → 水对桨叶的阻力朝前（+Z）→ 桨杆把这个力顶到船上 → 船向前（+Z）。
    ///   所以推进力 = F_water，不是 −F_water。写成 −F 会让船倒着走，而且"越用力越后退"，
    ///   联调时极难判断到底是哪一层错了。
    ///
    /// 建模约定：桨模型原点 = 桨叶中心；桨杆沿 local +Y；桨叶面法线 = local +Z。
    /// </summary>
    public class PaddleHydrodynamics : MonoBehaviour
    {
        [HideInInspector] public bool externallyStepped;
        public bool requireValidInput;
        public PaddleDriver input;
        public Vector3 RawWaterForceN { get; private set; }
        public Vector3 RawTorqueAtBladeNm { get; private set; }
        public float RawDissipativePowerW { get; private set; }
        public Vector3 TorqueAtBladeNm { get; private set; }
        public float WettedAreaM2 { get; private set; }
        public float WettedFraction => Mathf.Clamp01(WettedAreaM2 / Mathf.Max(1e-6f, bladeArea));
        public Vector3 BladeAngularVelocity { get; private set; }
        public Vector3 BoatPropulsionForceN { get; private set; }
        public Vector3 BoatPropulsionTorqueNm { get; private set; }
        public ForceFeedbackRequest FeedbackRequest { get; private set; }
        public bool InputValid { get; private set; } = true;
        uint _requestSequence;
        public uint CommandSequence => _requestSequence;
        public void ResetDynamics()
        {
            _hasLastPos = false; _wasInWater = false;
            InputValid = !requireValidInput;
            WaterForceN = RawWaterForceN = BoatPropulsionForceN = BoatPropulsionTorqueNm = Vector3.zero;
            RawTorqueAtBladeNm = TorqueAtBladeNm = BladeVelocity = BladeAngularVelocity = Vector3.zero;
            CurrentDrag = EffectiveDepth = RelativeSpeed = AttackAngleDeg = WettedAreaM2 = 0f;
            RawDissipativePowerW = 0f;
            BladeInWater = WaveInducedEntry = false;
            FeedbackRequest = default;
        }
        public void RestoreFeedbackState(Vector3 force)
        {
            if (!SimulationValue.Finite(force)) throw new System.ArgumentException("Invalid feedback state.");
            WaterForceN = force; CurrentDrag = force.magnitude;
        }
        public void RestoreFeedbackState(Vector3 force, uint commandSequence)
        {
            RestoreFeedbackState(force);
            _requestSequence = commandSequence;
        }
        public void RestoreFeedbackState(Vector3 force, ForceFeedbackRequest request, Vector3 bladePosition)
        {
            RestoreFeedbackState(force, request.commandSequence);
            Vector3 lever = bladePosition - request.gripWorldPosition;
            TorqueAtBladeNm = request.torqueAtGripWorldNm - Vector3.Cross(lever, force);
        }
        [Header("关联对象")]
        [Tooltip("要推进的船体；留空自动找 BoatBuoyancy")]
        public BoatBuoyancy targetBoat;

        [Header("桨叶参数")]
        [Tooltip("样机矩形桨叶单面参考面积（m²）。宽度=面积/长度；图形与物理共用。需按实测替换")]
        public float bladeArea = 0.0714f;

        [Tooltip("正面击水阻力系数（α=90°），平板 ≈ 1.28")]
        public float cdFlat = 1.28f;

        [Tooltip("刃面切水阻力系数（α=0°），尖薄桨叶 0.03~0.08")]
        public float cdEdge = 0.05f;

        [Tooltip("水的密度（kg/m³）")]
        public float waterDensity = 1000f;

        [Tooltip("样机矩形桨叶沿桨杆的全长（m）；实际湿区按几何裁剪")]
        public float bladeLength = 0.42f;
        public float bladeThickness = 0.025f;
        public float BladeWidth => bladeArea / Mathf.Max(1e-6f, bladeLength);

        [Header("推进")]
        [Tooltip("是否把水阻反作用力施加给船体。真机联调时可关（人身体也在推）")]
        public bool applyPropulsion = true;

        [Tooltip("推进力倍率。1.0 = 严格按牛顿第三定律")]
        public float propulsionScale = 1.0f;

        [Tooltip("推进力垂直分量衰减。桨拍水不该把船往上顶飞")]
        [Range(0f, 1f)] public float propulsionVerticalDamp = 0.2f;

        [Tooltip("配平单舷桨的偏航与横摇力矩，模拟另一舷对称划桨。关掉可演示单桨打转")]
        public bool balanceYawRoll = true;

        [Tooltip("配平强度。1=两舷完全对称，0=纯单桨")]
        [Range(0f, 1f)] public float balanceStrength = 1.0f;

        [Header("自检用")]
        [Tooltip("时间步覆盖（秒）。>0 时用这个值算桨速，供 batchmode 自检注入恒定 dt")]
        public float dtOverride = 0f;

        [Header("输出限幅（保护力反馈电机）")]
        [Tooltip("单桨反馈力上限（N）。电机额定值以下，超了会扯手")]
        public float maxFeedbackForceN = 300f;
        [Tooltip("样机反馈力矩上限（Nm），需根据实际机构能力替换")]
        public float maxFeedbackTorqueNm = 150f;

        [Tooltip("反馈幅值增长低通（秒）；减小和换向遵循当前水阻，电机另有响应/变化率限制")]
        public float smoothing = 0.05f;

        [Header("调试")]
        [Tooltip("本地演示时把水阻施加到桨的 Rigidbody（桨自己挂着刚体时）")]
        public bool applyForceToRigidbody = false;

        // ---------------- 状态输出（遥测 / HUD / 数字孪生 只读） ----------------

        /// <summary>水对桨叶的三维阻力矢量（N），世界坐标。这是要发给电机的量</summary>
        public Vector3 WaterForceN { get; private set; }

        /// <summary>平滑后的阻力模长（N），HUD 用</summary>
        public float CurrentDrag { get; private set; }

        /// <summary>桨叶是否存在非零浸水面积，包含中心仍在水上的情况</summary>
        public bool BladeInWater { get; private set; }

        /// <summary>有效入水深度（m），已含波浪抬升</summary>
        public float EffectiveDepth { get; private set; }

        /// <summary>攻角 α（度）：0=刃面切水，90=正面击水</summary>
        public float AttackAngleDeg { get; private set; }

        /// <summary>相对流速大小（m/s）</summary>
        public float RelativeSpeed { get; private set; }

        /// <summary>水质点速度（世界坐标），调试用</summary>
        public Vector3 WaterVelocity { get; private set; }

        /// <summary>桨叶世界速度（由帧间位移推算，兼容 UDP 直驱）</summary>
        public Vector3 BladeVelocity { get; private set; }

        /// <summary>本次入水是否由浪抬升触发</summary>
        public bool WaveInducedEntry { get; private set; }

        /// <summary>桨叶中心世界坐标（供牵引绳逆解）</summary>
        public Vector3 BladePosition => transform.position;

        WaveField _wave;
        Rigidbody _rb;
        Vector3 _lastBladePos;
        Quaternion _lastBladeRotation;
        bool _hasLastPos;
        bool _wasInWater;
        const int GridX = 4, GridY = 12;
        readonly Vector3[] _grid = new Vector3[(GridX + 1) * (GridY + 1)];
        readonly float[] _depths = new float[(GridX + 1) * (GridY + 1)];
        readonly Vector3[] _clipped = new Vector3[4];
        Transform _bladeVisual;
        Transform _shaftVisual;
        Vector3 _appliedVisualSize;
        float _appliedShaftLength = -1f;

        public void SynchronizeVisualGeometry()
        {
            if (_bladeVisual == null) _bladeVisual = transform.Find("Blade");
            if (_shaftVisual == null) _shaftVisual = transform.Find("Shaft");
            Vector3 size = new Vector3(BladeWidth, bladeLength, bladeThickness);
            if (_bladeVisual != null && size != _appliedVisualSize)
            {
                FitVisualBounds(_bladeVisual, size, Vector3.zero);
                _appliedVisualSize = size;
            }
            if (_shaftVisual != null)
            {
                float length = input != null ? input.shaftToBlade : 1.0f;
                if (length != _appliedShaftLength)
                {
                    FitVisualBounds(_shaftVisual, new Vector3(0.045f, length, 0.045f), Vector3.up * (length * 0.5f));
                    _appliedShaftLength = length;
                }
            }
        }

        static void FitVisualBounds(Transform part, Vector3 size, Vector3 centre)
        {
            var filter = part.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            Bounds bounds = filter.sharedMesh.bounds;
            // Unity's capsule and cylinder are two units tall. Dimensions in the
            // sensor/hydrodynamics contract are metres, not primitive scale values.
            Vector3 scale = new Vector3(size.x / Mathf.Max(1e-6f, bounds.size.x),
                size.y / Mathf.Max(1e-6f, bounds.size.y), size.z / Mathf.Max(1e-6f, bounds.size.z));
            part.localRotation = Quaternion.identity;
            part.localScale = scale;
            part.localPosition = centre - Vector3.Scale(bounds.center, scale);
        }

        WaveField.SurfaceSample WaterAt(Vector3 p)
        {
            return _wave != null ? _wave.Sample(p.x, p.z)
                : new WaveField.SurfaceSample { height = 0f, normal = Vector3.up, converged = true };
        }

        void Start()
        {
            _wave = WaveField.Instance;
            if (_wave == null) Debug.LogWarning("[PaddleHydrodynamics] 没有 WaveField，水深回退 0。");
            _rb = GetComponent<Rigidbody>();

            if (targetBoat == null) targetBoat = FindObjectOfType<BoatBuoyancy>(true);
            if (targetBoat == null) Debug.LogWarning("[PaddleHydrodynamics] 没有 targetBoat，桨不会推进任何东西。");

            _lastBladePos = transform.position;
            _lastBladeRotation = transform.rotation;
            _hasLastPos = true;
            SynchronizeVisualGeometry();
        }

        void FixedUpdate()
        {
            if (!externallyStepped) Step(dtOverride > 0f ? dtOverride : Time.fixedDeltaTime);
        }

        /// <summary>
        /// 单步解算。抽出来是为了让自动化自检能注入恒定 dt ——
        /// batchmode 非 Play 模式下 Time.fixedDeltaTime 与 Physics.Simulate 的步长不一致，
        /// 直接把位移差除以 fixedDeltaTime 会算错桨速，进而算错水阻。
        /// </summary>
        public void Step(float dt)
        {
            if (!SimulationValue.Finite(dt) || dt <= 0f) return;
            InputValid = (!requireValidInput || (input != null && input.Sample.valid))
                && SimulationValue.Finite(transform.position) && SimulationValue.Finite(transform.rotation)
                && SimulationValue.Finite(bladeArea) && bladeArea > 0f
                && SimulationValue.Finite(bladeLength) && bladeLength > 0f
                && SimulationValue.Finite(bladeThickness) && bladeThickness > 0f
                && SimulationValue.Finite(waterDensity) && waterDensity > 0f
                && SimulationValue.Finite(cdFlat) && cdFlat >= 0f && SimulationValue.Finite(cdEdge) && cdEdge >= 0f
                && SimulationValue.Finite(maxFeedbackForceN) && maxFeedbackForceN >= 0f
                && SimulationValue.Finite(maxFeedbackTorqueNm) && maxFeedbackTorqueNm >= 0f;
            if (!InputValid)
            {
                ResetDynamics();
                FeedbackRequest = new ForceFeedbackRequest { commandSequence = ++_requestSequence, valid = false,
                    timeSeconds = input != null ? input.Sample.timeSeconds : 0 };
                return;
            }
            if (_wave == null) _wave = WaveField.Instance;
            SynchronizeVisualGeometry();
            Vector3 bladePos = transform.position;
            BladeVelocity = requireValidInput && input != null ? input.Sample.bladeVelocity
                : (_hasLastPos ? (bladePos - _lastBladePos) / dt : Vector3.zero);
            BladeAngularVelocity = requireValidInput && input != null ? input.Sample.bladeAngularVelocity
                : (_hasLastPos ? SimulationValue.AngularVelocity(_lastBladeRotation, transform.rotation, dt) : Vector3.zero);
            _lastBladePos = bladePos;
            _lastBladeRotation = transform.rotation;
            _hasLastPos = true;
            RawWaterForceN = RawTorqueAtBladeNm = BoatPropulsionForceN = BoatPropulsionTorqueNm = Vector3.zero;
            WettedAreaM2 = EffectiveDepth = RelativeSpeed = AttackAngleDeg = 0f;
            RawDissipativePowerW = 0f;
            WaterVelocity = Vector3.zero;

            // Thin rectangular prototype. The same area/length generates its visual width.
            // Each triangle is clipped against the locally sampled water surface; the
            // approximation is piecewise linear over this small blade, not centre-depth based.
            for (int y = 0; y <= GridY; y++)
                for (int x = 0; x <= GridX; x++)
                {
                    int i = y * (GridX + 1) + x;
                    _grid[i] = bladePos + transform.rotation * new Vector3(
                        BladeWidth * ((float)x / GridX - 0.5f), bladeLength * ((float)y / GridY - 0.5f), 0f);
                    _depths[i] = WaterAt(_grid[i]).height - _grid[i].y;
                    EffectiveDepth = Mathf.Max(EffectiveDepth, _depths[i]);
                }
            for (int y = 0; y < GridY; y++)
                for (int x = 0; x < GridX; x++)
                {
                    int a = y * (GridX + 1) + x, b = a + 1, c = a + GridX + 1, d = c + 1;
                    IntegrateClippedTriangle(a, b, d, bladePos);
                    IntegrateClippedTriangle(a, d, c, bladePos);
                }

            BladeInWater = WettedAreaM2 > 1e-9f;
            WaveInducedEntry = BladeInWater && !_wasInWater
                && BladeVelocity.sqrMagnitude < 0.05f && BladeAngularVelocity.sqrMagnitude < 0.05f;
            _wasInWater = BladeInWater;
            if (BladeInWater)
            {
                WaterVelocity /= WettedAreaM2;
                RelativeSpeed = Mathf.Sqrt(RelativeSpeed / WettedAreaM2);
                AttackAngleDeg /= WettedAreaM2;
            }

            // The distributed moment is added once, in addition to the lever-arm moment
            // created by AddForceAtPosition. Pure rotation can have F=0 and nonzero torque.
            if (applyPropulsion && targetBoat != null && targetBoat.BoatRb != null)
            {
                var rb = targetBoat.BoatRb;
                Vector3 centreMoment = BoatPropulsionTorqueNm;
                BoatPropulsionTorqueNm += Vector3.Cross(bladePos - rb.worldCenterOfMass, BoatPropulsionForceN);
                rb.AddForceAtPosition(BoatPropulsionForceN, bladePos, ForceMode.Force);
                rb.AddTorque(centreMoment, ForceMode.Force);
                // Legacy symmetric fixture remains only for the synthetic demonstration.
                // Real/external paddle motion retains its actual steering moment.
                bool synthetic = input == null || input.Sample.source == PaddleInputKind.Synthetic
                    || (input.Sample.source == PaddleInputKind.Replay
                        && input.Sample.originalSource == PaddleInputKind.Synthetic);
                if (synthetic && balanceYawRoll && balanceStrength > 0.001f)
                {
                    Vector3 local = targetBoat.transform.InverseTransformDirection(BoatPropulsionTorqueNm);
                    local.x = 0f;
                    Vector3 cancelled = targetBoat.transform.TransformDirection(local) * balanceStrength;
                    rb.AddTorque(-cancelled, ForceMode.Force);
                    BoatPropulsionTorqueNm -= cancelled;
                }
            }
            if (applyForceToRigidbody && _rb != null)
            {
                _rb.AddForce(RawWaterForceN, ForceMode.Force);
                _rb.AddTorque(RawTorqueAtBladeNm, ForceMode.Force);
            }
            Vector3 grip = bladePos + transform.up * (input != null ? input.shaftToBlade : 1.0f);
            Vector3 rawGripTorque = RawTorqueAtBladeNm + Vector3.Cross(bladePos - grip, RawWaterForceN);
            float scale = Mathf.Min(1f, maxFeedbackForceN / Mathf.Max(1e-6f, RawWaterForceN.magnitude),
                maxFeedbackTorqueNm / Mathf.Max(1e-6f, rawGripTorque.magnitude));
            if (!BladeInWater || (RawWaterForceN.sqrMagnitude < 1e-12f && RawTorqueAtBladeNm.sqrMagnitude < 1e-12f))
            {
                // No low-pass tail may survive dry contact or zero relative-water motion.
                WaterForceN = TorqueAtBladeNm = Vector3.zero;
                scale = 0f;
            }
            else
            {
                float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, smoothing));
                Vector3 targetForce = RawWaterForceN * scale;
                Vector3 targetMoment = RawTorqueAtBladeNm * scale;
                // This model is distributed, memoryless drag. Filtering signed
                // vectors preserves the previous stroke direction after reversal
                // and can turn water resistance into an active driving force.
                // Smooth only a common nonnegative wrench magnitude on increase;
                // release follows current drag. Keeping the current distributed
                // wrench direction scales its relative-flow dissipation without
                // suppressing work supplied by a moving wave/current in world space.
                float momentScale = 1f / Mathf.Max(1e-6f, bladeLength);
                float targetMagnitude = Mathf.Sqrt(targetForce.sqrMagnitude
                    + targetMoment.sqrMagnitude * momentScale * momentScale);
                float previousMagnitude = Mathf.Sqrt(WaterForceN.sqrMagnitude
                    + TorqueAtBladeNm.sqrMagnitude * momentScale * momentScale);
                float gain = targetMagnitude > 1e-9f
                    ? Mathf.Clamp01(Mathf.Lerp(previousMagnitude, targetMagnitude, k) / targetMagnitude) : 0f;
                WaterForceN = targetForce * gain;
                TorqueAtBladeNm = targetMoment * gain;
                scale *= gain;
            }
            // Retain the final output guard at the actual grip and scale the
            // complete wrench together, including its distributed blade moment.
            Vector3 gripTorque = TorqueAtBladeNm + Vector3.Cross(bladePos - grip, WaterForceN);
            float outputScale = Mathf.Min(1f, maxFeedbackForceN / Mathf.Max(1e-6f, WaterForceN.magnitude),
                maxFeedbackTorqueNm / Mathf.Max(1e-6f, gripTorque.magnitude));
            WaterForceN *= outputScale;
            TorqueAtBladeNm *= outputScale;
            gripTorque *= outputScale;
            CurrentDrag = WaterForceN.magnitude;
            FeedbackRequest = new ForceFeedbackRequest { commandSequence = ++_requestSequence,
                inputSequence = input != null ? input.Sample.sequence : 0,
                timeSeconds = input != null ? input.Sample.timeSeconds : 0,
                forceWorldN = WaterForceN, torqueAtGripWorldNm = gripTorque,
                dissipativePowerW = RawDissipativePowerW * scale * outputScale,
                gripWorldPosition = grip, valid = SimulationValue.Finite(WaterForceN)
                    && SimulationValue.Finite(TorqueAtBladeNm) && SimulationValue.Finite(gripTorque)
                    && SimulationValue.Finite(RawDissipativePowerW) };
        }

        void IntegrateClippedTriangle(int a, int b, int c, Vector3 centre)
        {
            int count = 0;
            ClipEdge(c, a, ref count); ClipEdge(a, b, ref count); ClipEdge(b, c, ref count);
            for (int i = 1; i + 1 < count; i++) IntegrateWetTriangle(_clipped[0], _clipped[i], _clipped[i + 1], centre);
        }

        void ClipEdge(int from, int to, ref int count)
        {
            bool fromWet = _depths[from] > 0f, toWet = _depths[to] > 0f;
            if (fromWet != toWet)
            {
                float t = _depths[from] / (_depths[from] - _depths[to]);
                _clipped[count++] = Vector3.LerpUnclamped(_grid[from], _grid[to], t);
            }
            if (toWet) _clipped[count++] = _grid[to];
        }

        void IntegrateWetTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 centre)
        {
            float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            if (area <= 1e-12f) return;
            WettedAreaM2 += area;
            // Three-point quadrature also retains the distributed rotational moment.
            IntegratePoint((4f * a + b + c) / 6f, area / 3f, centre);
            IntegratePoint((a + 4f * b + c) / 6f, area / 3f, centre);
            IntegratePoint((a + b + 4f * c) / 6f, area / 3f, centre);
        }

        void IntegratePoint(Vector3 p, float area, Vector3 centre)
        {
            Vector3 waterVelocity = WaterAt(p).velocity;
            Vector3 relative = BladeVelocity + Vector3.Cross(BladeAngularVelocity, p - centre) - waterVelocity;
            float speed = relative.magnitude;
            WaterVelocity += waterVelocity * area;
            RelativeSpeed += speed * speed * area;
            if (speed < 1e-6f) return;
            Vector3 direction = relative / speed;
            float sinA = Mathf.Clamp01(Mathf.Abs(Vector3.Dot(direction, transform.forward)));
            AttackAngleDeg += Mathf.Asin(sinA) * Mathf.Rad2Deg * area;
            float cd = cdFlat * sinA * sinA + cdEdge * (1f - sinA * sinA);
            Vector3 force = -direction * (0.5f * waterDensity * cd * area * speed * speed);
            RawDissipativePowerW += -Vector3.Dot(force, relative);
            RawWaterForceN += force;
            RawTorqueAtBladeNm += Vector3.Cross(p - centre, force);
            Vector3 propulsion = force * propulsionScale;
            propulsion.y *= propulsionVerticalDamp;
            BoatPropulsionForceN += propulsion;
            BoatPropulsionTorqueNm += Vector3.Cross(p - centre, propulsion);
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            Gizmos.color = BladeInWater ? Color.cyan : Color.gray;
            Gizmos.DrawSphere(transform.position, 0.06f);

            // 水阻力矢量：红色箭头。逆浪时明显变长，顺浪时明显缩短
            if (Application.isPlaying && WaterForceN.sqrMagnitude > 0.5f)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(transform.position, transform.position + WaterForceN * 0.02f);
                Gizmos.DrawSphere(transform.position + WaterForceN * 0.02f, 0.05f);
            }
        }
#endif
    }
}
