using System;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class RiverAudioMotionSelfTest
    {
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("wet relative flow drives stroke while a coasting hull remains audible", WaterMotion, ref passed, ref failed);
            Check("stationary and uniform translation do not creak; changing load does", CreakMotion, ref passed, ref failed);
            Check("real shaft/rail approach creates one located wood event", ShaftContact, ref passed, ref failed);
            Check("blade edge contact is detected independently of the shaft", BladeContact, ref passed, ref failed);
            Check("reset revokes held levels and accepts a new motion timeline", Reset, ref passed, ref failed);
            Check("disabled audio rejects contacts and resets held motion", Disabled, ref passed, ref failed);
            Debug.Log($"[AUDIO_MOTION] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[AUDIO_MOTION] RESULT=PASS" : "[AUDIO_MOTION] RESULT=FAIL");
        }
        static void Check(string name, Action action, ref int passed, ref int failed)
        {
            try { action(); passed++; Debug.Log($"[AUDIO_MOTION] {name}=True"); }
            catch (Exception e) { failed++; Debug.LogError($"[AUDIO_MOTION] {name}: {e}"); }
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

        sealed class Fixture : IDisposable
        {
            readonly GameObject root;
            public readonly RiverAudioController audio;
            public readonly SimulationSession session;
            public readonly WaterInteractionVisuals effects;
            public Fixture()
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                root = new GameObject("audio motion fixture");
                var boatObject = new GameObject("boat"); boatObject.transform.SetParent(root.transform, false);
                var boat = boatObject.AddComponent<BoatBuoyancy>(); boat.GetComponent<Rigidbody>().isKinematic = true;
                var railObject = new GameObject("GunwaleTrimStarboard"); railObject.transform.SetParent(boatObject.transform, false);
                var rail = railObject.AddComponent<LineRenderer>(); rail.useWorldSpace = false; rail.positionCount = 2; rail.widthMultiplier = 0.025f;
                rail.SetPosition(0, new Vector3(0.6f, 0.2f, -1)); rail.SetPosition(1, new Vector3(0.6f, 0.2f, 1));
                var paddleObject = new GameObject("paddle"); paddleObject.transform.SetParent(root.transform, false);
                var driver = paddleObject.AddComponent<PaddleDriver>(); driver.boat = boat;
                var hydro = paddleObject.AddComponent<PaddleHydrodynamics>(); hydro.input = driver;
                var sessionObject = new GameObject("session"); sessionObject.transform.SetParent(root.transform, false);
                session = sessionObject.AddComponent<SimulationSession>(); effects = sessionObject.AddComponent<WaterInteractionVisuals>();
                session.boat = boat; session.drivers = new[] { driver }; session.hydrodynamics = new[] { hydro };
                var audioObject = new GameObject("audio"); audioObject.transform.SetParent(root.transform, false);
                audio = audioObject.AddComponent<RiverAudioController>(); audio.Bind(session);
                Require(audio.AudioReady, "Licensed audio bank did not load");
            }
            public void Observe(RiverAudioMotionSample sample)
            {
                var source = typeof(WaterInteractionVisuals).GetField("PhysicsObserved", BindingFlags.Instance | BindingFlags.NonPublic);
                ((Action<RiverAudioMotionSample>)source.GetValue(effects))?.Invoke(sample);
            }
            public void ResetTimeline()
            {
                var reset = typeof(SimulationSession).GetField("TimelineReset", BindingFlags.Instance | BindingFlags.NonPublic);
                ((Action)reset.GetValue(session))?.Invoke();
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); }
        }
        static RiverAudioMotionSample Sample(double time) => new RiverAudioMotionSample
        {
            timeSeconds = time, stepSeconds = 0.01f, boatRotation = Quaternion.identity,
            paddle = new PaddleSample { valid = true, bladePosition = new Vector3(1f, 0f, 0f), bladeRotation = Quaternion.identity }
        };
        static void WaterMotion()
        {
            using (var f = new Fixture())
            {
                var s = Sample(1); s.wetFraction = 0.8f; s.paddleRelativeSpeed = 1.3f; f.Observe(s);
                Require(f.audio.StrokeWaterLevel > 0.1f && f.audio.HullWaterLevel == 0f, "Wet stroke was silent or stationary hull rushed");
                s.timeSeconds += 0.01; s.paddle.valid = false; s.hullRelativeSpeed = 1.2f; f.Observe(s);
                Require(f.audio.StrokeWaterLevel == 0f && f.audio.HullWaterLevel > 0.1f, "Invalid paddle silenced coasting hull or retained stroke");
                s.timeSeconds += 0.01; s.paddle.valid = true; s.wetFraction = 0f; s.paddleRelativeSpeed = 10f; f.Observe(s);
                Require(f.audio.StrokeWaterLevel == 0f, "Dry paddle generated water rush");
            }
        }
        static void CreakMotion()
        {
            using (var f = new Fixture())
            {
                var s = Sample(1); f.Observe(s);
                s.timeSeconds = 2; s.boatVelocity = new Vector3(0, 0, 1); s.hullRelativeSpeed = 1f; f.Observe(s);
                Require(f.audio.WoodCreaks == 0, "No flex/load yet the hull creaked");
                s.timeSeconds = 2.1; s.boatAngularVelocity = new Vector3(0.2f, 0, 0.1f); f.Observe(s);
                Require(f.audio.WoodCreaks == 1, "Actual rocking did not produce the flex event");
                int observations = f.audio.MotionSourceFrames; f.Observe(s);
                Require(f.audio.MotionSourceFrames == observations && f.audio.WoodCreaks == 1, "Duplicate physical step replayed audio");
                s.timeSeconds += 0.01; f.Observe(s); Require(f.audio.WoodCreaks == 1, "Creak lacks a cooldown");
            }
        }
        static void ShaftContact()
        {
            using (var f = new Fixture())
            {
                var s = Sample(1); s.paddle.bladePosition = new Vector3(0.68f, -0.2f, 0); f.Observe(s);
                s.timeSeconds += 0.01; s.paddle.bladePosition.x = 0.62f; s.paddle.bladeVelocity = Vector3.left; f.Observe(s);
                Require(f.audio.WoodImpacts == 1 && Vector3.Distance(f.audio.LastWoodContactWorld, new Vector3(0.6f, 0.2f, 0)) < 0.001f,
                    "Shaft approach did not emit at the visible gunwale");
                s.paddle.bladeVelocity = Vector3.zero;
                for (int i = 0; i < 30; i++) { s.timeSeconds += 0.01; f.Observe(s); }
                Require(f.audio.WoodImpacts == 1, "Held contact repeated impact sounds");
            }
        }
        static void BladeContact()
        {
            using (var f = new Fixture())
            {
                var s = Sample(1); s.paddle.bladePosition = new Vector3(0.8f, 0.2f, 0); f.Observe(s);
                s.timeSeconds += 0.01; s.paddle.bladePosition.x = 0.69f; s.paddle.bladeVelocity = Vector3.left; f.Observe(s);
                Require(f.audio.WoodImpacts == 1, "Blade hit was missed because the shaft was clear");
            }
        }
        static void Reset()
        {
            using (var f = new Fixture())
            {
                var s = Sample(5); s.wetFraction = 1; s.paddleRelativeSpeed = s.hullRelativeSpeed = 1.8f; f.Observe(s);
                f.ResetTimeline();
                Require(f.audio.MotionSourceTime < 0 && f.audio.StrokeWaterLevel == 0f && f.audio.HullWaterLevel == 0f, "Reset retained motion audio");
                f.Observe(Sample(0.01)); Require(f.audio.MotionSourceTime == 0.01, "New timeline did not recover");
            }
        }
        static void Disabled()
        {
            using (var f = new Fixture())
            {
                var s = Sample(1); s.wetFraction = 1; s.paddleRelativeSpeed = 2; f.Observe(s);
                f.audio.enabled = false;
                // This suite runs outside Play Mode; invoke the engine lifecycle
                // callback explicitly, as the other edit-mode fixtures do.
                typeof(RiverAudioController).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.audio, null);
                Require(!f.audio.EmitWoodImpact(Vector3.zero, 1, 1.1) && f.audio.StrokeWaterLevel == 0f, "Disabled sound still accepts impacts or holds flow");
            }
        }
    }
}
