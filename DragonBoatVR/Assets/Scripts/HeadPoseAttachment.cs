using System;
using System.Diagnostics;
using UnityEngine;

namespace DragonBoatVR
{
    public enum HeadTrackingSource { None, DesktopSimulation, ExternalTracking }

    /// <summary>Provider-neutral adapter. Head/platform samples use the same tracking stage.
    /// worldFromCamera = worldFromBoat * inverse(stageFromPlatform) * stageFromHead.
    /// Transform existence is not tracking; preserve each source's local monotonic sample time.
    /// No XR provider is installed or validated by this adapter.</summary>
    [DefaultExecutionOrder(1000)]
    public class HeadPoseAttachment : MonoBehaviour
    {
        public Transform trackedHead;
        public Transform physicalPlatform;
        public Transform virtualBoat;
        public SpatialCalibrator registration;
        public bool useTrackedPose;
        // Legacy flag is output only; setting it cannot authorize tracking.
        public bool stageRegistrationMeasured;
        public bool allowUnmeasuredDesktopPreview;
        public float maximumTrackingAgeSeconds = 0.10f;
        public string PoseStatus { get; private set; } = "DISABLED_NO_TRACKING_PROVIDER";
        public double TrackingAgeSeconds { get; private set; } = -1;
        public double HeadTrackingAgeSeconds { get; private set; } = -1;
        public double PlatformTrackingAgeSeconds { get; private set; } = -1;
        public double HeadSampleTimeSeconds => _headSampleTime;
        public double PlatformSampleTimeSeconds => _platformSampleTime;
        public double TrackingSampleSkewSeconds => Math.Abs(_headSampleTime - _platformSampleTime);
        public double PoseEvaluationTimeSeconds { get; private set; } = double.NaN;
        public int PoseEvaluationFrame { get; private set; } = -1;
        public HeadTrackingSource TrackingSource { get; private set; }
        public bool PoseApplied { get; private set; }
        Matrix4x4 _stageFromHead, _stageFromPlatform;
        double _headSampleTime = double.NegativeInfinity;
        double _platformSampleTime = double.NegativeInfinity;
        bool _validSample;
        public static double MonotonicSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public static Matrix4x4 Compose(Matrix4x4 virtualBoatPose, Matrix4x4 physicalPlatformPose, Matrix4x4 trackedHeadPose)
        {
            if (!RigidRegistration.IsRigid(virtualBoatPose) || !RigidRegistration.IsRigid(physicalPlatformPose)
                || !RigidRegistration.IsRigid(trackedHeadPose))
                throw new ArgumentException("Head/platform/boat poses must be finite proper rigid transforms with unit scale.");
            return virtualBoatPose * physicalPlatformPose.inverse * trackedHeadPose;
        }

        public bool SubmitTrackingSample(Matrix4x4 stageFromHead, Matrix4x4 stageFromPlatform,
            double observedLocalSeconds, HeadTrackingSource source, bool headTracked, bool platformTracked)
            => SubmitIndependentTrackingSample(stageFromHead, observedLocalSeconds, stageFromPlatform,
                observedLocalSeconds, source, headTracked, platformTracked);

        /// <summary>Use the shared-clock overload only for a genuinely synchronized pair.
        /// A newer head observation must never refresh an older platform observation.
        /// Reusing an unchanged platform sample between head updates preserves its original age.</summary>
        public bool SubmitIndependentTrackingSample(Matrix4x4 stageFromHead, double headObservedLocalSeconds,
            Matrix4x4 stageFromPlatform, double platformObservedLocalSeconds,
            HeadTrackingSource source, bool headTracked, bool platformTracked)
        {
            PoseApplied = false;
            if (!headTracked || !platformTracked)
            { _validSample = false; PoseStatus = !headTracked ? "HEAD_TRACKING_LOST" : "PLATFORM_TRACKING_LOST"; return false; }
            if (source == HeadTrackingSource.None || !Enum.IsDefined(typeof(HeadTrackingSource), source)
                || !ValidSampleTime(headObservedLocalSeconds) || !ValidSampleTime(platformObservedLocalSeconds)
                || headObservedLocalSeconds <= _headSampleTime || platformObservedLocalSeconds < _platformSampleTime
                || (platformObservedLocalSeconds == _platformSampleTime && !stageFromPlatform.Equals(_stageFromPlatform))
                || !RigidRegistration.IsRigid(stageFromHead) || !RigidRegistration.IsRigid(stageFromPlatform))
            { _validSample = false; PoseStatus = "INVALID_OR_UNORDERED_TRACKING_SAMPLE"; return false; }
            _stageFromHead = stageFromHead; _stageFromPlatform = stageFromPlatform;
            _headSampleTime = headObservedLocalSeconds; _platformSampleTime = platformObservedLocalSeconds;
            TrackingSource = source; _validSample = true;
            PoseStatus = "TRACKING_SAMPLE_PENDING"; return true;
        }

