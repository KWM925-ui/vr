using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 龙舟刚体：六点浮力 + 各向异性船体水阻 + 供平台/UI 读取的 BoatState。
    ///
    /// 参数基准全部来自《龙舟VR仿真系统工程规划》的"工程全局与物理参数基准表"，
    /// 不要凭感觉改，尤其这三条：
    ///
    ///   1. 质量必须是 120~180kg（折算到单桨手的等效质量），
    ///      **绝不能按真实 22 人大龙舟设几吨**。单人单桨推力只有几十牛顿，
    ///      1500kg 的船算出来加速度 ≈ 0.02 m/s²，划一年也走不动 —— 这是最容易犯的错。
    ///
    ///   2. 浮力不能写成"刚度 × 浸深"再拿刚度去凑吃水。改成
    ///      "归一化浸深 × 单点最大浮力"，单点最大浮力 = mg·倍率/6。
    ///      好处：改质量不用重算刚度，且浮力天然有上限，不会数值爆掉把船弹飞。
    ///
    ///   3. 船体水阻必须各向异性。8m 长、1.2m 宽的龙舟，横向抗侧滑能力远大于纵向，
    ///      用一个各向同性的 drag，船会像在冰面上一样横着乱飘。
    ///      纵向二次方（25），横向线性且极大（350），偏航阻尼 120。
    ///
    /// 采样点必须贴船底（局部 y = -半高），否则"点在水下的深度"不等于吃水，船会沉穿。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class BoatBuoyancy : MonoBehaviour
    {
        [System.Serializable]
        public class BuoyancyPoint
        {
            /// <summary>船体本地坐标，**必须贴船底**（y = -船高/2）</summary>
            public Vector3 localOffset;
            [HideInInspector] public float lastDepth;
            [HideInInspector] public bool lastWet;
        }

        [Header("六点采样（基准表：8m×1.2m×0.6m 船体，y=-0.3 贴船底）")]
        public BuoyancyPoint[] points = new BuoyancyPoint[]
        {
            new BuoyancyPoint { localOffset = new Vector3(-0.50f, -0.3f,  3.0f) }, // 左前（艏）
            new BuoyancyPoint { localOffset = new Vector3( 0.50f, -0.3f,  3.0f) }, // 右前
            new BuoyancyPoint { localOffset = new Vector3(-0.60f, -0.3f,  0.0f) }, // 左中
            new BuoyancyPoint { localOffset = new Vector3( 0.60f, -0.3f,  0.0f) }, // 右中
            new BuoyancyPoint { localOffset = new Vector3(-0.50f, -0.3f, -3.0f) }, // 左后（艉）
            new BuoyancyPoint { localOffset = new Vector3( 0.50f, -0.3f, -3.0f) }, // 右后
        };

        [Header("浮力（基准表）")]
        [Tooltip("重心下移偏移，防止翻船。极其关键，不要设 0")]
        public Vector3 centerOfMassOffset = new Vector3(0f, -0.35f, 0f);

        [Tooltip("满吃水深度（m）。采样点入水超过此深度后浮力饱和")]
        public float maxSubmergenceDepth = 0.45f;

        [Tooltip("总最大浮力相对船重的倍率，推荐 2.2~3.0")]
        public float totalBuoyancyMultiplier = 2.5f;

        [Tooltip("垂直入水阻尼（N·s/m，六点均分）。抑制船体上下无休止震荡")]
        public float verticalWaterDamping = 450f;

        [Header("船体流线型各向异性水阻（基准表）")]
        [Tooltip("前进/后退方向水阻系数（二次方），较小以保证划桨推得动")]
        public float forwardDragCoeff = 25f;

        [Tooltip("左右横移方向水阻系数（线性），极大以模拟修长船身抗侧滑")]
        public float sidewaysDragCoeff = 350f;

        [Tooltip("偏航角速度阻尼系数，抑制多余的水平旋转")]
        public float yawAngularDragCoeff = 120f;

        [Header("波浪漂移")]
        [Tooltip("是否让波浪轨道速度推动船体（真实水动力）。关掉=船不会被浪推着走")]
        public bool applyWaveDrift = true;

        [Tooltip("每个采样点代表的湿面积（m²）。按本项目 3m 级等效小船取 0.35，六点合计约 2.1m²")]
        public float wetAreaPerPoint = 0.35f;

        [Tooltip("湿区附加阻力系数（只作用于横向，纵向阻力由 forwardDragCoeff 负责）")]
        public float hullDragCoefficient = 0.45f;

        [Header("无 WaveField 时的回退")]
        public float fallbackWaterY = 0f;

        [Header("状态输出（只读，供遥测/HUD/数字孪生读取）")]
        /// <summary>浸没采样点数 0~6</summary>
        public int submergedCount;
        /// <summary>平均吃水（m）</summary>
        public float draft;
        /// <summary>船位水面抬升（m），heave 遥测用</summary>
        public float waterElevation;
        /// <summary>当前航速（km/h）</summary>
        public float currentSpeedKmh;
        /// <summary>船体局部坐标系下的线加速度：x=Sway 右, y=Heave 上, z=Surge 前</summary>
        public Vector3 localLinearAcceleration;
        /// <summary>连续区间 [+180,−180] 的姿态角：x=Pitch, y=Yaw, z=Roll</summary>
        public Vector3 currentEulerAngles;

        public Rigidbody BoatRb { get; private set; }

        WaveField _wave;
        Vector3 _lastWorldVel;
        bool _hasLastVelocity;

        public BoatBuoyancyReplayState CaptureReplayState()
        {
            var state = new BoatBuoyancyReplayState
            {
                lastWorldVelocity = _lastWorldVel, hasLastVelocity = _hasLastVelocity,
                localLinearAcceleration = localLinearAcceleration, currentEulerAngles = currentEulerAngles,
                currentSpeedKmh = currentSpeedKmh, draft = draft, waterElevation = waterElevation,
                submergedCount = submergedCount,
                probeDepths = points == null ? null : new float[points.Length],
                probeWet = points == null ? null : new bool[points.Length]
            };
            if (points != null)
                for (int i = 0; i < points.Length; i++)
                {
                    DeviceReplayValue.Require(points[i] != null, "Missing buoyancy probe.");
                    state.probeDepths[i] = points[i].lastDepth;
                    state.probeWet[i] = points[i].lastWet;
                }
            ValidateReplayState(state);
            return state;
        }

        public void ValidateReplayState(BoatBuoyancyReplayState state)
        {
            DeviceReplayValue.Require(state != null && state.version == 1 && points != null && points.Length > 0,
                "Invalid initial buoyancy state.");
            DeviceReplayValue.Array(state.probeDepths, points.Length, "buoyancy probe depths");
            DeviceReplayValue.Require(state.probeWet != null && state.probeWet.Length == points.Length &&
                SimulationValue.Finite(state.lastWorldVelocity) && SimulationValue.Finite(state.localLinearAcceleration) &&
                SimulationValue.Finite(state.currentEulerAngles) && SimulationValue.Finite(state.currentSpeedKmh) &&
                state.currentSpeedKmh >= 0f && SimulationValue.Finite(state.draft) && state.draft >= 0f &&
                SimulationValue.Finite(state.waterElevation), "Nonfinite or malformed initial buoyancy state.");
            int wet = 0;
            for (int i = 0; i < points.Length; i++)
            {
                DeviceReplayValue.Require(points[i] != null && state.probeWet[i] == (state.probeDepths[i] > 0f),
                    "Inconsistent buoyancy probe state.");
                if (state.probeWet[i]) wet++;
            }
            DeviceReplayValue.Require(state.submergedCount == wet, "Inconsistent buoyancy submerged count.");
        }

        /// <summary>Restore after the rigidbody pose and velocity, before its next FixedUpdate.
        /// The derivative history is required even when the recorded boat velocity is restored.</summary>
        public void RestoreReplayState(BoatBuoyancyReplayState state)
        {
            ValidateReplayState(state);
            _lastWorldVel = state.lastWorldVelocity;
            _hasLastVelocity = state.hasLastVelocity;
            localLinearAcceleration = state.localLinearAcceleration;
            currentEulerAngles = state.currentEulerAngles;
            currentSpeedKmh = state.currentSpeedKmh;
            draft = state.draft;
            waterElevation = state.waterElevation;
            submergedCount = state.submergedCount;
            for (int i = 0; i < points.Length; i++)
            {
                points[i].lastDepth = state.probeDepths[i];
                points[i].lastWet = state.probeWet[i];
            }
        }

        void Awake()
        {
            BoatRb = GetComponent<Rigidbody>();
            BoatRb.centerOfMass = centerOfMassOffset;
        }

        void Start()
        {
            _wave = WaveField.Instance;
            if (_wave == null) Debug.LogWarning("[BoatBuoyancy] 场景中没有 WaveField，使用 fallbackWaterY。");

            Vector3 ls = transform.lossyScale;
            if (Mathf.Abs(ls.x - 1f) > 0.01f || Mathf.Abs(ls.y - 1f) > 0.01f || Mathf.Abs(ls.z - 1f) > 0.01f)
            {
                Debug.LogWarning(
                    $"[BoatBuoyancy] 船体缩放 ({ls.x:F2},{ls.y:F2},{ls.z:F2}) ≠ 1，采样点 localOffset 会被同比例放大。" +
                    "请把船的 Scale 设为 1，尺寸用网格/子物体表达。");
            }
        }

        internal bool KinematicsOwnedBySession { get; set; }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (points == null || points.Length == 0) return;

            // 单点最大浮力：总浮力 = 质量 × g × 倍率，六点均分
            float perPointMaxForce = BoatRb.mass * Mathf.Abs(Physics.gravity.y) * totalBuoyancyMultiplier / points.Length;
            float perPointDamping = verticalWaterDamping / points.Length;

            float submergenceSum = 0f;
            int wet = 0;
            float elevSum = 0f;
            float depthSum = 0f;
            Vector3 backgroundCurrent = _wave != null
                ? new Vector3(_wave.backgroundCurrentMps.x, 0f, _wave.backgroundCurrentMps.y)
                : Vector3.zero;

            for (int i = 0; i < points.Length; i++)
            {
                var p = points[i];
                Vector3 worldPos = transform.TransformPoint(p.localOffset);

                float waterY;
                Vector3 waveVel;
                if (_wave != null)
                {
                    var s = _wave.Sample(worldPos.x, worldPos.z);
                    waterY = s.height;
                    waveVel = s.velocity;
                    elevSum += s.elevation;
                }
                else
                {
                    waterY = fallbackWaterY;
                    waveVel = Vector3.zero;
                }

                float depth = waterY - worldPos.y;
                p.lastDepth = depth;
                p.lastWet = depth > 0f;
                if (depth <= 0f) continue;

                wet++;
                depthSum += depth;

                float submergence = Mathf.Clamp01(depth / Mathf.Max(0.01f, maxSubmergenceDepth));
                submergenceSum += submergence;

                Vector3 probeVel = BoatRb.GetPointVelocity(worldPos);
                Vector3 relVel = probeVel - waveVel;

                // 阿基米德浮力（竖直）+ 垂直速度阻尼（只抑制相对水的垂直运动）
                float buoyancy = perPointMaxForce * submergence;
                float damping = -relVel.y * perPointDamping;
                float totalUp = Mathf.Clamp(buoyancy + damping, 0f, perPointMaxForce * 1.4f);
                BoatRb.AddForceAtPosition(Vector3.up * totalUp, worldPos, ForceMode.Force);

                // 波浪漂移：水质点速度对船体的拖拽（侧浪推船就靠这一项）
                if (applyWaveDrift)
                {
                    float area = wetAreaPerPoint * submergence;
                    // Uniform river current is handled once by the anisotropic hull drag
                    // below. Keep this term for the oscillating wave motion only.
                    Vector3 swayAxis = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
                    float swaySpeed = Vector3.Dot(relVel + backgroundCurrent, swayAxis);
                    if (area > 1e-4f && Mathf.Abs(swaySpeed) > 1e-4f)
                    {
                        // A cross-flow strip resists sway. Project the velocity
                        // before taking its magnitude: forward speed/heave must
                        // not inflate sideways drag or add a second heave damper.
                        Vector3 lateralOnly = -0.5f * 1000f * hullDragCoefficient * area
                            * Mathf.Abs(swaySpeed) * swaySpeed * swayAxis;
                        BoatRb.AddForceAtPosition(lateralOnly, worldPos, ForceMode.Force);
                    }
                }
            }

            submergedCount = wet;
            draft = wet > 0 ? depthSum / wet : 0f;
            // Every probe contributes a surface sample, including dry probes.
            // Wave elevation describes the water and must not scale with immersion.
            waterElevation = elevSum / points.Length;

            float avgSubmergence = submergenceSum / points.Length;

            // ---- 船体各向异性水阻（必须投到船体局部轴）----
            if (avgSubmergence > 0.01f)
            {
                // Hull drag follows boat speed relative to the background river flow.
                // Paddle hydrodynamics already uses the same water-relative convention.
                Vector3 localVel = transform.InverseTransformDirection(BoatRb.velocity - backgroundCurrent);

                // 纵向：二次方阻力（高速时明显）
                float dragZ = -localVel.z * Mathf.Abs(localVel.z) * forwardDragCoeff * avgSubmergence;
                // 横向：线性且极大，模拟修长船身抗侧滑
                float dragX = -localVel.x * sidewaysDragCoeff * avgSubmergence;

                Vector3 worldDrag = transform.TransformDirection(new Vector3(dragX, 0f, dragZ));
                BoatRb.AddForce(worldDrag, ForceMode.Force);

                // 偏航阻尼
                // Roll and pitch are already damped by the pointwise vertical
                // water velocities. This coefficient is specifically for yaw.
                BoatRb.AddTorque(-Vector3.up * Vector3.Dot(BoatRb.angularVelocity, Vector3.up)
                                     * yawAngularDragCoeff * avgSubmergence,
                                 ForceMode.Force);
            }

            if (!KinematicsOwnedBySession) CaptureCompletedState(dt);
        }

        // The session calls this after PhysX, once per completed tick. Standalone
        // fixtures retain their existing FixedUpdate observation boundary.
        public void CaptureCompletedState(float dt)
        {
            if (BoatRb == null || !SimulationValue.Finite(dt) || dt <= 0f) return;
            // Differentiate in a fixed frame first: rotating body axes must not create fake acceleration.
            localLinearAcceleration = _hasLastVelocity && dt > 0
                ? Quaternion.Inverse(BoatRb.rotation) * ((BoatRb.velocity - _lastWorldVel) / dt) : Vector3.zero;
            _lastWorldVel = BoatRb.velocity;
            _hasLastVelocity = true;

            currentSpeedKmh = BoatRb.velocity.magnitude * 3.6f;
            Vector3 angles = BoatRb.rotation.eulerAngles;
            currentEulerAngles = new Vector3(
                NormalizeAngle(angles.x), NormalizeAngle(angles.y), NormalizeAngle(angles.z));
        }

        /// <summary>
        /// Unity 的 eulerAngles 在船头向下微倾 -1° 时会变成 +359°，
        /// 直接喂给六轴平台会让平台瞬间打满死区反转。必须折回连续区间。
        /// </summary>
        public static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            else if (angle < -180f) angle += 360f;
            return angle;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (points == null) return;
            foreach (var p in points)
            {
                Vector3 wp = transform.TransformPoint(p.localOffset);
                Gizmos.color = p.lastWet ? Color.red : Color.yellow;
                Gizmos.DrawSphere(wp, 0.08f);
                if (Application.isPlaying && p.lastDepth > 0f)
                    Gizmos.DrawLine(wp, wp + Vector3.up * p.lastDepth);
            }
        }
#endif
    }
}
