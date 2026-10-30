using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 场景中的软件设备约束检查。独立合成最终张力，验证方向/力矩残差与能力标记。
    /// 默认三绳示例能力不足；约束与残差报告正确不等于完整水阻保真度通过。
    /// </summary>
    public static class TwinsSelfTest
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";
        const float Dt = 0.01f;
        const int Steps = 600;      // 6 秒
        const int WarmupSteps = 150; // 先给浮力与波场 1.5 秒进入过渡后的采样区

        [MenuItem("DragonBoatVR/⑥ 执行机构孪生自检（约束与残差报告）", priority = 6)]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            if (!scene.IsValid())
            {
                Debug.LogError("[TWIN] FAIL 打不开场景");
                return;
            }

            var wave = UnityEngine.Object.FindObjectOfType<WaveField>();
            var buoy = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            var paddle = UnityEngine.Object.FindObjectOfType<PaddleHydrodynamics>();
            var driver = UnityEngine.Object.FindObjectOfType<PaddleDriver>();
            var cable = UnityEngine.Object.FindObjectOfType<CableDriveTwin>();
            var stewart = UnityEngine.Object.FindObjectOfType<StewartPlatformTwin>();

            if (wave == null || buoy == null || paddle == null || driver == null || cable == null || stewart == null)
            {
                Debug.LogError($"[TWIN] FAIL 组件缺失 wave={wave != null} buoy={buoy != null} " +
                               $"paddle={paddle != null} driver={driver != null} cable={cable != null} stewart={stewart != null}");
                return;
            }

            Call(wave, "Awake");
            Call(buoy, "Awake");
            Call(buoy, "Start");
            // Mirror SimulationSession.Initialize: in production the fixed-step owner
            // binds this input and requires a fresh, valid sample before solving water force.
            driver.boat = buoy;
            driver.externallyStepped = true;
            driver.mode = PaddleDriver.Mode.VirtualPaddler;
            driver.strokesPerMinute = 45f;
            driver.ResetInputTimeline();
            paddle.input = driver;
            paddle.targetBoat = buoy;
            paddle.requireValidInput = true;
            paddle.externallyStepped = true;
            paddle.ResetDynamics();
            Call(driver, "Start");
            Call(paddle, "Start");
            // This is a numeric plant test, not a render/collider test. Avoid generating
            // edit-mode display primitives whose deferred Destroy would leave colliders
            // in the physics scene and push the boat away from the water.
            cable.showCables = false;
            cable.showMotorUnits = false;
            stewart.autoBuildModel = false;
            string rigPath = System.IO.Path.Combine(Application.streamingAssetsPath, "tendon_rig.json");
            if (!System.IO.File.Exists(rigPath)) throw new System.IO.FileNotFoundException("Missing explicit software rig configuration.", rigPath);
            cable.ApplyRig(TendonRigProfiles.CadDesignRequested ? TendonRigProfiles.LoadCadDesign()
                : JsonUtility.FromJson<TendonRigConfig>(System.IO.File.ReadAllText(rigPath)));
            Debug.Log($"[TWIN] 配置={(TendonRigProfiles.CadDesignRequested ? TendonRigProfiles.CadFileName : "tendon_rig.json")} " +
                $"measured={cable.rig.measured} geometryAssumed={cable.rig.geometryAssumed}");
            Call(cable, "Start");
            Call(stewart, "Start");

            paddle.dtOverride = Dt;

            var simField = typeof(WaveField).GetField("<SimTime>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var previousMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;

            float simTime = 0f;
            float allocSum = 0f, allocMin = 1e9f, allocMax = 0f;
            float errSum = 0f, errMax = 0f;
            float forceRestSum = 0f, forceRestMin = 2f, rewSum = 0f;
            int allocN = 0;

            float rollErrSum = 0f, rollErrMax = 0f;
            float pitchErrSum = 0f, pitchErrMax = 0f;
            int syncN = 0;

            float legMin = 1e9f, legMax = 0f;
            float tMax = 0f;
            bool cableContract = true, platformContract = true;
            bool diagnosticCoordinates = CheckDiagnosticAttachmentCoordinates(out string diagnosticFailure);
            if (!diagnosticCoordinates) Debug.LogError("[TWIN] DIAGNOSTIC_COORDINATES_FAILURE " + diagnosticFailure);
            int limitedCableSamples = 0;
            int validInputSamples = 0, wetBladeSamples = 0;
            float maxWettedArea = 0f, maxRawForce = 0f, maxFilteredForce = 0f;
            int firstWetStep = -1, lastWetStep = -1;
            Vector3 firstWetPosition = Vector3.zero, lastWetPosition = Vector3.zero;
            float firstWetDepth = 0f, lastWetDepth = 0f;

            try
            {
                for (int i = 0; i < Steps; i++)
                {
                    simTime += Dt;
                    if (simField != null) simField.SetValue(wave, simTime);

                    if (driver != null) driver.Tick(Dt);
                    paddle.Step(Dt);
                    // Freeze the geometry that produced this request, before advancing PhysX.
                    Matrix4x4 observationBoat = buoy.transform.localToWorldMatrix;
                    Vector3 observationBlade = paddle.BladePosition;
                    Quaternion observationRotation = paddle.transform.rotation;
                    if (driver.Sample.valid) validInputSamples++;
                    if (paddle.BladeInWater)
                    {
                        wetBladeSamples++;
                        if (firstWetStep < 0)
                        {
                            firstWetStep = i; firstWetPosition = paddle.BladePosition; firstWetDepth = paddle.EffectiveDepth;
                        }
                        lastWetStep = i; lastWetPosition = paddle.BladePosition; lastWetDepth = paddle.EffectiveDepth;
                    }
                    maxWettedArea = Mathf.Max(maxWettedArea, paddle.WettedAreaM2);
                    maxRawForce = Mathf.Max(maxRawForce, paddle.RawWaterForceN.magnitude);
                    maxFilteredForce = Mathf.Max(maxFilteredForce, paddle.WaterForceN.magnitude);
                    Call(buoy, "FixedUpdate");
                    Physics.Simulate(Dt);

                    // Keep the wrench, grip and cable geometry at the same observation time.
                    // The completed boat state still drives the platform below.
                    cable.ReceiveAtPose(paddle.FeedbackRequest, Dt,
                        observationBoat, observationBlade, observationRotation);

                    // 六轴孪生：姿态 → 六缸长度（需要注入 dt）
                    stewart.Pump(Dt);
                    bool currentCableContract = CheckCableFeedback(cable, paddle.FeedbackRequest,
                        observationBoat, observationBlade, observationRotation, out string cableFailure);
                    if (cableContract && !currentCableContract)
                        Debug.LogError($"[TWIN] FIRST_CABLE_CONTRACT_FAILURE step={i} simTime={simTime:R} {cableFailure}");
                    cableContract &= currentCableContract;
                    platformContract &= CheckPlatformFeedback(stewart);
                    if (cable.Feedback.capabilityLimited) limitedCableSamples++;

                    if (i < WarmupSteps) continue;

                    // ---- 力分配：只在真正有水阻时统计，否则 0/0 无意义 ----
                    float wf = paddle.WaterForceN.magnitude;
                    if (wf > 5f)
                    {
                        allocSum += cable.allocationRatio;
                        if (cable.allocationRatio < allocMin) allocMin = cable.allocationRatio;
                        if (cable.allocationRatio > allocMax) allocMax = cable.allocationRatio;
                        errSum += cable.allocationErrorN;
                        if (cable.allocationErrorN > errMax) errMax = cable.allocationErrorN;

                        // 方向还原率才是手感保真度的真正指标：
                        // allocationRatio 只比"大小"，力大小对了但方向偏了它看不出来。
                        forceRestSum += cable.forceRestorationRate;
                        rewSum += cable.restorationRate;
                        if (cable.forceRestorationRate < forceRestMin) forceRestMin = cable.forceRestorationRate;
                        allocN++;
                    }

                    for (int k = 0; k < cable.motorTensionsN.Length; k++)
                        if (cable.motorTensionsN[k] > tMax) tMax = cable.motorTensionsN[k];

                    // ---- 六缸行程 ----
                    for (int k = 0; k < stewart.actuatorLengthsMm.Length; k++)
                    {
                        float L = stewart.actuatorLengthsMm[k];
                        if (L < legMin) legMin = L;
                        if (L > legMax) legMax = L;
                    }

                    // 跟随误差相对已经限幅的平台目标；另外保留原始船体目标供反馈/记录。
                    float boatRoll = stewart.Request.eulerDegrees.z;
                    float boatPitch = stewart.Request.eulerDegrees.x;
                    float dRoll = Mathf.Abs(Mathf.DeltaAngle(boatRoll, stewart.actualEuler.z));
                    float dPitch = Mathf.Abs(Mathf.DeltaAngle(boatPitch, stewart.actualEuler.x));

                    rollErrSum += dRoll; if (dRoll > rollErrMax) rollErrMax = dRoll;
                    pitchErrSum += dPitch; if (dPitch > pitchErrMax) pitchErrMax = dPitch;
                    syncN++;
                }
            }
            finally
            {
                Physics.simulationMode = previousMode;
            }

            float allocAvg = allocN > 0 ? allocSum / allocN : 0f;
            float errAvg = allocN > 0 ? errSum / allocN : 0f;
            float rollErrAvg = syncN > 0 ? rollErrSum / syncN : 0f;
            float pitchErrAvg = syncN > 0 ? pitchErrSum / syncN : 0f;

            // ---- 判定 ----
            bool okSamples = allocN > 50 && syncN > 50;
            // 保留最初数值精度门槛并加入矢量方向/6D残差，作为单独的保真度结果。
            // 不要求能力不足的三绳 fixture 实现任意6D，但必须正确报告未达到目标。
            bool fidelity = allocAvg > 0.95f && allocAvg < 1.05f && errAvg < 8f &&
                forceRestSum / Mathf.Max(1, allocN) >= 0.95f && forceRestMin >= 0.95f &&
                rewSum / Mathf.Max(1, allocN) >= 0.95f;
            bool okSync = rollErrAvg < 3f && pitchErrAvg < 3f; // 平台跟随误差 < 3°

            bool pass = okSamples && cableContract && platformContract && okSync && diagnosticCoordinates;

            Debug.Log($"[TWIN] 三绳：样本={allocN}  分配比 均={allocAvg:F3} 区间=[{allocMin:F3},{allocMax:F3}]");
            Debug.Log($"[TWIN] 输入/入水：valid={validInputSamples}/{Steps} wet={wetBladeSamples}/{Steps} " +
                $"wetAreaMax={maxWettedArea * 10000f:F1}cm2 rawForceMax={maxRawForce:F1}N filteredForceMax={maxFilteredForce:F1}N " +
                $"mode={driver.mode} source={driver.Sample.source} requireValidInput={paddle.requireValidInput} inputBound={paddle.input == driver}");
            Debug.Log($"[TWIN] 入水区间：first={firstWetStep} pos={firstWetPosition} depth={firstWetDepth:F3}m " +
                $"last={lastWetStep} pos={lastWetPosition} depth={lastWetDepth:F3}m waveY={wave.BaseHeight:F3} boatY={buoy.transform.position.y:F3}");
            Debug.Log($"[TWIN] 三绳：残差 均={errAvg:F2}N 峰值={errMax:F2}N  最大单绳张力={tMax:F1}N (上限{cable.maxTensionN})");
            Debug.Log($"[TWIN] 三绳：方向还原率 力={forceRestSum / Mathf.Max(1, allocN):F3}(最差{forceRestMin:F3})  " +
                      $"力+力矩={rewSum / Mathf.Max(1, allocN):F3}  ← 这个才是手感保真度");
            Debug.Log($"[TWIN] 当前独立绳路={cable.tendonCount} capabilityLimitedSamples={limitedCableSamples}; " +
                $"FIDELITY={(fidelity ? "PASS" : "FAIL")} (magnitude + force vector + normalized wrench residual)");
            Debug.Log($"[TWIN] 六缸：行程=[{legMin:F0},{legMax:F0}] mm  当前={Legs(stewart)}");
            Debug.Log($"[TWIN] 同步：横摇误差 均={rollErrAvg:F2}° 峰={rollErrMax:F2}°  纵摇误差 均={pitchErrAvg:F2}° 峰={pitchErrMax:F2}°");
            Debug.Log($"[TWIN] 判定 samples={okSamples} cableConstraintAndResidual={cableContract} " +
                $"platformGeometryAndLimits={platformContract} sync={okSync} diagnosticAttachmentCoordinates={diagnosticCoordinates}");
            Debug.Log(pass
                ? "[TWIN] RESULT=PASS software constraints and capability/residual reporting; see separate FIDELITY result"
                : "[TWIN] RESULT=FAIL 机构层有偏差（见上面各项）");
        }

        static bool CheckDiagnosticAttachmentCoordinates(out string failure)
        {
            try
            {
                // Conflicting cached centres expose the old parser's failure to use tip coordinates.
                // These expected centres are independent of the production BuildSpecs conversion.
                var raw = new TendonRigConfig {
                    attachmentCoordinatesFromBladeTip = true, bladeTipLocal = new Vector3(0f, -0.21f, 0f),
                    tendons = new[] {
                        new TendonSpec { motorId = 1, attachLocal = new Vector3(4f, 5f, 6f),
                            attachmentFromBladeTip = new Vector3(0f, 0.46f, 0f) },
                        new TendonSpec { motorId = 2, attachLocal = new Vector3(4f, 5f, 6f),
                            attachmentFromBladeTip = new Vector3(-0.08f, 0.01f, 0f) },
                        new TendonSpec { motorId = 3, attachLocal = new Vector3(4f, 5f, 6f),
                            attachmentFromBladeTip = new Vector3(0.08f, 0.01f, 0f) }
                    }
                };
                Vector3[] expected = { new Vector3(0f, 0.25f, 0f), new Vector3(-0.08f, -0.20f, 0f),
                    new Vector3(0.08f, -0.20f, 0f) };
                var parsed = TendonRigDiagnostic.ParseConfig(JsonUtility.ToJson(raw));
                var repeated = TendonRigDiagnostic.ParseConfig(JsonUtility.ToJson(parsed));
                bool tip = true, idempotent = true;
                for (int i = 0; i < expected.Length; i++)
                {
                    tip &= (parsed.tendons[i].attachLocal - expected[i]).magnitude < 0.000001f;
                    idempotent &= (repeated.tendons[i].attachLocal - expected[i]).magnitude < 0.000001f;
                }
                raw.attachmentCoordinatesFromBladeTip = false;
                var legacy = TendonRigDiagnostic.ParseConfig(JsonUtility.ToJson(raw));
                bool centre = true;
                for (int i = 0; i < expected.Length; i++) centre &= legacy.tendons[i].attachLocal == raw.tendons[i].attachLocal;
                failure = $"tipCoordinates={tip} centreCoordinates={centre} repeatedLoad={idempotent} " +
                    $"shaftExpected={expected[0]} shaftActual={parsed.tendons[0].attachLocal}";
                return tip && centre && idempotent;
            }
            catch (Exception e) { failure = e.ToString(); return false; }
        }

        static bool CheckCableFeedback(CableDriveTwin cable, ForceFeedbackRequest request,
            Matrix4x4 observationBoat, Vector3 observationBlade, Quaternion observationRotation, out string failure)
        {
            failure = null;
            var feedback = cable.Feedback;
            if (feedback == null) { failure = "stage=missing_feedback"; return false; }
            string identity = $"seq={request.commandSequence}/{feedback.commandSequence} time={request.timeSeconds:R}/{feedback.timeSeconds:R} " +
                $"requestValid={request.valid} feedbackValid={feedback.valid} simulated={feedback.simulated} status={feedback.status}";
            if (!feedback.simulated || feedback.commandSequence != request.commandSequence || feedback.timeSeconds != request.timeSeconds)
            { failure = "stage=identity " + identity; return false; }
            if (!request.valid)
            {
                bool zero = !feedback.valid && cable.modeledForceWorldN == Vector3.zero;
                if (!zero) failure = "stage=invalid_request_zero_output " + identity + $" force={cable.modeledForceWorldN}";
                return zero;
            }
            if (!feedback.valid) { failure = "stage=unexpected_rejection " + identity; return false; }
            var specs = new System.Collections.Generic.List<TendonSpec>();
            if (cable.rig != null)
            {
                foreach (var spec in cable.rig.tendons) if (spec != null && spec.enabled) specs.Add(spec);
            }
            else
            {
                for (int i = 0; i < cable.anchorLocal.Length; i++) specs.Add(new TendonSpec {
                    motorId = i + 1, anchorLocal = cable.anchorLocal[i], attachLocal = Vector3.zero,
                    preTensionN = cable.preTensionN, maxTensionN = cable.maxTensionN });
            }
            if (specs.Count != cable.motorTensionsN.Length)
            { failure = $"stage=tendon_count expected={specs.Count} actual={cable.motorTensionsN.Length} " + identity; return false; }
            Vector3 torque = Vector3.zero;
            double forceX = 0d, forceY = 0d, forceZ = 0d;
            for (int i = 0; i < specs.Count; i++)
            {
                var spec = specs[i]; float tension = cable.motorTensionsN[i];
                if (!SimulationValue.Finite(tension) || tension < spec.preTensionN - 0.001f ||
                    tension > spec.maxTensionN + 0.001f)
                { failure = $"stage=tension index={i} value={tension:R} limits={spec.preTensionN:R}/{spec.maxTensionN:R} " + identity; return false; }
                Vector3 attach = observationBlade + observationRotation * spec.attachLocal;
                Vector3 displacement = observationBoat.MultiplyPoint3x4(spec.anchorLocal) - attach;
                float expectedLength = displacement.magnitude * 1000f;
                if (Mathf.Abs(expectedLength - cable.cableLengthsMm[i]) > 0.01f)
                { failure = $"stage=length index={i} expected={expectedLength:R} actual={cable.cableLengthsMm[i]:R} " + identity; return false; }
                Vector3 direction = displacement.normalized;
                // Reconstruct independently from geometry and final commands. Accumulate before
                // rounding to the reported float vector; per-cable float rounding is amplified
                // by the restoration metric's small target denominator.
                forceX += (double)direction.x * tension;
                forceY += (double)direction.y * tension;
                forceZ += (double)direction.z * tension;
                Vector3 cableForce = direction * tension;
                torque += Vector3.Cross(attach - request.gripWorldPosition, cableForce);
            }
            Vector3 force = new Vector3((float)forceX, (float)forceY, (float)forceZ);
            float forceResidual = (force - request.forceWorldN).magnitude;
            float torqueResidual = (torque - request.torqueAtGripWorldNm).magnitude;
            float magnitude = request.forceWorldN.magnitude;
            // The published restoration metric defines its near-zero branch with squared norms < 1e-9.
            // The independent oracle must use that same definition; its direction-angle threshold is separate.
            // Keep the scalar evaluation boundary of the published float metric. Constructing
            // an intermediate residual Vector3 rounds each subtraction before squaring; at a
            // small target norm that extra rounding can exceed the unchanged 0.002 tolerance.
            Vector3 targetForce = request.forceWorldN;
            float targetNormSquared = targetForce.x * targetForce.x + targetForce.y * targetForce.y + targetForce.z * targetForce.z;
            float residualNormSquared = (force.x - targetForce.x) * (force.x - targetForce.x)
                + (force.y - targetForce.y) * (force.y - targetForce.y)
                + (force.z - targetForce.z) * (force.z - targetForce.z);
            float restoration = targetNormSquared < 1e-9f
                ? (residualNormSquared < 1e-9f ? 1f : 0f)
                : 1f - Mathf.Sqrt(residualNormSquared / targetNormSquared);
            float angle = magnitude > 1e-5f ? (force.magnitude > 1e-5f ? Vector3.Angle(force, request.forceWorldN) : 180f) : 0f;
            bool limited = forceResidual > 0.01f || torqueResidual > 0.01f;
            float forceDelta = (force - feedback.forceWorldN).magnitude;
            float torqueDelta = (torque - feedback.torqueAtGripWorldNm).magnitude;
            float forceResidualDelta = Mathf.Abs(forceResidual - feedback.forceResidualN);
            float torqueResidualDelta = Mathf.Abs(torqueResidual - feedback.torqueResidualNm);
            float restorationDelta = Mathf.Abs(restoration - cable.forceRestorationRate);
            float angleDelta = Mathf.Abs(angle - cable.directionErrorDegrees);
            bool result = forceDelta < 0.002f && torqueDelta < 0.002f && forceResidualDelta < 0.002f &&
                torqueResidualDelta < 0.002f && restorationDelta < 0.002f && angleDelta < 0.05f &&
                feedback.capabilityLimited == limited && (!limited || feedback.status.Contains("CAPABILITY_LIMITED"));
            if (!result)
                failure = "stage=composition_and_metrics " + identity +
                    $" forceDelta={forceDelta:R} torqueDelta={torqueDelta:R} forceResidualDelta={forceResidualDelta:R} torqueResidualDelta={torqueResidualDelta:R}" +
                    $" targetNorm2={targetNormSquared:R} residualNorm2={residualNormSquared:R} restorationExpected={restoration:R} restorationActual={cable.forceRestorationRate:R} restorationDelta={restorationDelta:R}" +
                    $" angleExpected={angle:R} angleActual={cable.directionErrorDegrees:R} angleDelta={angleDelta:R} limitedExpected={limited} limitedActual={feedback.capabilityLimited}";
            return result;
        }

        static bool CheckPlatformFeedback(StewartPlatformTwin platform)
        {
            var feedback = platform.Feedback; var request = platform.Request;
            if (feedback == null || !feedback.valid || !feedback.simulated || feedback.commandSequence != request.commandSequence ||
                feedback.timeSeconds != request.timeSeconds) return false;
            for (int i = 0; i < 6; i++)
            {
                Vector3 bottom, top;
                if (platform.useExplicitJointGeometry)
                { bottom = platform.explicitBaseJointsLocal[i]; top = platform.explicitTopJointsLocal[i]; }
                else
                {
                    float baseAngle = i * 60f * Mathf.Deg2Rad, topAngle = (i * 60f + 30f) * Mathf.Deg2Rad;
                    bottom = new Vector3(Mathf.Cos(baseAngle), 0f, Mathf.Sin(baseAngle)) * platform.baseRadius;
                    top = new Vector3(Mathf.Cos(topAngle), 0f, Mathf.Sin(topAngle)) * platform.topRadius;
                }
                float length = (Vector3.up * platform.neutralHeight + feedback.translationM +
                    Quaternion.Euler(feedback.eulerDegrees) * top - bottom).magnitude * 1000f;
                if (!SimulationValue.Finite(length) || length < platform.minActuatorLengthsMm[i] || length > platform.maxActuatorLengthsMm[i] ||
                    Mathf.Abs(length - feedback.actuatorLengthsMm[i]) > 0.01f) return false;
            }
            return feedback.capabilityLimited == (request.clamped || platform.kinematicRank < 6);
        }

        static string Legs(StewartPlatformTwin s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.actuatorLengthsMm.Length; i++)
                sb.Append($"{(i > 0 ? "/" : "")}{s.actuatorLengthsMm[i]:F0}");
            return sb.ToString() + " mm";
        }

        static void Call(object target, string method)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.LogWarning($"[TWIN] 找不到方法 {target.GetType().Name}.{method}");
                return;
            }
            m.Invoke(target, null);
        }

        static float Normalize(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            return deg;
        }
    }
}
