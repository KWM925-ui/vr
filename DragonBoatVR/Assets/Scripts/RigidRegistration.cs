using System;
using System.Collections.Generic;
using UnityEngine;

namespace DragonBoatVR
{
    [Serializable]
    public class RegistrationPointPair
    {
        public string id;
        public Vector3 platformMeters;
        public Vector3 stageMeters;
    }

    [Serializable]
    public class RegistrationPointSet
    {
        public int schemaVersion = 1;
        public string units = "m";
        public string platformFrame = "physical-platform";
        public string stageFrame = "tracking-stage";
        public string provenance;
        public bool measured;
        public string measuredAtUtc;
        public RegistrationPointPair[] points;
    }

    [Serializable]
    public class RegistrationOptions
    {
        public float maximumRmsResidualMeters = 0.01f;
        public float maximumPointResidualMeters = 0.02f;
        public float minimumAxisSpreadMeters = 0.005f;
        public float minimumSecondToFirstVarianceRatio = 0.0001f;
    }

    [Serializable]
    public class RegistrationFit
    {
        public bool accepted;
        public string status;
        public int pointCount;
        public Vector3 translation;
        public Quaternion rotation = Quaternion.identity;
        public float rmsResidualMeters;
        public float maximumResidualMeters;
        public float[] pointResidualMeters;
        public float platformSecondAxisSpreadMeters;
        public float stageSecondAxisSpreadMeters;
        public float platformVarianceRatio;
        public float stageVarianceRatio;
        public Matrix4x4 Matrix => Matrix4x4.TRS(translation, rotation, Vector3.one);
    }

    /// <summary>Least-squares proper rigid registration using Horn's unit-quaternion solution.
    /// Translation is fitted after centering; scale is fixed to one and a quaternion cannot
    /// introduce a reflection. Two independent geometric axes are required; planar point sets
    /// are valid, but a line or a coincident point cannot determine all three rotations.</summary>
    public static class RigidRegistration
    {
        public static RegistrationFit Fit(RegistrationPointPair[] points, RegistrationOptions options)
        {
            var fit = new RegistrationFit { pointCount = points == null ? 0 : points.Length };
            if (!ValidOptions(options)) return Reject(fit, "INVALID_THRESHOLDS");
            if (points == null || points.Length < 3) return Reject(fit, "AT_LEAST_THREE_POINTS_REQUIRED");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var pc = new double[3]; var qc = new double[3];
            foreach (var point in points)
            {
                if (point == null || string.IsNullOrWhiteSpace(point.id) || !ids.Add(point.id))
                    return Reject(fit, "MISSING_OR_DUPLICATE_POINT_ID");
                if (!SimulationValue.Finite(point.platformMeters) || !SimulationValue.Finite(point.stageMeters))
                    return Reject(fit, "NONFINITE_POINT");
                for (int k = 0; k < 3; k++) { pc[k] += point.platformMeters[k]; qc[k] += point.stageMeters[k]; }
            }
            for (int k = 0; k < 3; k++) { pc[k] /= points.Length; qc[k] /= points.Length; }
            var h = new double[3, 3]; var pp = new double[3, 3]; var qq = new double[3, 3];
            foreach (var point in points)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        double p = point.platformMeters[i] - pc[i], q = point.stageMeters[j] - qc[j];
                        h[i, j] += p * q;
                        pp[i, j] += p * (point.platformMeters[j] - pc[j]);
                        qq[i, j] += (point.stageMeters[i] - qc[i]) * q;
                    }
            Geometry(pp, points.Length, out fit.platformSecondAxisSpreadMeters, out fit.platformVarianceRatio);
            Geometry(qq, points.Length, out fit.stageSecondAxisSpreadMeters, out fit.stageVarianceRatio);
            if (fit.platformSecondAxisSpreadMeters < options.minimumAxisSpreadMeters
                || fit.platformVarianceRatio < options.minimumSecondToFirstVarianceRatio)
                return Reject(fit, "DEGENERATE_PLATFORM_GEOMETRY");
            if (fit.stageSecondAxisSpreadMeters < options.minimumAxisSpreadMeters
                || fit.stageVarianceRatio < options.minimumSecondToFirstVarianceRatio)
                return Reject(fit, "DEGENERATE_STAGE_GEOMETRY");

