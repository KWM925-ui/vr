using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 引擎内物理自检：不进入 Play 模式，直接加载 Demo 场景，
    /// 手动驱动 BoatBuoyancy.FixedUpdate + PhysX 步进，跑 20 秒仿真，
    /// 量出稳态吃水、垂荡幅度、横摇/纵摇、航速，判定"不沉、不飞、不发散、划得动"。
    ///
    /// 吃水的理论值来自浮力模型本身，不是拍脑袋：
    ///   单点最大浮力 = m·g·倍率/6，六点合计在平衡时等于 m·g
    ///   ⇒ 归一化浸深 = 1/倍率 = 1/2.5 = 0.4
    ///   ⇒ 吃水 = 0.4 × 满浸深 0.45m = 0.18m
    /// 这个值与质量无关（质量在等式两边消掉了），所以改质量不用重算吃水 —— 这是
    /// "归一化浸深 × 倍数浮力"模型相对"刚度 × 浸深"模型的直接好处。
    ///
    /// 用法：
    ///   Unity.exe -batchmode -nographics -quit -projectPath ... \
    ///     -executeMethod DragonBoatVR.EditorTools.PhysicsSelfTest.Run -logFile selftest.log
    /// 菜单：DragonBoatVR / ② 物理自检
    /// </summary>
    public static class PhysicsSelfTest
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";
        const float Dt = 0.01f;          // 与 TimeManager 的 100Hz 一致
        const int Steps = 2000;          // 20 秒（前 8 秒过渡，后 12 秒统计稳态）

        [MenuItem("DragonBoatVR/② 物理自检（不沉·不飞·不发散·划得动）", priority = 1)]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            if (!scene.IsValid())
            {
                Debug.LogError("[SELFTEST] FAIL 无法打开场景 " + ScenePath);
                return;
            }

            var wave = Object.FindObjectOfType<WaveField>();
            var buoy = Object.FindObjectOfType<BoatBuoyancy>();
            var paddle = Object.FindObjectOfType<PaddleHydrodynamics>();
            var driver = Object.FindObjectOfType<PaddleDriver>();
            if (wave == null || buoy == null)
            {
                Debug.LogError($"[SELFTEST] FAIL 场景缺对象 wave={(wave != null)} buoy={(buoy != null)}");
                return;
            }

            var rb = buoy.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Debug.LogError("[SELFTEST] FAIL 船体缺 Rigidbody");
                return;
            }

            // 非 Play 模式下生命周期不自动跑，手动按顺序唤醒
            Call(wave, "Awake");
            Call(buoy, "Awake");
            Call(buoy, "Start");
            if (paddle != null) Call(paddle, "Start");
            if (driver != null) Call(driver, "Start");

            if (WaveField.Instance == null)
            {
                Debug.LogError("[SELFTEST] FAIL WaveField.Instance 未建立（Awake 未生效）");
                return;
            }

            var simTimeField = typeof(WaveField).GetField(
                "<SimTime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

            // 半高从碰撞盒读，别写死 —— 写死会在改船尺寸后静默算错吃水
            var box = buoy.GetComponent<BoxCollider>();
            float hullHalfHeight = box != null ? box.size.y * 0.5f : 0.3f;

            float expectedDraft = buoy.maxSubmergenceDepth / Mathf.Max(1.01f, buoy.totalBuoyancyMultiplier);

            float simTime = 0f;
            bool prevAuto = Physics.autoSimulation;
            Physics.autoSimulation = false;

            float yMin = float.MaxValue, yMax = float.MinValue;
            float rollMax = 0f, pitchMax = 0f, yawMax = 0f;
            float draftSum = 0f; int draftN = 0;
            float yFinal = 0f;
            int wetSteps = 0;
            float dragSum = 0f, dragPeak = 0f, speedSum = 0f, relVelSum = 0f;
            int speedN = 0;

            // 用"初始船头方向"投影算前进距离，而不是直接读 z。
            // 船一旦偏航，z 位移就不再等于沿船身前进的距离，会被低估或高估。
            Vector3 startPos = rb.position;
            Vector3 heading = rb.transform.forward;
            heading.y = 0f;
            if (heading.sqrMagnitude < 1e-6f) heading = Vector3.forward;
            heading.Normalize();
            float yawStart = Normalize(rb.rotation.eulerAngles.y);

            // 桨速靠位移差分，必须用和 Physics.Simulate 一致的步长，
            // 否则非 Play 模式下 Time.fixedDeltaTime 与 Dt 不等，桨速算错、水阻跟着错。
            if (paddle != null) paddle.dtOverride = Dt;

            try
            {
                for (int i = 0; i < Steps; i++)
                {
                    simTime += Dt;
                    if (simTimeField != null) simTimeField.SetValue(wave, simTime);

                    // 桨也必须跑，否则"划得动"这一项测不出来。
                    // 注意用 Tick(Dt) 注入时间步长：非 Play 模式下 Time.deltaTime 恒为 0，
                    // 直接调 Update() 相位不会推进，桨永远停在原点。
                    if (driver != null) driver.Tick(Dt);
                    if (paddle != null) paddle.Step(Dt);
                    Call(buoy, "FixedUpdate");
                    Physics.Simulate(Dt);

                    var pos = rb.position;
                    var euler = rb.rotation.eulerAngles;
                    float roll = Normalize(euler.z);
                    float pitch = Normalize(euler.x);

                    // 吃水用船底到水面，船底 = 中心 − 半高
                    float waterY = wave.GetHeight(pos.x, pos.z);
                    float draft = waterY - (pos.y - hullHalfHeight);

                    if (i > 800)   // 前 8 秒是过渡过程
                    {
                        yMin = Mathf.Min(yMin, pos.y);
                        yMax = Mathf.Max(yMax, pos.y);
                        rollMax = Mathf.Max(rollMax, Mathf.Abs(roll));
                        pitchMax = Mathf.Max(pitchMax, Mathf.Abs(pitch));
                        draftSum += draft; draftN++;

                        wetSteps += paddle != null && paddle.BladeInWater ? 1 : 0;
                        dragSum += paddle != null ? paddle.CurrentDrag : 0f;
                        if (paddle != null && paddle.CurrentDrag > dragPeak) dragPeak = paddle.CurrentDrag;
                        if (paddle != null) relVelSum += paddle.RelativeSpeed;

                        speedSum += new Vector3(rb.velocity.x, 0f, rb.velocity.z).magnitude;
                        speedN++;
                    }
                    // 偏航累计偏差：用来判断"单桨把船划得原地打转"
                    float yawNow = Normalize(rb.rotation.eulerAngles.y);
                    yawMax = Mathf.Max(yawMax, Mathf.Abs(Normalize(yawNow - yawStart)));
                    yFinal = pos.y;
                }
            }
            finally
            {
                Physics.autoSimulation = prevAuto;
            }

            float draftAvg = draftN > 0 ? draftSum / draftN : 0f;
            float heaveAmp = (yMax - yMin) * 0.5f;
            float mass = rb.mass;
            Vector3 disp = rb.position - startPos;
            disp.y = 0f;
            float advance = Vector3.Dot(disp, heading);
            float wetRatio = draftN > 0 ? (float)wetSteps / draftN : 0f;
            float dragAvg = draftN > 0 ? dragSum / draftN : 0f;
            float speedAvg = speedN > 0 ? speedSum / speedN : 0f;
            float relVelAvg = speedN > 0 ? relVelSum / speedN : 0f;

            bool okDraft = draftAvg > 0.02f && draftAvg < hullHalfHeight * 1.8f;
            bool okDraftTheory = Mathf.Abs(draftAvg - expectedDraft) < 0.06f;
            bool okHeave = heaveAmp < 0.30f;
            bool okRoll = rollMax < 35f;
            bool okPitch = pitchMax < 35f;
            bool okPos = !float.IsNaN(yFinal) && yFinal > -1f && yFinal < 3f;
            bool okMove = driver == null || advance > 2f;   // 20 秒至少前进 2 米
            bool okWet = paddle == null || wetRatio > 0.15f; // 桨确实入水过
            bool okYaw = yawMax < 25f;                       // 两舷配平后不该明显打转

            bool pass = okDraft && okDraftTheory && okHeave && okRoll && okPitch && okPos && okMove && okWet && okYaw;

            Debug.Log($"[SELFTEST] mass={mass:F0}kg  半高={hullHalfHeight:F2}m  steps={Steps}  sim={simTime:F1}s  fixedDt={Time.fixedDeltaTime:F3}");
            Debug.Log($"[SELFTEST] 稳态吃水={draftAvg * 100:F1}cm  理论={expectedDraft * 100:F1}cm  偏差={Mathf.Abs(draftAvg - expectedDraft) * 100:F1}cm");
            Debug.Log($"[SELFTEST] 垂荡振幅={heaveAmp * 100:F1}cm  y范围=[{yMin:F3},{yMax:F3}]  末态y={yFinal:F3}");
            Debug.Log($"[SELFTEST] 横摇峰值={rollMax:F2}°  纵摇峰值={pitchMax:F2}°  偏航={yawMax:F2}°");
            Debug.Log($"[SELFTEST] 桨：入水占比={wetRatio * 100:F0}%  平均水阻={dragAvg:F1}N  峰值={dragPeak:F1}N  平均相对流速={relVelAvg:F2}m/s");
            Debug.Log($"[SELFTEST] 船：平均航速={speedAvg:F2}m/s  前进距离={advance:F1}m  (驱动={(driver != null ? driver.mode.ToString() : "无")})");
            Debug.Log($"[SELFTEST] 判定 draft={okDraft} draftTheory={okDraftTheory} heave={okHeave} roll={okRoll} pitch={okPitch} pos={okPos} wet={okWet} yaw={okYaw} move={okMove}");
            Debug.Log(pass
                ? "[SELFTEST] RESULT=PASS 船体能浮、能摇、能划走，可进入联调"
                : "[SELFTEST] RESULT=FAIL 参数需调整（见上面各项）");
        }

        static void Call(object target, string method)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.LogWarning($"[SELFTEST] 找不到方法 {target.GetType().Name}.{method}");
                return;
            }
            m.Invoke(target, null);
        }

        /// <summary>把 0..360 折到 -180..180，避免 359° 被当成大角度</summary>
        static float Normalize(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            return deg;
        }
    }
}
