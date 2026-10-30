using System;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 船内第一人称桌面预览。右键转头，WASD 在船舱内移动，R 回到划桨位。
    /// 移动仅改变船体坐标系中的视点；不创建人物或物理碰撞体。
    /// </summary>
    public sealed class BoatSeatCamera : MonoBehaviour
    {
        public Transform boat;
        // The desktop preview sits above the gunwales and slightly behind the
        // single seat.  This keeps the imported bow ornament in the forward
        // view instead of placing the camera inside the hull mesh.
        public Vector3 seatLocalPosition = new Vector3(0f, 1.12f, -1.35f);
        public Vector3 seatLocalEulerAngles = Vector3.zero;
        [Tooltip("桌面桨手视点随划桨工位定位；后退55cm对应坐姿上身到划桨点，属未实测人体近似")]
        public bool alignWithPaddleStation = true;
        [Header("桌面查看")]
        [Min(0.1f)] public float moveSpeedMetersPerSecond = 1.1f;
        [Min(0.1f)] public float lookSensitivity = 3f;
        [Tooltip("镜头到舷边/实体的查看留量，米；不是已测人体尺寸")]
        [Range(0.08f, 0.3f)] public float viewClearanceMeters = 0.18f;
        PaddleDriver _paddle;
        Camera _camera;
        SimulationSession _session;
        HeadPoseAttachment _headPose;
        BoatCabinBoundary _boundary;
        Func<Vector2, bool> _headroomQuery;
        Vector2 _translation, _look;
        bool _looking, _atBoundary;
        CursorLockMode _previousCursorLock;
        bool _previousCursorVisible;
        GUIStyle _hintStyle;
        Font _hintFont;

        public Vector3 DefaultLocalEyePosition
        {
            get
            {
                var position = seatLocalPosition;
                if (alignWithPaddleStation && boat != null)
                {
                    if (_paddle == null) _paddle = FindObjectOfType<PaddleDriver>();
                    if (_paddle != null && _paddle.boat != null && _paddle.boat.transform == boat)
                        position.z = _paddle.pivotLocalPos.z - 0.55f;
                }
                return position;
            }
        }

        public Vector3 LocalEyePosition => DefaultLocalEyePosition
            + (UnityEngine.XR.XRSettings.enabled ? Vector3.zero : new Vector3(_translation.x, 0f, _translation.y));

        Vector3 DefaultGaze
        {
            get
            {
                // Resolve the same paddle used by DefaultLocalEyePosition.
                var eye = DefaultLocalEyePosition;
                return seatLocalEulerAngles + (alignWithPaddleStation
                    ? new Vector3(12f, _paddle != null && _paddle.leftSide ? -12f : 12f, 0f) : Vector3.zero);
            }
        }

        public Quaternion LocalViewRotation => Quaternion.Euler(DefaultGaze
            + (UnityEngine.XR.XRSettings.enabled ? Vector3.zero : new Vector3(_look.y, _look.x, 0f)));
        public BoatCabinBoundary CabinBoundary { get { PrepareNavigation(); return _boundary; } }
        public bool AtCabinBoundary => _atBoundary;

        void Start()
        {
            _camera = GetComponent<Camera>();
            _headPose = GetComponent<HeadPoseAttachment>();
            _session = FindObjectOfType<SimulationSession>();
            if (_session != null) _session.TimelineReset += ResetView;
            PrepareNavigation();
        }

        void OnEnable() { if (_session != null) _session.TimelineReset += ResetView; }
        void OnDisable()
        {
            EndLook();
            if (_session != null) _session.TimelineReset -= ResetView;
        }
        void OnApplicationFocus(bool focused) { if (!focused) EndLook(); }

        void PrepareNavigation()
        {
            if (_boundary != null || boat == null) return;
            var geometry = BoatHullGeometry.FindOrCreate(boat.GetComponent<BoatBuoyancy>());
            _boundary = new BoatCabinBoundary(geometry, DefaultLocalEyePosition, viewClearanceMeters);
            _headroomQuery = ClearHeadroom;
        }

        bool ClearHeadroom(Vector2 position)
        {
            var local = new Vector3(position.x, DefaultLocalEyePosition.y, position.y);
            // Read-only PhysX query: no camera collider can push the boat or
            // participate in the initial rigid-body step. Ignore diagnostic layer 2.
            return !Physics.CheckSphere(boat.TransformPoint(local), viewClearanceMeters,
                ~(1 << 2), QueryTriggerInteraction.Ignore);
        }

        public void MoveInCabin(Vector2 boatLocalDisplacement)
        {
            PrepareNavigation();
            if (_boundary == null || !_boundary.Ready || boat == null) return;
            var eye = LocalEyePosition;
            Vector2 start = new Vector2(eye.x, eye.z);
            Vector2 next = _boundary.Move(start, boatLocalDisplacement, _headroomQuery);
            Vector3 home = DefaultLocalEyePosition;
            _translation = next - new Vector2(home.x, home.z);
            _atBoundary = (next - start - boatLocalDisplacement).sqrMagnitude > 1e-6f;
        }

        public void ResetView()
        {
            EndLook();
            _translation = _look = Vector2.zero;
            _atBoundary = false;
        }

        void BeginLook()
        {
            _previousCursorLock = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            _looking = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void EndLook()
        {
            if (!_looking) return;
            _looking = false;
            if (Cursor.lockState == CursorLockMode.Locked) Cursor.lockState = _previousCursorLock;
            Cursor.visible = _previousCursorVisible;
        }

        void Update()
        {
            if (_headPose != null && _headPose.useTrackedPose) { EndLook(); return; }
            if (!DesktopViewInput.Available(_camera, _session)) { EndLook(); return; }
            if (_looking && (!Input.GetMouseButton(1) || Input.GetKeyDown(KeyCode.Escape))) EndLook();
            bool began = false;
            if (!_looking && DesktopViewInput.PointerInside(_camera) && Input.GetMouseButtonDown(1))
            {
                BeginLook(); began = true;
            }
            // Ignore the acquisition frame: locking the cursor must not be
            // interpreted as a mouse movement or a sudden camera jump.
            if (_looking && !began)
            {
                _look.x = Mathf.Repeat(_look.x + Input.GetAxis("Mouse X") * lookSensitivity + 180f, 360f) - 180f;
                float basePitch = DefaultGaze.x;
                _look.y = Mathf.Clamp(basePitch + _look.y - Input.GetAxis("Mouse Y") * lookSensitivity, -89f, 89f) - basePitch;
            }
            if (!_looking && !DesktopViewInput.PointerInside(_camera)) return;
            if (Input.GetKeyDown(KeyCode.R)) { ResetView(); return; }
            float horizontal = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f)
                - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
            float forward = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f)
                - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
            Vector3 direction = Vector3.ClampMagnitude(new Vector3(horizontal, 0f, forward), 1f);
            if (direction.sqrMagnitude == 0f) return;
            direction = Quaternion.Euler(0f, DefaultGaze.y + _look.x, 0f) * direction;
            float travel = moveSpeedMetersPerSecond * Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            MoveInCabin(new Vector2(direction.x, direction.z) * travel);
        }

        void LateUpdate()
        {
            if (_headPose == null) _headPose = GetComponent<HeadPoseAttachment>();
            // A tracked camera has a single pose owner, including during loss.
            // Desktop navigation must not overwrite or clamp measured head motion.
            if (_headPose != null && _headPose.useTrackedPose) return;
            if (boat == null) return;
            Vector3 eye = LocalEyePosition;
            transform.SetPositionAndRotation(
                boat.TransformPoint(eye),
                boat.rotation * LocalViewRotation);
        }

        void OnGUI()
        {
            if (_camera == null || !_camera.enabled || UnityEngine.XR.XRSettings.enabled
                || RideHud.DiagnosticsVisible || DesktopViewInput.PanelVisible(_session)) return;
            if (_hintStyle == null)
            {
                _hintFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 13);
                _hintStyle = new GUIStyle(GUI.skin.label) { font = _hintFont, fontSize = 13, alignment = TextAnchor.MiddleLeft };
            }
            GUI.Label(new Rect(_camera.pixelRect.x + 12f, Screen.height - 69f,
                _camera.pixelRect.width - 24f, 24f), _atBoundary
                ? "已到船舱边界 · R 回到划桨位" : "右键转头 · WASD 移动 · R 回到划桨位", _hintStyle);
        }

        void OnDestroy() { EndLook(); if (_hintFont != null) Destroy(_hintFont); }
    }
}
