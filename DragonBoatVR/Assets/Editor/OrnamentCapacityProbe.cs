using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Isolated resource measurements, not a change to the product's
    /// collision allocation limit or an acceptance bypass. No scene is saved.</summary>
    public static class OrnamentCapacityProbe
    {
        [Serializable] sealed class Part { public Vector3[] vertices; public int[] triangles; }
        [Serializable] sealed class Checkpoint { public Part[] parts; }
        [Serializable] sealed class Sample
        {
            public string label;
            public int parts, emptyCookedParts;
            public double constructionSeconds, freeMeanMs, freeMaximumMs, contactMeanMs, contactMaximumMs;
            public long managedBytesAdded, unityBytesAdded, workingSetBytesAdded;
            public Vector3 finalPosition, finalVelocity;
        }
        [Serializable] sealed class Report
        {
            public string scope = "PROTOTYPE_RESOURCE_MEASUREMENT_ONLY: standalone PhysX compound, no product guard change, no full scene/rendering or hardware acceptance";
            public string unityVersion;
            public int productPartLimit;
            public List<Sample> measurements = new List<Sample>();
        }

        public static void Run()
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath));
            var report = new Report { unityVersion = Application.unityVersion,
                productPartLimit = BoatHullCollisionShape.MaximumParts };
            var previous = Physics.simulationMode;
            var created = new List<Mesh>();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Physics.simulationMode = SimulationMode.Script;
                report.measurements.Add(Measure("existing majesty reference", Resources.LoadAll<Mesh>("Collision/DragonBoatHull")));
                foreach (string relative in new[] { "cad_collision/head.json", "cad_collision_hull/hull_and_details.json" })
                {
                    var input = JsonUtility.FromJson<Checkpoint>(File.ReadAllText(Path.Combine(root,
                        "tmp/boat_selection_20261030", relative)));
                    if (input == null || input.parts == null) throw new InvalidDataException("Missing completed native checkpoint.");
                    foreach (var part in input.parts)
                    {
                        if (part.vertices == null || part.triangles == null || part.vertices.Length < 4
                            || part.vertices.Length > 120 || part.triangles.Length / 3 > 236
                            || part.vertices.Any(v => !SimulationValue.Finite(v)))
                            throw new InvalidDataException("Invalid native convex part.");
                        created.Add(new Mesh { vertices = part.vertices, triangles = part.triangles });
                    }
                }
                // A fixed, explicit experimental allocation. The production
                // loader still refuses this 2090-part candidate above its 1024 cap.
                if (created.Count != 2090) throw new InvalidDataException("Probe candidate count changed; review its scope first.");
                report.measurements.Add(Measure("unadopted ornament candidate", created.ToArray()));
                var args = Environment.GetCommandLineArgs(); int log = Array.IndexOf(args, "-logFile");
                string output = Path.GetDirectoryName(args[log + 1]);
                File.WriteAllText(Path.Combine(output, "capacity_probe.json"), JsonUtility.ToJson(report, true));
                Debug.Log("[CAPACITY_PROBE] MEASURED_ONLY; production allocation guard is unchanged.");
            }
            finally
            {
                Physics.simulationMode = previous;
                foreach (var mesh in created) Object.DestroyImmediate(mesh);
            }
        }

        static Sample Measure(string label, Mesh[] meshes)
        {
            GC.Collect();
            long managed = GC.GetTotalMemory(true), allocated = Profiler.GetTotalAllocatedMemoryLong();
            using var process = Process.GetCurrentProcess(); process.Refresh(); long working = process.WorkingSet64;
            var timer = Stopwatch.StartNew();
            var boat = new GameObject("Isolated collision-capacity measurement");
            var floor = new GameObject("Isolated reference floor");
            var result = new Sample { label = label, parts = meshes.Length };
            try
            {
                var body = boat.AddComponent<Rigidbody>(); body.mass = 150f; body.useGravity = false;
                var box = boat.AddComponent<BoxCollider>(); box.size = new Vector3(1.2f, .32f, 8.5f);
                Vector3 inertia = body.inertiaTensor;
                Quaternion inertiaRotation = body.inertiaTensorRotation;
                var colliders = new List<MeshCollider>();
                foreach (var mesh in meshes)
                {
                    var item = boat.AddComponent<MeshCollider>(); item.enabled = false; item.convex = true;
                    item.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                        | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices
                        | MeshColliderCookingOptions.UseFastMidphase;
                    item.sharedMesh = mesh; colliders.Add(item);
                }
                box.enabled = false;
                foreach (var item in colliders) item.enabled = true;
                Physics.SyncTransforms();
                body.centerOfMass = Vector3.zero; body.inertiaTensor = inertia; body.inertiaTensorRotation = inertiaRotation;
                body.position = Vector3.zero; body.rotation = Quaternion.identity;
                result.constructionSeconds = timer.Elapsed.TotalSeconds;
                result.emptyCookedParts = colliders.Count(c => c.bounds.size.sqrMagnitude < 1e-10f);
                var plane = floor.AddComponent<BoxCollider>(); plane.size = new Vector3(6f, .1f, 14f);
                floor.transform.position = new Vector3(0f, -5f, 0f);
                Physics.SyncTransforms();
                body.velocity = new Vector3(0f, 0f, .5f); body.WakeUp();
                MeasureSteps(body, 600, out result.freeMeanMs, out result.freeMaximumMs);
                body.position = new Vector3(0f, .31f, 0f); body.rotation = Quaternion.identity;
                body.velocity = body.angularVelocity = Vector3.zero; body.useGravity = true;
                floor.transform.position = new Vector3(0f, -.05f, 0f);
                Physics.SyncTransforms(); body.WakeUp();
                MeasureSteps(body, 600, out result.contactMeanMs, out result.contactMaximumMs);
                result.finalPosition = body.position; result.finalVelocity = body.velocity;
                result.managedBytesAdded = GC.GetTotalMemory(false) - managed;
                result.unityBytesAdded = Profiler.GetTotalAllocatedMemoryLong() - allocated;
                process.Refresh(); result.workingSetBytesAdded = process.WorkingSet64 - working;
                Debug.Log($"[CAPACITY_PROBE] {label}: parts={result.parts}, create={result.constructionSeconds:F3}s, "
                    + $"free mean/max={result.freeMeanMs:F3}/{result.freeMaximumMs:F3}ms, contact mean/max={result.contactMeanMs:F3}/{result.contactMaximumMs:F3}ms");
                return result;
            }
            finally { Object.DestroyImmediate(boat); Object.DestroyImmediate(floor); }
        }

        static void MeasureSteps(Rigidbody body, int count, out double mean, out double maximum)
        {
            for (int i = 0; i < 60; i++) { body.WakeUp(); Physics.Simulate(.01f); }
            var timer = new Stopwatch(); double sum = 0.0; maximum = 0.0;
            for (int i = 0; i < count; i++)
            {
                body.AddForce(new Vector3((float)Math.Sin(i * .02) * 2f, 0f, 8f), ForceMode.Force);
                timer.Restart(); Physics.Simulate(.01f); timer.Stop();
                double milliseconds = timer.Elapsed.TotalMilliseconds; sum += milliseconds;
                maximum = Math.Max(maximum, milliseconds);
            }
            mean = sum / count;
        }
    }
}
