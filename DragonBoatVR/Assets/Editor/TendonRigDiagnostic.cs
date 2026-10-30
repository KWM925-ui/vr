using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 在明确标注的名义桨姿态和 100 N 目标下诊断独立绳路。
    /// 128 个球面方向是抽样；矩阵秩、覆盖率都不能证明任意姿态的六维力旋量能力。
    /// 使用配置预紧/上限，共享电机绳路因耦合关系未知而拒绝求解。
    /// </summary>
    public static class TendonRigDiagnostic
    {
        const string ConfigAssetPath = "Assets/StreamingAssets/tendon_rig.json";

        [MenuItem("DragonBoatVR/⑦ 绳驱布置诊断（受限张力与残差）", priority = 7)]
        public static void Run()
        {
            var cfg = LoadOrCreateConfig();
            if (cfg == null || cfg.tendons == null || cfg.tendons.Length == 0)
            {
                Debug.LogError("[TENDON] 配置为空，检查 " + ConfigAssetPath);
                return;
            }

            var sb = new StringBuilder();
            void W(string s) { Debug.Log(s); sb.AppendLine(s); }

            if (!SimulationValue.Finite(cfg.charLengthM) || cfg.charLengthM <= 0f)
            {
                Debug.LogError("[TENDON] RESULT=FAIL invalid characteristic length");
                return;
            }
            var enabled = new System.Collections.Generic.List<TendonSpec>();
            foreach (var spec in cfg.tendons)
            {
                if (spec == null) { Debug.LogError("[TENDON] RESULT=FAIL null tendon"); return; }
                if (spec.enabled) enabled.Add(spec);
            }
            cfg.tendons = enabled.ToArray();
            if (cfg.tendons.Length == 0) { Debug.LogError("[TENDON] RESULT=FAIL no enabled tendons"); return; }
            float charLen = cfg.charLengthM;
            var lower = new float[cfg.tendons.Length];
            var upper = new float[cfg.tendons.Length];
            for (int i = 0; i < cfg.tendons.Length; i++)
            {
                var spec = cfg.tendons[i];
                if (!SimulationValue.Finite(spec.anchorLocal) || !SimulationValue.Finite(spec.attachLocal) ||
                    !SimulationValue.Finite(spec.preTensionN) || !SimulationValue.Finite(spec.maxTensionN) ||
                    spec.preTensionN < 0f || spec.maxTensionN < spec.preTensionN || spec.motorId <= 0 ||
                    (spec.anchorLocal - Blade - spec.attachLocal).sqrMagnitude < 1e-8f)
                { Debug.LogError("[TENDON] RESULT=FAIL invalid tendon geometry or tension limits"); return; }
                lower[i] = spec.preTensionN; upper[i] = spec.maxTensionN;
            }

            // 明确的名义姿态快照；实时姿态改变绳方向，本结果只对应此快照。
            var cols = BuildColumns(cfg.tendons, charLen);
            int n = cfg.tendons.Length;
            int attachN = CountDistinctAttach(cfg.tendons);
            int motorN = CountMotors(cfg.tendons);

            W($"[TENDON] ══ 绳驱布置诊断 ══");
            W($"[TENDON] 腱={n}  电机={motorN}  拴点={attachN}  力矩尺度={charLen:F2}m");
            W($"[TENDON] CONFIG={(TendonRigProfiles.CadDesignRequested ? TendonRigProfiles.CadFileName : "tendon_rig.json")} " +
                $"measured={cfg.measured} geometryAssumed={cfg.geometryAssumed}; provenance={cfg.provenance}");
            W($"[TENDON] ASSUMED_SNAPSHOT blade={Blade} rotation=identity grip={Grip}; target force=100 N; " +
                "fixed diagnostic pose, not a recorded paddling trajectory or measured CAD installation");
            if (motorN != n)
            {
                W("[TENDON] RESULT=UNSUPPORTED_SHARED_MOTOR_COUPLING no independent-tendon allocation or coverage claim");
                return;
            }

            int rank = NumericalRank(cols);
            W($"[TENDON] 抓取矩阵数值秩 = {rank} / 6" +
              "；只描述线性自由度，不证明非负、有界张力可实现任意目标。");

            // ---- 128 方向覆盖 ----
            var cov = Coverage(cols, charLen, lower, upper);
            float sumR = cov.mean, worst = cov.worst, worstDirAng = cov.worstAngle;
            int ge95 = cov.ge95, ge90 = cov.ge90, lt50 = cov.lt50, total = cov.total;

            W($"[TENDON] 方向还原率（{total} 个球面抽样方向，有预紧和上限）");
            W($"[TENDON]   平均={sumR:F3}  最差={worst:F3}");
            W($"[TENDON]   ≥0.95 占 {ge95 * 100f / total:F0}%   ≥0.90 占 {ge90 * 100f / total:F0}%   <0.50 占 {lt50 * 100f / total:F0}%");

            // ---- 六个基本方向 ----
            var axes = new (Vector3 dir, string label)[]
            {
                (Vector3.right,   "+X 右舷外"),
                (Vector3.left,    "-X 左舷内"),
                (Vector3.up,      "+Y 向上"),
                (Vector3.down,    "-Y 向下"),
                (Vector3.forward, "+Z 船首"),
                (Vector3.back,    "-Z 船尾"),
            };

            W($"[TENDON] 六个基本方向：");
            var t = new float[n];
            var sum = new float[TendonRig.Dim];
            var target = new float[TendonRig.Dim];

            float axisWorst = 2f; string axisWorstLabel = "";
            foreach (var a in axes)
            {
                float r = Probe(a.dir, cols, charLen, t, sum, target, lower, upper);
                Vector3 actualForce = new Vector3(sum[0], sum[1], sum[2]);
                Vector3 wantedForce = a.dir * ProbeForceN;
                Vector3 actualTorque = new Vector3(sum[3], sum[4], sum[5]) * charLen;
                Vector3 wantedTorque = Vector3.Cross(Blade - Grip, wantedForce);
                float angle = actualForce.magnitude > 1e-5f ? Vector3.Angle(actualForce, wantedForce) : 180f;
                W($"[TENDON]   {a.label} restoration={r:F3} forceResidual={(actualForce - wantedForce).magnitude:F2}N " +
                    $"torqueResidual={(actualTorque - wantedTorque).magnitude:F2}Nm directionError={angle:F1}deg");
                if (r < axisWorst) { axisWorst = r; axisWorstLabel = a.label; }
            }

            // ---- 结论 ----
            W($"[TENDON] 最弱方向 = {axisWorstLabel}（{axisWorst:F3}），全向最差 {worst:F3}（方位角 {worstDirAng:F0}°）");
            W($"[TENDON] 物理解释：绳子只能拉不能推，N 根绳能合成的力是 {n} 个方向的非负张成锥。");
            W($"[TENDON]   三根独立绳不能正张成整个三维空间；同一拴点会产生握把力矩，但力与力矩不能独立指定。");
            W($"[TENDON]   实际能力还受姿态、预紧、张力上限与电机耦合影响；抽样不等于全方向保证。");

            // ---- 补绳建议 ----
            const bool performAssumedGeometrySearch = false;
            if (performAssumedGeometrySearch && (rank < 6 || worst < 0.90f))
            {
                W($"[TENDON] 补绳搜索：在候选出绳口网格里找'加一根最划算'的位置…");
                var searchDirs = CoarseDirections();
                var best = SearchExtraTendon(cfg.tendons, charLen, searchDirs,
                                             out float bestWorst, out float bestMean, out float bestAt);
                if (best.HasValue)
                {
                    var newTendons = new System.Collections.Generic.List<TendonSpec>(cfg.tendons);
                    newTendons.Add(new TendonSpec
                    {
                        name = $"T{n + 1}",
                        motorId = n + 1,
                        anchorLocal = best.Value,
                        attachLocal = new Vector3(0f, ShaftToBlade - bestAt, 0f),
                    });
                    var cov2 = Coverage(BuildColumns(newTendons.ToArray(), charLen), charLen);

                    W($"[TENDON] 建议加第 {n + 1} 根绳，出绳口放在 ({best.Value.x:F2}, {best.Value.y:F2}, {best.Value.z:F2}) m（船体坐标）");
                    W($"[TENDON]   拴点：距握把 {bestAt:F2} m（attachLocal = (0, {ShaftToBlade - bestAt:F2}, 0)）");
                    W($"[TENDON]   加完后的 128 向覆盖：平均 {cov.mean:F3} → {cov2.mean:F3}，" +
                      $"最差 {worst:F3} → {cov2.worst:F3}，≥0.90 占比 {ge90 * 100f / total:F0}% → {cov2.ge90 * 100f / cov2.total:F0}%");
                    if (cov2.mean < 0.99f)
                        W($"[TENDON]   注：单加一根仍不到满覆盖，剩下的是'多点拴'能补的部分 —— 见数据清单");
                }
            }
            else
            {
                W("[TENDON] 未自动提出新增电机/绳路拓扑；需已知实际耦合和安装约束后再评价候选几何。");
            }
            W("[TENDON] RESULT=DIAGNOSTIC bounded nominal-snapshot residuals; hardware capability unverified");

            // ---- 落盘 ----
            string path = Path.Combine(Application.dataPath, "../../tools/tendon_report.txt");
            try
            {
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Debug.Log("[TENDON] 报告已写入 " + Path.GetFullPath(path));
            }
            catch (Exception e) { Debug.LogWarning("[TENDON] 报告写入失败: " + e.Message); }
        }

        // ─────────────────────── 几何 ───────────────────────

        // 桨叶在船体坐标系里的名义位置（PaddleDriver 的 pivot + sideOffset 的静止值）。
        // 诊断是静态快照，忽略桨的实时旋转；拴点全在桨叶中心时这个近似是精确的。
        const float BladeX = 1.55f, BladeY = -0.55f, BladeZ = 0.20f;
        const float ShaftToBlade = 1.35f;
        const float ProbeForceN = 100f;

        static Vector3 Blade => new Vector3(BladeX, BladeY, BladeZ);
        static Vector3 Grip => Blade + new Vector3(0f, ShaftToBlade, 0f);

        /// <summary>
        /// 注意坐标系：anchorLocal 在**船体**坐标，attachLocal 在**桨体**坐标（原点=桨叶中心）。
        /// 两者不能直接相减 —— 早先这里混用过，结果三根绳的方向全部反向，
        /// 诊断出"最弱方向 ±X 正好搞反"的假结论。
        /// </summary>
        static float[][] BuildColumns(TendonSpec[] tendons, float charLen)
        {
            int n = tendons.Length;
            var cols = new float[n][];
            for (int i = 0; i < n; i++) cols[i] = new float[TendonRig.Dim];

            Vector3 blade = Blade, grip = Grip;

            for (int i = 0; i < n; i++)
            {
                Vector3 attach = blade + tendons[i].attachLocal;
                Vector3 v = tendons[i].anchorLocal - attach;
                Vector3 dir = v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.up;
                TendonRig.BuildColumn(dir, attach - grip, charLen, cols[i]);
            }
            return cols;
        }

        static float Probe(Vector3 dir, float[][] cols, float charLen,
                           float[] t, float[] sum, float[] target, float[] lower = null, float[] upper = null)
        {
            Vector3 bladeArm = Blade - Grip;
            TendonRig.BuildTarget(dir * ProbeForceN, bladeArm, charLen, target);
            if (lower == null || upper == null) TendonRig.Solve(cols, target, t, 160);
            else if (!TendonRig.SolveBounded(cols, target, t, lower, upper, 160))
                throw new InvalidOperationException("Invalid bounded diagnostic input");
            if (!TendonRig.TryCompose(cols, t, sum)) throw new InvalidOperationException("Invalid diagnostic composition");
            return TendonRig.Restoration(sum, target);
        }

        static (float mean, float worst, float worstAngle, int ge95, int ge90, int lt50, int total)
            Coverage(float[][] cols, float charLen, float[] lower = null, float[] upper = null)
        {
            var directions = TendonRig.SphereDirections(128);
            var tensions = new float[cols.Length];
            var sum = new float[TendonRig.Dim];
            var target = new float[TendonRig.Dim];
            float mean = 0f, worst = float.PositiveInfinity, angle = 0f;
            int ge95 = 0, ge90 = 0, lt50 = 0;
            foreach (var direction in directions)
            {
                float restoration = Probe(direction, cols, charLen, tensions, sum, target, lower, upper);
                mean += restoration;
                if (restoration < worst)
                {
                    worst = restoration;
                    angle = Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg;
                }
                if (restoration >= 0.95f) ge95++;
                if (restoration >= 0.90f) ge90++;
                if (restoration < 0.50f) lt50++;
            }
            return (mean / directions.Length, worst, angle, ge95, ge90, lt50, directions.Length);
        }

        static (Vector3 dir, string label)[] CoarseDirections()
        {
            var directions = TendonRig.SphereDirections(24);
            var result = new (Vector3 dir, string label)[directions.Length + 6];
            for (int i = 0; i < directions.Length; i++) result[i] = (directions[i], "sphere " + i);
            result[directions.Length] = (Vector3.right, "+X");
            result[directions.Length + 1] = (Vector3.left, "-X");
            result[directions.Length + 2] = (Vector3.up, "+Y");
            result[directions.Length + 3] = (Vector3.down, "-Y");
            result[directions.Length + 4] = (Vector3.forward, "+Z");
            result[directions.Length + 5] = (Vector3.back, "-Z");
            return result;
        }

        static int CountDistinctAttach(TendonSpec[] tendons)
        {
            int c = 0;
            var seen = new System.Collections.Generic.List<Vector3>();
            foreach (var t in tendons)
            {
                bool dup = false;
                foreach (var s in seen)
                    if ((s - t.attachLocal).sqrMagnitude < 1e-6f) { dup = true; break; }
                if (!dup) { seen.Add(t.attachLocal); c++; }
            }
            return c;
        }

        static int CountMotors(TendonSpec[] tendons)
        {
            var ids = new System.Collections.Generic.HashSet<int>();
            foreach (var t in tendons) ids.Add(t.motorId);
            return ids.Count;
        }

        /// <summary>修正 Gram-Schmidt 求数值秩（列数 ≤ 16，6 维）</summary>
        static int NumericalRank(float[][] cols)
        {
            int n = cols.Length;
            var basis = new float[TendonRig.Dim][];
            int r = 0;

            foreach (var c in cols)
            {
                var v = (float[])c.Clone();
                for (int i = 0; i < r; i++)
                {
                    float dot = 0f;
                    for (int k = 0; k < TendonRig.Dim; k++) dot += v[k] * basis[i][k];
                    for (int k = 0; k < TendonRig.Dim; k++) v[k] -= dot * basis[i][k];
                }
                float norm = 0f;
                for (int k = 0; k < TendonRig.Dim; k++) norm += v[k] * v[k];
                norm = Mathf.Sqrt(norm);
                if (norm < 1e-3f) continue;      // 这一列和已有列线性相关
                for (int k = 0; k < TendonRig.Dim; k++) v[k] /= norm;
                basis[r++] = v;
                if (r >= TendonRig.Dim) break;
            }
            return r;
        }

        /// <summary>
        /// 贪心搜索：在固定网格上试一根额外的绳，取"六向最差还原率最高"的位置。
        /// 目标是 max-min，不是平均值 —— 木桶最短那块板才决定手感。
        /// </summary>
        static Vector3? SearchExtraTendon(TendonSpec[] tendons, float charLen,
                                          (Vector3 dir, string label)[] axes,
                                          out float bestWorst, out float bestMean, out float bestAttachM)
        {
            bestWorst = -1f; bestMean = 0f; bestAttachM = 0f;
            Vector3? bestAnchor = null;

            var cands = new System.Collections.Generic.List<TendonSpec>(tendons);

            float[] xs = { 0.2f, 0.6f, 1.0f, 1.4f, 1.8f, 2.3f, 2.8f, 3.2f };
            float[] ys = { -1.6f, -1.1f, -0.6f, -0.1f, 0.4f, 0.9f, 1.4f };
            float[] zs = { -2.4f, -1.8f, -1.2f, -0.6f, 0f, 0.6f, 1.2f, 1.8f, 2.4f };
            float[] attachW = { 1.35f, 1.00f, 0.60f, 0.20f };

            int n = tendons.Length + 1;
            var t = new float[n];
            var sum = new float[TendonRig.Dim];
            var target = new float[TendonRig.Dim];

            var test = new TendonSpec { name = "T*", attachLocal = Vector3.zero };

            foreach (float ax in xs)
                foreach (float ay in ys)
                    foreach (float az in zs)
                        foreach (float aw in attachW)
                        {
                            test.anchorLocal = new Vector3(ax, ay, az);
                            test.attachLocal = new Vector3(0f, 1.35f - aw, 0f);

                            cands.Clear();
                            foreach (var s in tendons) cands.Add(s);
                            cands.Add(test);

                            var cols = BuildColumns(cands.ToArray(), charLen);

                            float mn = 2f, sm = 0f;
                            foreach (var a in axes)
                            {
                                float r = Probe(a.dir, cols, charLen, t, sum, target);
                                sm += r;
                                if (r < mn) mn = r;
                            }
                            float mean = sm / axes.Length;

                            if (mn > bestWorst)
                            {
                                bestWorst = mn;
                                bestMean = mean;
                                bestAnchor = test.anchorLocal;
                                bestAttachM = aw;
                            }
                        }

            return bestAnchor;
        }

        // ─────────────────────── 配置读写 ───────────────────────

        internal static TendonRigConfig ParseConfig(string json)
        {
            var cfg = JsonUtility.FromJson<TendonRigConfig>(json);
            if (cfg == null || cfg.tendons == null || cfg.tendons.Length == 0)
                throw new InvalidDataException("Empty tendon configuration; no default fallback.");
            if (cfg.attachmentCoordinatesFromBladeTip)
            {
                if (!SimulationValue.Finite(cfg.bladeTipLocal))
                    throw new InvalidDataException("Invalid blade-tip origin.");
                foreach (var spec in cfg.tendons)
                {
                    if (spec == null) throw new InvalidDataException("Null tendon.");
                    if (!spec.enabled) continue;
                    if (!SimulationValue.Finite(spec.attachmentFromBladeTip))
                        throw new InvalidDataException("Invalid attachment from blade tip.");
                    // Use the same coordinate contract as the runtime rig; attachLocal may be stale.
                    spec.attachLocal = cfg.bladeTipLocal + spec.attachmentFromBladeTip;
                    if (!SimulationValue.Finite(spec.attachLocal))
                        throw new InvalidDataException("Nonfinite resolved attachment.");
                }
            }
            return cfg;
        }

        public static TendonRigConfig LoadOrCreateConfig()
        {
            if (TendonRigProfiles.CadDesignRequested)
            {
                try { return ParseConfig(JsonUtility.ToJson(TendonRigProfiles.LoadCadDesign())); }
                catch (Exception e)
                {
                    Debug.LogError("[TENDON] CAD design configuration rejected: " + e.Message);
                    return null;
                }
            }
            string abs = Path.Combine(Application.dataPath, "StreamingAssets/tendon_rig.json");

            if (!File.Exists(abs))
            {
                var def = DefaultConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(abs));
                File.WriteAllText(abs, JsonUtility.ToJson(def, true), Encoding.UTF8);
                Debug.Log("[TENDON] 未找到配置，已生成默认三绳布置：" + abs);
                AssetDatabase.Refresh();
                return def;
            }

            try
            {
                return ParseConfig(File.ReadAllText(abs));
            }
            catch (Exception e)
            {
                Debug.LogError("[TENDON] 配置解析失败：" + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 默认布置 = 目前代码里用的三绳（全拴桨叶中心）。
        /// 硬件组给出真实坐标后，改这里或直接改 json 都行。
        /// </summary>
        public static TendonRigConfig DefaultConfig()
        {
            var cfg = new TendonRigConfig { charLengthM = 0.60f };
            cfg.tendons = new[]
            {
                new TendonSpec { name = "T1", motorId = 1, anchorLocal = new Vector3(1.40f,  0.50f,  1.60f),
                                 attachLocal = Vector3.zero, maxTensionN = 300f, preTensionN = 5f },
                new TendonSpec { name = "T2", motorId = 2, anchorLocal = new Vector3(1.40f,  0.50f, -1.20f),
                                 attachLocal = Vector3.zero, maxTensionN = 300f, preTensionN = 5f },
                new TendonSpec { name = "T3", motorId = 3, anchorLocal = new Vector3(0.60f, -1.40f,  0.20f),
                                 attachLocal = Vector3.zero, maxTensionN = 300f, preTensionN = 5f },
            };
            return cfg;
        }
    }
}
