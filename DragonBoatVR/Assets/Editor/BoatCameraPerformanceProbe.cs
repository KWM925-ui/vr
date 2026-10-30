using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DragonBoatVR.EditorTools
{
    // Controlled component measurements at saved real Player poses. This is
    // separate from normal-loop acceptance and never saves the opened scenes.
    public static class BoatCameraPerformanceProbe
    {
        [Serializable] public class Sample
        {
            public string name, boatModelId;
            public int selectedScene, frame;
            public Vector3 boatPosition, twinLookAt;
            public Quaternion boatRotation;
            public float twinYaw, twinPitch, twinDistance, twinAspect, twinFieldOfView, twinNearClipPlane;
        }
        [Serializable] public class Fixture { public string rawSha256; public Sample[] samples; }
        [Serializable] public class Region
        {
            public string solid;
            public Bounds worldBounds;
            public bool hullBundleMayHit;
        }
        [Serializable] public class Result
        {
            public Sample input;
            [NonSerialized] public BoatViewOcclusion.Pose applied;
            public float appliedYaw, appliedPitch, appliedDistance;
            public Vector3 appliedEye;
            public bool clear, adjusted;
            public int vertices, witnesses, groups, queries, blocked, outside;
            public double[] solveMilliseconds;
            public List<Region> regions = new List<Region>();
        }
        [Serializable] public class Report
        {
            public string scope = "Saved Player poses; isolated component timing and independent original-vertex PhysX/projection checks, not normal runtime acceptance.";
            public string rawSha256;
            public List<Result> cases = new List<Result>();
        }
        static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static T Field<T>(BoatViewOcclusion solver, string name) =>
            (T)typeof(BoatViewOcclusion).GetField(name, Private).GetValue(solver);

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string fixturePath = args[Array.IndexOf(args, "-cameraFixture") + 1];
            string outputPath = args[Array.IndexOf(args, "-cameraReport") + 1];
            var fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(fixturePath));
            var report = new Report { rawSha256 = fixture.rawSha256 };
            var previousMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                string currentBoat = null;
                BoatBuoyancy boat = null;
                Camera camera = null;
                RiverEnvironmentController environment = null;
                Vector3[] vertices = null;
                foreach (Sample input in fixture.samples)
                {
                    if (currentBoat != input.boatModelId)
                    {
                        EditorSceneManager.OpenScene("Assets/Scenes/" + BoatModelChoice.SceneName(input.boatModelId) + ".unity", OpenSceneMode.Single);
                        boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                        camera = GameObject.Find("TwinCamera").GetComponent<Camera>();
                        environment = UnityEngine.Object.FindObjectOfType<RiverEnvironmentController>();
                        if (environment == null) environment = new GameObject("Camera probe environment").AddComponent<RiverEnvironmentController>();
                        var original = new List<Vector3>();
                        foreach (MeshFilter mesh in boat.transform.Find("RealDragonBoat").GetComponentsInChildren<MeshFilter>())
                        {
                            var renderer = mesh.GetComponent<Renderer>();
                            if (mesh.sharedMesh == null || renderer == null || !renderer.enabled) continue;
                            Matrix4x4 matrix = Matrix4x4.identity;
                            for (Transform node = mesh.transform; node != boat.transform; node = node.parent)
                                matrix = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * matrix;
                            foreach (Vector3 point in mesh.sharedMesh.vertices) original.Add(matrix.MultiplyPoint3x4(point));
                        }
                        vertices = original.ToArray();
                        currentBoat = input.boatModelId;
                    }
                    environment.SetScene((RiverEnvironmentController.SceneProfile)input.selectedScene);
                    var body = boat.GetComponent<Rigidbody>();
                    body.position = input.boatPosition; body.rotation = input.boatRotation;
                    boat.transform.SetPositionAndRotation(input.boatPosition, input.boatRotation);
                    Physics.SyncTransforms();
                    camera.aspect = input.twinAspect; camera.fieldOfView = input.twinFieldOfView;
                    camera.nearClipPlane = input.twinNearClipPlane;
                    float heading = Mathf.Atan2(boat.transform.forward.x, boat.transform.forward.z) * Mathf.Rad2Deg;
                    var solver = new BoatViewOcclusion(boat.transform, camera);
                    var result = new Result { input = input, vertices = solver.SurfaceVertexCount,
                        witnesses = solver.WitnessCount, solveMilliseconds = new double[7] };
                    solver.Resolve(input.twinLookAt, heading, input.twinYaw, input.twinPitch, input.twinDistance, .02f, true, environment);
                    for (int i = 0; i < result.solveMilliseconds.Length; i++)
                    {
                        long started = Stopwatch.GetTimestamp();
                        result.applied = solver.Resolve(input.twinLookAt, heading, input.twinYaw,
                            input.twinPitch, input.twinDistance, .02f, true, environment);
                        result.solveMilliseconds[i] = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    }
                    result.clear = solver.Clear; result.adjusted = solver.Adjusted; result.queries = solver.Queries;
                    result.groups = Field<ICollection>(solver, "_surfaceGroups").Count;
                    Vector3 eye = result.applied.Eye(input.twinLookAt, heading);
                    result.appliedYaw = result.applied.yaw; result.appliedPitch = result.applied.pitch;
                    result.appliedDistance = result.applied.distance; result.appliedEye = eye;
                    camera.transform.SetPositionAndRotation(eye, result.applied.Rotation(heading));
                    var solids = Field<List<Collider>>(solver, "_solids");
                    var bounds = Field<List<Bounds>>(solver, "_surfaceRegions");
                    var regionSolids = Field<List<int>>(solver, "_surfaceRegionSolids");
                    var localBounds = Field<List<Bounds>>(solver, "_localSolidBounds");
                    var mayHit = typeof(BoatViewOcclusion).GetMethod("BundleMayHit", BindingFlags.Static | BindingFlags.NonPublic);
                    for (int i = 0; i < bounds.Count; i++) result.regions.Add(new Region {
                        solid = solids[regionSolids[i]].name, worldBounds = bounds[i],
                        hullBundleMayHit = (bool)mayHit.Invoke(null, new object[] { Field<Bounds>(solver, "_localBounds"),
                            boat.transform.InverseTransformPoint(eye), localBounds[i] }) });
                    // Independent exhaustive native rays use the imported
                    // original vertices, with no solver bundle/group pruning.
                    foreach (Vector3 local in vertices)
                    {
                        Vector3 point = boat.transform.TransformPoint(local);
                        Vector3 p = camera.WorldToViewportPoint(point);
                        if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1 || p.z < camera.nearClipPlane) result.outside++;
                        Vector3 delta = eye - point; var ray = new Ray(point, delta.normalized);
                        foreach (Collider solid in solids)
                        {
                            if (solid == null || !solid.enabled || !solid.gameObject.activeInHierarchy) continue;
                            if (!solid.bounds.IntersectRay(ray, out float entry) || entry > delta.magnitude) continue;
                            if (solid.Raycast(ray, out _, delta.magnitude)) { result.blocked++; break; }
                        }
                    }
                    report.cases.Add(result);
                    Debug.Log("[CAMERA_COST] " + input.name + " query=" + result.queries + " blocked=" + result.blocked + " outside=" + result.outside);
                }
                File.WriteAllText(outputPath, JsonUtility.ToJson(report, true));
                Debug.Log("[CAMERA_COST] complete cases=" + report.cases.Count);
            }
            finally { Physics.simulationMode = previousMode; }
        }
    }
}
