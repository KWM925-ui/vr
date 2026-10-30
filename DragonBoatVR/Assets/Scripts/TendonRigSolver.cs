using System;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>一根腱（一根牵引绳）的物理布置。</summary>
    [Serializable]
    public class TendonSpec
    {
        [Tooltip("腱编号，和硬件组接线表对齐")]
        public string name = "T1";

        [Tooltip("这根绳接哪台电机（舵机/伺服编号）")]
        public int motorId = 1;

        [Tooltip("电机出绳口位置，船体坐标系 (m)")]
        public Vector3 anchorLocal = new Vector3(1.4f, 0.5f, 1.6f);

        [Tooltip("这根绳拴在桨上的哪个点，桨体坐标系 (m)，原点=桨叶中心，+Y 指向握把")]
        public Vector3 attachLocal = Vector3.zero;

        [Tooltip("以桨叶最下沿为原点的连接坐标；仅 rig.attachmentCoordinatesFromBladeTip 启用时使用")]
        public Vector3 attachmentFromBladeTip;
        public string attachmentRegion;
        public bool heightFromTipConfirmed;
        public bool lateralOffsetConfirmed;

        [Tooltip("该电机单绳张力上限 (N)")]
        public float maxTensionN = 300f;

        [Tooltip("预紧力 (N)，防止绳子松弛")]
        public float preTensionN = 5f;

        [Tooltip("是否参与求解（调试时可以先关掉某根）")]
        public bool enabled = true;
    }

    /// <summary>整套绳驱布置。</summary>
    [Serializable]
    public class TendonRigConfig
    {
        [Tooltip("配置来源；示例几何不能当作实测机构")]
        public string provenance = "Unmeasured software rig configuration";

        [Tooltip("几何及绳路是否经过实测核验")]
        public bool measured = false;

        [Tooltip("几何是否仍含软件示例假设")]
        public bool geometryAssumed = true;

        public bool attachmentCoordinatesFromBladeTip;
        [Tooltip("桨叶最下沿在桨叶中心坐标系的位置；未测时必须标明假设")]
        public Vector3 bladeTipLocal;
        public bool bladeTipOffsetMeasured;
        public bool motorMappingConfirmed;
        public string excludedConnections;

        [Tooltip("力矩归一化尺度 (m)。把 N·m 除以它折成 N，使 6 维残差里力和力矩可比")]
        public float charLengthM = 0.60f;

        [Tooltip("全部腱。数量不限，几根就填几根")]
        public TendonSpec[] tendons = new TendonSpec[0];
    }

    /// <summary>
    /// 多点绳驱（tendon-driven）力分配求解器。
    ///
    /// ── 为什么不能按"每根绳只投影自己的分量"算 ──
    /// 绳子只能拉、不能推。第 k 根绳能贡献的只有沿它自己方向的一个正标量 t_k，
    /// 所以所有绳能合成的力是 { Σ t_k·û_k | t_k ≥ 0 } —— 一个**凸锥**。
    /// 水阻矢量如果落在锥内，可以精确还原；落在锥外，无论电机出多大力都做不到，
    /// 因为那需要"推"。
    ///
    /// ── 锥能不能覆盖整个三维空间 ──
    /// 不能，除非至少 4 根绳，且四个方向的原点在它们凸包内部（正四面体式包围）。
    /// 三个方向可以线性独立，但三根只能拉的绳不能正张成整个三维空间。
    /// 可还原的方向取决于姿态、几何、预紧及上限；不存在通用的 0.90 还原率上限。
    ///
    /// ── 6 维（力 + 力矩）──
    /// 除了力，桨还被"拧"。拴点不在握把上时，每根绳除了给力还给力矩：
    ///     τ_k = t_k · [ û_k ; (p_k − g) × û_k ]
    /// 把 6 维拼起来就是绳驱的"抓取矩阵"（grasp matrix）。拴点沿桨杆分散开，
    /// 各列的秩只描述线性自由度，不能证明非负、有界张力可实现任意力旋量。
    /// 全拴在一个点上仍会对握把产生力矩，但力与力矩不能独立指定。
    ///
    /// 求解：min ‖W·t − τ_target‖²，约束为各腱的最小预紧与最大张力。
    /// 三根以内枚举有界最小二乘的活动集；更多腱保留有限次坐标下降。
    /// 实际残差必须从最终命令合成后报告，数值最优不代表目标力旋量可达。
    /// </summary>
    public static class TendonRig
    {
        public const int Dim = 6;

        /// <summary>把一根腱在某一瞬间的贡献写成 6 维列向量。</summary>
        public static void BuildColumn(Vector3 dir, Vector3 arm, float charLen, float[] col)
        {
            float L = Mathf.Max(1e-3f, charLen);
            col[0] = dir.x;
            col[1] = dir.y;
            col[2] = dir.z;
            Vector3 m = Vector3.Cross(arm, dir) / L;
            col[3] = m.x;
            col[4] = m.y;
            col[5] = m.z;
        }

        /// <summary>水阻 → 目标力旋量。力矩参考点是握把，臂 = 桨叶 − 握把。</summary>
        public static void BuildTarget(Vector3 waterForce, Vector3 bladeArm, float charLen, float[] target)
        {
            float L = Mathf.Max(1e-3f, charLen);
            target[0] = waterForce.x;
            target[1] = waterForce.y;
            target[2] = waterForce.z;
            Vector3 m = Vector3.Cross(bladeArm, waterForce) / L;
            target[3] = m.x;
            target[4] = m.y;
            target[5] = m.z;
        }

        /// <summary>
        /// 非负最小二乘。cols 是 n 个 6 维列向量，t 是待求张力（就地写回）。
        /// 逐坐标下降：每轮把某根绳的张力调到"只看剩下残差"的最优值，为负就截断到 0。
        /// </summary>
        public static void Solve(float[][] cols, float[] target, float[] t, int maxIter = 160)
        {
            int n = cols == null ? 0 : cols.Length;
            var lower = new float[n];
            var upper = new float[n];
            for (int i = 0; i < n; i++) upper[i] = float.MaxValue;
            SolveBounded(cols, target, t, lower, upper, maxIter);
        }

        /// <summary>Bounded independent-tendon allocation. Invalid inputs clear the command and return false.</summary>
        public static bool SolveBounded(float[][] cols, float[] target, float[] t,
                                        float[] lower, float[] upper, int maxIter = 160)
        {
            if (t != null) Array.Clear(t, 0, t.Length);
            if (cols == null || target == null || target.Length != Dim || t == null ||
                t.Length != cols.Length || lower == null || upper == null ||
                lower.Length != cols.Length || upper.Length != cols.Length || maxIter <= 0) return false;
            int n = cols.Length;
            var residual = new double[Dim];
            var norm2 = new double[n];
            for (int k = 0; k < Dim; k++)
            {
                if (!SimulationValue.Finite(target[k])) return false;
                residual[k] = target[k];
            }
            for (int j = 0; j < n; j++)
            {
                if (cols[j] == null || cols[j].Length != Dim || !SimulationValue.Finite(lower[j]) ||
                    !SimulationValue.Finite(upper[j]) || lower[j] < 0f || upper[j] < lower[j]) return false;
                for (int k = 0; k < Dim; k++)
                {
                    if (!SimulationValue.Finite(cols[j][k])) return false;
                    norm2[j] += (double)cols[j][k] * cols[j][k];
                }
            }
            if (n <= 3) return SolveSmallBounded(cols, target, t, lower, upper);
            for (int j = 0; j < n; j++)
            {
                t[j] = lower[j];
                for (int k = 0; k < Dim; k++) residual[k] -= (double)cols[j][k] * t[j];
            }
            for (int iter = 0; iter < maxIter; iter++)
            {
                double maxDelta = 0d;
                for (int j = 0; j < n; j++)
                {
                    if (norm2[j] < 1e-12d) continue;
                    double dot = 0d;
                    for (int k = 0; k < Dim; k++) dot += cols[j][k] * residual[k];
                    float next = (float)Math.Max(lower[j], Math.Min(upper[j], t[j] + dot / norm2[j]));
                    double applied = (double)next - t[j];
                    t[j] = next;
                    for (int k = 0; k < Dim; k++) residual[k] -= cols[j][k] * applied;
                    maxDelta = Math.Max(maxDelta, Math.Abs(applied));
                }
                if (maxDelta < 1e-5d) break;
            }
            return true;
        }

        // With at most three tendons the complete box active set has only 3^n faces.
        // This removes coordinate-descent iteration error without changing the wrench,
        // pretension, upper bounds or the caller's physical acceptance criteria.
        static bool SolveSmallBounded(float[][] cols, float[] target, float[] result, float[] lower, float[] upper)
        {
            int n = cols.Length, faces = 1;
            for (int j = 0; j < n; j++) faces *= 3;
            var candidate = new double[n];
            var best = new double[n];
            var free = new int[n];
            var rhs = new double[Dim];
            var q = new double[Dim, n];
            var r = new double[n, n];
            var scaled = new double[n];
            var columnNorm = new double[n];
            double bestObjective = double.PositiveInfinity;
            bool found = false;
            for (int face = 0; face < faces; face++)
            {
                int code = face, freeCount = 0;
                for (int k = 0; k < Dim; k++) rhs[k] = target[k];
                for (int j = 0; j < n; j++)
                {
                    int state = code % 3; code /= 3;
                    candidate[j] = state == 2 ? upper[j] : lower[j];
                    if (state == 1)
                    {
                        free[freeCount++] = j;
                        candidate[j] = 0;
                    }
                    else
                        for (int k = 0; k < Dim; k++) rhs[k] -= cols[j][k] * candidate[j];
                }
                if (!SolveFreeColumns(cols, rhs, free, freeCount, candidate, q, r, scaled, columnNorm)) continue;
                bool feasible = true;
                for (int j = 0; j < n; j++)
                {
                    double x = candidate[j];
                    // Only accommodate double arithmetic at a bound; the emitted value
                    // is still clamped to the original exact float limits.
                    double rounding = 128d * DoubleEpsilon * Math.Max(Math.Abs(x), Math.Abs(lower[j]));
                    if (!FiniteDouble(x) || x < lower[j] - rounding || x > upper[j] + rounding)
                    { feasible = false; break; }
                    candidate[j] = Math.Max(lower[j], Math.Min(upper[j], x));
                }
                if (!feasible) continue;
                double objective = SmallObjective(cols, target, candidate);
                if (!FiniteDouble(objective) || (found && objective >= bestObjective)) continue;
                bestObjective = objective; found = true;
                Array.Copy(candidate, best, n);
            }
            if (!found || !SmallKktSatisfied(cols, target, best, lower, upper, null)) return false;

            var roundingError = new double[n];
            for (int j = 0; j < n; j++)
            {
                float rounded = (float)best[j];
                if (!SimulationValue.Finite(rounded)) return false;
                rounded = Math.Max(lower[j], Math.Min(upper[j], rounded));
                candidate[j] = rounded;
                roundingError[j] = Math.Abs(candidate[j] - best[j]);
            }
            double roundedObjective = SmallObjective(cols, target, candidate);
            if (!FiniteDouble(roundedObjective) || !SmallKktSatisfied(cols, target, candidate, lower, upper, roundingError))
                return false;
            // Commit only after the complete candidate and its float representation pass.
            // Any rejected solve leaves the output cleared by SolveBounded.
            for (int j = 0; j < n; j++) result[j] = (float)candidate[j];
            return true;
        }

        const double DoubleEpsilon = 2.2204460492503131e-16;
        static bool FiniteDouble(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        static bool SolveFreeColumns(float[][] cols, double[] rhs, int[] free, int count, double[] candidate,
            double[,] q, double[,] r, double[] scaled, double[] columnNorm)
        {
            Array.Clear(r, 0, r.Length);
            for (int j = 0; j < count; j++)
            {
                double norm = 0;
                for (int k = 0; k < Dim; k++) norm += (double)cols[free[j]][k] * cols[free[j]][k];
                norm = Math.Sqrt(norm);
                if (norm == 0 || !FiniteDouble(norm)) return false;
                columnNorm[j] = norm;
                for (int k = 0; k < Dim; k++) q[k, j] = cols[free[j]][k] / norm;
                // Reorthogonalized QR avoids squaring the condition number in normal equations.
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < j; i++)
                    {
                        double dot = 0;
                        for (int k = 0; k < Dim; k++) dot += q[k, i] * q[k, j];
                        r[i, j] += dot;
                        for (int k = 0; k < Dim; k++) q[k, j] -= dot * q[k, i];
                    }
                double diagonal = 0;
                for (int k = 0; k < Dim; k++) diagonal += q[k, j] * q[k, j];
                diagonal = Math.Sqrt(diagonal);
                // A rank-deficient free face has an equivalent optimum on a boundary:
                // move along its nullspace until a variable reaches a bound. Enumeration
                // includes that smaller face. Unresolvable near-rank cases must pass KKT.
                if (!FiniteDouble(diagonal) || diagonal <= 1e-12d) return false;
                r[j, j] = diagonal;
                scaled[j] = 0;
                for (int k = 0; k < Dim; k++)
                {
                    q[k, j] /= diagonal;
                    scaled[j] += q[k, j] * rhs[k];
                }
            }
            for (int j = count - 1; j >= 0; j--)
            {
                double value = scaled[j];
                for (int i = j + 1; i < count; i++) value -= r[j, i] * scaled[i];
                scaled[j] = value / r[j, j];
                candidate[free[j]] = scaled[j] / columnNorm[j];
            }
            return true;
        }

        static double SmallObjective(float[][] cols, float[] target, double[] candidate)
        {
            double objective = 0;
            for (int k = 0; k < Dim; k++)
            {
                double sum = 0;
                for (int j = 0; j < cols.Length; j++) sum += cols[j][k] * candidate[j];
                if (!FiniteDouble(sum) || Math.Abs(sum) > float.MaxValue) return double.PositiveInfinity;
                double residual = sum - target[k];
                objective += residual * residual;
            }
            return objective;
        }

        static bool SmallKktSatisfied(float[][] cols, float[] target, double[] candidate,
            float[] lower, float[] upper, double[] floatRounding)
        {
            var residual = new double[Dim];
            var error = new double[Dim];
            for (int k = 0; k < Dim; k++)
            {
                double sum = 0, magnitude = Math.Abs(target[k]), quantization = 0;
                for (int j = 0; j < cols.Length; j++)
                {
                    double product = cols[j][k] * candidate[j];
                    sum += product; magnitude += Math.Abs(product);
                    if (floatRounding != null) quantization += Math.Abs(cols[j][k]) * floatRounding[j];
                }
                residual[k] = sum - target[k];
                error[k] = 128d * DoubleEpsilon * magnitude + quantization;
            }
            for (int j = 0; j < cols.Length; j++)
            {
                if (candidate[j] < lower[j] || candidate[j] > upper[j]) return false;
                if (lower[j] == upper[j]) continue;
                double gradient = 0, tolerance = 0, dotMagnitude = 0;
                for (int k = 0; k < Dim; k++)
                {
                    double term = cols[j][k] * residual[k];
                    gradient += term; dotMagnitude += Math.Abs(term);
                    tolerance += Math.Abs(cols[j][k]) * error[k];
                }
                tolerance += 128d * DoubleEpsilon * dotMagnitude;
                if (!FiniteDouble(gradient) || !FiniteDouble(tolerance)) return false;
                if (candidate[j] == lower[j]) { if (gradient < -tolerance) return false; }
                else if (candidate[j] == upper[j]) { if (gradient > tolerance) return false; }
                else if (Math.Abs(gradient) > tolerance) return false;
            }
            return true;
        }

        /// <summary>把解出的张力合成为实际力旋量。</summary>
        public static void Compose(float[][] cols, float[] t, float[] sum)
        {
            TryCompose(cols, t, sum);
        }

        /// <summary>Compose finite final tensions; malformed or overflowing input clears the output.</summary>
        public static bool TryCompose(float[][] cols, float[] t, float[] sum)
        {
            if (sum != null) Array.Clear(sum, 0, sum.Length);
            if (cols == null || t == null || sum == null || sum.Length != Dim || t.Length != cols.Length)
                return false;
            var composed = new double[Dim];
            for (int j = 0; j < cols.Length; j++)
            {
                if (!SimulationValue.Finite(t[j]) || t[j] < 0f || cols[j] == null || cols[j].Length != Dim)
                    return false;
                var c = cols[j];
                for (int k = 0; k < Dim; k++)
                {
                    if (!SimulationValue.Finite(c[k])) return false;
                    composed[k] += (double)c[k] * t[j];
                }
            }
            for (int k = 0; k < Dim; k++)
                if (double.IsNaN(composed[k]) || double.IsInfinity(composed[k]) || Math.Abs(composed[k]) > float.MaxValue)
                    return false;
            for (int k = 0; k < Dim; k++) sum[k] = (float)composed[k];
            return true;
        }

        /// <summary>
        /// 还原率 = 1 − ‖实际 − 目标‖ / ‖目标‖。
        /// 1.0 = 此归一化力旋量的残差为零；0.9 = 残差范数为目标范数的 10%。
        /// </summary>
        public static float Restoration(float[] sum, float[] target)
        {
            float num = 0f, den = 0f;
            for (int k = 0; k < Dim; k++)
            {
                float d = sum[k] - target[k];
                num += d * d;
                den += target[k] * target[k];
            }
            if (den < 1e-9f) return num < 1e-9f ? 1f : 0f;
            return 1f - Mathf.Sqrt(num / den);
        }

        /// <summary>力的还原率（单独看 3 维力，不掺力矩）</summary>
        public static float ForceRestoration(float[] sum, float[] target)
        {
            float num = (sum[0] - target[0]) * (sum[0] - target[0])
                      + (sum[1] - target[1]) * (sum[1] - target[1])
                      + (sum[2] - target[2]) * (sum[2] - target[2]);
            float den = target[0] * target[0] + target[1] * target[1] + target[2] * target[2];
            if (den < 1e-9f) return num < 1e-9f ? 1f : 0f;
            return 1f - Mathf.Sqrt(num / den);
        }

        /// <summary>斐波那契球面采样：得到 n 个近似均匀分布的方向，用于评估"覆盖了多少方向"。</summary>
        public static Vector3[] SphereDirections(int n)
        {
            var dirs = new Vector3[n];
            float ga = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++)
            {
                float y = 1f - (i + 0.5f) / n * 2f;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float th = ga * i;
                dirs[i] = new Vector3(Mathf.Cos(th) * r, y, Mathf.Sin(th) * r);
            }
            return dirs;
        }

        /// <summary>
        /// 用给定布置去还原某个方向的水阻，返回还原率。
        /// 这是诊断工具的核心：逐个方向试，就能知道"哪个方向做不出来"。
        /// </summary>
        public static float ProbeDirection(Vector3 dir, Vector3[] dirs, Vector3[] arms,
                                           float charLen, Vector3 bladeArm, float[] t, float[][] cols, float[] sum, float[] target)
        {
            for (int j = 0; j < dirs.Length; j++)
                BuildColumn(dirs[j], arms[j], charLen, cols[j]);

            BuildTarget(dir, bladeArm, charLen, target);
            Solve(cols, target, t);
            Compose(cols, t, sum);
            return Restoration(sum, target);
        }
    }
}
