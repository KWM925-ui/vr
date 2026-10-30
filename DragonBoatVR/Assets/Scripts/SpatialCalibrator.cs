using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DragonBoatVR
{
    [Serializable]
    public class RegistrationRecord
    {
        public int schemaVersion = 1;
        public string solvedAtUtc;
        public RegistrationPointSet pointSet;
        public RegistrationOptions thresholds;
        public RegistrationFit fit;
    }

    /// <summary>Platform-to-tracking-stage registration from corresponding points.
    /// A head-position prior cannot measure six degrees of freedom. The measured flag
    /// records supplied provenance; numerical acceptance does not validate real hardware.</summary>
    public class SpatialCalibrator : MonoBehaviour
    {
        [Header("Coordinate frames")]
        public Transform trackingStage;
        // Legacy scene/API fields retained; single head poses and priors never enter the fit.
        public Transform headCamera;
        public Transform boatRoot;
        public Vector3 standardPaddlePlatformPos = new Vector3(0f, 0.95f, 0.40f);
        public Vector3 standardHeadPlatformPos = new Vector3(0f, 1.25f, -0.15f);

        [Header("Corresponding point data (project-relative JSON)")]
        public string measurementJsonPath = "Calibration/registration_measurements.json";
        public string calibrationJsonPath = "Calibration/registration_result.json";
        public bool loadSavedCalibrationOnStart;
        public bool allowUnmeasuredDesktopPreview;
        public RegistrationOptions thresholds = new RegistrationOptions();

        [Header("Status")]
        public bool calibrated;
        public float lastResidual = -1f;
        public string Status { get; private set; } = "NO_CORRESPONDING_POINTS";
        // Return a diagnostic snapshot; callers must not mutate the accepted trust record.
        public RegistrationFit LastFit => _lastFit == null ? null
            : JsonUtility.FromJson<RegistrationFit>(JsonUtility.ToJson(_lastFit));
        public bool IsMeasuredRegistration => _record != null && _record.pointSet.measured && _record.fit.accepted;
        public bool HasUsableCalibration => _record != null && _record.fit.accepted
            && (_record.pointSet.measured || allowUnmeasuredDesktopPreview) && StageIsRigid;
        public Matrix4x4 PlatformToStage { get; private set; } = Matrix4x4.identity;
        public string ConfigurationSignature
        {
            get
            {
                string signature = JsonUtility.ToJson(_record)
                    + ";desktopPreview=" + allowUnmeasuredDesktopPreview + ";stageToWorld=";
                // The tracking stage defines the fixed coordinate mapping used by paddle
                // inputs. Include the full world matrix so parent motion, scale and shear
                // cannot change that mapping without invalidating a recording or replay.
                var matrix = StageToWorld;
                for (int i = 0; i < 16; i++)
                    signature += matrix[i].ToString("R", CultureInfo.InvariantCulture) + ";";
                return signature;
            }
        }
        public Matrix4x4 StageToWorld => trackingStage != null ? trackingStage.localToWorldMatrix : Matrix4x4.identity;
        bool StageIsRigid => RigidRegistration.IsRigid(StageToWorld);
        RegistrationRecord _record;
        RegistrationFit _lastFit;

        void Awake() { ClearCalibration(); }
        void Start()
        {
            if (headCamera == null && Camera.main != null) headCamera = Camera.main.transform;
            // Legacy DBVR_Cal_* PlayerPrefs contain assumed translation/yaw, not measured
            // point sets. They are deliberately ignored rather than promoted to calibration.
            if (loadSavedCalibrationOnStart) LoadCalibration(calibrationJsonPath);
        }
        void Update()
        {
            calibrated = HasUsableCalibration;
            if (Input.GetKeyDown(KeyCode.C)) Calibrate();
            if (Input.GetKeyDown(KeyCode.V)) ClearCalibration();
        }

        public void Calibrate()
        {
            if (LoadMeasurements(measurementJsonPath)) SaveCalibration(calibrationJsonPath);
        }

        public bool LoadMeasurements(string projectRelativePath)
        {
            try
            {
                return SetCorrespondences(JsonUtility.FromJson<RegistrationPointSet>(
                    File.ReadAllText(ResolveProjectPath(projectRelativePath))));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is FormatException)
            { return Reject("MEASUREMENT_LOAD_FAILED: " + error.Message); }
        }

        public bool SetCorrespondences(RegistrationPointSet pointSet)
        {
            ClearCalibration();
            if (!ValidMetadata(pointSet, out string reason)) return Reject(reason);
            if (!RigidRegistration.ValidOptions(thresholds)) return Reject("INVALID_THRESHOLDS");
            // Own the accepted snapshot; changing the caller's point set cannot change trust.
            var copy = JsonUtility.FromJson<RegistrationPointSet>(JsonUtility.ToJson(pointSet));
            var options = JsonUtility.FromJson<RegistrationOptions>(JsonUtility.ToJson(thresholds));
            _lastFit = RigidRegistration.Fit(copy.points, options);
            lastResidual = _lastFit.rmsResidualMeters;
            if (!_lastFit.accepted) { Status = _lastFit.status; return false; }
            _record = new RegistrationRecord { solvedAtUtc = DateTime.UtcNow.ToString("O"),
                pointSet = copy, thresholds = options, fit = _lastFit };
            PlatformToStage = _lastFit.Matrix;
            calibrated = HasUsableCalibration;
            Status = copy.measured ? "ACCEPTED_DECLARED_MEASURED_POINT_SET"
                : allowUnmeasuredDesktopPreview ? "UNMEASURED_DESKTOP_PREVIEW" : "UNMEASURED_FIT_NOT_ENABLED";
            if (!StageIsRigid) { calibrated = false; Status = "TRACKING_STAGE_MUST_HAVE_UNIT_SCALE"; }
            return true;
        }

        public bool SaveCalibration(string projectRelativePath)
        {
            if (_record == null || !_record.fit.accepted) return Reject("NO_ACCEPTED_FIT_TO_SAVE");
            try
            {
                string path = ResolveProjectPath(projectRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(_record, true) + "\n", new System.Text.UTF8Encoding(false));
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            { Status = "CALIBRATION_SAVE_FAILED: " + error.Message; return false; }
        }

        public bool LoadCalibration(string projectRelativePath)
        {
            ClearCalibration();
            try
            {
                var record = JsonUtility.FromJson<RegistrationRecord>(File.ReadAllText(ResolveProjectPath(projectRelativePath)));
                if (record == null || record.schemaVersion != 1 || record.fit == null || !record.fit.accepted
                    || !ValidMetadata(record.pointSet, out _) || !RigidRegistration.ValidOptions(record.thresholds))
                    return Reject("INVALID_CALIBRATION_RECORD");
                // Refit under current thresholds; stored success flags and transforms are evidence,
                // not authority. Reject a transform or residual that disagrees with the saved points.
                if (!SetCorrespondences(record.pointSet)) return false;
                if (!SavedFitMatches(record.fit, _lastFit))
                    return Reject("SAVED_TRANSFORM_OR_RESIDUAL_DOES_NOT_MATCH_POINTS");
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is FormatException)
            { return Reject("CALIBRATION_LOAD_FAILED: " + error.Message); }
        }

        public void ClearCalibration()
        {
            _record = null; calibrated = false; lastResidual = -1f; _lastFit = null;
            PlatformToStage = Matrix4x4.identity; Status = "NO_CORRESPONDING_POINTS";
        }

        public Vector3 PlatformToWorld(Vector3 platformPos)
        {
            if (!HasUsableCalibration || !SimulationValue.Finite(platformPos))
                throw new InvalidOperationException("Platform-to-world mapping requires an enabled valid registration.");
            return (StageToWorld * PlatformToStage).MultiplyPoint3x4(platformPos);
        }

        public Quaternion PlatformRotationToWorld(Quaternion platformRotation)
        {
            if (!HasUsableCalibration || !SimulationValue.Finite(platformRotation))
                throw new InvalidOperationException("Platform rotation mapping requires an enabled valid registration.");
            return (trackingStage != null ? trackingStage.rotation : Quaternion.identity) * _lastFit.rotation * platformRotation;
        }

        bool Reject(string reason)
        { ClearCalibration(); Status = reason; return false; }

        static bool SavedFitMatches(RegistrationFit saved, RegistrationFit solved)
        {
            if (saved.pointCount != solved.pointCount || saved.status != solved.status
                || !SimulationValue.Finite(saved.translation)
                || Vector3.Distance(saved.translation, solved.translation) > 1e-5f
                || !RotationMatches(saved.rotation, solved.rotation)
                || !Near(saved.rmsResidualMeters, solved.rmsResidualMeters)
                || !Near(saved.maximumResidualMeters, solved.maximumResidualMeters)
                || !Near(saved.platformSecondAxisSpreadMeters, solved.platformSecondAxisSpreadMeters)
                || !Near(saved.stageSecondAxisSpreadMeters, solved.stageSecondAxisSpreadMeters)
                || !Near(saved.platformVarianceRatio, solved.platformVarianceRatio)
                || !Near(saved.stageVarianceRatio, solved.stageVarianceRatio)
                || saved.pointResidualMeters == null || saved.pointResidualMeters.Length != solved.pointCount)
                return false;
            for (int i = 0; i < saved.pointResidualMeters.Length; i++)
                if (!Near(saved.pointResidualMeters[i], solved.pointResidualMeters[i])) return false;
            return true;
        }

        static bool Near(float a, float b) => SimulationValue.Finite(a) && Mathf.Abs(a - b) <= 1e-5f;

        static bool RotationMatches(Quaternion saved, Quaternion solved)
        {
            if (!SimulationValue.Finite(saved)) return false;
            double norm = (double)saved.x * saved.x + (double)saved.y * saved.y
                + (double)saved.z * saved.z + (double)saved.w * saved.w;
            if (Math.Abs(norm - 1) > 1e-4) return false;
            // q and -q denote the same rotation. Component distance avoids Angle's
            // single-precision acos rounding, which can reject an identical saved fit.
            double direct = 0, opposite = 0;
            for (int i = 0; i < 4; i++)
            {
                double difference = (double)saved[i] - solved[i], sum = (double)saved[i] + solved[i];
                direct += difference * difference; opposite += sum * sum;
            }
            return Math.Min(direct, opposite) <= 1e-10;
        }

        static bool ValidMetadata(RegistrationPointSet data, out string reason)
        {
            reason = "INVALID_POINT_SET_METADATA";
            if (data == null || data.schemaVersion != 1 || data.units != "m"
                || string.IsNullOrWhiteSpace(data.platformFrame) || string.IsNullOrWhiteSpace(data.stageFrame)
                || data.platformFrame == data.stageFrame || string.IsNullOrWhiteSpace(data.provenance)) return false;
            if (data.measured && (string.IsNullOrWhiteSpace(data.measuredAtUtc)
                || !DateTimeOffset.TryParse(data.measuredAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var measuredAt)
                || measuredAt.Offset != TimeSpan.Zero))
            { reason = "MEASURED_POINT_SET_REQUIRES_UTC_ACQUISITION_TIME"; return false; }
            return true;
        }

        public static string ResolveProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A project-relative JSON path is required.");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string resolved = Path.GetFullPath(Path.Combine(root, path));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(resolved), ".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Calibration JSON must stay inside the Unity project.");
            return resolved;
        }
    }
}
