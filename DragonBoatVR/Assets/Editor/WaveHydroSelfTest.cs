using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// Gerstner 波场 + 桨叶水动力方向性自检。
    ///
    /// 验证三件事（对应工程规划里明确要求的验收点）：
    ///   A. Gerstner 参数曲面自洽：世界坐标反解回静止坐标的误差、法线归一化、法线朝上
    ///   B. 顺浪 / 逆浪阻力必须明显不同（这是"桨的力反馈能感受到浪"的唯一证据）
    ///   C. 浪抬升能让空中的桨被动入水（h_eff 生效：手没动，浪把桨淹了）
    ///
    /// 为什么必须有这项自检：
    ///   旧版 PaddleHydrodynamics 用各向同性常数阻力，浪的轨道速度虽参与计算，
    ///   但"顺浪轻、逆浪重"的差异小到测不出来 —— 联调时人会以为力反馈坏了。
    ///   这项自检把差异量化成数字，差值不达标就说明公式写错了。
    ///
    /// 用法：
    ///   Unity.exe -batchmode -nographics -quit -projectPath ... \
    ///     -executeMethod DragonBoatVR.EditorTools.WaveHydroSelfTest.Run -logFile wave.log
    /// 菜单：DragonBoatVR / ④ 波浪·桨力自检
    /// </summary>
    public static class WaveHydroSelfTest
    {
        const float Dt = 0.01f;      // 100Hz，比物理帧细，波形采样更准
        const float PaddleSpeed = 2.0f;

        [MenuItem("DragonBoatVR/④ 波浪·桨力自检（顺浪/逆浪差异）", priority = 3)]
        public static void Run()
        {
            // 自检不依赖 Demo 场景，临时新建空场景，避免被场景里的参数干扰
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            bool okA = TestGerstnerSampling();
            bool okB = TestWaveDirectionalDrag();
            bool okC = TestWaveInducedEntry();

            bool pass = okA && okB && okC;
            Debug.Log($"[WAVETEST] 判定 sampling={okA} directionalDrag={okB} waveEntry={okC}");
            Debug.Log(pass
                ? "[WAVETEST] RESULT=PASS Gerstner 波场与桨叶水动力方向性正常"
                : "[WAVETEST] RESULT=FAIL 见上面各项");
        }

        // ---------- A. Gerstner 采样自洽 ----------
        static bool TestGerstnerSampling()
        {
            var go = new GameObject("WaveField_Test");
            var wave = go.AddComponent<WaveField>();
            Call(wave, "Awake");
            SetSimTime(wave, 3.7f);

            float maxInvErr = 0f;
            float maxNormalErr = 0f;
            bool allUp = true;

            // 固定种子，结果可复现
            var rng = new System.Random(20261020);
            for (int i = 0; i < 2000; i++)
            {
                float wx = (float)(rng.NextDouble() * 200 - 100);
                float wz = (float)(rng.NextDouble() * 200 - 100);

                wave.InverseMap(wx, wz, out float x0, out float z0, out bool conv);
                if (!conv) { Debug.LogWarning($"[WAVETEST] 反解未收敛于 ({wx:F2},{wz:F2})"); }

                // 反解出的静止坐标再做正向位移，应该回到原世界点
                Vector3 d = wave.Displace(x0, z0);
                float ex = Mathf.Abs(x0 + d.x - wx);
                float ez = Mathf.Abs(z0 + d.z - wz);
                maxInvErr = Mathf.Max(maxInvErr, Mathf.Max(ex, ez));

                Vector3 n = wave.NormalAt(x0, z0);
                maxNormalErr = Mathf.Max(maxNormalErr, Mathf.Abs(n.magnitude - 1f));
                if (n.y <= 0.2f) allUp = false;
            }

            // 高度连续性：相邻 1cm 的高度差不应该跳变超过 1cm（波面是连续的）
            float maxJump = 0f;
            for (float x = -20f; x < 20f; x += 1f)
            {
                float h1 = wave.GetHeight(x, 0f);
                float h2 = wave.GetHeight(x + 0.01f, 0f);
                maxJump = Mathf.Max(maxJump, Mathf.Abs(h2 - h1));
            }

            bool okInv = maxInvErr < 0.005f;      // 反解误差 < 5mm
            bool okNrm = maxNormalErr < 1e-3f && allUp;
            bool okCont = maxJump < 0.01f;

            Debug.Log($"[WAVETEST] A 反解最大误差={maxInvErr * 1000f:F3}mm  法线模长误差={maxNormalErr:E2}  法线全朝上={allUp}");
            Debug.Log($"[WAVETEST] A 相邻1cm高度最大跳变={maxJump * 1000f:F2}mm");

            Object.DestroyImmediate(go);
            return okInv && okNrm && okCont;
        }

        // ---------- B. 顺浪 vs 逆浪阻力 ----------
        static bool TestWaveDirectionalDrag()
        {
            // 单一沿 +X 传播的波，桨沿 −X 拉（人向后划）。
            //
            // 参数为什么要这么挑（否则测出来的不是"浪的影响"）：
            //   桨必须放得够深，保证浪谷时也不出水 —— 一旦出水，阻力归零，
            //   比值直接变 0，看着像公式写错了，其实是工况设置错了。
            //   这里桨在静水面下 0.80m，波幅 0.25m，最浅也有 0.55m 浸深。
            //
            //   更关键的一点：水质点速度与波面高度是 90° 相位差
            //   （v ∝ cosθ，h ∝ sinθ）。所以顺浪和逆浪发生时 θ 都满足 sinθ=0，
            //   此刻浸深完全相同、浸湿面积完全相同 —— 阻力差异只可能来自速度，
            //   这正是我们要单独隔离出来的物理量。
            const float bladeY = -0.80f;

            var waveGo = new GameObject("WaveField_Drag");
            var wave = waveGo.AddComponent<WaveField>();
            wave.waves = new[]
            {
                new WaveField.WaveComponent
                {
                    direction = new Vector2(1f, 0f),
                    wavelength = 6f,
                    amplitude = 0.25f,
                    steepness = 0.60f,
                }
            };
            wave.intensity = 1f;
            Call(wave, "Awake");

            var paddleGo = new GameObject("Paddle_Test");
            paddleGo.transform.position = new Vector3(0f, bladeY, 0f);
            // 桨叶面法线朝 +X（transform.forward），桨沿 ±X 运动时是"正面击水"
            paddleGo.transform.rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            var rb = paddleGo.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.drag = 0f;
            rb.angularDrag = 0f;
            var hydro = paddleGo.AddComponent<PaddleHydrodynamics>();
            hydro.applyForceToRigidbody = false;
            hydro.applyPropulsion = false;
            // Compare the fluid force independently of the real actuator torque limit.
            // At a 1 m sample lever arm the default 150 Nm cap clips the 182.8 N
            // static-water reference before this hydrodynamics test can inspect it.
            hydro.maxFeedbackTorqueNm = 1000f;
            var input = paddleGo.AddComponent<PaddleDriver>();
            input.mode = PaddleDriver.Mode.External;
            hydro.requireValidInput = true; hydro.input = input;
            hydro.smoothing = 0.01f;   // 快速收敛，便于取极值
            // Keep the actual prototype blade entirely wet, isolating relative flow speed.
            Call(hydro, "Start");

            bool prevAuto = Physics.autoSimulation;
            Physics.autoSimulation = false;

            float fMax = 0f, fMin = float.MaxValue;
            float minDepth = float.MaxValue;
            double sim = 0;

            try
            {
                // 跑 6 秒：λ=6 → k=1.047, ω=3.20, T=1.96s，6s 覆盖 3 个周期
                for (int i = 0; i < 600; i++)
                {
                    sim += Dt;
                    SetSimTime(wave, (float)sim);

                    // 桨钉在原地，恒速沿 −X 拉
                    paddleGo.transform.position = new Vector3(0f, bladeY, 0f);
                    rb.velocity = new Vector3(-PaddleSpeed, 0f, 0f);
                    rb.angularVelocity = Vector3.zero;

                    input.SetExternalSample(new PaddleSample { sequence = (uint)i + 1, timeSeconds = sim,
                        bladePosition = paddleGo.transform.position, bladeRotation = paddleGo.transform.rotation,
                        bladeVelocity = new Vector3(-PaddleSpeed, 0, 0), valid = true });
                    input.Tick(Dt); hydro.Step(Dt);
                    Physics.Simulate(Dt);

                    if (i > 150)   // 跳过前 1.5 秒过渡
                    {
                        fMax = Mathf.Max(fMax, hydro.CurrentDrag);
                        fMin = Mathf.Min(fMin, hydro.CurrentDrag);
                        minDepth = Mathf.Min(minDepth, hydro.EffectiveDepth);
                    }
                }
            }
            finally
            {
                Physics.autoSimulation = prevAuto;
            }

            // 对照：关掉浪，静水阻力的理论值
            wave.intensity = 0f;
            SetSimTime(wave, 0f);
            input.ResetInputTimeline(); hydro.ResetDynamics();
            Physics.autoSimulation = false;
            float fCalm = 0f;
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    paddleGo.transform.position = new Vector3(0f, bladeY, 0f);
                    rb.velocity = new Vector3(-PaddleSpeed, 0f, 0f);
                    rb.angularVelocity = Vector3.zero;
                    input.SetExternalSample(new PaddleSample { sequence = (uint)i + 1, timeSeconds = (i + 1) * (double)Dt,
                        bladePosition = paddleGo.transform.position, bladeRotation = paddleGo.transform.rotation,
                        bladeVelocity = new Vector3(-PaddleSpeed, 0, 0), valid = true });
                    input.Tick(Dt); hydro.Step(Dt);
                    Physics.Simulate(Dt);
                    if (i > 60) fCalm = Mathf.Max(fCalm, hydro.CurrentDrag);
                }
            }
            finally
            {
                Physics.autoSimulation = prevAuto;
            }

            // 理论静水阻力：F = ½ρ·Cd·A_eff·v²
            float areaEff = hydro.bladeArea;
            float theory = 0.5f * 1000f * hydro.cdFlat * areaEff * PaddleSpeed * PaddleSpeed;

            float ratio = fMin > 0.01f ? fMax / fMin : 0f;
            bool okNoDry = minDepth > 0.05f;                        // 全程未出水
            bool okRatio = ratio > 1.15f;                           // 顺逆浪至少差 15%
            bool okCalmMid = fCalm > fMin * 0.98f && fCalm < fMax * 1.02f;  // 静水落在两者之间
            bool okTheory = Mathf.Abs(fCalm - theory) / Mathf.Max(1f, theory) < 0.15f;
            bool okPositive = fMin > 1f;

            Debug.Log($"[WAVETEST] B 桨速={PaddleSpeed:F1}m/s 桨叶y={bladeY:F2}m 最小浸深={minDepth * 100f:F1}cm 有效面积={areaEff * 10000f:F1}cm2");
            Debug.Log($"[WAVETEST] B 逆浪峰值={fMax:F1}N  顺浪谷值={fMin:F1}N  比值={ratio:F2}x  静水={fCalm:F1}N(理论 {theory:F1}N)");
            Debug.Log($"[WAVETEST] B 判定 noDry={okNoDry} ratio={okRatio} calmBetween={okCalmMid} theory={okTheory} positive={okPositive}");

            Object.DestroyImmediate(waveGo);
            Object.DestroyImmediate(paddleGo);
            return okNoDry && okRatio && okCalmMid && okTheory && okPositive;
        }

        // ---------- C. 浪把空中的桨淹没 ----------
        static bool TestWaveInducedEntry()
        {
            var waveGo = new GameObject("WaveField_Entry");
            var wave = waveGo.AddComponent<WaveField>();
            wave.waves = new[]
            {
                new WaveField.WaveComponent
                {
                    direction = new Vector2(1f, 0f),
                    wavelength = 8f,
                    amplitude = 0.45f,    // 大浪：峰谷差 0.9m
                    steepness = 0.5f,
                }
            };
            Call(wave, "Awake");

            // Bottom edge 9cm above calm water: centre depth alone is not a contact test.
            var paddleGo = new GameObject("Paddle_Air");
            paddleGo.transform.position = new Vector3(0f, 0.30f, 0f);
            paddleGo.transform.rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            var rb = paddleGo.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;
            var hydro = paddleGo.AddComponent<PaddleHydrodynamics>();
            hydro.applyForceToRigidbody = false;
            Call(hydro, "Start");

            // 静水对照
            wave.intensity = 0f;
            SetSimTime(wave, 0f);
            Call(hydro, "FixedUpdate");
            bool wetInCalm = hydro.BladeInWater;

            // 大浪：扫 4 秒看是否有入水时刻
            wave.intensity = 1f;
            bool wetInWave = false;
            float maxDepth = 0f;
            for (int i = 0; i < 400; i++)
            {
                SetSimTime(wave, i * 0.01f);
                Call(hydro, "FixedUpdate");
                if (hydro.BladeInWater) wetInWave = true;
                maxDepth = Mathf.Max(maxDepth, hydro.EffectiveDepth);
            }

            bool ok = !wetInCalm && wetInWave && maxDepth > 0.1f;

            Debug.Log($"[WAVETEST] C 静水入水={wetInCalm}(应为False)  大浪入水={wetInWave}  最大有效浸深={maxDepth * 100f:F1}cm");

            Object.DestroyImmediate(waveGo);
            Object.DestroyImmediate(paddleGo);
            return ok;
        }

        // ---------- 工具 ----------
        static void SetSimTime(WaveField wave, float t)
        {
            var f = typeof(WaveField).GetField("<SimTime>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            f?.SetValue(wave, t);
        }

        static void Call(object target, string method)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.LogWarning($"[WAVETEST] 找不到方法 {target.GetType().Name}.{method}");
                return;
            }
            m.Invoke(target, null);
        }
    }
}
