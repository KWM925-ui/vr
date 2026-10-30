using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// Gerstner 波浪场 —— 全系统唯一的水面真相源。
    ///
    /// 为什么必须是 Gerstner 而不是 sin：
    ///   1. sin 波只有垂直起伏，浪尖圆钝、没有水平挤压，看上去像"湖面抖动"；
    ///   2. sin 波的水质点速度与波面坡度不同源，桨叶阻力算出来对不上画面；
    ///   3. Gerstner 有水平位移 Q·A·cos，能形成尖峰缓谷，并且提供解析法向量，
    ///      船体才能被"侧浪推"而不是只被上下颠。
    ///
    /// 同源铁律：本文件的 Gerstner 公式与 WaveFieldWater.shader 中 vert() 的公式
    /// 必须逐项一致（k、ω、Q、θ 的符号都不能差）。改一处必须改另一处。
    ///
    /// 坐标约定：采样输入是世界水平坐标 (x,z)，但 Gerstner 的水平位移会让
    /// "世界点"与"静止点"错开，因此所有采样都先用牛顿迭代反解静止坐标。
    /// </summary>
    public class WaveField : MonoBehaviour
    {
        [HideInInspector] public bool externallyStepped;
        public void Step(float dt) { if (SimulationValue.Finite(dt) && dt > 0) SimTime += dt * timeScale; }
        public void SetTime(float seconds) { SimTime = Mathf.Max(0, seconds); }
        [System.Serializable]
        public class WaveComponent
        {
            [Tooltip("波传播方向（水平面内，脚本会自动归一化）")]
            public Vector2 direction = new Vector2(1f, 0.3f);

            [Tooltip("波长（米）。河道训练场景以 3~24m 的小尺度波为主")]
            public float wavelength = 8f;

            [Tooltip("波幅（米，单侧幅度；峰谷高 = 2×amplitude）")]
            public float amplitude = 0.05f;

            [Tooltip("初相位偏移（弧度）")]
            public float phaseOffset = 0f;

            [Tooltip("陡度 Q（0~1）。0=纯正弦，越大浪尖越尖。所有波的 Q·k·A 之和须 ≤1，否则波形自交")]
            [Range(0f, 1f)] public float steepness = 0.30f;
        }

        [Header("波分量（类默认=历史自检基线;场景谱由向导序列化覆盖）")]
        public WaveComponent[] waves = new WaveComponent[]
        {
            // SimulationChainSelfTest 以类默认自建会话,24h 的通过基线建立在这组
            // 原版 3 波上;改默认会平移回放确定性感(2026-10-25 实测)。
            new WaveComponent { direction = new Vector2(1f, 0.2f),   wavelength = 12f,  amplitude = 0.18f, steepness = 0.55f },
            new WaveComponent { direction = new Vector2(0.7f, 1f),   wavelength = 6.5f, amplitude = 0.10f, steepness = 0.50f },
            new WaveComponent { direction = new Vector2(-0.4f, 1f),  wavelength = 3.2f, amplitude = 0.05f, steepness = 0.40f },
        };

        [Tooltip("全局浪强倍率（0=镜面水）")]
        [Range(0f, 2f)] public float intensity = 1f;

        [HideInInspector] public float intensityRatePerSecond;
        // Background river flow is independent of oscillating wave-particle velocity.
        // Components are world X and world Z, in metres per second.
        public Vector2 backgroundCurrentMps;

        [Tooltip("时间倍率，演示时加速/减速浪")]
        [Range(0f, 3f)] public float timeScale = 1f;

        [Tooltip("水平位移总开关。关掉相当于退回 sin 波（仅排障用）")]
        public bool horizontalDisplacement = true;

        [Tooltip("牛顿反解迭代次数。3 次在 Q·k·A<0.3 时误差 <1mm")]
        [Range(1, 6)] public int inverseIterations = 3;

        /// <summary>共享时间：所有采样必须用同一时间源，否则同帧内各点不自洽</summary>
        public float SimTime { get; private set; }

        /// <summary>一次采样拿到的完整水面信息（浮力/桨阻/HUD 都用这个）</summary>
        public struct SurfaceSample
        {
            /// <summary>波面绝对高度（世界 Y）</summary>
            public float height;
            /// <summary>波面法线（世界空间，已归一化，y 恒为正）</summary>
            public Vector3 normal;
            /// <summary>水质点运动速度（世界空间，含垂直分量）</summary>
            public Vector3 velocity;
            /// <summary>该点相对静水面的抬升量（= height − baseHeight），用于 heave 遥测</summary>
            public float elevation;
            /// <summary>是否成功反解（false 表示迭代未收敛，结果仅供兜底）</summary>
            public bool converged;
        }

        /// <summary>静水面基准高度（本物体世界 Y）</summary>
        public float BaseHeight => transform.position.y;

        void Update()
        {
            if (!externallyStepped) Step(Time.deltaTime);
        }

        // ---------------- 核心：单个波分量的相位 ----------------
        float Phase(in WaveComponent w, Vector2 dir, float k, float x0, float z0)
        {
            // ω = sqrt(g·k) 深水色散关系
            float omega = Mathf.Sqrt(9.81f * k);
            return k * Vector2.Dot(dir, new Vector2(x0, z0)) - omega * SimTime + w.phaseOffset;
        }

        /// <summary>
        /// 静止坐标 (x0,z0) → 波面位移矢量。
        /// dx = Σ Q·A·D.x·cosθ,  dz = Σ Q·A·D.z·cosθ,  dy = Σ A·sinθ
        /// </summary>
        public Vector3 Displace(float x0, float z0)
        {
            Vector3 d = Vector3.zero;
            if (waves == null) return d;

            for (int i = 0; i < waves.Length; i++)
            {
                var w = waves[i];
                if (w.amplitude <= 0f || w.wavelength <= 0f) continue;

                Vector2 dir = w.direction.sqrMagnitude > 1e-6f ? w.direction.normalized : Vector2.right;
                float k = 2f * Mathf.PI / w.wavelength;
                float amp = w.amplitude * intensity;
                float q = horizontalDisplacement ? w.steepness : 0f;

                float theta = Phase(w, dir, k, x0, z0);
                float c = Mathf.Cos(theta);

                d.x += q * amp * dir.x * c;
                d.z += q * amp * dir.y * c;
                d.y += amp * Mathf.Sin(theta);
            }
            return d;
        }

        /// <summary>
        /// 世界水平坐标 → 静止坐标反解。
        /// Gerstner 是有水平位移的参数曲面，给定世界 (x,z) 不能直接得到高度，
        /// 必须解 x0 + Dx(x0,z0) = x, z0 + Dz(x0,z0) = z。用定点迭代（压缩映射）收敛很快。
        /// </summary>
        public void InverseMap(float x, float z, out float x0, out float z0, out bool ok)
        {
            x0 = x; z0 = z;
            int iters = Mathf.Max(1, inverseIterations);
            float err = 0f;
            for (int i = 0; i < iters; i++)
            {
                Vector3 d = Displace(x0, z0);
                float nx = x - d.x;
                float nz = z - d.z;
                err = Mathf.Abs(nx - x0) + Mathf.Abs(nz - z0);
                x0 = nx; z0 = nz;
                if (err < 1e-4f) break;
            }
            // 位移量本身很小（<0.6m），误差超过 0.5m 视为不收敛
            ok = err < 0.5f;
        }

        /// <summary>
        /// 解析法向量。对参数曲面 P(x0,z0) 求两个偏导再叉乘。
        /// 平静水面时 dPdx=(1,0,0)、dPdz=(0,0,1)，cross(dPdz,dPdx)=(0,1,0) 朝上。
        /// </summary>
        public Vector3 NormalAt(float x0, float z0)
        {
            float dxx = 1f, dxy = 0f, dxz = 0f;
            float dzx = 0f, dzy = 0f, dzz = 1f;

            if (waves != null)
            {
                for (int i = 0; i < waves.Length; i++)
                {
                    var w = waves[i];
                    if (w.amplitude <= 0f || w.wavelength <= 0f) continue;

                    Vector2 dir = w.direction.sqrMagnitude > 1e-6f ? w.direction.normalized : Vector2.right;
                    float k = 2f * Mathf.PI / w.wavelength;
                    float amp = w.amplitude * intensity;
                    float q = horizontalDisplacement ? w.steepness : 0f;

                    float theta = Phase(w, dir, k, x0, z0);
                    float s = Mathf.Sin(theta);
                    float c = Mathf.Cos(theta);

                    float kA = k * amp;
                    float qkA = q * kA;

                    // ∂P/∂x0
                    dxx -= qkA * dir.x * dir.x * s;
                    dxy += kA * dir.x * c;
                    dxz -= qkA * dir.x * dir.y * s;

                    // ∂P/∂z0
                    dzx -= qkA * dir.x * dir.y * s;
                    dzy += kA * dir.y * c;
                    dzz -= qkA * dir.y * dir.y * s;
                }
            }

            Vector3 dPdx = new Vector3(dxx, dxy, dxz);
            Vector3 dPdz = new Vector3(dzx, dzy, dzz);
            Vector3 n = Vector3.Cross(dPdz, dPdx);
            if (n.sqrMagnitude < 1e-8f) return Vector3.up;
            n.Normalize();
            if (n.y < 0f) n = -n;   // 保证朝上
            return n;
        }

        /// <summary>Wave-particle velocity, including the current time scale and
        /// operator transition amplitude derivative. Background river flow is separate.</summary>
        public Vector3 FlowVelocityAt(float x0, float z0)
        {
            Vector3 v = Vector3.zero;
            if (waves == null) return v;

            for (int i = 0; i < waves.Length; i++)
            {
                var w = waves[i];
                if (w.amplitude <= 0f || w.wavelength <= 0f) continue;

                Vector2 dir = w.direction.sqrMagnitude > 1e-6f ? w.direction.normalized : Vector2.right;
                float k = 2f * Mathf.PI / w.wavelength;
                float omega = Mathf.Sqrt(9.81f * k);
                float amp = w.amplitude * intensity;
                float ampRate = w.amplitude * intensityRatePerSecond;
                float q = horizontalDisplacement ? w.steepness : 0f;

                float theta = Phase(w, dir, k, x0, z0);
                float s = Mathf.Sin(theta);
                float c = Mathf.Cos(theta);

                float Aomega = amp * omega * timeScale;
                float horizontalRate = ampRate * c + Aomega * s;
                v.x += q * horizontalRate * dir.x;
                v.z += q * horizontalRate * dir.y;
                v.y += ampRate * s - Aomega * c;
            }
            return v;
        }

        // ---------------- 对外统一入口 ----------------

        /// <summary>一次采样拿全（推荐：浮力/桨阻每帧多处调用，避免重复反解）</summary>
        public SurfaceSample Sample(float worldX, float worldZ)
        {
            InverseMap(worldX, worldZ, out float x0, out float z0, out bool ok);
            var s = new SurfaceSample
            {
                height = BaseHeight + Displace(x0, z0).y,
                normal = NormalAt(x0, z0),
                velocity = FlowVelocityAt(x0, z0) + new Vector3(backgroundCurrentMps.x, 0f, backgroundCurrentMps.y),
                converged = ok,
            };
            s.elevation = s.height - BaseHeight;
            return s;
        }

        /// <summary>水面绝对高度（世界 Y）。兼容旧调用。</summary>
        public float GetHeight(float x, float z) => Sample(x, z).height;

        /// <summary>水面法线（世界空间）。兼容旧调用。</summary>
        public Vector3 GetNormal(float x, float z) => Sample(x, z).normal;

        /// <summary>水质点速度。兼容旧调用。</summary>
        public Vector3 GetFlowVelocity(float x, float z) => Sample(x, z).velocity;

        /// <summary>静态便捷访问：场景里应只有一个 WaveField</summary>
        public static WaveField Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[WaveField] 场景中存在多个 WaveField，保留先创建的那个。");
                Destroy(this);
                return;
            }
            Instance = this;
            ValidateSteepness();
        }

        /// <summary>陡度自检：Σ Q·k·A 必须 ≤ 1，超过会产生自交（浪尖翻卷成环）</summary>
        public void ValidateSteepness()
        {
            if (waves == null) return;
            float sum = 0f;
            foreach (var w in waves)
            {
                if (w.amplitude <= 0f || w.wavelength <= 0f) continue;
                sum += w.steepness * (2f * Mathf.PI / w.wavelength) * w.amplitude * intensity;
            }
            if (sum > 0.95f)
            {
                Debug.LogWarning($"[WaveField] 陡度总和 Σ(Q·k·A)={sum:F2} > 0.95，波形会自交出现破浪。" +
                                 "请降低任一分量的 steepness 或 amplitude。");
            }
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            const float t = 0f;
            for (float x = -10; x <= 10; x += 2f)
            {
                for (float z = -10; z <= 10; z += 2f)
                {
                    float wx = transform.position.x + x;
                    float wz = transform.position.z + z;

                    // 编辑态用 Time.realtimeSinceStartup 代替 SimTime，只为可视化
                    float saved = SimTime;
                    if (!Application.isPlaying) SimTime = Time.realtimeSinceStartup;

                    InverseMap(wx, wz, out float x0, out float z0, out _);
                    Vector3 d = Displace(x0, z0);
                    Vector3 p = new Vector3(wx + d.x * 0f, transform.position.y + d.y, wz);

                    SimTime = saved;
                    Gizmos.DrawSphere(p, 0.08f);
                    // 额外画一小段法线，直观看出浪面倾斜方向
                    Gizmos.DrawLine(p, p + NormalAt(x0, z0) * 0.5f);
                }
            }
            _ = t;
        }
#endif
    }
}
