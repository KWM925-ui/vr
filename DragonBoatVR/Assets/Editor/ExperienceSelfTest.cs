using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    public static class ExperienceSelfTest
    {
        const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        const float Dt = 0.01f;
        static readonly MethodInfo BuoyancyStep = typeof(BoatBuoyancy).GetMethod("FixedUpdate", Hidden);
        [Serializable] sealed class Trial
        {
            public float offsetM, steadyRollDeg, returnedRollDeg, maximumRollDeg, finalDraftM;
            public bool massPropertiesUnchanged;
        }
        [Serializable] sealed class Report
        {
            public string scope = "Actual PhysX load couple and buoyancy response; assumed occupant model, not measured human comfort.";
            public int passed, failed;
            public float expectedSmallAngleRollDeg;
            public List<Trial> trials = new List<Trial>();
            public List<string> checks = new List<string>();
        }

        public static void Run()
        {
            var previous = Physics.simulationMode;
            var report = new Report();
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                foreach (float offset in new[] { 0f, 0.012f, -0.012f }) report.trials.Add(LoadTrial(offset));
                // For the declared six-point fixture, dF/dz=mg/(N*draft).
                // Sum(x^2) and buoyancy height over COM set its restoring moment.
                float meanXSquared = (4f * 0.5f * 0.5f + 2f * 0.6f * 0.6f) / 6f;
                report.expectedSmallAngleRollDeg = -0.012f / (meanXSquared / 0.18f + 0.05f) * Mathf.Rad2Deg;
                var centered = report.trials[0]; var right = report.trials[1]; var left = report.trials[2];
                Check(report, Mathf.Abs(centered.steadyRollDeg) < 0.01f && Mathf.Abs(centered.finalDraftM - 0.18f) < 0.001f,
                    "Centered fixture retains its independent equilibrium draft and roll");
                Check(report, Math.Abs(right.steadyRollDeg - report.expectedSmallAngleRollDeg) < 0.03f
                    && right.steadyRollDeg < -0.05f && right.steadyRollDeg > -1.5f,
                    "Right weight shift gives a small right heel matching restoring-moment calculation");
                Check(report, Math.Abs(right.steadyRollDeg + left.steadyRollDeg) < 0.01f && left.steadyRollDeg > 0.05f,
                    "Left and right shifts have opposite, symmetric physical responses");
                foreach (var trial in report.trials)
                {
                    Check(report, trial.massPropertiesUnchanged, "No duplicate mass, COM or inertia change for offset " + trial.offsetM);
                    Check(report, Mathf.Abs(trial.returnedRollDeg) < 0.02f && trial.maximumRollDeg < 2f,
                        "Returning the occupant to centre restores the boat without divergence for offset " + trial.offsetM);
                }
                InputGates(report);
            }
            catch (Exception error) { report.failed++; report.checks.Add(error.ToString()); Debug.LogError(error); }
            finally
            {
                Physics.simulationMode = previous;
                string[] args = Environment.GetCommandLineArgs(); int log = Array.IndexOf(args, "-logFile");
                string output = log >= 0 && log + 1 < args.Length ? Path.GetDirectoryName(args[log + 1]) : ".";
                File.WriteAllText(Path.Combine(output, "experience_physics.json"), JsonUtility.ToJson(report, true));
                Debug.Log($"[EXPERIENCE] passed={report.passed} failed={report.failed}");
                Debug.Log(report.failed == 0 ? "[EXPERIENCE] RESULT=PASS" : "[EXPERIENCE] RESULT=FAIL");
            }
        }

        static BoatBuoyancy Fixture()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("flat-water isolated occupant fixture");
            var box = go.AddComponent<BoxCollider>(); box.size = new Vector3(1.2f, 0.6f, 8f);
            var boat = go.AddComponent<BoatBuoyancy>();
            typeof(BoatBuoyancy).GetMethod("Awake", Hidden).Invoke(boat, null);
            var rb = boat.BoatRb;
            rb.mass = 150f; rb.drag = rb.angularDrag = 0f;
            rb.position = new Vector3(0f, 0.12f, 0f); rb.rotation = Quaternion.identity;
            rb.velocity = rb.angularVelocity = Vector3.zero;
            boat.applyWaveDrift = false;
            Physics.SyncTransforms();
            return boat;
        }

        static Trial LoadTrial(float offset)
        {
            var boat = Fixture(); var rb = boat.BoatRb;
            var load = boat.gameObject.AddComponent<SimulatedPaddlerLoad>();
            Vector3 com = rb.centerOfMass, inertia = rb.inertiaTensor;
            Quaternion inertiaRotation = rb.inertiaTensorRotation;
            var trial = new Trial { offsetM = offset };
            float steadySum = 0f; int count = 0;
            for (int step = 0; step < 1600; step++)
            {
                float time = step * Dt;
                float ramp = time < 2f ? Mathf.SmoothStep(0f, 1f, time / 2f)
                    : time < 8f ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (time - 8f) / 2f);
                load.Apply(boat, Vector3.right * offset * ramp);
                BuoyancyStep.Invoke(boat, null);
                Physics.Simulate(Dt);
                float roll = Mathf.DeltaAngle(0f, rb.rotation.eulerAngles.z);
                trial.maximumRollDeg = Mathf.Max(trial.maximumRollDeg, Mathf.Abs(roll));
                if (time >= 6f && time < 8f) { steadySum += roll; count++; }
            }
            trial.steadyRollDeg = steadySum / count;
            trial.returnedRollDeg = Mathf.DeltaAngle(0f, rb.rotation.eulerAngles.z);
            trial.finalDraftM = 0.3f - rb.position.y;
            trial.massPropertiesUnchanged = rb.mass == 150f && rb.centerOfMass == com
                && rb.inertiaTensor == inertia && rb.inertiaTensorRotation == inertiaRotation && rb.useGravity;
            return trial;
        }

        static void InputGates(Report report)
        {
            var boat = Fixture();
            var load = boat.gameObject.AddComponent<SimulatedPaddlerLoad>();
            var paddle = new GameObject("script input");
            var driver = paddle.AddComponent<PaddleDriver>();
            driver.boat = boat; driver.externallyStepped = true;
            driver.mode = PaddleDriver.Mode.VirtualPaddler; driver.SetPhase01(0.20f);
            driver.Tick(Dt);
            load.Step(boat, driver, driver.Sample.timeSeconds);
            Vector3 offset = load.CenterShiftLocalM, torque = load.TorqueWorldNm;
            Check(report, offset.x > 0f && torque.z < 0f && torque.magnitude > 0f,
                "A fresh executed synthetic paddle observation produces the load couple");
            var camera = new GameObject("independent observer").AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(-12f, 5f, 20f), Quaternion.Euler(60f, 90f, 0f));
            load.Step(boat, driver, driver.Sample.timeSeconds);
            Check(report, offset == load.CenterShiftLocalM && torque == load.TorqueWorldNm,
                "Moving only the observer cannot change occupant loading");
            load.Step(boat, driver, driver.Sample.timeSeconds + driver.inputTimeoutSeconds + 0.001f);
            Check(report, load.CenterShiftLocalM == Vector3.zero && load.TorqueWorldNm == Vector3.zero,
                "Stale paddle observations do not continue simulated body input");
            load.Step(boat, driver, driver.Sample.timeSeconds - 0.001f);
            Check(report, load.TorqueWorldNm == Vector3.zero, "Future observations are not treated as body input");
            driver.mode = PaddleDriver.Mode.External;
            load.Step(boat, driver, driver.Sample.timeSeconds);
            Check(report, load.TorqueWorldNm == Vector3.zero, "Real external input is not an inferred human-body measurement");
            Check(report, !load.ValidOffset(new Vector3(float.NaN, 0f, 0f)) && !load.ValidOffset(Vector3.right * .5f),
                "Malformed or excessive internal weight shifts are rejected");
            Object.DestroyImmediate(paddle); Object.DestroyImmediate(camera.gameObject);
        }

        static void Check(Report report, bool passed, string message)
        {
            if (passed) report.passed++; else report.failed++;
            report.checks.Add(message + (passed ? " PASS" : " FAIL"));
            Debug.Log("[EXPERIENCE] " + message + "=" + passed);
        }
    }
}