            double xx = h[0, 0], xy = h[0, 1], xz = h[0, 2];
            double yx = h[1, 0], yy = h[1, 1], yz = h[1, 2];
            double zx = h[2, 0], zy = h[2, 1], zz = h[2, 2];
            var horn = new double[,] {
                { xx + yy + zz, yz - zy, zx - xz, xy - yx },
                { yz - zy, xx - yy - zz, xy + yx, zx + xz },
                { zx - xz, xy + yx, -xx + yy - zz, yz + zy },
                { xy - yx, zx + xz, yz + zy, -xx - yy + zz } };
            Eigen(horn, out var eigenvalues, out var eigenvectors);
            int largest = 0;
            for (int i = 1; i < 4; i++) if (eigenvalues[i] > eigenvalues[largest]) largest = i;
            double norm = 0;
            for (int i = 0; i < 4; i++) norm += eigenvectors[i, largest] * eigenvectors[i, largest];
            norm = Math.Sqrt(norm);
            if (!(norm > 0) || double.IsNaN(norm) || double.IsInfinity(norm)) return Reject(fit, "INVALID_ROTATION_SOLUTION");
            fit.rotation = new Quaternion((float)(eigenvectors[1, largest] / norm), (float)(eigenvectors[2, largest] / norm),
                (float)(eigenvectors[3, largest] / norm), (float)(eigenvectors[0, largest] / norm)).normalized;
            var r = Matrix4x4.Rotate(fit.rotation);
            fit.translation = new Vector3(
                (float)(qc[0] - r.m00 * pc[0] - r.m01 * pc[1] - r.m02 * pc[2]),
                (float)(qc[1] - r.m10 * pc[0] - r.m11 * pc[1] - r.m12 * pc[2]),
                (float)(qc[2] - r.m20 * pc[0] - r.m21 * pc[1] - r.m22 * pc[2]));
            if (!SimulationValue.Finite(fit.translation) || !IsRigid(fit.Matrix)) return Reject(fit, "NONFINITE_TRANSFORM");
            double squared = 0, maximum = 0;
            fit.pointResidualMeters = new float[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p = points[i].platformMeters, q = points[i].stageMeters;
                double x = (double)r.m00 * p.x + (double)r.m01 * p.y + (double)r.m02 * p.z + fit.translation.x - q.x;
                double y = (double)r.m10 * p.x + (double)r.m11 * p.y + (double)r.m12 * p.z + fit.translation.y - q.y;
                double z = (double)r.m20 * p.x + (double)r.m21 * p.y + (double)r.m22 * p.z + fit.translation.z - q.z;
                double error = Math.Sqrt(x * x + y * y + z * z);
                squared += error * error; maximum = Math.Max(maximum, error);
                fit.pointResidualMeters[i] = (float)error;
            }
            fit.rmsResidualMeters = (float)Math.Sqrt(squared / points.Length);
            fit.maximumResidualMeters = (float)maximum;
            if (!SimulationValue.Finite(fit.rmsResidualMeters) || !SimulationValue.Finite(fit.maximumResidualMeters))
                return Reject(fit, "NONFINITE_RESIDUAL");
            if (fit.rmsResidualMeters > options.maximumRmsResidualMeters) return Reject(fit, "RMS_RESIDUAL_EXCEEDED");
            if (fit.maximumResidualMeters > options.maximumPointResidualMeters) return Reject(fit, "POINT_RESIDUAL_EXCEEDED");
            fit.accepted = true; fit.status = "RIGID_FIT_ACCEPTED"; return fit;
        }