        static bool ValidSampleTime(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

        public bool SubmitTransformTracking(double observedLocalSeconds, HeadTrackingSource source, bool headTracked, bool platformTracked)
        {
            if (trackedHead == null || physicalPlatform == null || trackedHead == transform
                || physicalPlatform == transform || trackedHead == physicalPlatform)
            { _validSample = false; PoseApplied = false; PoseStatus = "INDEPENDENT_TRACKING_TRANSFORMS_REQUIRED"; return false; }
            return SubmitTrackingSample(trackedHead.localToWorldMatrix, physicalPlatform.localToWorldMatrix,
                observedLocalSeconds, source, headTracked, platformTracked);
        }

        void LateUpdate() { ApplyLatestPose(MonotonicSeconds); }
        public bool ApplyLatestPose(double nowLocalSeconds)
        {
            PoseEvaluationTimeSeconds = nowLocalSeconds;
            PoseEvaluationFrame = Time.frameCount;
            PoseApplied = false;
            stageRegistrationMeasured = registration != null && registration.IsMeasuredRegistration && registration.HasUsableCalibration;
            if (!useTrackedPose) { PoseStatus = "DISABLED_NO_TRACKING_PROVIDER"; return false; }
            if (!_validSample) { if (PoseStatus == "DISABLED_NO_TRACKING_PROVIDER") PoseStatus = "WAITING_FOR_VALID_TRACKING"; return false; }
            if (!stageRegistrationMeasured && !(allowUnmeasuredDesktopPreview && TrackingSource == HeadTrackingSource.DesktopSimulation))
            { PoseStatus = "MEASURED_REGISTRATION_REQUIRED"; return false; }
            HeadTrackingAgeSeconds = nowLocalSeconds - _headSampleTime;
            PlatformTrackingAgeSeconds = nowLocalSeconds - _platformSampleTime;
            TrackingAgeSeconds = Math.Max(HeadTrackingAgeSeconds, PlatformTrackingAgeSeconds);
            if (double.IsNaN(nowLocalSeconds) || double.IsInfinity(nowLocalSeconds)
                || !SimulationValue.Finite(maximumTrackingAgeSeconds) || maximumTrackingAgeSeconds <= 0
                || HeadTrackingAgeSeconds < 0 || PlatformTrackingAgeSeconds < 0
                || TrackingAgeSeconds > maximumTrackingAgeSeconds)
            { PoseStatus = "TRACKING_STALE_OR_INVALID_CLOCK"; return false; }
            if (virtualBoat == null || virtualBoat == transform || !RigidRegistration.IsRigid(virtualBoat.localToWorldMatrix))
            { PoseStatus = "VALID_VIRTUAL_BOAT_REQUIRED"; return false; }
            Matrix4x4 pose = Compose(virtualBoat.localToWorldMatrix, _stageFromPlatform, _stageFromHead);
            transform.SetPositionAndRotation(pose.GetColumn(3), Quaternion.LookRotation(pose.GetColumn(2), pose.GetColumn(1)));
            PoseApplied = true;
            PoseStatus = TrackingSource == HeadTrackingSource.DesktopSimulation ? "DESKTOP_TRACKING_PREVIEW"
                : "EXTERNAL_TRACKING_APPLIED_XR_HARDWARE_UNVERIFIED";
            return true;
        }

        public void ClearTracking()
        {
            _validSample = false; _headSampleTime = _platformSampleTime = double.NegativeInfinity;
            TrackingAgeSeconds = HeadTrackingAgeSeconds = PlatformTrackingAgeSeconds = -1;
            PoseEvaluationTimeSeconds = double.NaN; PoseEvaluationFrame = -1;
            TrackingSource = HeadTrackingSource.None; PoseApplied = false; PoseStatus = "WAITING_FOR_VALID_TRACKING";
        }
        void OnDisable() { ClearTracking(); }
    }
}
