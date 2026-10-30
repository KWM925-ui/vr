using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR
{
    /// <summary>Opt-in observation of real Player keyboard/mouse and completed
    /// camera transforms. Never injects input, invokes navigation, or steps physics.</summary>
    [DefaultExecutionOrder(2000)]
    public sealed class DesktopViewEvidence : MonoBehaviour
    {
        [Serializable] public sealed class Sample
        {
            public double wallTime, utcTime;
            public int frame, screenCameras, deviceRenderersEnabled;
            public bool focused, leftMouse, rightMouse, forward, backward, left, right, reset, f2, f3, f4, f8, f9, f10, f11;
            public bool sessionPresent, environmentPresent, menuOpen, diagnosticsOpen, mainEnabled, twinEnabled, boundary;
            public bool experienceActive, controllerActive, controllerReady;
            public double simulationTime;
            public int inputMode, selectedScene, selectedCondition;
            public float strokesPerMinute, targetCurrentSpeedMps, targetCurrentHeadingDegrees, waveIntensity;
            public uint observationSequence;
            public string observationSessionId;
            public string boatModelId, sceneName, collisionResource;
            public int collisionParts, mainVolumeMask, twinVolumeMask;
            public bool comfortEnabled, comfortPresent, occupantLoadPresent;
            public float comfortAmount;
            public float ambientProbeEnergy;
            public bool environmentProbeUpdateRequested;
            public Vector3 occupantCenterShiftLocalM, occupantTorqueWorldNm;
            public Vector3 actualBladeLocal;
            public Quaternion actualPaddleRotationLocal;
            public bool paddleValid;
            public float wettedFraction, rawWaterForceMagnitudeN;
            public Vector2 backgroundCurrentMps;
            public int cursorLock, mainMask, twinMask;
            public Rect mainViewport, twinViewport;
            public int screenWidth, screenHeight;
            public Vector3 mouse, mainLocal, mainWorld, boatPosition, twinWorld;
            public Quaternion mainLocalRotation, mainRotation, boatRotation, twinRotation;
            public float twinYaw, twinPitch, twinDistance;
            public float twinFieldOfView, twinAspect, twinNearClipPlane;
            public float twinAppliedYaw, twinAppliedPitch, twinAppliedDistance, twinOcclusionMilliseconds;
            public Vector3 twinLookAt;
            public bool twinOcclusionAvailable, twinOcclusionAdjusted, twinOcclusionClear;
            public int twinOcclusionQueries;
        }
        [Serializable] public sealed class Report
        {
            public int schemaVersion = 2;
            public string scope = "Actual focused/unfocused input and LateUpdate camera observations; no direct calls to navigation methods.";
            public bool cleanQuit;
            public string error;
            public List<Sample> samples = new List<Sample>();
        }
        readonly Report _report = new Report();
        string _directory;
        Camera _main, _twin;
        Camera[] _cameras;
        Renderer[] _devices;
        BoatSeatCamera _seat;
        DebugFollowCamera _follow;
        SimulationSession _session;
        BoatModelChoice _choice;
        BoatHullCollisionShape _collision;
        SimulatedPaddlerLoad _occupant;
        ComfortVignette _comfort;
        double _next;
        static double WallTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartIfRequested()
        {
            if (Application.isEditor) return;
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-desktopViewEvidence");
            if (index < 0 || index + 1 >= args.Length) return;
            var probe = new GameObject("DesktopViewEvidence").AddComponent<DesktopViewEvidence>();
            DontDestroyOnLoad(probe.gameObject);
            probe._directory = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(probe._directory);
        }

        void Start()
        {
            BindScene();
            SceneManager.sceneLoaded += SceneLoaded;
        }

        void SceneLoaded(Scene scene, LoadSceneMode mode) { BindScene(); }

        void BindScene()
        {
            _seat = FindObjectOfType<BoatSeatCamera>();
            _main = _seat != null ? _seat.GetComponent<Camera>() : null;
            _follow = FindObjectOfType<DebugFollowCamera>();
            _twin = _follow != null ? _follow.GetComponent<Camera>() : null;
            _session = FindObjectOfType<SimulationSession>();
            _choice = FindObjectOfType<BoatModelChoice>();
            _collision = FindObjectOfType<BoatHullCollisionShape>();
            _occupant = FindObjectOfType<SimulatedPaddlerLoad>();
            _comfort = _seat != null ? _seat.GetComponent<ComfortVignette>() : null;
            _cameras = FindObjectsOfType<Camera>(true);
            var devices = new List<Renderer>();
            foreach (string name in new[] { "CableRig", "StewartPlatformRig" })
            {
                var rig = GameObject.Find(name);
                if (rig != null) devices.AddRange(rig.GetComponentsInChildren<Renderer>(true));
            }
            _devices = devices.ToArray();
        }

        void LateUpdate()
        {
            if (_report.error != null || WallTime < _next) return;
            _next = WallTime + 1.0 / 30;
            if (_seat == null || _follow == null || _session == null) BindScene();
            if (_seat == null || _follow == null || _session == null || _seat.boat == null) return;
            if (_collision == null) _collision = _seat.boat.GetComponent<BoatHullCollisionShape>();
            if (_report.samples.Count >= 6000) { _report.error = "Desktop input evidence capacity exceeded."; return; }
            var boat = _seat.boat;
            var environment = _session != null ? _session.riverEnvironment : null;
            var driver = _session != null && _session.drivers != null && _session.drivers.Length > 0
                ? _session.drivers[0] : null;
            var controller = _session != null ? _session.closedLoop : null;
            var wave = _session != null ? _session.wave : null;
            var hydro = _session != null && _session.hydrodynamics != null && _session.hydrodynamics.Length > 0
                ? _session.hydrodynamics[0] : null;
            int screenCameras = 0, visibleDevices = 0;
            foreach (var camera in _cameras)
                if (camera != null && camera.isActiveAndEnabled && camera.targetTexture == null) screenCameras++;
            foreach (var renderer in _devices)
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy) visibleDevices++;
            var ambientProbe = RenderSettings.ambientProbe;
            float ambientEnergy = 0f;
            for (int channel = 0; channel < 3; channel++)
                for (int term = 0; term < 9; term++) ambientEnergy += ambientProbe[channel, term] * ambientProbe[channel, term];
            _report.samples.Add(new Sample
            {
                // Mono Stopwatch has a process-local epoch. UTC additionally
                // aligns these observations with the external input runner.
                wallTime = WallTime, utcTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                frame = Time.frameCount, screenCameras = screenCameras, deviceRenderersEnabled = visibleDevices,
                ambientProbeEnergy = ambientEnergy, environmentProbeUpdateRequested = EnvironmentProbeBootstrap.UpdateRequested,
                focused = Application.isFocused, leftMouse = Input.GetMouseButton(0), rightMouse = Input.GetMouseButton(1),
                forward = Input.GetKey(KeyCode.W), backward = Input.GetKey(KeyCode.S), left = Input.GetKey(KeyCode.A), right = Input.GetKey(KeyCode.D),
                reset = Input.GetKey(KeyCode.R), f2 = Input.GetKey(KeyCode.F2), f3 = Input.GetKey(KeyCode.F3), f4 = Input.GetKey(KeyCode.F4),
                f8 = Input.GetKey(KeyCode.F8), f9 = Input.GetKey(KeyCode.F9), f10 = Input.GetKey(KeyCode.F10), f11 = Input.GetKey(KeyCode.F11),
                sessionPresent = _session != null, environmentPresent = environment != null,
                menuOpen = environment != null && environment.PanelVisible,
                diagnosticsOpen = RideHud.DiagnosticsVisible,
                // Read scalar/value state only; LastFrame and telemetry cloning
                // would add unrelated work to this input observation.
                experienceActive = _session != null && _session.ExperienceActive,
                simulationTime = _session != null ? _session.SimulationTime : 0d,
                inputMode = driver != null ? (int)driver.mode : -1,
                strokesPerMinute = driver != null ? driver.strokesPerMinute : 0f,
                controllerActive = controller != null && controller.Active,
                controllerReady = controller != null && controller.Ready,
                observationSequence = controller != null ? controller.ObservationSequence : 0,
                observationSessionId = controller != null ? controller.ObservationSessionId : null,
                boatModelId = _choice != null ? _choice.modelId : null,
                sceneName = SceneManager.GetActiveScene().name,
                collisionResource = _choice != null ? _choice.collisionResource : null,
                collisionParts = _collision != null && _collision.Ready ? _collision.Colliders.Length : 0,
                mainVolumeMask = _main.GetUniversalAdditionalCameraData().volumeLayerMask.value,
                twinVolumeMask = _twin.GetUniversalAdditionalCameraData().volumeLayerMask.value,
                comfortEnabled = ComfortVignette.UserEnabled, comfortPresent = _comfort != null,
                comfortAmount = _comfort != null ? _comfort.Amount : 0f,
                occupantLoadPresent = _occupant != null,
                occupantCenterShiftLocalM = _occupant != null ? _occupant.CenterShiftLocalM : Vector3.zero,
                occupantTorqueWorldNm = _occupant != null ? _occupant.TorqueWorldNm : Vector3.zero,
                actualBladeLocal = driver != null ? boat.InverseTransformPoint(driver.transform.position) : Vector3.zero,
                actualPaddleRotationLocal = driver != null ? Quaternion.Inverse(boat.rotation) * driver.transform.rotation : Quaternion.identity,
                paddleValid = driver != null && driver.Sample.valid,
                wettedFraction = hydro != null ? hydro.WettedFraction : 0f,
                rawWaterForceMagnitudeN = hydro != null ? hydro.RawWaterForceN.magnitude : 0f,
                selectedScene = environment != null ? (int)environment.SelectedScene : -1,
                selectedCondition = environment != null ? (int)environment.SelectedCondition : -1,
                targetCurrentSpeedMps = environment != null ? environment.TargetCurrentSpeedMps : 0f,
                targetCurrentHeadingDegrees = environment != null ? environment.TargetCurrentHeadingDegrees : 0f,
                waveIntensity = wave != null ? wave.intensity : 0f,
                backgroundCurrentMps = wave != null ? wave.backgroundCurrentMps : Vector2.zero,
                mainEnabled = _main.enabled, twinEnabled = _twin.enabled, boundary = _seat.AtCabinBoundary,
                cursorLock = (int)Cursor.lockState, mainMask = _main.cullingMask, twinMask = _twin.cullingMask,
                mainViewport = _main.rect, twinViewport = _twin.rect, screenWidth = Screen.width, screenHeight = Screen.height,
                mouse = Input.mousePosition, mainLocal = boat.InverseTransformPoint(_main.transform.position),
                mainWorld = _main.transform.position, boatPosition = boat.position,
                mainLocalRotation = Quaternion.Inverse(boat.rotation) * _main.transform.rotation,
                mainRotation = _main.transform.rotation, boatRotation = boat.rotation, twinRotation = _twin.transform.rotation,
                twinYaw = _follow.OrbitYaw, twinPitch = _follow.pitch, twinDistance = _follow.distance,
                twinWorld = _twin.transform.position, twinFieldOfView = _twin.fieldOfView,
                twinAspect = _twin.aspect, twinNearClipPlane = _twin.nearClipPlane,
                twinAppliedYaw = _follow.AppliedYaw, twinAppliedPitch = _follow.AppliedPitch,
                twinAppliedDistance = _follow.AppliedDistance, twinLookAt = _follow.AppliedLookAt,
                twinOcclusionAvailable = _follow.OcclusionAvailable, twinOcclusionAdjusted = _follow.OcclusionAdjusted,
                twinOcclusionClear = _follow.OcclusionClear, twinOcclusionQueries = _follow.OcclusionQueries,
                twinOcclusionMilliseconds = _follow.OcclusionMilliseconds
            });
        }

        void OnDestroy() { SceneManager.sceneLoaded -= SceneLoaded; }

        void OnApplicationQuit()
        {
            LifecycleTrace.Mark("camera evidence OnApplicationQuit begin");
            _report.cleanQuit = true;
            if (!string.IsNullOrEmpty(_directory))
            {
                string json = JsonUtility.ToJson(_report, false) + "\n";
                LifecycleTrace.Mark("camera evidence serialization complete chars=" + json.Length);
                // Keep every sample and field. A lossless archive avoids large raw
                // diagnostic writes during Player shutdown; the host expands it
                // only after this process has exited and validates the GZip CRC.
                using (var file = File.Create(Path.Combine(_directory, "camera_observations.json.gz")))
                using (var compressed = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                using (var writer = new StreamWriter(compressed, new UTF8Encoding(false)))
                    writer.Write(json);
                LifecycleTrace.Mark("camera evidence file closed");
            }
            LifecycleTrace.Mark("camera evidence OnApplicationQuit end");
        }
    }
}
