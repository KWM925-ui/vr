using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DragonBoatVR
{
    /// <summary>
    /// 桌面预览跟随相机：没戴头显时也能看清船和浪。
    /// VR 接入后禁用本组件（XR Origin 接管相机）。
    /// 默认随船航向跟拍；右窗左键改变相对环绕角，滚轮调整远近。
    /// </summary>
    public class DebugFollowCamera : MonoBehaviour
    {
        public Transform target;
        [Tooltip("环绕距离（米）")]
        public float distance = 8.5f;
        [Tooltip("俯视角度（度）")]
        public float pitch = 12f;
        [Tooltip("跟随平滑时间常数（秒）")]
        public float followSmoothing = 0.25f;

        [Tooltip("船体有起伏时，相机跟着轻微浮动增强体感（关闭=稳定视角）")]
        public bool followBobbing = false;

        float _yaw; // User orbit offset relative to the boat's horizontal heading.
        Vector3 _lookAtSmooth;
        Camera _camera;
        Rigidbody _targetBody;
        SimulationSession _session;
        RiverEnvironmentController _fallbackEnvironment;
        BoatViewOcclusion _occlusion;
        bool _orbiting, _resetFollowPending;
        public float OrbitYaw => _yaw;
        public float AppliedYaw { get; private set; }
        public float AppliedPitch { get; private set; }
        public float AppliedDistance { get; private set; }
        public Vector3 AppliedLookAt => _lookAtSmooth;
        public bool OcclusionAvailable => _occlusion != null && _occlusion.Available;
        public bool OcclusionAdjusted => _occlusion != null && _occlusion.Adjusted;
        public bool OcclusionClear => _occlusion != null && _occlusion.Clear;
        public int OcclusionQueries => _occlusion != null ? _occlusion.Queries : 0;
        public float OcclusionMilliseconds { get; private set; }

        void Start()
        {
            _yaw = transform.eulerAngles.y;
            _camera = GetComponent<Camera>();
            _targetBody = target != null ? target.GetComponent<Rigidbody>() : null;
            _session = FindObjectOfType<SimulationSession>();
            _fallbackEnvironment = FindObjectOfType<RiverEnvironmentController>();
            if (target != null) _occlusion = new BoatViewOcclusion(target, _camera);
            if (_session != null) _session.TimelineReset += ResetFollow;
            ResetFollow();
        }

        void OnEnable() { if (_session != null) _session.TimelineReset += ResetFollow; }

        Vector3 LookAtPoint()
        {
            Vector3 point = target.position;
            if (!followBobbing) point.y = Mathf.Lerp(point.y, 0.5f, 0.9f);
            return point;
        }

        void ResetFollow()
        {
            // The event can precede the interpolated Transform's teleport.
            // Keep the chosen orbit/zoom and defer completion to LateUpdate.
            _orbiting = false;
            _resetFollowPending = true;
        }

        void LateUpdate()
        {
            if (target == null) return;

            bool available = DesktopViewInput.Available(_camera, _session)
                && Cursor.lockState != CursorLockMode.Locked;
            bool began = Input.GetMouseButtonDown(0);
            if (!available || !Input.GetMouseButton(0)) _orbiting = false;
            else if (began) _orbiting = DesktopViewInput.PointerInside(_camera);
            if (_orbiting && !began)
            {
                _yaw += Input.GetAxis("Mouse X") * 3f;
                // This is an above-water boat view. Crossing below the look-at
                // plane previously put the camera underwater, where the water
                // shader deliberately disables planar reflection.
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 2f, 0f, 80f);
            }
            float scroll = available && DesktopViewInput.PointerInside(_camera) ? Input.GetAxis("Mouse ScrollWheel") : 0f;
            if (Mathf.Abs(scroll) > 0.001f)
                distance = Mathf.Clamp(distance - scroll * 12f, 4f, 60f);

            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, followSmoothing));
            Vector3 lookAt = LookAtPoint();
            bool resetOcclusion = _resetFollowPending;
            if (_resetFollowPending)
            {
                _lookAtSmooth = lookAt;
                // Keep snapping to the displayed hull while its old pose is
                // still far from the reset body. Normal interpolation trails
                // by at most one physics step; these are read-only queries.
                float travel = _targetBody != null
                    ? _targetBody.velocity.magnitude * Time.fixedDeltaTime + 0.0001f : 0f;
                _resetFollowPending = _targetBody != null
                    && (target.position - _targetBody.position).sqrMagnitude > travel * travel;
            }
            else _lookAtSmooth = Vector3.Lerp(_lookAtSmooth, lookAt, k);

            // Follow yaw only: boat pitch/roll must not tilt the horizon.
            Vector3 forward = target.forward;
            float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            var environment = _session != null ? _session.riverEnvironment : null;
            if (environment == null)
            {
                if (_fallbackEnvironment == null) _fallbackEnvironment = FindObjectOfType<RiverEnvironmentController>();
                environment = _fallbackEnvironment;
            }
            long beganQuery = Stopwatch.GetTimestamp();
            var applied = _occlusion.Resolve(_lookAtSmooth, heading, _yaw, pitch, distance,
                Time.deltaTime, resetOcclusion, environment);
            OcclusionMilliseconds = (float)((Stopwatch.GetTimestamp() - beganQuery) * 1000.0 / Stopwatch.Frequency);
            AppliedYaw = applied.yaw; AppliedPitch = applied.pitch; AppliedDistance = applied.distance;
            Quaternion rot = applied.Rotation(heading);
            Vector3 pos = applied.Eye(_lookAtSmooth, heading);
            transform.position = pos;
            transform.rotation = rot;
        }

        void OnApplicationFocus(bool focused) { if (!focused) _orbiting = false; }
        void OnDisable()
        {
            _orbiting = false;
            if (_session != null) _session.TimelineReset -= ResetFollow;
        }
    }
}
