using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR
{
    /// <summary>Optional motion-responsive peripheral shading of the seat view.
    /// Uses URP's own vignette and the completed simulation frame. It does not
    /// change camera pose, projection, boat dynamics or platform requests.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class ComfortVignette : MonoBehaviour
    {
        // This project reserves layer 31 for this runtime volume. Only the seat
        // camera's volume mask includes it; the external view remains readable.
        public const int VolumeLayer = 31;
        public static bool UserEnabled { get; private set; }
        public float Amount { get; private set; }
        public float MotionDemand => _demand;
        SimulationSession _session;
        UniversalAdditionalCameraData _cameraData;
        Volume _volume;
        VolumeProfile _profile;
        LayerMask _previousMask;
        Vector3 _lastVelocity;
        double _lastSimulationTime;
        float _demand, _lastFrameRealtime;
        bool _haveSample, _bound;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetPreference() { UserEnabled = false; }

        public static void SetUserEnabled(bool enabled) { UserEnabled = enabled; }

        void Start()
        {
            _session = FindObjectOfType<SimulationSession>();
            _cameraData = GetComponent<Camera>().GetUniversalAdditionalCameraData();
            _previousMask = _cameraData.volumeLayerMask;
            _cameraData.volumeLayerMask = _previousMask.value | (1 << VolumeLayer);
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var vignette = _profile.Add<Vignette>(true);
            vignette.color.Override(Color.black);
            vignette.center.Override(new Vector2(0.5f, 0.5f));
            vignette.intensity.Override(0.62f);
            vignette.smoothness.Override(0.60f);
            vignette.rounded.Override(true);
            var volumeObject = new GameObject("Seat comfort volume") { layer = VolumeLayer };
            volumeObject.transform.SetParent(transform, false);
            _volume = volumeObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.sharedProfile = _profile;
            _volume.weight = 0f;
            Bind();
        }

        void OnEnable() { Bind(); }
        void Bind()
        {
            if (_bound || _session == null) return;
            _session.FrameCompleted += Observe;
            _session.TimelineReset += ResetMotion;
            _bound = true;
        }

        public void Observe(SimulationFrame frame)
        {
            if (frame == null || !SimulationValue.Finite(frame.boatVelocity)
                || !SimulationValue.Finite(frame.boatAngularVelocity)
                || double.IsNaN(frame.timeSeconds) || double.IsInfinity(frame.timeSeconds))
            { ResetMotion(); return; }
            float elapsed = (float)(frame.timeSeconds - _lastSimulationTime);
            float acceleration = _haveSample && elapsed > 0f && elapsed <= 0.2f
                ? (frame.boatVelocity - _lastVelocity).magnitude / elapsed : 0f;
            _demand = Mathf.Max(Mathf.InverseLerp(0.7f, 2.5f, frame.boatVelocity.magnitude),
                Mathf.InverseLerp(0.08f, 0.65f, frame.boatAngularVelocity.magnitude),
                Mathf.InverseLerp(0.4f, 2f, acceleration));
            _lastVelocity = frame.boatVelocity;
            _lastSimulationTime = frame.timeSeconds;
            _lastFrameRealtime = Time.unscaledTime;
            _haveSample = true;
        }

        void LateUpdate()
        {
            if (!UserEnabled) Amount = 0f;
            else
            {
                float target = _session != null && _session.ExperienceActive && _haveSample
                    && Time.unscaledTime - _lastFrameRealtime <= 0.2f ? _demand : 0f;
                float tau = target > Amount ? 0.18f : 0.45f;
                Amount = Mathf.Lerp(Amount, target, 1f - Mathf.Exp(-Mathf.Max(0f, Time.unscaledDeltaTime) / tau));
            }
            if (_volume != null) _volume.weight = Amount;
        }

        void ResetMotion()
        {
            _demand = Amount = 0f; _haveSample = false;
            if (_volume != null) _volume.weight = 0f;
        }

        void OnDisable()
        {
            if (_bound && _session != null)
            {
                _session.FrameCompleted -= Observe;
                _session.TimelineReset -= ResetMotion;
            }
            _bound = false;
            ResetMotion();
        }

        void OnDestroy()
        {
            if (_cameraData != null) _cameraData.volumeLayerMask = _previousMask;
            if (_volume != null) Destroy(_volume.gameObject);
            if (_profile != null)
            {
                foreach (var component in _profile.components) if (component != null) Destroy(component);
                Destroy(_profile);
            }
        }
    }
}
