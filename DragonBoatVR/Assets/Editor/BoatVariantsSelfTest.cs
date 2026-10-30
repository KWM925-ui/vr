using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Read scenes without saving them. Probe actual cooked contacts,
    /// cabin navigation and equivalent dynamics independently for both hulls.</summary>
    public static class BoatVariantsSelfTest
    {
        const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable] sealed class RaySample
        {
            public Vector3 originLocal, directionLocal, visualPointLocal, collisionPointLocal;
            public bool visualHit, collisionHit;
            public string group;
        }
        [Serializable] sealed class Variant
        {
            public string model, scene, collisionSourceKind;
            public int parts, visibleVertices, visibleTriangles;
            public float massKg, clearanceM, stationGapM, floorGapM, longitudinalTravelM, lateralTravelM;
            public Vector3 centerOfMass, inertia, boxSize, home, visibleMinimum, visibleMaximum;
            public Quaternion inertiaRotation;
            public string buoyancyConfiguration;
            public List<RaySample> rays = new List<RaySample>();
        }
        [Serializable] sealed class Report
        {
            public string scope = "Both scene identities, actual PhysX contacts and cabin movements. Surface samples are observations, not proof of whole-surface fidelity.";
            public int passed, failed;
            public List<string> checks = new List<string>();
            public List<Variant> variants = new List<Variant>();
        }

        public static void Run()
        {
            var report = new Report();
            var mode = Physics.simulationMode; bool backfaces = Physics.queriesHitBackfaces;
            try
            {
                Physics.simulationMode = SimulationMode.Script; Physics.queriesHitBackfaces = true;
                foreach (string id in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
                {
                    try { Inspect(id, report); }
                    catch (Exception error) { Check(report, false, id + " setup: " + error); }
                }
                if (report.variants.Count == 2)
                {
                    var a = report.variants[0]; var b = report.variants[1];
                    Check(report, a.massKg == b.massKg && a.massKg == 150f
                        && a.centerOfMass == b.centerOfMass && a.inertia == b.inertia
                        && a.inertiaRotation == b.inertiaRotation && a.boxSize == b.boxSize
                        && a.buoyancyConfiguration == b.buoyancyConfiguration,
                        "Both hulls retain the same declared equivalent dynamics");
                }
            }
            finally
            {
                Physics.simulationMode = mode; Physics.queriesHitBackfaces = backfaces;
                var args = Environment.GetCommandLineArgs(); int log = Array.IndexOf(args, "-logFile");
                string folder = log >= 0 ? Path.GetDirectoryName(args[log + 1]) : ".";
                File.WriteAllText(Path.Combine(folder, "boat_variants.json"), JsonUtility.ToJson(report, true));
                Debug.Log($"[BOAT_VARIANTS] passed={report.passed} failed={report.failed}");
                Debug.Log(report.failed == 0 ? "[BOAT_VARIANTS] RESULT=PASS" : "[BOAT_VARIANTS] RESULT=FAIL");
            }
        }

        static void Inspect(string id, Report report)
        {
            string path = "Assets/Scenes/" + BoatModelChoice.SceneName(id) + ".unity";
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var boat = Object.FindObjectOfType<BoatBuoyancy>();
            var driver = Object.FindObjectOfType<PaddleDriver>();
            var seat = Object.FindObjectOfType<BoatSeatCamera>();
            var choice = boat.GetComponent<BoatModelChoice>();
            if (choice == null || choice.modelId != id) throw new InvalidDataException("Scene/hull identity mismatch.");
            typeof(BoatBuoyancy).GetMethod("Awake", Hidden).Invoke(boat, null);
            var body = boat.BoatRb; body.mass = 150f; boat.enabled = false;
            driver.boat = boat; seat.boat = boat.transform;
            var box = boat.GetComponent<BoxCollider>();
            var result = new Variant { model = id, scene = path, massKg = body.mass,
                centerOfMass = body.centerOfMass, inertia = body.inertiaTensor,
                inertiaRotation = body.inertiaTensorRotation, boxSize = box.size,
                home = seat.DefaultLocalEyePosition, clearanceM = seat.viewClearanceMeters,
                buoyancyConfiguration = JsonUtility.ToJson(boat) };
            report.variants.Add(result);
            Transform model = boat.transform.Find("RealDragonBoat");
            var inputs = model.GetComponentsInChildren<MeshFilter>()
                .Where(f => f.sharedMesh != null && f.GetComponent<Renderer>() != null && f.GetComponent<Renderer>().enabled)
                .Select(f => new CombineInstance { mesh = f.sharedMesh,
                    transform = boat.transform.worldToLocalMatrix * f.transform.localToWorldMatrix }).ToArray();
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.CombineMeshes(inputs, true, true, false);
            result.visibleMinimum = mesh.bounds.min; result.visibleMaximum = mesh.bounds.max;
            result.visibleVertices = mesh.vertexCount; result.visibleTriangles = mesh.triangles.Length / 3;
            var reference = new GameObject("Independent displayed-mesh query reference");
            reference.layer = 2;
            reference.transform.SetPositionAndRotation(boat.transform.position, boat.transform.rotation);
            var visible = reference.AddComponent<MeshCollider>(); visible.sharedMesh = mesh;
            try
            {
                Vector3 beforePosition = body.position; Quaternion beforeRotation = body.rotation;
                var shape = BoatHullCollisionShape.Install(boat);
                if (shape == null || !shape.Ready) throw new InvalidDataException("No complete contact resource.");
                result.parts = shape.Colliders.Length; result.collisionSourceKind = shape.Source.collisionSourceKind;
                Physics.SyncTransforms();
                Check(report, Vector3.Distance(beforePosition, body.position) < 1e-5f
                    && (new Vector4(beforeRotation.x - body.rotation.x, beforeRotation.y - body.rotation.y,
                        beforeRotation.z - body.rotation.z, beforeRotation.w - body.rotation.w)).magnitude < 1e-5f
                    && body.inertiaTensor == result.inertia && body.centerOfMass == result.centerOfMass,
                    id + " contact installation preserves equivalent mass frame and starting pose");
                Check(report, shape.Colliders.All(c => c.enabled && c.convex && c.attachedRigidbody == body
                    && c.sharedMesh.vertexCount <= 120 && c.sharedMesh.triangles.Length / 3 <= 236
                    && c.bounds.size.sqrMagnitude > 1e-10f), id + " every convex part is cooked on the boat body");
                var home = seat.DefaultLocalEyePosition;
                var boundary = seat.CabinBoundary;
                Check(report, seat.viewClearanceMeters == .18f && boundary.Ready
                    && boundary.Contains(new Vector2(home.x, home.z))
                    && !Physics.CheckSphere(boat.transform.TransformPoint(home), seat.viewClearanceMeters,
                        ~(1 << 2), QueryTriggerInteraction.Ignore), id + " default eye has a clear cabin and original 18 cm allowance");
                var floor = Observe("seat floor", home, Vector3.down, 4f, boat.transform, visible, shape, result);
                result.floorGapM = floor.visualHit && floor.collisionHit
                    ? Vector3.Distance(floor.visualPointLocal, floor.collisionPointLocal) : -1f;
                Check(report, result.floorGapM >= 0f && result.floorGapM <= .04f,
                    id + " cooked seat floor matches its visible floor within original 4 cm witness limit");
                float sign = driver.leftSide ? -1f : 1f;
                var station = new Vector3(sign * (Mathf.Max(Mathf.Abs(mesh.bounds.min.x), Mathf.Abs(mesh.bounds.max.x)) + .3f),
                    .065f, driver.pivotLocalPos.z + .06f);
                var side = Observe("paddle station side", station, Vector3.left * sign, 4f,
                    boat.transform, visible, shape, result);
                result.stationGapM = side.visualHit && side.collisionHit
                    ? Vector3.Distance(side.visualPointLocal, side.collisionPointLocal) : -1f;
                Check(report, result.stationGapM >= 0f && result.stationGapM <= .025f,
                    id + " cooked station contact matches visible hull within original 2.5 cm witness limit");
                seat.MoveInCabin(new Vector2(0f, 5f)); float forward = seat.LocalEyePosition.z;
                seat.MoveInCabin(new Vector2(0f, -5f)); seat.MoveInCabin(new Vector2(0f, -5f));
                result.longitudinalTravelM = forward - seat.LocalEyePosition.z;
                seat.ResetView(); seat.MoveInCabin(new Vector2(-3f, 0f)); float port = seat.LocalEyePosition.x;
                seat.MoveInCabin(new Vector2(3f, 0f)); result.lateralTravelM = seat.LocalEyePosition.x - port;
                seat.ResetView();
                Check(report, result.longitudinalTravelM > .5f && result.lateralTravelM > .1f,
                    id + " actual cooked cabin permits forward-backward and side exploration");
                // Dense observations preserve holes, near-misses and ornament
                // contacts explicitly; do not silently treat the liner as the
                // whole visible vessel. Interpret errors in metres after scale.
                for (int z = 0; z <= 48; z++)
                {
                    float along = Mathf.Lerp(mesh.bounds.min.z + .1f, mesh.bounds.max.z - .1f, z / 48f);
                    for (int h = 0; h <= 10; h++)
                        foreach (float sideSign in new[] { -1f, 1f })
                            Observe("hull side", new Vector3(sideSign * (mesh.bounds.extents.x + .5f), -.25f + h * .05f, along),
                                Vector3.left * sideSign, 4f, boat.transform, visible, shape, result);
                    for (int x = -3; x <= 3; x++)
                        Observe("open cabin top", new Vector3(x * .1f, mesh.bounds.max.y + .5f, along), Vector3.down,
                            mesh.bounds.size.y + 1f, boat.transform, visible, shape, result);
                }
                foreach (float end in new[] { -1f, 1f })
                    for (int y = 0; y <= 24; y++)
                        for (int x = -2; x <= 2; x++)
                            Observe(end > 0 ? "bow" : "stern", new Vector3(x * .15f,
                                Mathf.Lerp(mesh.bounds.min.y + .02f, mesh.bounds.max.y - .02f, y / 24f),
                                end > 0 ? mesh.bounds.max.z + .5f : mesh.bounds.min.z - .5f),
                                Vector3.back * end, 10f, boat.transform, visible, shape, result);
            }
            finally { Object.DestroyImmediate(reference); Object.DestroyImmediate(mesh); }
        }

        static RaySample Observe(string group, Vector3 origin, Vector3 direction, float range,
            Transform boat, MeshCollider visible, BoatHullCollisionShape shape, Variant result)
        {
            var ray = new Ray(boat.TransformPoint(origin), boat.TransformDirection(direction));
            var sample = new RaySample { group = group, originLocal = origin, directionLocal = direction };
            sample.visualHit = visible.Raycast(ray, out RaycastHit v, range);
            sample.collisionHit = shape.Raycast(ray, out RaycastHit p, range);
            if (sample.visualHit) sample.visualPointLocal = boat.InverseTransformPoint(v.point);
            if (sample.collisionHit) sample.collisionPointLocal = boat.InverseTransformPoint(p.point);
            result.rays.Add(sample); return sample;
        }

        static void Check(Report report, bool pass, string name)
        {
            if (pass) report.passed++; else report.failed++;
            report.checks.Add(name + (pass ? " PASS" : " FAIL"));
            Debug.Log("[BOAT_VARIANTS] " + name + "=" + pass);
        }
    }
}