        public static bool ValidOptions(RegistrationOptions x) => x != null
            && SimulationValue.Finite(x.maximumRmsResidualMeters) && x.maximumRmsResidualMeters > 0
            && SimulationValue.Finite(x.maximumPointResidualMeters) && x.maximumPointResidualMeters > 0
            && SimulationValue.Finite(x.minimumAxisSpreadMeters) && x.minimumAxisSpreadMeters > 0
            && SimulationValue.Finite(x.minimumSecondToFirstVarianceRatio) && x.minimumSecondToFirstVarianceRatio > 0
            && x.minimumSecondToFirstVarianceRatio <= 1;

        public static bool IsRigid(Matrix4x4 matrix, float tolerance = 0.0001f)
        {
            if (!SimulationValue.Finite(tolerance) || tolerance <= 0) return false;
            for (int i = 0; i < 16; i++) if (!SimulationValue.Finite(matrix[i])) return false;
            if (Mathf.Abs(matrix.m30) > tolerance || Mathf.Abs(matrix.m31) > tolerance
                || Mathf.Abs(matrix.m32) > tolerance || Mathf.Abs(matrix.m33 - 1) > tolerance) return false;
            Vector3 x = matrix.GetColumn(0), y = matrix.GetColumn(1), z = matrix.GetColumn(2);
            return Mathf.Abs(x.sqrMagnitude - 1) < tolerance && Mathf.Abs(y.sqrMagnitude - 1) < tolerance
                && Mathf.Abs(z.sqrMagnitude - 1) < tolerance && Mathf.Abs(Vector3.Dot(x, y)) < tolerance
                && Mathf.Abs(Vector3.Dot(y, z)) < tolerance && Mathf.Abs(Vector3.Dot(z, x)) < tolerance
                && Mathf.Abs(Vector3.Dot(x, Vector3.Cross(y, z)) - 1) < tolerance;
        }

        static RegistrationFit Reject(RegistrationFit fit, string reason)
        { fit.accepted = false; fit.status = reason; return fit; }

        static void Geometry(double[,] scatter, int count, out float spread, out float ratio)
        {
            Eigen(scatter, out var values, out _);
            Array.Sort(values);
            spread = (float)Math.Sqrt(Math.Max(0, values[1]) / count);
            ratio = values[2] > 0 ? (float)(Math.Max(0, values[1]) / values[2]) : 0;
        }

        // Symmetric Jacobi eigensolver, used only for fixed 3x3/4x4 matrices.
        // Selecting the greatest algebraic eigenvalue avoids power iteration's sign/180-degree ambiguity.
        static void Eigen(double[,] input, out double[] values, out double[,] vectors)
        {
            int n = input.GetLength(0); var a = (double[,])input.Clone();
            vectors = new double[n, n];
            for (int i = 0; i < n; i++) vectors[i, i] = 1;
            for (int iteration = 0; iteration < 96; iteration++)
            {
                int p = 0, q = 1; double maximum = 0, scale = 0;
                for (int i = 0; i < n; i++)
                {
                    scale = Math.Max(scale, Math.Abs(a[i, i]));
                    for (int j = i + 1; j < n; j++)
                        if (Math.Abs(a[i, j]) > maximum) { maximum = Math.Abs(a[i, j]); p = i; q = j; }
                }
                if (maximum <= Math.Max(1e-30, scale * 1e-14)) break;
                double angle = 0.5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
                double c = Math.Cos(angle), s = Math.Sin(angle), ap = a[p, p], aq = a[q, q], off = a[p, q];
                a[p, p] = c * c * ap - 2 * s * c * off + s * s * aq;
                a[q, q] = s * s * ap + 2 * s * c * off + c * c * aq;
                a[p, q] = a[q, p] = 0;
                for (int k = 0; k < n; k++)
                {
                    if (k != p && k != q)
                    {
                        double kp = a[k, p], kq = a[k, q];
                        a[k, p] = a[p, k] = c * kp - s * kq;
                        a[k, q] = a[q, k] = s * kp + c * kq;
                    }
                    double vp = vectors[k, p], vq = vectors[k, q];
                    vectors[k, p] = c * vp - s * vq;
                    vectors[k, q] = s * vp + c * vq;
                }
            }
            values = new double[n];
            for (int i = 0; i < n; i++) values[i] = a[i, i];
        }
    }
}
