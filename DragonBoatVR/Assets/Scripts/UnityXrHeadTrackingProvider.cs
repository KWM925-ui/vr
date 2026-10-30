using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace DragonBoatVR
{
    /// <summary>Reads a tracked center-eye pose from an installed Unity XR runtime.
    /// No XR plugin is installed by this component. The platform adapter must supply
    /// actual observations in the SAME tracking stage, on the Unity main thread.
    /// Unity's node API exposes local observation time, not device capture time.
    /// Do not also attach a TrackedPoseDriver to the output camera.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(HeadPoseAttachment))]
    [DefaultExecutionOrder(900)]
    public sealed class UnityXrHeadTrackingProvider : MonoBehaviour
    {
        public bool pollUnityXr = true;
        public string Status { get; private set; } = "XR_RUNTIME_AND_PLATFORM_OBSERVATIONS_REQUIRED";
        public double LastHeadObservedLocalSeconds { get; private set; } = double.NegativeInfinity;
        public double LastPlatformObservedLocalSeconds { get; private set; } = double.NegativeInfinity;
        public ulong HeadNodeId { get; private set; }
        public bool DeviceCaptureTimeAvailable => false;
        readonly List<XRNodeState> _nodes = new List<XRNodeState>();
        readonly List<XRInputSubsystem> _inputs = new List<XRInputSubsystem>();
        readonly HashSet<XRInputSubsystem> _subscribedInputs = new HashSet<XRInputSubsystem>();
        HeadPoseAttachment _attachment;
        Matrix4x4 _stageFromPlatform;
        HeadTrackingSource _platformSource;
        bool _platformTracked;

        void Awake() { _attachment = GetComponent<HeadPoseAttachment>(); }
        void OnEnable() { Application.onBeforeRender += BeforeRender; }
        void LateUpdate() { Poll(false); }
        void BeforeRender() { Poll(true); }

        /// <summary>Never submit the platform's command/target as measured feedback.
        /// The original local observation timestamp must survive repeated head polls.</summary>
        public bool SubmitPlatformObservation(Matrix4x4 stageFromPlatform, double observedLocalSeconds,
            HeadTrackingSource source, bool tracked)
        {
            if (!tracked || source != HeadTrackingSource.ExternalTracking
                || double.IsNaN(observedLocalSeconds) || double.IsInfinity(observedLocalSeconds)
                || observedLocalSeconds < 0 || observedLocalSeconds <= LastPlatformObservedLocalSeconds
                || !RigidRegistration.IsRigid(stageFromPlatform))
            {
                _platformTracked = false;
                Invalidate("INVALID_OR_UNORDERED_PLATFORM_OBSERVATION", true);
                return false;
            }
            _stageFromPlatform = stageFromPlatform;
            LastPlatformObservedLocalSeconds = observedLocalSeconds;
            _platformSource = source; _platformTracked = true;
            return true;
        }

        void Poll(bool applyBeforeRender)
        {
            if (_attachment == null) _attachment = GetComponent<HeadPoseAttachment>();
            if (!pollUnityXr || !XRSettings.enabled)
            { Invalidate("XR_RUNTIME_NOT_ACTIVE", false); return; }
            SubscribeOriginChanges();
            InputTracking.GetNodeStates(_nodes);
            foreach (var node in _nodes)
            {
                if (node.nodeType != XRNode.CenterEye || !node.tracked
                    || !node.TryGetPosition(out Vector3 position) || !node.TryGetRotation(out Quaternion rotation)) continue;
                if (HeadNodeId != 0 && HeadNodeId != node.uniqueID)
                {
                    OriginChanged(null);
                    HeadNodeId = node.uniqueID;
                    return;
                }
                HeadNodeId = node.uniqueID;
                double now = HeadPoseAttachment.MonotonicSeconds;
                LastHeadObservedLocalSeconds = now;
                var head = Matrix4x4.TRS(position, rotation, Vector3.one);
                bool accepted = _attachment.SubmitIndependentTrackingSample(head, now,
                    _stageFromPlatform, LastPlatformObservedLocalSeconds, HeadTrackingSource.ExternalTracking,
                    RigidRegistration.IsRigid(head), _platformTracked && _platformSource == HeadTrackingSource.ExternalTracking);
                if (!accepted) { Status = _attachment.PoseStatus; return; }
                // The normal LateUpdate applies at order 1000. This extra application
                // uses a fresh XR observation immediately before rendering.
                if (applyBeforeRender) _attachment.ApplyLatestPose(now);
                Status = "XR_POSE_OBSERVED_DEVICE_CAPTURE_TIME_UNAVAILABLE";
                return;
            }
            Invalidate("XR_HEAD_TRACKING_LOST", false);
        }

        void SubscribeOriginChanges()
        {
            SubsystemManager.GetInstances(_inputs);
            foreach (var input in _inputs)
                if (_subscribedInputs.Add(input)) input.trackingOriginUpdated += OriginChanged;
        }

        void OriginChanged(XRInputSubsystem input)
        {
            _platformTracked = false; HeadNodeId = 0;
            LastHeadObservedLocalSeconds = LastPlatformObservedLocalSeconds = double.NegativeInfinity;
            if (_attachment != null)
            {
                _attachment.ClearTracking();
                if (_attachment.registration != null) _attachment.registration.ClearCalibration();
            }
            Status = "XR_ORIGIN_CHANGED_REGISTRATION_AND_PLATFORM_OBSERVATIONS_REQUIRED";
        }

        void Invalidate(string status, bool headTracked)
        {
            Status = status;
            if (_attachment == null) return;
            _attachment.SubmitIndependentTrackingSample(Matrix4x4.identity, HeadPoseAttachment.MonotonicSeconds,
                _stageFromPlatform, LastPlatformObservedLocalSeconds, HeadTrackingSource.ExternalTracking,
                headTracked, headTracked && _platformTracked);
        }

        void OnDisable()
        {
            Application.onBeforeRender -= BeforeRender;
            foreach (var input in _subscribedInputs) input.trackingOriginUpdated -= OriginChanged;
            _subscribedInputs.Clear();
            _platformTracked = false; HeadNodeId = 0;
            LastHeadObservedLocalSeconds = LastPlatformObservedLocalSeconds = double.NegativeInfinity;
            if (_attachment != null) _attachment.ClearTracking();
            Status = "XR_PROVIDER_DISABLED";
        }
    }
}
