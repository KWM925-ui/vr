using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class NatureRockSelfTest
    {
        [Serializable] sealed class Rock
        {
            public string name;
            public Vector3 position;
            public bool correctMaterial, matchingCollider;
            public int meshes, rays;
            public float maximumRayError;
        }
        [Serializable] sealed class Contact
        {
            public string rock;
            public bool colliderEnabled, crossedRockCentre;
            public Vector3 start, end, velocity;
            public float maximumPenetration;
        }
        [Serializable] sealed class RockView
        {
            public string rock;
            public Vector3 boat, requestedEye, appliedEye;
            public int requestedBlockedVertices, appliedBlockedVertices, outsideVertices;
            public int surfaceVertices, nativeQueries;
            public double solveMilliseconds;
            public bool clear;
        }
        [Serializable] sealed class Report
        {
            public string boatModelId, scene;
            public string scope = "Serialized waterline rocks: material identity, native cooked rays against independent mesh triangles, and actual displayed-model compound contacts with disabled-collider counterfactuals. No water or paddle forces in isolated contact trials.";
            public int passed, failed;
            public List<Rock> rocks = new List<Rock>();
            public List<Contact> contacts = new List<Contact>();
            public List<RockView> views = new List<RockView>();
            public List<SurfaceSweep> surfaceSweeps = new List<SurfaceSweep>();
            public List<string> checks = new List<string>();
        }
        [Serializable] sealed class SurfaceSweep
        {
            public string rock;
            public int frames, blockedVertices, outsideVertices, unresolved, nativeQueriesMaximum;
            public float eyeStepMaximum, angleStepMaximum;
            public double solveMedianMilliseconds, solveP95Milliseconds, solveMaximumMilliseconds;
            public List<SweepFrame> samples = new List<SweepFrame>();
        }
        [Serializable] sealed class SweepFrame
        {
            public int step, blocked, outside, queries, constraints;
            public bool clear;
            public Vector3 boat, eye;
            public BoatViewOcclusion.Pose pose;
            public float eyeStep, angleStep;
            public double solveMilliseconds;
            public VertexTrace firstBlocker;
        }
        [Serializable] sealed class VertexTrace
        {
            public Vector3 independentLocal, productionLocal, independentWorld, productionWorld;
            public float localDifference, worldDifference;
            public bool productionRayBlocked, productionRegionRayMayHit, sourceBundleMayHit, groupBundleMayHit;
        }

        public static void Run()
        {
            var report = new Report();
            var mode = Physics.simulationMode;
            Collider[] all = null;
            bool[] enabled = null;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                string[] modelArgs = Environment.GetCommandLineArgs();
                int modelArgument = Array.IndexOf(modelArgs, "-testBoat");
                report.boatModelId = modelArgument >= 0 && modelArgument + 1 < modelArgs.Length
                    ? modelArgs[modelArgument + 1] : BoatModelChoice.Majesty;
                report.scene = "Assets/Scenes/" + BoatModelChoice.SceneName(report.boatModelId) + ".unity";
                EditorSceneManager.OpenScene(report.scene, OpenSceneMode.Single);
                var choice = UnityEngine.Object.FindObjectOfType<BoatModelChoice>();
                Require(choice != null && choice.modelId == report.boatModelId, "Requested boat and scene identity differ.");
                var nature = GameObject.Find("NatureProps");
                Require(nature != null, "Actual NatureProps root is absent.");
                var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/Nature_Rock.mat");
                Require(material != null, "Existing stone material is absent.");
                var objects = new List<GameObject>();
                bool materialsMatch = true, collidersMatch = true, otherPropsUnchanged = true;
                int meshCount = 0, rayCount = 0;
                foreach (Transform child in nature.transform)
                {
                    if (!child.name.StartsWith("Rock_", StringComparison.Ordinal))
                    {
                        otherPropsUnchanged &= child.GetComponentsInChildren<Collider>(true).Length == 0;
                        continue;
                    }
                    objects.Add(child.gameObject);
                    var row = new Rock { name = child.name, position = child.position,
                        correctMaterial = true, matchingCollider = true };
                    foreach (var mesh in child.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var renderer = mesh.GetComponent<Renderer>();
                        if (mesh.sharedMesh == null || renderer == null || !renderer.enabled) continue;
                        row.meshes++; meshCount++;
                        foreach (var actual in renderer.sharedMaterials) row.correctMaterial &= actual == material;
                        var collider = mesh.GetComponent<MeshCollider>();
                        bool matches = collider != null && collider.enabled && !collider.isTrigger && !collider.convex
                            && collider.attachedRigidbody == null && collider.sharedMesh == mesh.sharedMesh
                            && mesh.GetComponents<Collider>().Length == 1;
                        row.matchingCollider &= matches;
                        if (!matches) continue;
                        Physics.SyncTransforms();
                        foreach (Vector3 direction in new[] { Vector3.right, Vector3.left, Vector3.up,
                            Vector3.down, Vector3.forward, Vector3.back })
                        {
                            var ray = new Ray(renderer.bounds.center + direction * (renderer.bounds.size.magnitude + 2f), -direction);
                            float expected = MeshHit(mesh, ray);
                            Require(!float.IsInfinity(expected), "Independent original mesh ray missed " + child.name);
                            Require(collider.Raycast(ray, out RaycastHit hit, 20f), "Native rock ray missed " + child.name);
                            float error = Mathf.Abs(hit.distance - expected);
                            row.maximumRayError = Mathf.Max(row.maximumRayError, error);
                            Require(error < 0.001f, "Native rock surface differs from its displayed triangles: " + error);
                            row.rays++; rayCount++;
                        }
                    }
                    materialsMatch &= row.correctMaterial && row.meshes > 0;
                    collidersMatch &= row.matchingCollider && row.meshes > 0;
                    report.rocks.Add(row);
                }
                Check(report, objects.Count == 32, "All 32 existing waterline rocks are inspected");
                Check(report, materialsMatch, "Every rock uses the existing stone material");
                Check(report, collidersMatch, "Every visible rock mesh has one matching static solid collider");
                Check(report, rayCount == meshCount * 6 && rayCount > 0, "Six independent triangle/native rays per actual rock mesh");
                Check(report, otherPropsUnchanged, "Trees and bushes retain their decorative collision policy");

                var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                Require(boat != null, "Actual boat is absent.");
                typeof(BoatBuoyancy).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boat, null);
                var body = boat.BoatRb;
                var shape = BoatHullCollisionShape.Install(boat);
                Require(shape != null && shape.Ready, "Actual native boat compound is absent.");
                CameraChecks(report, objects, boat);
                body.isKinematic = false; body.useGravity = false;
                body.drag = body.angularDrag = 0f;
                body.interpolation = RigidbodyInterpolation.None;
                body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotation;
                all = UnityEngine.Object.FindObjectsOfType<Collider>(true);
                enabled = new bool[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    enabled[i] = all[i].enabled;
                    if (all[i].attachedRigidbody != body) all[i].enabled = false;
                }
                var selected = new HashSet<string>();
                foreach (var rock in objects)
                {
                    int side = rock.transform.position.x < 0f ? -1 : 1;
                    if (!selected.Add(rock.name + "/" + side)) continue;
                    var mesh = rock.GetComponentInChildren<MeshFilter>();
                    var collider = mesh.GetComponent<MeshCollider>();
                    var bounds = mesh.GetComponent<Renderer>().bounds;
                    foreach (bool solid in new[] { false, true })
                    {
                        if (collider != null) collider.enabled = solid;
                        var trial = new Contact { rock = rock.name + "/" + side, colliderEnabled = solid && collider != null,
                            start = new Vector3(bounds.center.x - side * 3f, 0.4f, bounds.center.z) };
                        body.position = trial.start; body.rotation = Quaternion.identity;
                        body.velocity = Vector3.right * (side * 2f); body.angularVelocity = Vector3.zero;
                        body.WakeUp(); Physics.SyncTransforms();
                        for (int step = 0; step < 250; step++) Physics.Simulate(0.01f);
                        trial.end = body.position; trial.velocity = body.velocity;
                        trial.crossedRockCentre = side * (trial.end.x - bounds.center.x) > 1f;
                        if (collider != null && solid)
                            foreach (var part in body.GetComponentsInChildren<Collider>())
                                if (part.enabled && part.attachedRigidbody == body && Physics.ComputePenetration(
                                    part, part.transform.position, part.transform.rotation, collider,
                                    collider.transform.position, collider.transform.rotation, out _, out float depth))
                                    trial.maximumPenetration = Mathf.Max(trial.maximumPenetration, depth);
                        report.contacts.Add(trial);
                        if (solid)
                            Check(report, trial.colliderEnabled && !trial.crossedRockCentre
                                && side * (trial.end.x - bounds.center.x) < 0f
                                && side * trial.velocity.x < 0.1f && trial.maximumPenetration <= 0.001f
                                && (trial.end - trial.start).magnitude > 0.5f,
                                trial.rock + " moving native boat is stopped by the real rock surface");
                        else
                            Check(report, trial.crossedRockCentre, trial.rock + " disabled-collider counterfactual crosses the rock");
                        if (collider != null) collider.enabled = false;
                    }
                }
                Check(report, selected.Count == 6, "Contact/counterfactual trials cover all three models on both banks");
            }
            catch (Exception error) { report.failed++; Debug.LogError("[NATURE_ROCKS] setup: " + error); }
            finally
            {
                if (all != null && enabled != null)
                    for (int i = 0; i < all.Length; i++) if (all[i] != null) all[i].enabled = enabled[i];
                Physics.simulationMode = mode;
            }
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-logFile");
            if (index >= 0 && index + 1 < args.Length)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1])), "nature_rocks.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[NATURE_ROCKS] passed=" + report.passed + " failed=" + report.failed);
            Debug.Log(report.failed == 0 ? "[NATURE_ROCKS] RESULT=PASS" : "[NATURE_ROCKS] RESULT=FAIL");
        }

        static void CameraChecks(Report report, List<GameObject> rocks, BoatBuoyancy boat)
        {
            var camera = GameObject.Find("TwinCamera").GetComponent<Camera>();
            camera.aspect = 1280f * camera.rect.width / 760f;
            var environment = UnityEngine.Object.FindObjectOfType<RiverEnvironmentController>();
            if (environment == null) environment = new GameObject("Rock view fixture binding").AddComponent<RiverEnvironmentController>();
            environment.SetScene(RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
            var vertices = new List<Vector3>();
            foreach (var mesh in boat.transform.Find("RealDragonBoat").GetComponentsInChildren<MeshFilter>())
                foreach (Vector3 p in mesh.sharedMesh.vertices)
                    vertices.Add(boat.transform.InverseTransformPoint(mesh.transform.TransformPoint(p)));
            float halfWidth = 0f;
            foreach (Vector3 p in vertices) halfWidth = Mathf.Max(halfWidth, Mathf.Abs(p.x));
            foreach (int side in new[] { -1, 1 })
            {
                GameObject rock = rocks.Find(item => item.name == "Rock_1" && Mathf.Sign(item.transform.position.x) == side);
                var collider = rock.GetComponentInChildren<MeshCollider>();
                if (collider == null) { Check(report, false, "Rock camera fixture requires the actual solid on side " + side); continue; }
                var bounds = rock.GetComponentInChildren<Renderer>().bounds;
                Vector3 position = new Vector3(bounds.center.x - side * (bounds.extents.x + halfWidth + .1f), .12f, bounds.center.z);
                boat.BoatRb.position = position; boat.BoatRb.rotation = Quaternion.identity;
                boat.transform.SetPositionAndRotation(position, Quaternion.identity); Physics.SyncTransforms();
                Vector3 focus = position + Vector3.up * .55f;
                var requested = new BoatViewOcclusion.Pose { yaw = -side * 90f, pitch = 50f, distance = 24f };
                var row = new RockView { rock = rock.name + "/" + side, boat = position, requestedEye = requested.Eye(focus, 0f) };
                foreach (Vector3 local in vertices)
                    if (Blocked(collider, boat.transform.TransformPoint(local), row.requestedEye)) row.requestedBlockedVertices++;
                var protection = new BoatViewOcclusion(boat.transform, camera);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var applied = protection.Resolve(focus, 0f, requested.yaw, requested.pitch, requested.distance, .02f, true, environment);
                timer.Stop(); row.solveMilliseconds = timer.Elapsed.TotalMilliseconds;
                row.surfaceVertices = protection.SurfaceVertexCount; row.nativeQueries = protection.Queries;
                row.appliedEye = applied.Eye(focus, 0f); row.clear = protection.Clear;
                camera.transform.SetPositionAndRotation(row.appliedEye, applied.Rotation(0f));
                foreach (Vector3 local in vertices)
                {
                    Vector3 world = boat.transform.TransformPoint(local);
                    if (Blocked(collider, world, row.appliedEye)) row.appliedBlockedVertices++;
                    Vector3 p = camera.WorldToViewportPoint(world);
                    if (p.z < camera.nearClipPlane || p.x < 0f || p.x > 1f || p.y < 0f || p.y > 1f) row.outsideVertices++;
                }
                report.views.Add(row);
                Check(report, row.requestedBlockedVertices > 0, row.rock + " real rock obstructs the requested near-shore view");
                Check(report, row.clear && row.appliedBlockedVertices == 0 && row.outsideVertices == 0,
                    row.rock + " protected view clears the rock and retains the whole original boat mesh");
            }
            // Consecutive real poses exercise the learned constraints, smoothing,
            // full surface checks and broad-phase pruning around every rock model.
            foreach (int side in new[] { -1, 1 }) foreach (string name in new[] { "Rock_1", "Rock_2", "Rock_3" })
            {
                var rock = rocks.Find(item => item.name == name && Mathf.Sign(item.transform.position.x) == side);
                var collider = rock.GetComponentInChildren<MeshCollider>();
                if (collider == null) continue;
                var bounds = rock.GetComponentInChildren<Renderer>().bounds;
                var protection = new BoatViewOcclusion(boat.transform, camera);
                var sweep = new SurfaceSweep { rock = name + "/" + side };
                var times = new List<double>();
                Vector3 previousEye = Vector3.zero; Quaternion previousRotation = Quaternion.identity;
                for (int step = 0; step <= 60; step++)
                {
                    Vector3 position = new Vector3(bounds.center.x - side * (bounds.extents.x + halfWidth + .1f),
                        .12f, bounds.center.z - 1.5f + step * .05f);
                    boat.BoatRb.position = position; boat.BoatRb.rotation = Quaternion.identity;
                    boat.transform.SetPositionAndRotation(position, Quaternion.identity); Physics.SyncTransforms();
                    Vector3 focus = position + Vector3.up * .55f;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var applied = protection.Resolve(focus, 0f, -side * 90f, 50f, 24f, .02f, step == 0, environment);
                    timer.Stop(); times.Add(timer.Elapsed.TotalMilliseconds);
                    Vector3 eye = applied.Eye(focus, 0f); Quaternion rotation = applied.Rotation(0f);
                    camera.transform.SetPositionAndRotation(eye, rotation);
                    var frame = new SweepFrame { step = step, boat = position, eye = eye, pose = applied,
                        clear = protection.Clear, queries = protection.Queries, solveMilliseconds = timer.Elapsed.TotalMilliseconds,
                        constraints = ((List<int>)typeof(BoatViewOcclusion).GetField("_surfaceConstraints", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(protection)).Count };
                    sweep.frames++; if (!protection.Clear) sweep.unresolved++;
                    sweep.nativeQueriesMaximum = Mathf.Max(sweep.nativeQueriesMaximum, protection.Queries);
                    if (step > 0)
                    {
                        frame.eyeStep = Vector3.Distance(eye, previousEye);
                        frame.angleStep = Quaternion.Angle(rotation, previousRotation);
                        sweep.eyeStepMaximum = Mathf.Max(sweep.eyeStepMaximum, frame.eyeStep);
                        sweep.angleStepMaximum = Mathf.Max(sweep.angleStepMaximum, frame.angleStep);
                    }
                    foreach (Vector3 local in vertices)
                    {
                        Vector3 world = boat.transform.TransformPoint(local);
                        if (Blocked(collider, world, eye))
                        {
                            sweep.blockedVertices++; frame.blocked++;
                            if (frame.firstBlocker == null) frame.firstBlocker = TraceVertex(protection, collider, local, world, eye);
                        }
                        Vector3 p = camera.WorldToViewportPoint(world);
                        if (p.z < camera.nearClipPlane || p.x < 0f || p.x > 1f || p.y < 0f || p.y > 1f)
                        { sweep.outsideVertices++; frame.outside++; }
                    }
                    sweep.samples.Add(frame);
                    previousEye = eye; previousRotation = rotation;
                }
                times.Sort(); sweep.solveMedianMilliseconds = times[times.Count / 2];
                sweep.solveP95Milliseconds = times[(int)Math.Ceiling(times.Count * .95) - 1];
                sweep.solveMaximumMilliseconds = times[times.Count - 1];
                report.surfaceSweeps.Add(sweep);
                Check(report, sweep.unresolved == 0 && sweep.blockedVertices == 0 && sweep.outsideVertices == 0,
                    sweep.rock + " consecutive 50 mm travel keeps every original vertex clear and framed");
                Check(report, sweep.eyeStepMaximum < 2f && sweep.angleStepMaximum < 20f,
                    sweep.rock + " consecutive 50 mm travel has no large view jump");
            }
        }
        // Diagnose only after the independent assertion. Reflection here reads
        // the production broad phase without adding constraints or changing the
        // next frame. The original independent ray and all thresholds remain.
        static VertexTrace TraceVertex(BoatViewOcclusion protection, Collider collider,
            Vector3 local, Vector3 world, Vector3 eye)
        {
            object Read(string name) => typeof(BoatViewOcclusion).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(protection);
            var surface = (Vector3[])Read("_localSurface");
            int nearest = 0;
            for (int i = 1; i < surface.Length; i++)
                if ((surface[i] - local).sqrMagnitude < (surface[nearest] - local).sqrMagnitude) nearest = i;
            var matrix = (Matrix4x4)Read("_boatWorld");
            Vector3 production = matrix.MultiplyPoint3x4(surface[nearest]);
            var result = new VertexTrace { independentLocal = local, productionLocal = surface[nearest],
                independentWorld = world, productionWorld = production,
                localDifference = Vector3.Distance(local, surface[nearest]), worldDifference = Vector3.Distance(world, production),
                productionRayBlocked = Blocked(collider, production, eye) };
            Bounds groupBounds = (Bounds)Read("_localBounds");
            foreach (object group in (System.Collections.IEnumerable)Read("_surfaceGroups"))
            {
                var type = group.GetType();
                int start = (int)type.GetField("start").GetValue(group), count = (int)type.GetField("count").GetValue(group);
                if (nearest >= start && nearest < start + count)
                { groupBounds = (Bounds)type.GetField("bounds").GetValue(group); break; }
            }
            var solids = (List<Collider>)Read("_solids");
            var regions = (List<Bounds>)Read("_surfaceRegions");
            var regionSolids = (List<int>)Read("_surfaceRegionSolids");
            var localRegions = (List<Bounds>)Read("_localSolidBounds");
            var bundle = typeof(BoatViewOcclusion).GetMethod("BundleMayHit", BindingFlags.Static | BindingFlags.NonPublic);
            Vector3 localEye = ((Matrix4x4)Read("_boatInverse")).MultiplyPoint3x4(eye), delta = eye - production;
            Ray ray = new Ray(production, delta.normalized);
            for (int i = 0; i < regions.Count; i++)
            {
                if (solids[regionSolids[i]] != collider) continue;
                result.productionRegionRayMayHit |= regions[i].IntersectRay(ray, out float entry) && entry <= delta.magnitude;
                result.sourceBundleMayHit |= (bool)bundle.Invoke(null, new object[] { (Bounds)Read("_localBounds"), localEye, localRegions[i] });
                result.groupBundleMayHit |= (bool)bundle.Invoke(null, new object[] { groupBounds, localEye, localRegions[i] });
            }
            return result;
        }
        static bool Blocked(Collider collider, Vector3 start, Vector3 end)
        {
            Vector3 delta = end-start;
            return delta.sqrMagnitude > 0.000001f && collider.Raycast(new Ray(start, delta.normalized), out _, delta.magnitude);
        }

        static float MeshHit(MeshFilter mesh, Ray ray)
        {
            Vector3[] vertices = mesh.sharedMesh.vertices; int[] triangles = mesh.sharedMesh.triangles;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = mesh.transform.TransformPoint(vertices[triangles[i]]);
                Vector3 e1 = mesh.transform.TransformPoint(vertices[triangles[i+1]]) - a;
                Vector3 e2 = mesh.transform.TransformPoint(vertices[triangles[i+2]]) - a;
                Vector3 p = Vector3.Cross(ray.direction, e2); float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 0.000001f) continue;
                Vector3 t = ray.origin-a; float u = Vector3.Dot(t, p)/det;
                if (u < 0f || u > 1f) continue;
                Vector3 q = Vector3.Cross(t, e1); float v = Vector3.Dot(ray.direction, q)/det;
                if (v < 0f || u+v > 1f) continue;
                float distance = Vector3.Dot(e2, q)/det;
                if (distance >= 0f) nearest = Mathf.Min(nearest, distance);
            }
            return nearest;
        }
        static void Check(Report report, bool ok, string name)
        {
            report.checks.Add(name + (ok ? " PASS" : " FAIL"));
            if (ok) report.passed++; else report.failed++;
            Debug.Log("[NATURE_ROCKS] " + name + "=" + ok);
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}
