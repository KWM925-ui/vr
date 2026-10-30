using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR
{
    /// <summary>Opt-in contact/audio fixture using ordinary input, automatic PhysX,
    /// production contact detection and the real listener. Never installs in a normal ride.</summary>
    [DefaultExecutionOrder(-2000)]
    public sealed class WoodContactRuntimeCheck : MonoBehaviour
    {
        [Serializable] public sealed class PhysicsObservation
        {
            public string stage;
            public int unityFrame, woodImpacts;
            public double simulationTime, stageTime, wallTime, dspTime, lastImpactTime;
            public bool automaticPhysics, bodyKinematic, valid, strikerPresent;
            public Vector3 bladeLocal, relativeVelocityLocal, lastContactLocal;
            public Vector3 strikerPosition, strikerVelocity;
            public Vector3 boatPosition, boatVelocity, boatAngularVelocity, inertiaTensor, centreOfMass;
            public Quaternion rotationLocal, boatRotation, inertiaRotation;
            public float displayedPositionErrorM;
        }
        [Serializable] public sealed class HullPartObservation
        {
            public string name;
            public int instanceId, vertices, triangles;
            public bool enabled, convex, sameBody, isTrigger;
            public Vector3 boundsSize;
            public float contactOffsetM;
        }
        [Serializable] public sealed class RenderObservation
        {
            public string stage;
            public int unityFrame, woodImpacts, audioFrames;
            public double simulationTime, wallTime, dspTime;
            public float impactSourceRms, creakSourceRms;
        }
        [Serializable] public sealed class CollisionObservation
        {
            public string kind;
            public double simulationTime, fixedTime, wallTime, dspTime;
            public Vector3 point, normal, relativeVelocity, impulse;
            public Vector3 strikerVelocityBeforeStep, strikerVelocityAfterStep;
            public float strikerMassKg, separationM;
            public int hullColliderInstanceId;
            public string hullMeshName;
        }
        [Serializable] public sealed class Screenshot
        {
            public string stage, file, sha256;
            public int unityFrame;
            public double wallTime, dspTime;
        }
        [Serializable] public sealed class Report
        {
            public int schemaVersion = 2;
            public string result = "FAIL";
            public string scope = "CONTACT_AUDIO_FIXTURE: synthetic poses pass through the normal encoder/input and physical observation loop; one real PhysX hull collision. Other audio gains are zero only in this unsaved diagnostic run. No force-feedback or subjective listening acceptance.";
            public string execution = "StandalonePlayer; automatic PhysX; no Physics.Simulate, manual session step or direct EmitWoodImpact call";
            public string audioCapture = "listener_mix.wav", audioCaptureSha256;
            public int audioSampleFrames, audioSampleRate, audioChannels, audioClippedSamples;
            public float audioPeak, gripToBladeM, shaftRadiusM = 0.0225f, railRadiusM, impactVolume;
            public double firstAudioBufferDspTime, firstAudioBufferWallTime;
            public Vector3 railPointLocal, bladeHalfSize;
            public Vector3[] starboardRailLocal;
            public float positionToleranceM;
            public float fixedStepSeconds, hullContactOffsetM, strikerContactOffsetM;
            public bool meshHullRequested;
            public string hullColliderKind;
            public int hullSourceVertices, hullSourceTriangles, dspBufferFrames, dspBufferCount;
            public int hullCollisionVertices, hullCollisionTriangles;
            public Vector3 inertiaBeforeContactShape, inertiaAfterContactShape, centreBeforeContactShape, centreAfterContactShape;
            public Quaternion inertiaRotationBeforeContactShape, inertiaRotationAfterContactShape;
            public BoatCollisionMeshSource hullSource;
            public List<HullPartObservation> hullParts = new List<HullPartObservation>();
            public List<PhysicsObservation> physics = new List<PhysicsObservation>();
            public List<RenderObservation> render = new List<RenderObservation>();
            public List<CollisionObservation> collisions = new List<CollisionObservation>();
            public List<CollisionObservation> contactSteps = new List<CollisionObservation>();
            public List<Screenshot> screenshots = new List<Screenshot>();
            public List<string> errors = new List<string>();
        }

        static readonly string[] Stages = { "Warmup", "ShaftTouch", "ShaftRelease", "BladeTouch", "BladeRelease", "HullCollision" };
        const float StageSeconds = 2.2f;
        readonly Report _report = new Report();
        SimulationSession _session;
        PaddleDriver _driver;
        RiverAudioController _audio;
        RuntimeAudioCapture _capture;
        Rigidbody _body, _striker;
        Transform _boat;
        BoatHullGeometry _geometry;
        string _directory;
        int _stage, _stageFrames, _initialImpacts;
        uint _sequence;
        bool _ready, _finished, _shotPending, _shotTaken;
        double _started;
        Vector3 _rail;
        Vector3 _strikerVelocityBeforeStep;
        float _bladeHalfWidth;
        static double WallTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-woodContactArtifacts");
            if (index < 0) return;
            if (Application.isEditor || index + 1 >= args.Length || Array.IndexOf(args, "-kinematic-fixture") < 0)
                throw new InvalidOperationException("Wood contact evidence requires a dedicated Player and -kinematic-fixture.");
            var check = new GameObject("Wood contact runtime acceptance observer").AddComponent<WoodContactRuntimeCheck>();
            check._directory = Path.GetFullPath(args[index + 1]);
        }

        IEnumerator Start()
        {
            _started = WallTime;
            Directory.CreateDirectory(_directory);
            Application.logMessageReceived += OnLog;
            while ((_session = FindObjectOfType<SimulationSession>()) == null || _session.LastFrame == null)
                yield return null;
            try { Bind(); }
            catch (Exception e) { Finish(e.ToString()); }
            var endOfFrame = new WaitForEndOfFrame();
            while (!_finished)
            {
                yield return endOfFrame;
                try
                {
                    if (_ready && _stage > 0) ObserveRender();
                    if (_shotPending) CaptureScreen();
                }
                catch (Exception e) { Finish(e.ToString()); }
            }
        }

        void Bind()
        {
            _driver = _session.drivers[0]; _body = _session.boat.BoatRb; _boat = _body.transform;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 90;
            AudioSettings.GetDSPBufferSize(out _report.dspBufferFrames, out _report.dspBufferCount);
            _report.meshHullRequested = Array.IndexOf(Environment.GetCommandLineArgs(), "-meshHullContact") >= 0;
            _report.inertiaBeforeContactShape = _body.inertiaTensor;
            _report.inertiaRotationBeforeContactShape = _body.inertiaTensorRotation;
            _report.centreBeforeContactShape = _body.centerOfMass;
            var shape = _session.boat.GetComponent<BoatHullCollisionShape>();
            if (_report.meshHullRequested) shape = BoatHullCollisionShape.Install(_session.boat);
            if (_report.meshHullRequested && (shape == null || !shape.Ready))
                throw new InvalidOperationException("Displayed native hull contact shape was not created.");
            if (shape != null && shape.Ready)
            {
                _report.hullSourceVertices = shape.SourceVertices; _report.hullSourceTriangles = shape.SourceTriangles;
                _report.hullCollisionVertices = shape.CollisionVertices; _report.hullCollisionTriangles = shape.CollisionTriangles;
                _report.hullSource = shape.Source;
                foreach (var collider in shape.Colliders)
                    _report.hullParts.Add(new HullPartObservation { name = collider.sharedMesh.name, instanceId = collider.GetInstanceID(),
                        vertices = collider.sharedMesh.vertexCount, triangles = collider.sharedMesh.triangles.Length / 3,
                        enabled = collider.enabled, convex = collider.convex, sameBody = collider.attachedRigidbody == _body,
                        isTrigger = collider.isTrigger, boundsSize = collider.bounds.size, contactOffsetM = collider.contactOffset });
            }
            _report.inertiaAfterContactShape = _body.inertiaTensor;
            _report.inertiaRotationAfterContactShape = _body.inertiaTensorRotation;
            _report.centreAfterContactShape = _body.centerOfMass;
            _geometry = BoatHullGeometry.FindOrCreate(_session.boat);
            _audio = FindObjectOfType<RiverAudioController>();
            if (_geometry == null || _audio == null || !_audio.AudioReady)
                throw new InvalidOperationException("Actual hull geometry or licensed audio is missing.");
            if (_session.closedLoop != null && _session.closedLoop.Active)
                throw new InvalidOperationException("Contact fixture must not drive an independent hardware controller.");
            var hydro = _session.hydrodynamics[0];
            _report.bladeHalfSize = new Vector3(hydro.BladeWidth, hydro.bladeLength, hydro.bladeThickness) * 0.5f;
            _bladeHalfWidth = _report.bladeHalfSize.x;
            _report.gripToBladeM = _driver.shaftToBlade;
            _report.railRadiusM = BoatHullGeometry.RailRadius;
            _report.impactVolume = _audio.woodImpactVolume;
            _report.fixedStepSeconds = Time.fixedDeltaTime;
            _report.positionToleranceM = Mathf.Sqrt(3f) * 0.5f / _session.encoderPipelines[0].countsPerMeter + 2e-5f;
            _report.starboardRailLocal = _geometry.Starboard;
            _rail = _geometry.Starboard[0];
            foreach (var point in _geometry.Starboard)
                if (Mathf.Abs(point.z - _driver.pivotLocalPos.z) < Mathf.Abs(_rail.z - _driver.pivotLocalPos.z)) _rail = point;
            _report.railPointLocal = _rail;
            // Isolate the contact stem at the final listener, without boosting it.
            // Normal scene settings are neither saved nor changed in the delivered asset.
            _audio.waterAmbientVolume = _audio.shoreAmbientVolume = _audio.paddleSplashVolume = _audio.paddleDripVolume = 0f;
            _audio.strokeWaterVolume = _audio.hullWaterVolume = _audio.woodCreakVolume = 0f;
            _driver.SetMode(PaddleDriver.Mode.External);
            if (_session.riverEnvironment != null) _session.riverEnvironment.enabled = false;
            _session.riverEnvironment = null;
            _session.wave.intensity = _session.wave.intensityRatePerSecond = 0f;
            _session.wave.backgroundCurrentMps = Vector2.zero;
            _session.showDebugOverlay = false;
            _session.bridge.showDebugOverlay = false;
            var split = FindObjectOfType<SplitViewController>(); if (split != null) split.enabled = false;
            var hud = FindObjectOfType<RideHud>(); if (hud != null) hud.enabled = false;
            foreach (var camera in FindObjectsOfType<Camera>()) if (camera != Camera.main) camera.enabled = false;
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("Presented camera missing.");
            var follow = main.GetComponent<DebugFollowCamera>(); if (follow != null) follow.enabled = false;
            var head = main.GetComponent<HeadPoseAttachment>(); if (head != null) head.enabled = false;
            var seat = main.GetComponent<BoatSeatCamera>(); if (seat != null) seat.enabled = false;
            main.rect = new Rect(0, 0, 1, 1); main.fieldOfView = 48f;
            var listener = FindObjectOfType<AudioListener>();
            if (listener == null) throw new InvalidOperationException("Actual AudioListener missing.");
            _capture = listener.gameObject.AddComponent<RuntimeAudioCapture>();
            _session.FrameCompleted += ObservePhysics;
            _ready = true;
        }

        void FixedUpdate()
        {
            if (!_ready || _finished) return;
            try
            {
                float t = _stageFrames * Time.fixedDeltaTime;
                if (_stage == 0 && t >= 0.75f)
                {
                    _stage = 1; _stageFrames = 0; t = 0;
                    _initialImpacts = _audio.WoodImpacts; _capture.Begin();
                }
                else if (_stage > 0 && t >= StageSeconds)
                {
                    if (_stage == Stages.Length - 1) { Finish(null); return; }
                    _stage++; _stageFrames = 0; t = 0; _shotTaken = false;
                }
                float shaftGap = 0.15f, bladeGap = _bladeHalfWidth + 0.15f;
                float nearShaft = _report.shaftRadiusM + _report.railRadiusM - 0.004f;
                float nearBlade = _bladeHalfWidth + _report.railRadiusM - 0.004f;
                float gap = shaftGap, speed = 0f, height = -0.30f;
                if (_stage == 1) MoveGap(shaftGap, nearShaft, t, out gap, out speed);
                if (_stage == 2) MoveGap(nearShaft, shaftGap, t, out gap, out speed);
                if (_stage == 3) { MoveGap(bladeGap, nearBlade, t, out gap, out speed); height = 0f; }
                if (_stage == 4) { MoveGap(nearBlade, bladeGap, t, out gap, out speed); height = 0f; }
                if (_stage == 5)
                {
                    gap = bladeGap; height = 0.75f;
                    if (_stageFrames == 0) CreateStriker();
                }
                Vector3 world = _boat.TransformPoint(_rail + new Vector3(gap, height, 0f));
                var sample = new PaddleSample
                {
                    sequence = ++_sequence, timeSeconds = _session.SimulationTime + Time.fixedDeltaTime,
                    valid = true, bladePosition = world, bladeRotation = _boat.rotation,
                    bladeVelocity = _body.GetPointVelocity(world) + _boat.right * speed,
                    bladeAngularVelocity = _body.angularVelocity, hasAngularVelocity = true
                };
                if (!_driver.SetExternalSample(sample)) throw new InvalidOperationException("Normal input rejected fixture pose.");
                if (_striker != null) _strikerVelocityBeforeStep = _striker.velocity;
                _stageFrames++;
                if (_stage > 0 && t >= 0.9f && !_shotTaken) { _shotPending = true; _shotTaken = true; }
            }
            catch (Exception e) { Finish(e.ToString()); }
        }

        static void MoveGap(float start, float target, float time, out float gap, out float speed)
        {
            const float rate = 0.5f, delay = 0.4f;
            float duration = Mathf.Abs(target - start) / rate;
            gap = Mathf.MoveTowards(start, target, Mathf.Max(0f, time - delay) * rate);
            speed = time > delay && time < delay + duration ? Mathf.Sign(target - start) * rate : 0f;
        }

        void CreateStriker()
        {
            var shape = _session.boat.GetComponent<BoatHullCollisionShape>();
            Collider hull = shape != null && shape.Ready ? shape.Colliders[0] : _session.boat.GetComponent<Collider>();
            if (hull == null || !hull.enabled) throw new InvalidOperationException("Physical hull collider missing.");
            _report.hullColliderKind = hull.GetType().Name;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = "Diagnostic wooden contact block";
            go.transform.localScale = Vector3.one * 0.12f;
            go.GetComponent<Renderer>().sharedMaterial = _driver.GetComponentInChildren<Renderer>().sharedMaterial;
            Bounds bounds = shape != null && shape.Ready ? shape.WorldBounds : hull.bounds;
            go.transform.position = new Vector3(bounds.max.x + 0.36f, _boat.position.y, bounds.center.z);
            _striker = go.AddComponent<Rigidbody>(); _striker.useGravity = false; _striker.mass = 0.6f;
            _striker.drag = 0f;
            _striker.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _striker.velocity = _body.GetPointVelocity(go.transform.position) + Vector3.left * 0.6f;
            _report.hullContactOffsetM = hull.contactOffset;
            _report.strikerContactOffsetM = go.GetComponent<Collider>().contactOffset;
            var probe = go.AddComponent<WoodContactCollisionProbe>(); probe.owner = this; probe.target = _body;
        }

        internal void ObserveCollision(Collision collision, bool entering)
        {
            if (_finished || collision.contactCount == 0) return;
            var contact = collision.GetContact(0);
            var hull = contact.thisCollider.attachedRigidbody == _body ? contact.thisCollider : contact.otherCollider;
            var observation = new CollisionObservation { kind = entering ? "enter" : "stay", simulationTime = _session.SimulationTime,
                fixedTime = Time.fixedTimeAsDouble, wallTime = WallTime, dspTime = AudioSettings.dspTime,
                point = contact.point, normal = contact.normal, relativeVelocity = collision.relativeVelocity, impulse = collision.impulse,
                strikerVelocityBeforeStep = _strikerVelocityBeforeStep, strikerVelocityAfterStep = _striker.velocity,
                strikerMassKg = _striker.mass, separationM = contact.separation,
                hullColliderInstanceId = hull.GetInstanceID(), hullMeshName = (hull as MeshCollider)?.sharedMesh.name };
            _report.contactSteps.Add(observation);
            if (entering) _report.collisions.Add(observation);
        }

        void ObservePhysics(SimulationFrame frame)
        {
            if (!_ready || _finished || _stage == 0) return;
            var paddle = _driver.Sample;
            _report.physics.Add(new PhysicsObservation { stage = Stages[_stage], unityFrame = Time.frameCount,
                simulationTime = frame.timeSeconds, stageTime = _stageFrames * Time.fixedDeltaTime,
                wallTime = WallTime, dspTime = AudioSettings.dspTime, automaticPhysics = Physics.simulationMode == SimulationMode.FixedUpdate,
                bodyKinematic = _body.isKinematic, valid = paddle.valid, woodImpacts = _audio.WoodImpacts - _initialImpacts,
                bladeLocal = _boat.InverseTransformPoint(paddle.bladePosition), rotationLocal = Quaternion.Inverse(_boat.rotation) * paddle.bladeRotation,
                relativeVelocityLocal = _boat.InverseTransformDirection(paddle.bladeVelocity - _body.GetPointVelocity(paddle.bladePosition)),
                lastContactLocal = _boat.InverseTransformPoint(_audio.LastWoodContactWorld), lastImpactTime = _audio.LastWoodImpactTime,
                strikerPresent = _striker != null, strikerPosition = _striker != null ? _striker.position : Vector3.zero,
                strikerVelocity = _striker != null ? _striker.velocity : Vector3.zero,
                boatPosition = _body.position, boatRotation = _body.rotation, boatVelocity = _body.velocity, boatAngularVelocity = _body.angularVelocity,
                inertiaTensor = _body.inertiaTensor, inertiaRotation = _body.inertiaTensorRotation, centreOfMass = _body.centerOfMass,
                displayedPositionErrorM = Vector3.Distance(paddle.bladePosition, _driver.transform.position) });
        }

        void ObserveRender()
        {
            _report.render.Add(new RenderObservation { stage = Stages[_stage], unityFrame = Time.frameCount,
                simulationTime = _session.SimulationTime, wallTime = WallTime, dspTime = AudioSettings.dspTime,
                woodImpacts = _audio.WoodImpacts - _initialImpacts, audioFrames = _capture.SampleFrames,
                impactSourceRms = _audio.SampleWoodImpactOutputRms(), creakSourceRms = _audio.SampleWoodCreakOutputRms() });
        }
        void LateUpdate()
        {
            if (!_ready || _finished || Camera.main == null) return;
            Vector3 focus = _boat.TransformPoint(_rail + Vector3.up * 0.1f);
            Camera.main.transform.position = focus + _boat.TransformDirection(new Vector3(1.6f, 0.8f, -2f));
            Camera.main.transform.LookAt(focus);
        }
        void CaptureScreen()
        {
            _shotPending = false;
            var shot = new Screenshot { stage = Stages[_stage], file = Stages[_stage] + ".png",
                unityFrame = Time.frameCount, wallTime = WallTime, dspTime = AudioSettings.dspTime };
            Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
            byte[] data;
            try { data = texture.EncodeToPNG(); } finally { Destroy(texture); }
            File.WriteAllBytes(Path.Combine(_directory, shot.file), data);
            using (var hash = SHA256.Create()) shot.sha256 = BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            _report.screenshots.Add(shot);
        }
        void OnLog(string message, string stack, LogType type)
        {
            if (!_finished && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && _report.errors.Count < 20)
                _report.errors.Add(message);
        }
        void Update() { if (!_finished && WallTime - _started > 40) Finish("Contact runtime timeout."); }
        void Finish(string error)
        {
            if (_finished) return;
            _finished = true; _ready = false;
            if (!string.IsNullOrEmpty(error)) _report.errors.Add(error);
            if (_session != null) _session.FrameCompleted -= ObservePhysics;
            try
            {
                string path = Path.Combine(_directory, _report.audioCapture);
                _capture.Save(path);
                _report.audioSampleFrames = _capture.SampleFrames; _report.audioSampleRate = _capture.SampleRate;
                _report.audioChannels = _capture.Channels; _report.audioPeak = _capture.Peak; _report.audioClippedSamples = _capture.ClippedSamples;
                _report.firstAudioBufferDspTime = _capture.FirstBufferDspTime; _report.firstAudioBufferWallTime = _capture.FirstBufferWallTime;
                using (var hash = SHA256.Create()) _report.audioCaptureSha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
                if (_report.physics.Count < 1000 || _report.collisions.Count == 0) _report.errors.Add("Physical contact observations incomplete.");
            }
            catch (Exception e) { _report.errors.Add(e.ToString()); }
            if (_striker != null) Destroy(_striker.gameObject);
            if (_session != null) _session.EndExperience();
            _report.result = _report.errors.Count == 0 ? "PASS" : "FAIL";
            File.WriteAllText(Path.Combine(_directory, "contact_result.json"), JsonUtility.ToJson(_report, true));
            Debug.Log("[WOODCONTACT] RESULT=" + _report.result);
            Application.Quit(_report.errors.Count == 0 ? 0 : 1);
        }
        void OnDestroy()
        {
            if (_session != null) _session.FrameCompleted -= ObservePhysics;
            Application.logMessageReceived -= OnLog;
        }
    }
    internal sealed class WoodContactCollisionProbe : MonoBehaviour
    {
        internal WoodContactRuntimeCheck owner;
        internal Rigidbody target;
        void OnCollisionEnter(Collision collision)
        {
            if (collision.rigidbody == target && owner != null) owner.ObserveCollision(collision, true);
        }
        void OnCollisionStay(Collision collision)
        {
            if (collision.rigidbody == target && owner != null) owner.ObserveCollision(collision, false);
        }
    }
}
