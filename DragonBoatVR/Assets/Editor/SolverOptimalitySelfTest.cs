using System;
using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Independent analytical bounded-LS oracles. These do not change rig capability or acceptance limits.</summary>
    public static class SolverOptimalitySelfTest
    {
        static int passed, failed;

        [MenuItem("DragonBoatVR/三绳有界分配最优性自检")]
        public static void Run()
        {
            passed = failed = 0;
            Check("near-opposing columns: known exact solution versus original 160 sweeps", TestNearOpposing);
            Check("unreachable wrench retains the analytical nonzero residual", TestUnreachable);
            Check("pretension, upper saturation and a fixed-tension variable", TestBounds);
            Check("dependent, zero and empty column sets", TestSingular);
            Check("different column scales and uncertifiable near-rank failure", TestScaling);
            Check("invalid inputs clear output without relaxing limits", TestInvalid);
            Check("more than three tendons preserves the existing iterative path", TestLargerFallback);
            Debug.Log($"[SOLVEROPTIMAL] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[SOLVEROPTIMAL] RESULT=PASS analytical bounded least-squares oracles"
                : "[SOLVEROPTIMAL] RESULT=FAIL see individual checks");
            if (failed != 0) throw new InvalidOperationException($"Solver optimality self-test failed: {failed} groups.");
        }

        static void Check(string name, Action test)
        {
            try { test(); passed++; Debug.Log($"[SOLVEROPTIMAL] {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"[SOLVEROPTIMAL] {name}: {error}"); }
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static void Near(double actual, double expected, double tolerance, string message)
        {
            Require(!double.IsNaN(actual) && !double.IsInfinity(actual) && Math.Abs(actual - expected) <= tolerance,
                $"{message}: actual={actual:G17}, expected={expected:G17}, tolerance={tolerance:G6}");
        }
        static float[] Column(float x = 0, float y = 0, float z = 0, float tx = 0, float ty = 0, float tz = 0)
            => new[] { x, y, z, tx, ty, tz };
        static float[] Filled(int count, float value)
        { var values = new float[count]; for (int i = 0; i < count; i++) values[i] = value; return values; }
        static bool Zero(float[] values)
        { foreach (float value in values) if (value != 0f) return false; return true; }
        static void Bounds(float[] tensions, float[] lower, float[] upper)
        {
            for (int i = 0; i < tensions.Length; i++)
                Require(SimulationValue.Finite(tensions[i]) && tensions[i] >= lower[i] && tensions[i] <= upper[i],
                    "output violates original bound at tendon " + i);
        }
        static double Objective(float[][] columns, float[] target, float[] tensions)
        {
            double objective = 0;
            for (int axis = 0; axis < 6; axis++)
            {
                double actual = 0;
                for (int tendon = 0; tendon < columns.Length; tendon++)
                    actual += (double)columns[tendon][axis] * tensions[tendon];
                double difference = actual - target[axis];
                objective += difference * difference;
            }
            return objective;
        }

        static void TestNearOpposing()
        {
            var columns = new[] { Column(1), Column(-1, 0.01f), Column(z: 1) };
            var target = Column(y: 1);
            var lower = Filled(3, 0); var upper = Filled(3, 300);
            var oldTensions = OriginalCoordinateDescent(columns, target, lower, upper, 160);
            var tensions = Filled(3, 99);
            // n<=3 must solve the face problem even when the legacy iteration budget is one.
            Require(TendonRig.SolveBounded(columns, target, tensions, lower, upper, 1), "known feasible problem rejected");
            Bounds(tensions, lower, upper);
            double known = 1d / columns[1][1];
            Near(tensions[0], known, 0.00001, "first counteracting tension");
            Near(tensions[1], known, 0.00001, "second counteracting tension");
            Near(tensions[2], 0, 0, "unused orthogonal tendon");
            double oldResidual = Math.Sqrt(Objective(columns, target, oldTensions));
            double actualResidual = Math.Sqrt(Objective(columns, target, tensions));
            Require(oldResidual > 0.98 && oldResidual < 0.99, "historical slow-convergence fixture changed");
            Require(actualResidual < 0.000001, "active-set output missed the independent feasible oracle");
            Debug.Log($"[SOLVEROPTIMAL] nearOpposing old160Residual={oldResidual:G17} exactResidual={actualResidual:G17} "
                + $"knownTension={known:G17} output=({tensions[0]:G9},{tensions[1]:G9},{tensions[2]:G9})");
        }

        static void TestUnreachable()
        {
            var columns = new[] { Column(1), Column(y: 1), Column(z: 1) };
            var target = Column(-4, 2, 3, 7);
            var lower = Filled(3, 0); var upper = new[] { 3f, 1f, 2f }; var tensions = new float[3];
            Require(TendonRig.SolveBounded(columns, target, tensions, lower, upper), "unreachable bounded optimum rejected");
            Bounds(tensions, lower, upper);
            Near(tensions[0], 0, 0, "negative target cannot be produced by a pulling tendon");
            Near(tensions[1], 1, 0, "Y upper saturation");
            Near(tensions[2], 2, 0, "Z upper saturation");
            // Residual components are (4,-1,-1,-7,0,0), so the exact squared norm is 67.
            Near(Objective(columns, target, tensions), 67, 0.00000001, "unreachable wrench residual");
            Debug.Log("[SOLVEROPTIMAL] unreachable objective=67; numerical optimality does not remove geometry/force limits");
        }

        static void TestBounds()
        {
            var columns = new[] { Column(1), Column(y: 1), Column(z: 1) };
            var lower = new[] { 5f, 3f, 7f }; var upper = new[] { 8f, 3f, 10f };
            var target = Column(0, 99, 9); var tensions = new float[3];
            Require(TendonRig.SolveBounded(columns, target, tensions, lower, upper), "pretension/fixed bound solve rejected");
            Bounds(tensions, lower, upper);
            Near(tensions[0], 5, 0, "pretension cannot be removed to improve force fidelity");
            Near(tensions[1], 3, 0, "fixed-tension motor cannot move toward target");
            Near(tensions[2], 9, 0.000001, "free interior tension");
            Near(Objective(columns, target, tensions), 9241, 0.000001, "pretension/fixed-bound residual");

            var one = new[] { Column(1, tx: 1) };
            var force = Column(100, tx: 100); var output = new float[1];
            Require(TendonRig.SolveBounded(one, force, output, new[] { 5f }, new[] { 15f }), "one-cable solve rejected");
            Near(output[0], 15, 0, "force and torque saturation share one motor command");
            Near(Objective(one, force, output), 2 * 85 * 85, 0, "one-cable saturated wrench residual");
        }

        static void TestSingular()
        {
            var columns = new[] { Column(1), Column(2), Column() };
            var target = Column(7, 4);
            var lower = new[] { 1f, 2f, 4f }; var upper = new[] { 5f, 6f, 9f }; var tensions = new float[3];
            Require(TendonRig.SolveBounded(columns, target, tensions, lower, upper), "dependent free face not resolved via boundary subsets");
            Bounds(tensions, lower, upper);
            Near(tensions[0] + 2d * tensions[1], 7, 0.000001, "dependent columns must retain their reachable resultant");
            Near(Objective(columns, target, tensions), 16, 0.000001, "dependent columns cannot create missing Y direction");

            var zeroColumns = new[] { Column(), Column(), Column() };
            var zeroTarget = Column(1, -2, 3);
            Require(TendonRig.SolveBounded(zeroColumns, zeroTarget, tensions, lower, upper), "zero-rank box optimum rejected");
            Bounds(tensions, lower, upper);
            Near(Objective(zeroColumns, zeroTarget, tensions), 14, 0, "zero-rank target residual must remain");
            Require(TendonRig.SolveBounded(new float[0][], zeroTarget, new float[0], new float[0], new float[0]),
                "empty command set has a well-defined residual-only optimum");
        }

        static void TestScaling()
        {
            var columns = new[] { Column(0.000001f), Column(y: 1), Column(z: 1000000f) };
            var target = Column(0.0001f, 120, 2000000);
            var tensions = new float[3]; var lower = Filled(3, 0); var upper = Filled(3, 300);
            Require(TendonRig.SolveBounded(columns, target, tensions, lower, upper), "column-scaled independent system rejected");
            Bounds(tensions, lower, upper);
            Near(tensions[0], (double)target[0] / columns[0][0], 0.00001, "small column tension");
            Near(tensions[1], 120, 0.00001, "unit column tension");
            Near(tensions[2], 2, 0.000001, "large column tension");
            Require(Objective(columns, target, tensions) < 0.00000001, "scaled-column objective");

            // The free columns fall below the documented QR numerical-rank threshold.
            // No boundary face meets KKT; returning an arbitrary 'optimal' solution is unsafe.
            var unresolved = new[] { Column(1), Column(-1, 1e-14f) };
            var unresolvedOutput = Filled(2, 99);
            Require(!TendonRig.SolveBounded(unresolved, Column(y: 1), unresolvedOutput,
                Filled(2, 0), Filled(2, 3e15f)) && Zero(unresolvedOutput), "uncertifiable near-rank result did not fail closed");
        }

        static void TestInvalid()
        {
            for (int kind = 0; kind < 10; kind++)
            {
                float[][] columns = new[] { Column(1), Column(y: 1) };
                float[] target = Column(2, 3), lower = Filled(2, 0), upper = Filled(2, 10), output = Filled(2, 99);
                int iterations = 160;
                switch (kind)
                {
                    case 0: columns = null; break;
                    case 1: columns[0] = new float[2]; break;
                    case 2: target[1] = float.NaN; break;
                    case 3: columns[1][0] = float.PositiveInfinity; break;
                    case 4: lower[0] = -1; break;
                    case 5: lower[0] = 11; break;
                    case 6: upper[0] = float.PositiveInfinity; break;
                    case 7: iterations = 0; break;
                    case 8: iterations = -1; break;
                    case 9: output = Filled(3, 99); break;
                }
                Require(!TendonRig.SolveBounded(columns, target, output, lower, upper, iterations) && Zero(output),
                    "invalid input was accepted or retained output: case " + kind);
            }
        }

        static void TestLargerFallback()
        {
            var columns = new[] { Column(1), Column(-1, 0.01f), Column(z: 1), Column(tx: 1) };
            var target = Column(y: 1); var lower = Filled(4, 0); var upper = Filled(4, 300);
            foreach (int iterations in new[] { 1, 160 })
            {
                float[] expected = OriginalCoordinateDescent(columns, target, lower, upper, iterations);
                var actual = new float[4];
                Require(TendonRig.SolveBounded(columns, target, actual, lower, upper, iterations), "larger fallback rejected");
                Bounds(actual, lower, upper);
                for (int i = 0; i < actual.Length; i++)
                    Require(actual[i] == expected[i], "n>3 iterative behavior changed at tendon " + i);
            }
        }

        // Frozen pre-change method for a requested before/after numerical comparison and
        // n>3 compatibility only. The exact-solver acceptance oracles above are analytical.
        static float[] OriginalCoordinateDescent(float[][] columns, float[] target, float[] lower, float[] upper, int iterations)
        {
            int n = columns.Length;
            var tensions = new float[n]; var residual = new double[6]; var normSquared = new double[n];
            for (int k = 0; k < 6; k++) residual[k] = target[k];
            for (int j = 0; j < n; j++)
            {
                for (int k = 0; k < 6; k++) normSquared[j] += (double)columns[j][k] * columns[j][k];
                tensions[j] = lower[j];
                for (int k = 0; k < 6; k++) residual[k] -= (double)columns[j][k] * tensions[j];
            }
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                double maximumChange = 0;
                for (int j = 0; j < n; j++)
                {
                    if (normSquared[j] < 1e-12) continue;
                    double dot = 0;
                    for (int k = 0; k < 6; k++) dot += columns[j][k] * residual[k];
                    float next = (float)Math.Max(lower[j], Math.Min(upper[j], tensions[j] + dot / normSquared[j]));
                    double applied = (double)next - tensions[j]; tensions[j] = next;
                    for (int k = 0; k < 6; k++) residual[k] -= columns[j][k] * applied;
                    maximumChange = Math.Max(maximumChange, Math.Abs(applied));
                }
                if (maximumChange < 1e-5) break;
            }
            return tensions;
        }
    }
}
