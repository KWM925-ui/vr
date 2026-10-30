using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Checks the replaceable first-version river audio sources.</summary>
    public static class RiverAudioSelfTest
    {
        [MenuItem("DragonBoatVR/⑥ 河道声音事件自检", priority = 6)]
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var audioObject = new GameObject("RiverAudio_AudioTest");
            var controller = audioObject.AddComponent<RiverAudioController>();
            var sessionObject = new GameObject("Session_AudioTest");
            var session = sessionObject.AddComponent<SimulationSession>();
            var effects = sessionObject.AddComponent<WaterInteractionVisuals>();
            var secondSessionObject = new GameObject("SecondSession_AudioTest");
            var secondSession = secondSessionObject.AddComponent<SimulationSession>();
            var secondEffects = secondSessionObject.AddComponent<WaterInteractionVisuals>();
            try
            {
                controller.Bind(session);
                Call(controller, "Start");

                var sourcesInHierarchy = audioObject.GetComponentsInChildren<AudioSource>(true);
                var waterSource = Get<AudioSource>(controller, "_waterSource");
                var shoreSource = Get<AudioSource>(controller, "_shoreSource");
                var paddleSource = Get<AudioSource>(controller, "_paddleSource");
                var waterClip = Get<AudioClip>(controller, "_waterClip");
                var shoreClip = Get<AudioClip>(controller, "_shoreClip");
                var splash = Get<AudioClip>(controller, "_splashClip");
                var drip = Get<AudioClip>(controller, "_dripClip");
                var creaks = typeof(RiverAudioController).GetField("_creakClips", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(controller) as AudioClip[];
                bool sources = controller.AudioReady && sourcesInHierarchy.Length == 7
                    && waterSource != null && shoreSource != null && paddleSource != null
                    && waterSource.transform == audioObject.transform && shoreSource.transform == audioObject.transform
                    && paddleSource.transform != audioObject.transform;
                bool clips = ValidClip(waterClip) && ValidClip(shoreClip) && ValidClip(splash) && ValidClip(drip)
                    && waterSource != null && waterSource.clip == waterClip
                    && shoreSource != null && shoreSource.clip == shoreClip
                    && RecordedPayloadValid(waterClip, shoreClip, splash, drip)
                    && creaks != null && creaks.Length == 3 && RecordedPayloadValid(creaks);
                Call(controller, "Update");
                bool initiallySilent = controller.SplashSounds == 0 && controller.DripSounds == 0;

                // Exercise the public binding and real event counters. The old test set
                // _lastExits=1, bypassing the 0/0 state that swallowed the first entry.
                SetAutoProperty(effects, "EntryBursts", 1);
                SetAutoProperty(effects, "LastEntryStrength", 0.5f);
                controller.Bind(session);
                controller.Bind(session);
                Call(controller, "Update");
                Call(controller, "Update");
                bool firstEntry = controller.SplashSounds == 1 && controller.DripSounds == 0;
                float playingSplashGain = paddleSource.volume;
                SetAutoProperty(effects, "ExitEvents", 1);
                Call(controller, "Update");
                Call(controller, "Update");
                // A quiet drip must not turn down a splash already playing on
                // the same AudioSource; each one-shot has its own event gain.
                bool firstExit = controller.SplashSounds == 1 && controller.DripSounds == 1
                    && paddleSource.volume == playingSplashGain;

                // A reset and the first new stroke can occur before the next render
                // Update. Equal old/new totals must still produce the new sounds.
                effects.ClearEffects();
                RaiseTimelineReset(session);
                SetAutoProperty(effects, "EntryBursts", 1);
                SetAutoProperty(effects, "ExitEvents", 1);
                Call(controller, "Update");
                Call(controller, "Update");
                bool reset = controller.SplashSounds == 2 && controller.DripSounds == 2;

                SetAutoProperty(secondEffects, "EntryBursts", 5);
                SetAutoProperty(secondEffects, "ExitEvents", 7);
                controller.Bind(secondSession);
                Call(controller, "Update");
                bool noHistoricalReplay = controller.SplashSounds == 2 && controller.DripSounds == 2;
                SetAutoProperty(secondEffects, "EntryBursts", 6);
                SetAutoProperty(secondEffects, "ExitEvents", 8);
                // A former session must no longer reset the new session's pending events.
                RaiseTimelineReset(session);
                controller.Bind(secondSession);
                Call(controller, "Update");
                Call(controller, "Update");
                bool rebind = controller.SplashSounds == 3 && controller.DripSounds == 3;
                secondEffects.ClearEffects();
                RaiseTimelineReset(secondSession);
                SetAutoProperty(secondEffects, "EntryBursts", 1);
                SetAutoProperty(secondEffects, "ExitEvents", 1);
                Call(controller, "Update");
                bool reboundReset = controller.SplashSounds == 4 && controller.DripSounds == 4;
                bool events = initiallySilent && firstEntry && firstExit && reset
                    && noHistoricalReplay && rebind && reboundReset;
                bool contactAdmission = CheckWaterContactAdmission();
                bool pass = sources && clips && events && contactAdmission && !string.IsNullOrWhiteSpace(controller.provenance);
                Debug.Log($"[AUDIOTEST] ready={controller.AudioReady} sources={sourcesInHierarchy.Length} clips={clips} "
                    + $"wired={waterSource != null}/{shoreSource != null}/{paddleSource != null} "
                    + $"clipSamples={waterClip?.samples}/{shoreClip?.samples}/{splash?.samples}/{drip?.samples} "
                    + $"clipRate={waterClip?.frequency}/{shoreClip?.frequency}/{splash?.frequency}/{drip?.frequency} "
                    + $"clipChannels={waterClip?.channels}/{shoreClip?.channels}/{splash?.channels}/{drip?.channels} "
                    + $"entry/exitSounds={controller.SplashSounds}/{controller.DripSounds} events={events} "
                    + $"initiallySilent={initiallySilent} firstEntry={firstEntry} firstExit={firstExit} "
                    + $"reset={reset} noHistoricalReplay={noHistoricalReplay} rebind={rebind} reboundReset={reboundReset} "
                    + $"contactAdmission={contactAdmission} "
                    + $"provenance={controller.provenance}");
                Debug.Log(pass ? "[AUDIOTEST] RESULT=PASS 水声、岸上环境声和桨音源已建立，可替换素材"
                    : "[AUDIOTEST] RESULT=FAIL 音频源或可替换音源缺失");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(audioObject);
                UnityEngine.Object.DestroyImmediate(sessionObject);
                UnityEngine.Object.DestroyImmediate(secondSessionObject);
            }
        }

        // Reproduce completed physical observations through the real water-effect
        // event subscription and audio Update. The two tiny fractions come from
        // 165937/line 5523 and 005210/line 9216; no output counters are injected.
        static bool CheckWaterContactAdmission()
        {
            var root = new GameObject("WaterContactAdmission_AudioTest");
            try
            {
                var boatObject = new GameObject("boat"); boatObject.transform.SetParent(root.transform);
                var boat = boatObject.AddComponent<BoatBuoyancy>(); Call(boat, "Awake");
                boat.BoatRb.isKinematic = true;
                var wave = root.AddComponent<WaveField>();
                var hydro = root.AddComponent<PaddleHydrodynamics>();
                var session = root.AddComponent<SimulationSession>();
                session.boat = boat; session.wave = wave; session.hydrodynamics = new[] { hydro };
                var effects = root.AddComponent<WaterInteractionVisuals>(); effects.Bind(session);
                var audio = root.AddComponent<RiverAudioController>(); audio.Bind(session);
                var completed = typeof(SimulationSession).GetField("FrameCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
                double time = 0;
                bool rawUnchanged = true;
                float observedWet = -1;
                effects.PhysicsObserved += sample => observedWet = sample.wetFraction;
                Action<float, bool> step = (fraction, valid) =>
                {
                    SetAutoProperty(hydro, "InputValid", valid);
                    SetAutoProperty(hydro, "BladeInWater", valid && fraction > 0);
                    SetAutoProperty(hydro, "WettedAreaM2", fraction * hydro.bladeArea);
                    SetAutoProperty(hydro, "RelativeSpeed", 1f);
                    hydro.transform.position = new Vector3(1, 0, 0);
                    SetAutoProperty(hydro, "WaterForceN", new Vector3(1, 2, 3));
                    float physicalArea = hydro.WettedAreaM2;
                    var frame = new SimulationFrame { fixedStepSeconds = 0.01f, timeSeconds = time,
                        boatRotation = Quaternion.identity, paddles = new[] { new PaddleSample {
                            valid = valid, timeSeconds = time, bladePosition = new Vector3(1, 0, 0),
                            bladeRotation = Quaternion.identity } } };
                    ((Action<SimulationFrame>)completed.GetValue(session))?.Invoke(frame);
                    Call(audio, "Update");
                    rawUnchanged &= hydro.WettedAreaM2 == physicalArea && hydro.WaterForceN == new Vector3(1, 2, 3)
                        && Mathf.Abs(observedWet - (valid ? fraction : 0f)) < 1e-7f;
                    time += 0.01;
                };
                step(0f, true);
                step(5.5689327e-7f, true); step(0f, true);
                step(3.1090378e-5f, true); step(0f, true);
                bool tinySilent = effects.EntryBursts == 0 && effects.ExitEvents == 0
                    && audio.SplashSounds == 0 && audio.DripSounds == 0;

                step(0.0041522183f, true); step(0.3f, true);
                step(0.0004f, true); step(3.1090378e-5f, true);
                bool decliningContact = effects.EntryBursts == 1 && effects.ExitEvents == 0
                    && audio.SplashSounds == 1 && audio.DripSounds == 0;
                step(0f, true); step(0f, true);
                bool oneFullStroke = effects.EntryBursts == 1 && effects.ExitEvents == 1
                    && audio.SplashSounds == 1 && audio.DripSounds == 1;

                step(0.3f, true);
                int beforeInvalidEntry = audio.SplashSounds, beforeInvalidExit = audio.DripSounds;
                step(0.3f, false); step(0.3f, true); step(0.3f, true);
                bool noRecoveryReplay = audio.SplashSounds == beforeInvalidEntry && audio.DripSounds == beforeInvalidExit;

                effects.ClearEffects(); RaiseTimelineReset(session);
                int beforeResetEntry = audio.SplashSounds, beforeResetExit = audio.DripSounds;
                step(0.3f, true); step(0.3f, true);
                bool initialWetSilent = effects.EntryBursts == 0 && effects.ExitEvents == 0
                    && audio.SplashSounds == beforeResetEntry && audio.DripSounds == beforeResetExit;
                step(0f, true); step(0.3f, true);
                bool restartAcceptsStroke = effects.EntryBursts == 1 && effects.ExitEvents == 1
                    && audio.SplashSounds == beforeResetEntry + 1 && audio.DripSounds == beforeResetExit + 1;
                Debug.Log($"[AUDIOTEST] waterContact tinySilent={tinySilent} decliningContact={decliningContact} "
                    + $"oneFullStroke={oneFullStroke} noRecoveryReplay={noRecoveryReplay} initialWetSilent={initialWetSilent} "
                    + $"restartAcceptsStroke={restartAcceptsStroke} rawUnchanged={rawUnchanged}");
                return tinySilent && decliningContact && oneFullStroke && noRecoveryReplay
                    && initialWetSilent && restartAcceptsStroke && rawUnchanged;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void Call(object target, string method)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(target.GetType().Name, method);
            m.Invoke(target, null);
        }

        static void RaiseTimelineReset(SimulationSession session)
        {
            var field = typeof(SimulationSession).GetField("TimelineReset", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(nameof(SimulationSession), "TimelineReset");
            ((Action)field.GetValue(session))?.Invoke();
        }

        static T Get<T>(object target, string field) where T : UnityEngine.Object
        {
            var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(target.GetType().Name, field);
            return f.GetValue(target) as T;
        }

        static bool ValidClip(AudioClip clip) => clip != null && clip.samples > 0
            && clip.channels == 1 && clip.frequency == 22050 && clip.length > 0f;

        static bool RecordedPayloadValid(params AudioClip[] clips)
        {
            foreach (var clip in clips)
            {
                if (clip == null) return false;
                var samples = new float[clip.samples];
                if (!clip.GetData(samples, 0)) return false;
                double energy = 0;
                foreach (float value in samples)
                {
                    if (!SimulationValue.Finite(value) || Mathf.Abs(value) > 1f) return false;
                    energy += value * value;
                }
                if (energy / samples.Length < 1e-8) return false;
            }
            return true;
        }

        static void SetAutoProperty(object target, string property, object value)
        {
            var f = target.GetType().GetField("<" + property + ">k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(target.GetType().Name, property);
            f.SetValue(target, value);
        }
    }
}
