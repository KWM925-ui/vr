using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class InteractionFoamTrailSelfTest
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [MenuItem("DragonBoatVR/World anchored foam trail self test")]
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            int passed = 0, failed = 0;
            Check("old water marks remain at their world locations", WorldMapping, ref passed, ref failed);
            Check("texture and anchor publish together at 30 Hz", Publication, ref passed, ref failed);
            Check("six second decay is independent of step count", Decay, ref passed, ref failed);
            Check("large relocation discards nonoverlapping history", Teleport, ref passed, ref failed);
            Check("completed frame and relative water speed drive the wake", FrameSource, ref passed, ref failed);
            Check("timeline reset hides history and releases subscriptions", ResetAndDispose, ref passed, ref failed);
            Check("hull foam uses completed horizontal water-relative motion", HullFoamSource, ref passed, ref failed);
            Check("wet paddle churn follows actual relative motion", PaddleMotionSource, ref passed, ref failed);
            Debug.Log($"[FOAM_TRAIL] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[FOAM_TRAIL] RESULT=PASS" : "[FOAM_TRAIL] RESULT=FAIL");
        }
        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try { test(); passed++; Debug.Log("[FOAM_TRAIL] " + name + "=True"); }
            catch (Exception e) { failed++; Debug.LogError("[FOAM_TRAIL] " + name + ": " + e); }
        }
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }
        static object Call(object target, string name, params object[] args)
            => target.GetType().GetMethod(name, Private).Invoke(target, args);
        static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
        sealed class Fixture : IDisposable
        {
            public readonly GameObject root = new GameObject("FoamFixture");
            public readonly InteractionFoamTrail trail;
            public Fixture()
            {
                trail = root.AddComponent<InteractionFoamTrail>();
                if (trail.FoamTexture == null) Call(trail, "Awake");
            }
            public void Paint(Vector3 world) { Call(trail, "Paint", world, 0.8f, 1.6f); Call(trail, "Publish"); }
            public void Dispose()
            {
                // Non-ExecuteAlways components do not receive engine lifecycle calls
                // in this edit-mode fixture; pair the explicit Awake above with cleanup.
                Call(trail, "OnDestroy");
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        static Vector3 PeakWorld(InteractionFoamTrail trail)
        {
            Color32[] pixels = trail.FoamTexture.GetPixels32();
            int peak = 0;
            for (int i = 1; i < pixels.Length; i++) if (pixels[i].r > pixels[peak].r) peak = i;
            Require(pixels[peak].r > 0, "history disappeared inside the retained window");
            var anchor = trail.AnchorParams;
            return new Vector3(anchor.x + ((peak % 256 + 0.5f) / 256f - 0.5f) * 48f, 0,
                anchor.z + ((peak / 256 + 0.5f) / 256f - 0.5f) * 48f);
        }
        static void WorldMapping()
        {
            using (var f = new Fixture())
            {
                f.Paint(new Vector3(1.03f, 0, -2.1f));
                Vector3 original = PeakWorld(f.trail);
                foreach (var position in new[] { new Vector3(0.5f, 2, 0.7f), new Vector3(-1.5f, -4, 3.2f),
                    new Vector3(8.1f, 6, -5.2f), Vector3.zero })
                {
                    f.root.transform.SetPositionAndRotation(position, Quaternion.Euler(20, 137, 5));
                    Call(f.trail, "Recenter", position);
                    Call(f.trail, "Publish");
                    Require(Vector3.Distance(PeakWorld(f.trail), original) < 1e-6f, "old trail moved with the boat");
                }
            }
        }
        static void Publication()
        {
            using (var f = new Fixture())
            {
                f.Paint(Vector3.zero);
                Vector4 anchor = f.trail.AnchorParams;
                Vector3 original = PeakWorld(f.trail);
                int uploads = f.trail.TextureUploads;
                Call(f.trail, "Recenter", new Vector3(1.2f, 0, -1.3f));
                for (int i = 0; i < 3; i++) Call(f.trail, "UploadIfDue", 0.01f);
                Require(f.trail.TextureUploads == uploads && f.trail.AnchorParams == anchor
                    && PeakWorld(f.trail) == original, "new mapping was exposed before matching pixels");
                Call(f.trail, "UploadIfDue", 0.01f);
                Require(f.trail.TextureUploads == uploads + 1 && f.trail.AnchorParams != anchor
                    && PeakWorld(f.trail) == original, "publication moved the world mark");
                for (int i = 0; i < 120; i++)
                {
                    Call(f.trail, "Recenter", new Vector3(i, 0, 0));
                    Call(f.trail, "UploadIfDue", 1f / 120f);
                }
                Require(f.trail.TextureUploads - uploads - 1 <= 30, "normal uploads exceeded 30 Hz");
            }
        }
        static void Decay()
        {
            float[] results = new float[2];
            for (int k = 0; k < 2; k++)
                using (var f = new Fixture())
                {
                    float[] values = Field<float[]>(f.trail, "_density");
                    values[100] = 1;
                    int steps = k == 0 ? 180 : 720;
                    for (int i = 0; i < steps; i++) Call(f.trail, "Decay", 6f / steps);
                    results[k] = values[100];
                    Require(Mathf.Abs(results[k] - Mathf.Exp(-1)) < 0.00002f, "declared time constant was not preserved");
                }
            Require(Mathf.Abs(results[0] - results[1]) < 0.00002f, "decay changed with step count");
        }
        static void Teleport()
        {
            using (var f = new Fixture())
            {
                f.Paint(Vector3.zero);
                Call(f.trail, "Recenter", new Vector3(100, 0, -100));
                Call(f.trail, "Publish");
                foreach (var p in f.trail.FoamTexture.GetPixels32()) Require(p.r == 0, "old window wrapped on teleport");
            }
        }
        static SimulationSession Session(GameObject root)
        {
            var session = root.AddComponent<SimulationSession>();
            session.wave = root.AddComponent<WaveField>();
            session.wave.intensity = 0;
            session.wave.backgroundCurrentMps = new Vector3(0.3f, 0, 0);
            session.hydrodynamics = Array.Empty<PaddleHydrodynamics>();
            return session;
        }
        static void Emit(SimulationSession session, SimulationFrame frame)
            => Field<Action<SimulationFrame>>(session, "FrameCompleted")?.Invoke(frame);
        static void Reset(SimulationSession session) => Field<Action>(session, "TimelineReset")?.Invoke();
        static void FrameSource()
        {
            using (var f = new Fixture())
            {
                var session = Session(f.root);
                f.trail.Bind(session);
                var frame = new SimulationFrame { timeSeconds = 2, fixedStepSeconds = 0.01f,
                    boatPosition = new Vector3(2, 0, 3), boatRotation = Quaternion.identity,
                    boatVelocity = new Vector3(0.3f, 1, 0), paddles = Array.Empty<PaddleSample>() };
                f.root.transform.position = new Vector3(100, 0, 100);
                Emit(session, frame);
                Require(f.trail.WakePaintEvents == 0, "common current or vertical bobbing generated a wake");
                frame.timeSeconds += 0.01; frame.boatVelocity += Vector3.forward;
                Emit(session, frame); Call(f.trail, "Publish");
                Require(f.trail.ObservedFrames == 2 && f.trail.WakePaintEvents == 1
                    && f.trail.SourceObservationTime == frame.timeSeconds, "completed source frame was not consumed");
                Require(Vector3.Distance(PeakWorld(f.trail), frame.boatPosition) < 10,
                    "wake followed the live transform instead of the recorded body pose");
            }
        }
        static void PaddleMotionSource()
        {
            using (var f = new Fixture())
            {
                var session = Session(f.root);
                session.wave.backgroundCurrentMps = Vector2.zero;
                var paddle = new GameObject("wet stationary paddle");
                paddle.transform.SetParent(f.root.transform, false);
                paddle.transform.position = new Vector3(1.25f, -0.5f, 0f);
                var hydro = paddle.AddComponent<PaddleHydrodynamics>();
                hydro.externallyStepped = true; hydro.requireValidInput = false;
                hydro.applyPropulsion = hydro.applyForceToRigidbody = false;
                typeof(PaddleHydrodynamics).GetField("_wave", Private).SetValue(hydro, session.wave);
                session.hydrodynamics = new[] { hydro };
                f.trail.Bind(session);
                var frame = new SimulationFrame { fixedStepSeconds = 0.01f, boatRotation = Quaternion.identity,
                    paddles = new[] { new PaddleSample { valid = true, bladePosition = paddle.transform.position } } };
                for (int i = 0; i < 20; i++)
                {
                    hydro.Step(0.01f); frame.timeSeconds += 0.01; Emit(session, frame);
                }
                Require(hydro.BladeInWater && hydro.RelativeSpeed == 0f, "stationary wet fixture was not established");
                Require(f.trail.PaddlePaintEvents == 0, "stationary submerged paddle continuously generated foam");
                foreach (float value in Field<float[]>(f.trail, "_density"))
                    Require(value == 0f, "stationary submerged paddle left a white patch");
                session.wave.backgroundCurrentMps = new Vector2(0.4f, 0f);
                frame.boatVelocity = new Vector3(0.4f, 0f, 0f);
                hydro.Step(0.01f); frame.timeSeconds += 0.01; Emit(session, frame);
                Require(f.trail.PaddlePaintEvents == 1 && f.trail.WakePaintEvents == 0,
                    "water moving past a held paddle did not generate local churn");
                session.wave.backgroundCurrentMps = Vector2.zero; frame.boatVelocity = Vector3.zero;
                paddle.transform.rotation = Quaternion.Euler(0f, 0f, 2f);
                hydro.Step(0.01f); frame.timeSeconds += 0.01; Emit(session, frame);
                Require(f.trail.PaddlePaintEvents == 2, "pure submerged rotation did not generate local churn");
            }
        }
        static void HullFoamSource()
        {
            var root = new GameObject("HullFoamSource");
            BoatFoamWake wake = null;
            try
            {
                var session = Session(root);
                var body = root.AddComponent<Rigidbody>();
                body.useGravity = false;
                var child = new GameObject("HullFoam"); child.transform.SetParent(root.transform);
                wake = child.AddComponent<BoatFoamWake>(); wake.boatBody = body; wake.wave = session.wave;
                if (Field<Material>(wake, "_mat") == null) Call(wake, "Awake");
                var frame = new SimulationFrame { timeSeconds = 1, fixedStepSeconds = 0.01f,
                    boatPosition = Vector3.zero, boatRotation = Quaternion.identity,
                    boatVelocity = new Vector3(0.3f, 1f, 0f), paddles = Array.Empty<PaddleSample>() };
                body.velocity = frame.boatVelocity;
                Emit(session, frame); Call(wake, "LateUpdate");
                var properties = new MaterialPropertyBlock();
                child.GetComponent<MeshRenderer>().GetPropertyBlock(properties);
                Require(properties.GetColor("_Tint").a == 0f,
                    "common current and vertical bobbing created horizontal hull foam");
                frame.timeSeconds += 0.01; frame.boatVelocity = new Vector3(0.3f, 0f, 1f);
                body.velocity = Vector3.zero; // Render-time state must not replace the completed observation.
                Emit(session, frame); Call(wake, "LateUpdate");
                child.GetComponent<MeshRenderer>().GetPropertyBlock(properties);
                Require(properties.GetColor("_Tint").a > 0f, "completed forward motion did not drive hull foam");
                Reset(session); Call(wake, "LateUpdate");
                child.GetComponent<MeshRenderer>().GetPropertyBlock(properties);
                Require(properties.GetColor("_Tint").a == 0f, "timeline reset retained the previous hull wake");
            }
            finally
            {
                if (wake != null)
                {
                    var destroy = typeof(BoatFoamWake).GetMethod("OnDestroy", Private);
                    if (destroy != null) destroy.Invoke(wake, null);
                    else
                    {
                        var material = Field<Material>(wake, "_mat");
                        if (material != null) { UnityEngine.Object.DestroyImmediate(material.mainTexture); UnityEngine.Object.DestroyImmediate(material); }
                        UnityEngine.Object.DestroyImmediate(Field<Mesh>(wake, "_mesh"));
                    }
                }
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void ResetAndDispose()
        {
            var root = new GameObject("FoamResetSession");
            try
            {
                var session = Session(root);
                Texture2D texture;
                using (var f = new Fixture())
                {
                    f.trail.Bind(session); f.Paint(Vector3.zero);
                    int epoch = f.trail.TimelineEpoch, uploads = f.trail.TextureUploads;
                    Reset(session);
                    Require(f.trail.TimelineEpoch == epoch + 1 && f.trail.AnchorParams.w == 0
                        && f.trail.TextureUploads == uploads, "reset exposed old history or bypassed the upload cap");
                    Call(f.trail, "UploadIfDue", 1f / 30f);
                    foreach (var p in f.trail.FoamTexture.GetPixels32()) Require(p.r == 0, "reset retained old pixels");
                    texture = f.trail.FoamTexture;
                }
                Require(texture == null && InteractionFoamTrail.Instance == null, "destroy leaked native texture or singleton");
                Require(Field<Action>(session, "TimelineReset") == null
                    && Field<Action<SimulationFrame>>(session, "FrameCompleted") == null, "destroy left subscribed callbacks");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
