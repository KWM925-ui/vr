using System;
using System.IO;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Manual desktop water settings. SimulationSession owns the only Step
    /// call; this controller does not advance wave phase or drive physical devices.</summary>
    public sealed class RiverEnvironmentController : MonoBehaviour
    {
        public enum WaterCondition { Calm, Windy }
        public enum SceneProfile { XiangjiangOrangeIsland, ShundeFengjian }

        [Header("河景选择")]
        public GameObject xiangjiangScene;
        public GameObject fengjianScene;

        [Serializable]
        public sealed class Configuration
        {
            public string provenance = "UNMEASURED_FIRST_VERSION_RIVER_ENVIRONMENT";
            public bool measured;
            public float calmIntensity = 0.12f;
            public float windyIntensity = 0.65f;
            public float defaultCurrentSpeedMps = 0.08f;
            public float defaultCurrentHeadingDegrees;
            public float maximumCurrentSpeedMps = 0.35f;
            public float transitionSeconds = 2f;
            public bool Valid => !string.IsNullOrWhiteSpace(provenance)
                && SimulationValue.Finite(calmIntensity) && calmIntensity > 0f && calmIntensity <= 2f
                && SimulationValue.Finite(windyIntensity) && windyIntensity >= calmIntensity && windyIntensity <= 2f
                && SimulationValue.Finite(defaultCurrentSpeedMps) && defaultCurrentSpeedMps >= 0f
                && SimulationValue.Finite(defaultCurrentHeadingDegrees)
                && SimulationValue.Finite(maximumCurrentSpeedMps) && maximumCurrentSpeedMps > 0f
                && maximumCurrentSpeedMps <= 0.35f && defaultCurrentSpeedMps <= maximumCurrentSpeedMps
                && SimulationValue.Finite(transitionSeconds) && transitionSeconds > 0f;
        }

        public WaterCondition SelectedCondition { get; private set; } = WaterCondition.Calm;
        public SceneProfile SelectedScene { get; private set; } = SceneProfile.XiangjiangOrangeIsland;
        public float TargetCurrentSpeedMps { get; private set; }
        // Heading 0 degrees is world +Z; 90 degrees is world +X.
        public float TargetCurrentHeadingDegrees { get; private set; }
        public bool IsTransitioning { get; private set; }
        public float MaximumCurrentSpeedMps => _configuration.maximumCurrentSpeedMps;
        public string ConfigurationProvenance => _configuration.provenance;
        public bool ConfigurationMeasured => _configuration.measured;
        public bool PanelVisible { get; private set; }

        SimulationSession _session;
        WaveField _wave;
        Configuration _configuration = new Configuration();
        float _elapsed, _startIntensity, _startIntensityRate, _targetIntensity;
        Vector2 _startCurrent, _startCurrentRate, _targetCurrent, _currentRate;
        GUIStyle _panel, _label, _button;
        Font _font;

        public void Bind(SimulationSession session)
        {
            if (session == null || session.wave == null) throw new ArgumentException("A session with its wave field is required.");
            if (_session == session && _wave == session.wave) return;
            _session = session; _wave = session.wave;
            ResolveSceneRoots();
            SetScene(SceneProfile.XiangjiangOrangeIsland);
            string path = Path.Combine(Application.streamingAssetsPath, "river_environment.json");
            if (File.Exists(path))
            {
                Configuration parsed = JsonUtility.FromJson<Configuration>(File.ReadAllText(path));
                if (parsed == null || !parsed.Valid) throw new InvalidDataException("Invalid river environment configuration.");
                _configuration = parsed;
            }
            SelectedCondition = WaterCondition.Calm;
            TargetCurrentSpeedMps = _configuration.defaultCurrentSpeedMps;
            TargetCurrentHeadingDegrees = Mathf.Repeat(_configuration.defaultCurrentHeadingDegrees, 360f);
            var choice = _session.boat != null ? _session.boat.GetComponent<BoatModelChoice>() : null;
            if (choice != null && BoatModelChoice.TakeEnvironment(choice.modelId, out SceneProfile selectedScene,
                out WaterCondition selectedCondition, out float selectedSpeed, out float selectedHeading))
            {
                SetScene(selectedScene); SelectedCondition = selectedCondition;
                TargetCurrentSpeedMps = Mathf.Clamp(selectedSpeed, 0f, _configuration.maximumCurrentSpeedMps);
                TargetCurrentHeadingDegrees = Mathf.Repeat(selectedHeading, 360f);
                PanelVisible = true;
            }
            _targetIntensity = SelectedCondition == WaterCondition.Calm ? _configuration.calmIntensity : _configuration.windyIntensity;
            _targetCurrent = CurrentVector(TargetCurrentSpeedMps, TargetCurrentHeadingDegrees);
            _wave.intensity = _targetIntensity; _wave.intensityRatePerSecond = 0f;
            _wave.backgroundCurrentMps = _targetCurrent; _currentRate = Vector2.zero;
            IsTransitioning = false;
        }

        void ResolveSceneRoots()
        {
            if (xiangjiangScene == null) xiangjiangScene = FindSceneRoot("RiverCourse_XiangjiangOrangeIsland");
            if (fengjianScene == null) fengjianScene = FindSceneRoot("RiverCourse_ShunDeFengjian");
            EnsureSceneCollision(xiangjiangScene);
            EnsureSceneCollision(fengjianScene);
        }

        // Rendering, native boat contact and camera visibility use the same
        // terrain registry, including land outside the finite river banks.
        public static readonly System.Collections.ObjectModel.ReadOnlyCollection<string> SolidSceneMeshes
            = Array.AsReadOnly(new[] { "Bank", "Walkway", "Railing", "FarHills", "ShoreGround" });

        static void EnsureSceneCollision(GameObject root)
        {
            if (root == null) return;
            // Reuse the serialized display mesh. A convex hull would fill the
            // river and bridge openings between these disconnected static solids.
            foreach (string name in SolidSceneMeshes)
            {
                Transform part = root.transform.Find(name);
                if (part == null) continue;
                MeshFilter filter = part.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    throw new InvalidOperationException("River solid is missing its display mesh: " + name);
                var renderer = part.GetComponent<Renderer>();
                if (renderer != null && renderer.isPartOfStaticBatch)
                    throw new InvalidOperationException("River solid must retain its authored mesh for collision; disable static batching on " + name);
                MeshCollider collider = part.GetComponent<MeshCollider>();
                if (collider == null) collider = part.gameObject.AddComponent<MeshCollider>();
                if (collider.attachedRigidbody != null)
                    throw new InvalidOperationException("River scenery collision must remain static: " + name);
                collider.convex = false;
                collider.isTrigger = false;
                if (collider.sharedMesh != filter.sharedMesh) collider.sharedMesh = filter.sharedMesh;
                collider.enabled = true;
            }
        }

        static GameObject FindSceneRoot(string objectName)
        {
            GameObject active = GameObject.Find(objectName);
            if (active != null) return active;
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                if (candidate.name == objectName && candidate.scene.IsValid()) return candidate;
            return null;
        }

        public void SetScene(SceneProfile scene)
        {
            if (!Enum.IsDefined(typeof(SceneProfile), scene)) throw new ArgumentOutOfRangeException(nameof(scene));
            ResolveSceneRoots();
            SelectedScene = scene;
            if (xiangjiangScene != null) xiangjiangScene.SetActive(scene == SceneProfile.XiangjiangOrangeIsland);
            if (fengjianScene != null) fengjianScene.SetActive(scene == SceneProfile.ShundeFengjian);
        }

        public void SetCondition(WaterCondition condition)
        {
            if (!Enum.IsDefined(typeof(WaterCondition), condition)) throw new ArgumentOutOfRangeException(nameof(condition));
            if (_wave == null) throw new InvalidOperationException("Bind the river environment before changing its condition.");
            if (condition == SelectedCondition) return;
            SelectedCondition = condition;
            _targetIntensity = condition == WaterCondition.Calm ? _configuration.calmIntensity : _configuration.windyIntensity;
            BeginTransition();
        }

        public void SetCurrent(float speedMps, float headingDegrees)
        {
            if (!SimulationValue.Finite(speedMps) || !SimulationValue.Finite(headingDegrees))
                throw new ArgumentException("Current speed and heading must be finite.");
            if (_wave == null) throw new InvalidOperationException("Bind the river environment before changing its current.");
            float speed = Mathf.Clamp(speedMps, 0f, _configuration.maximumCurrentSpeedMps);
            float heading = Mathf.Repeat(headingDegrees, 360f);
            if (Mathf.Abs(speed - TargetCurrentSpeedMps) < 1e-6f
                && Mathf.Abs(Mathf.DeltaAngle(heading, TargetCurrentHeadingDegrees)) < 1e-4f) return;
            TargetCurrentSpeedMps = speed; TargetCurrentHeadingDegrees = heading;
            _targetCurrent = CurrentVector(speed, heading);
            BeginTransition();
        }

        static Vector2 CurrentVector(float speed, float headingDegrees)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)) * speed;
        }

        void BeginTransition()
        {
            // Carry derivatives when the operator retargets an unfinished transition.
            // Both value and velocity stay continuous; final derivatives are zero.
            _startIntensity = _wave.intensity; _startIntensityRate = _wave.intensityRatePerSecond;
            _startCurrent = _wave.backgroundCurrentMps; _startCurrentRate = _currentRate;
            _elapsed = 0f; IsTransitioning = true;
        }

        public void Step(float dt)
        {
            if (_wave == null || !SimulationValue.Finite(dt) || dt <= 0f) return;
            if (!IsTransitioning) { _wave.intensityRatePerSecond = 0f; return; }
            _elapsed = Mathf.Min(_configuration.transitionSeconds, _elapsed + dt);
            float t = _elapsed / _configuration.transitionSeconds;
            float t2 = t * t, t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f, h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float d00 = (6f * t2 - 6f * t) / _configuration.transitionSeconds;
            float d10 = 3f * t2 - 4f * t + 1f;
            float d01 = (-6f * t2 + 6f * t) / _configuration.transitionSeconds;
            _wave.intensity = h00 * _startIntensity + h10 * _configuration.transitionSeconds * _startIntensityRate + h01 * _targetIntensity;
            _wave.intensityRatePerSecond = d00 * _startIntensity + d10 * _startIntensityRate + d01 * _targetIntensity;
            _wave.backgroundCurrentMps = h00 * _startCurrent + h10 * _configuration.transitionSeconds * _startCurrentRate + h01 * _targetCurrent;
            _currentRate = d00 * _startCurrent + d10 * _startCurrentRate + d01 * _targetCurrent;
            if (_elapsed >= _configuration.transitionSeconds)
            {
                _wave.intensity = _targetIntensity; _wave.intensityRatePerSecond = 0f;
                _wave.backgroundCurrentMps = _targetCurrent; _currentRate = Vector2.zero;
                IsTransitioning = false;
            }
        }

        void Update()
        {
            // Input only: all environment changes are stepped by the session.
            if (Input.GetKeyDown(KeyCode.F2)) PanelVisible = !PanelVisible;
        }

        void PrepareStyles()
        {
            if (_panel != null) return;
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 15);
            _panel = new GUIStyle(GUI.skin.box) { font = _font, fontSize = 16, alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 10, 10) };
            _label = new GUIStyle(GUI.skin.label) { font = _font, fontSize = 14 };
            _button = new GUIStyle(GUI.skin.button) { font = _font, fontSize = 14, fixedHeight = 27f };
        }

        void OnGUI()
        {
            if (_wave == null || UnityEngine.XR.XRSettings.enabled) return;
            PrepareStyles();
            if (!PanelVisible)
            {
                if (GUI.Button(new Rect(12f, Screen.height - 39f, 170f, 27f), "河景与体验 · F2", _button))
                    PanelVisible = true;
                return;
            }
            float panelWidth = Mathf.Min(330f, Screen.width - 24f);
            float panelHeight = Mathf.Min(480f, Screen.height - 24f);
            GUILayout.BeginArea(new Rect(12f, Screen.height - panelHeight - 12f, panelWidth, panelHeight), GUIContent.none, _panel);
            var choice = _session != null && _session.boat != null ? _session.boat.GetComponent<BoatModelChoice>() : null;
            if (choice != null)
            {
                GUILayout.Label("船型", _label);
                bool enabled = GUI.enabled;
                GUI.enabled = enabled && !choice.Switching && !_session.IsReplaying && !_session.IsRecording;
                GUILayout.BeginHorizontal();
                foreach (string id in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
                    if (GUILayout.Button((choice.modelId == id ? "✓ " : "") + BoatModelChoice.Label(id), _button)
                        && id != choice.modelId && choice.Select(id, _session, this)) GUIUtility.ExitGUI();
                GUILayout.EndHorizontal();
                GUI.enabled = enabled;
                GUILayout.Label(choice.Error ?? "切换船型将重新开始当前体验", _label);
            }
            GUILayout.Label("河景", _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(SelectedScene == SceneProfile.XiangjiangOrangeIsland ? "✓ 湘江·橘子洲" : "湘江·橘子洲", _button))
                SetScene(SceneProfile.XiangjiangOrangeIsland);
            if (GUILayout.Button(SelectedScene == SceneProfile.ShundeFengjian ? "✓ 顺德·逢简水乡" : "顺德·逢简水乡", _button))
                SetScene(SceneProfile.ShundeFengjian);
            GUILayout.EndHorizontal();
            GUILayout.Label("河段、建筑与地标比例：样机，未实测", _label);
            GUILayout.Label("水况设置 · F2 收起", _label);
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && _session != null && _session.ExperienceActive && !_session.IsReplaying && !_session.IsRecording;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(SelectedCondition == WaterCondition.Calm ? "✓ 平静" : "平静", _button)) SetCondition(WaterCondition.Calm);
            if (GUILayout.Button(SelectedCondition == WaterCondition.Windy ? "✓ 有风" : "有风", _button)) SetCondition(WaterCondition.Windy);
            GUILayout.EndHorizontal();
            GUILayout.Label("缓流速度 " + TargetCurrentSpeedMps.ToString("F2") + " m/s", _label);
            float speed = GUILayout.HorizontalSlider(TargetCurrentSpeedMps, 0f, _configuration.maximumCurrentSpeedMps);
            GUILayout.Label("水流方向 " + TargetCurrentHeadingDegrees.ToString("F0") + "°", _label);
            float heading = GUILayout.HorizontalSlider(TargetCurrentHeadingDegrees, 0f, 359.9f);
            if (GUI.enabled) SetCurrent(speed, heading);
            GUI.enabled = previousEnabled;
            GUILayout.Label(_session != null && _session.IsRecording ? "录制期间设置已锁定"
                : IsTransitioning ? "正在平滑调整" : "保持当前水况", _label);
            GUI.enabled = previousEnabled;
            GUILayout.Label("体验控制", _label);
            bool comfort = GUILayout.Toggle(ComfortVignette.UserEnabled, " 舒适模式 · 运动时减弱周边视野", _button);
            if (comfort != ComfortVignette.UserEnabled) ComfortVignette.SetUserEnabled(comfort);
            GUILayout.BeginHorizontal();
            GUI.enabled = previousEnabled && _session != null && _session.ExperienceActive;
            if (GUILayout.Button("结束体验 · F10", _button)) _session.EndExperience();
            GUI.enabled = previousEnabled && _session != null && _session.ExperienceEnded;
            if (GUILayout.Button("重新定位 · F11", _button)) _session.RepositionExperience();
            GUILayout.EndHorizontal();
            GUI.enabled = previousEnabled;
            if (_session != null) GUILayout.Label(_session.Status, _label);
            GUILayout.EndArea();
        }

        void OnDestroy() { if (_font != null) Destroy(_font); }
    }
}
