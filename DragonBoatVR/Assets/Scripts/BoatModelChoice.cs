using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DragonBoatVR
{
    /// <summary>Each scene retains its own displayed hull, paddle station, cabin
    /// and collision resource. A user choice starts a fresh simulation session.</summary>
    public sealed class BoatModelChoice : MonoBehaviour
    {
        public const string Majesty = "majesty", Ornament = "ornament";
        public string modelId = Majesty;
        public string collisionResource = "Collision/DragonBoatHull";
        public string collisionSourceResource = "Collision/DragonBoatHullSource";
        public string sourceObjAsset = "Assets/Art/Boat/DragonBoat.obj";

        sealed class PendingEnvironment
        {
            public string model;
            public RiverEnvironmentController.SceneProfile scene;
            public RiverEnvironmentController.WaterCondition condition;
            public float currentSpeed, currentHeading;
        }
        static PendingEnvironment _pending;
        static bool _switching;
        public string Error { get; private set; }
        public string DisplayName => Label(modelId);
        public bool Switching => _switching;

        public static string Label(string id) => id == Majesty ? "经典木船" : id == Ornament ? "雕饰龙舟" : "未知船型";
        public static string SceneName(string id) => id == Majesty ? "Demo"
            : id == Ornament ? "DragonBoatOrnament" : throw new ArgumentOutOfRangeException(nameof(id));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSelection() { _pending = null; _switching = false; }

        void Awake()
        {
            if (modelId != Majesty && modelId != Ornament)
                throw new InvalidOperationException("The scene has no recognized boat identity.");
            if (string.IsNullOrWhiteSpace(collisionResource) || string.IsNullOrWhiteSpace(collisionSourceResource)
                || string.IsNullOrWhiteSpace(sourceObjAsset))
                throw new InvalidOperationException("Boat-specific collision and source identity are required.");
            if (_pending != null && _pending.model != modelId)
                throw new InvalidOperationException("The loaded boat differs from the requested boat.");
        }

        public bool Select(string id, SimulationSession session, RiverEnvironmentController environment)
        {
            if (id == modelId) return true;
            if (_switching || session == null || environment == null || session.IsReplaying || session.IsRecording) return false;
            string scene = SceneName(id);
            if (!Application.CanStreamedLevelBeLoaded(scene))
            { Error = "所选船型暂时无法载入"; return false; }
            Error = null;
            // Finish the current actuator process and clear its authorization
            // before any new hull/session is created. No live object is retargeted.
            if (!session.EndExperience()) return false;
            _pending = new PendingEnvironment { model = id, scene = environment.SelectedScene,
                condition = environment.SelectedCondition, currentSpeed = environment.TargetCurrentSpeedMps,
                currentHeading = environment.TargetCurrentHeadingDegrees };
            _switching = true;
            try { SceneManager.LoadScene(scene, LoadSceneMode.Single); return true; }
            catch (Exception error)
            {
                _pending = null; _switching = false; Error = "船型切换失败，当前体验已结束";
                Debug.LogException(error); return false;
            }
        }

        public static bool TakeEnvironment(string modelId, out RiverEnvironmentController.SceneProfile scene,
            out RiverEnvironmentController.WaterCondition condition, out float speed, out float heading)
        {
            scene = default; condition = default; speed = heading = 0f;
            if (_pending == null) return false;
            if (_pending.model != modelId) throw new InvalidOperationException("Boat/environment selection identity differs.");
            scene = _pending.scene; condition = _pending.condition;
            speed = _pending.currentSpeed; heading = _pending.currentHeading;
            _pending = null; _switching = false; return true;
        }
    }
}
