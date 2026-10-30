using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Opt-in runtime evidence for ordinary river controls. This class
    /// never advances physics, wave time, controller input or the audio clock.</summary>
    public sealed class EnvironmentRuntimeSweep
    {
        [Serializable] public sealed class Sample
        {
            public int scenarioIndex;
            public double simulationTime;
            public string selectedScene, selectedCondition;
            public bool sceneRootsPresent, xiangjiangActive, fengjianActive, transitioning;
            public float intensity, intensityRatePerSecond, targetCurrentSpeedMps, targetCurrentHeadingDegrees, waveTime;
            public Vector2 backgroundCurrentMps;
            public Vector3 boatPosition;
            public Quaternion boatRotation;
        }
        [Serializable] public sealed class Change
        {
            public string reason;
            public Sample before, after;
        }
        [Serializable] public sealed class Report
        {
            public string scope = "Four operator-selected environment cases in one continuous independent UDP loop, including current retarget during a transition; unmeasured environmental parameters.";
            public double startSimulationTime;
            public float caseDurationSeconds = 8f;
            public RiverEnvironmentController.Configuration configuration;
            public List<Change> changes = new List<Change>();
        }

        public const double NormalDurationSeconds = 32;
        public readonly Report Evidence;
        readonly SimulationSession _session;
        readonly RiverEnvironmentController _environment;
        int _scenario = -1;
        bool _retargeted;
        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-environmentSweep") >= 0;

        public EnvironmentRuntimeSweep(SimulationSession session)
        {
            _session = session; _environment = session.riverEnvironment;
            if (_environment == null) throw new InvalidOperationException("Runtime river controls are missing.");
            var configuration = JsonUtility.FromJson<RiverEnvironmentController.Configuration>(File.ReadAllText(
                Path.Combine(Application.streamingAssetsPath, "river_environment.json")));
            if (configuration == null || !configuration.Valid) throw new InvalidDataException("Environment evidence configuration is invalid.");
            Evidence = new Report { startSimulationTime = session.SimulationTime, configuration = configuration };
            Update();
        }

        public void Update()
        {
            double elapsed = _session.SimulationTime - Evidence.startSimulationTime;
            int next = Math.Min(3, (int)(elapsed / Evidence.caseDurationSeconds));
            if (next != _scenario)
            {
                var before = Observe(); _scenario = next;
                _environment.SetScene(next < 2 ? RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland
                    : RiverEnvironmentController.SceneProfile.ShundeFengjian);
                _environment.SetCondition(next == 1 || next == 2 ? RiverEnvironmentController.WaterCondition.Windy
                    : RiverEnvironmentController.WaterCondition.Calm);
                switch (next)
                {
                    case 0: _environment.SetCurrent(Evidence.configuration.defaultCurrentSpeedMps,
                        Evidence.configuration.defaultCurrentHeadingDegrees); break;
                    case 1: _environment.SetCurrent(0.20f, 60f); break;
                    case 2: _environment.SetCurrent(0.25f, 300f); break;
                    case 3: _environment.SetCurrent(0f, 0f); break;
                }
                Evidence.changes.Add(new Change { reason = "case_" + next, before = before, after = Observe() });
            }
            // Change the current target before its two-second transition ends.
            // The public control must preserve both its value and first derivative.
            if (_scenario == 2 && !_retargeted && elapsed >= 16.7)
            {
                var before = Observe();
                _environment.SetCurrent(0.10f, 90f); _retargeted = true;
                Evidence.changes.Add(new Change { reason = "current_retarget", before = before, after = Observe() });
            }
        }

        public Sample Observe()
        {
            var wave = _session.wave;
            var body = _session.boat.BoatRb;
            return new Sample { scenarioIndex = _scenario, simulationTime = _session.SimulationTime,
                selectedScene = _environment.SelectedScene.ToString(), selectedCondition = _environment.SelectedCondition.ToString(),
                sceneRootsPresent = _environment.xiangjiangScene != null && _environment.fengjianScene != null,
                xiangjiangActive = _environment.xiangjiangScene != null && _environment.xiangjiangScene.activeInHierarchy,
                fengjianActive = _environment.fengjianScene != null && _environment.fengjianScene.activeInHierarchy,
                transitioning = _environment.IsTransitioning, intensity = wave.intensity,
                intensityRatePerSecond = wave.intensityRatePerSecond, waveTime = wave.SimTime,
                backgroundCurrentMps = wave.backgroundCurrentMps,
                targetCurrentSpeedMps = _environment.TargetCurrentSpeedMps, targetCurrentHeadingDegrees = _environment.TargetCurrentHeadingDegrees,
                boatPosition = body.position, boatRotation = body.rotation };
        }
    }
}
