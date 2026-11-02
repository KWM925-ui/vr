using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DragonBoatVR
{
    /// <summary>
    /// User-facing commands for the desktop presentation. Existing domain owners
    /// still control scenes, input, physics and device lifecycles. No frame is
    /// stepped and no actuator feedback is manufactured here.
    /// </summary>
    public sealed class DemoExperienceController : MonoBehaviour
    {
        public static DemoExperienceController Instance { get; private set; }
        public event Action<string, string> ActionInvoked;
        public event Action SceneReady;

        SimulationSession _session;
        RideHud _hud;
        BoatSeatCamera _seat;
        BoatModelChoice _choice;
        SimulationFrame _frame;
        bool _bindPending = true;

        public SimulationSession Session => _session;
        public RiverEnvironmentController Environment => _session != null ? _session.riverEnvironment : null;
        public RideHud Hud => _hud;
        public BoatSeatCamera Seat => _seat;
        public BoatModelChoice BoatChoice => _choice;
        public PaddleDriver Driver => _session != null && _session.drivers != null
            && _session.drivers.Length == 1 ? _session.drivers[0] : null;
        public bool Ready => _session != null && Environment != null && Driver != null && _choice != null;
        public bool CanConfigure => Ready && !_choice.Switching && !_session.IsReplaying && !_session.IsRecording;
        public bool ExperienceActive => Ready && _session.ExperienceActive;
        public bool Paddling => Driver != null && Driver.strokesPerMinute > 0f;
        public bool MetricsVisible => _hud != null && _hud.compactMetricsVisible;
        public string BoatLabel => _choice != null ? _choice.DisplayName : "正在准备";
        public string RiverLabel => Environment == null ? "正在准备"
            : Environment.SelectedScene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland
                ? "湘江 · 橘子洲" : "顺德 · 逢简水乡";
        public string Status { get; private set; }
        // Consumers receive an owned copy, as with the existing session contract.
        public SimulationFrame ObservedFrame => SimulationSnapshot.Copy(_frame);

        void Awake()
        {
            Instance = this;
            SceneManager.sceneLoaded += SceneLoaded;
        }

        void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Unbind();
            _bindPending = true;
        }

        void Update()
        {
            if (_bindPending) TryBind();
        }

        void TryBind()
        {
            var session = FindObjectOfType<SimulationSession>();
            // SimulationSession.Start establishes these references.
            if (session == null || session.riverEnvironment == null || session.drivers == null
                || session.drivers.Length != 1 || session.drivers[0] == null) return;
            var choice = session.boat != null ? session.boat.GetComponent<BoatModelChoice>() : null;
            if (choice == null) return;
            _session = session;
            _choice = choice;
            _hud = FindObjectOfType<RideHud>();
            _seat = FindObjectOfType<BoatSeatCamera>();
            _session.FrameCompleted += Observe;
            _session.TimelineReset += ResetObservation;
            _bindPending = false;
            Status = null;
            SceneReady?.Invoke();
        }

        void Observe(SimulationFrame frame) { _frame = frame; }
        void ResetObservation() { _frame = null; }
        void Unbind()
        {
            if (_session != null)
            {
                _session.FrameCompleted -= Observe;
                _session.TimelineReset -= ResetObservation;
            }
            _session = null; _hud = null; _seat = null; _choice = null; _frame = null;
        }

        bool Report(string action, string value, bool success, string failure = null)
        {
            Status = success ? null : failure ?? "当前状态暂不能执行此操作";
            ActionInvoked?.Invoke(action, value + (success ? "" : " [REFUSED]"));
            return success;
        }

        public bool BeginExperience()
        {
            if (!Ready || _session.IsReplaying || _session.IsRecording)
                return Report("begin", "", false, "请先结束正在使用的联调输入");
            // Begin always establishes a fresh session and seat position, through
            // the same owners as F10/F11. Only one controller may own it.
            bool accepted = (!_session.ExperienceActive || _session.EndExperience())
                && _session.RepositionExperience();
            return Report("begin", "", accepted);
        }

        public bool EndExperience()
            => Report("end", "", Ready && _session.EndExperience());

        public bool RepositionExperience()
            => Report("reposition", "", Ready && _session.RepositionExperience());

        public bool SelectBoat(string id)
        {
            bool success = CanConfigure && _choice.Select(id, _session, Environment);
            return Report("boat", id, success, _choice != null ? _choice.Error : null);
        }

        public bool SelectRiver(RiverEnvironmentController.SceneProfile scene)
        {
            if (!CanConfigure) return Report("river", scene.ToString(), false);
            Environment.SetScene(scene);
            return Report("river", scene.ToString(), true);
        }

        public bool SelectWater(RiverEnvironmentController.WaterCondition condition)
        {
            if (!CanConfigure) return Report("water", condition.ToString(), false);
            Environment.SetCondition(condition);
            return Report("water", condition.ToString(), true);
        }

        public bool SetCurrent(float speedMps, float headingDegrees)
        {
            if (!CanConfigure || !SimulationValue.Finite(speedMps) || !SimulationValue.Finite(headingDegrees))
                return Report("current", "", false);
            Environment.SetCurrent(speedMps, headingDegrees);
            return Report("current", speedMps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + "," + headingDegrees.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), true);
        }

        public bool SetPaddling(bool enabled)
        {
            if (!ExperienceActive || _session.IsReplaying || Driver == null
                || Driver.mode != PaddleDriver.Mode.VirtualPaddler)
                return Report("paddling", enabled.ToString(), false, "划桨暂停按钮用于桌面模拟桨输入");
            // Same pause/resume semantics and resume rate as SimulationSession F8.
            Driver.strokesPerMinute = enabled ? 45f : 0f;
            return Report("paddling", enabled.ToString(), true);
        }

        public void SetComfort(bool enabled)
        {
            ComfortVignette.SetUserEnabled(enabled);
            Report("comfort", enabled.ToString(), true);
        }

        public bool ResetSeatView()
        {
            var tracked = _seat != null ? _seat.GetComponent<HeadPoseAttachment>() : null;
            if (_seat == null || (tracked != null && tracked.useTrackedPose))
                return Report("seat-reset", "", false, "实测头部追踪使用其配准与重新定位流程");
            _seat.ResetView();
            return Report("seat-reset", "", true);
        }

        public void SetMetricsVisible(bool visible)
        {
            if (_hud != null && _hud.compactMetricsVisible != visible) _hud.ToggleCompactMetrics();
            Report("metrics", visible.ToString(), _hud != null);
        }

        public void SetDiagnosticsVisible(bool visible)
        {
            if (_hud != null) { _hud.visible = visible; if (visible) _hud.detailsExpanded = true; }
            Report("diagnostics", visible.ToString(), _hud != null);
        }

        public void ShowAcquisitionDetails()
        {
            if (_hud != null) _hud.ShowAcquisitionDetails();
            Report("acquisition", "", _hud != null);
        }

        public void CycleInputMode()
        {
            if (_hud != null) _hud.CycleInputMode();
            Report("input-mode", Driver != null ? Driver.mode.ToString() : "", _hud != null);
        }

        public bool LoadRawAcquisitionProfile(string path)
        {
            if (_hud != null) _hud.rawAcquisitionProfilePath = path;
            return Report("acquisition-profile", path ?? "", _hud != null && _hud.LoadRawAcquisitionProfile());
        }

        void OnDestroy()
        {
            Unbind();
            SceneManager.sceneLoaded -= SceneLoaded;
            if (Instance == this) Instance = null;
        }
    }
}
