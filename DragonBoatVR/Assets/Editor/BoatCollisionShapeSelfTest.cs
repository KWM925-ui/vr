using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DragonBoatVR.EditorTools
{
    public static class BoatCollisionShapeSelfTest
    {
        [Serializable] sealed class RayObservation
        {
            public Vector3 originLocal, directionLocal, visualPointLocal, collisionPointLocal;
            public bool visualHit, collisionHit;
        }
        [Serializable] sealed class GeometryReport
        {
            public string scope = "Actual cooked compound versus nonconvex displayed-mesh ray queries; nearest-surface errors require independent analysis.";
            public int parts, vertices, triangles;
            public Vector3 inertiaBefore, inertiaAfter, centreBefore, centreAfter;
            public Vector3 bodyPositionBefore, bodyPositionAfter;
            public Quaternion bodyRotationBefore, bodyRotationAfter;
            public float oldBoxCabinGapM, newCabinGapM, stationSideGapM, partitionFloorGapM;
            public List<RayObservation> rays = new List<RayObservation>();
        }

        public static void Run()
        {
            int passed = 0, failed = 0;
            GameObject reference = null; Mesh referenceMesh = null;
            var mode = Physics.simulationMode; bool backfaces = Physics.queriesHitBackfaces;
            try
            {
                Physics.simulationMode = SimulationMode.Script; Physics.queriesHitBackfaces = true;
                Check("pinned native CoACD bridge preserves a known solid", CheckNativeBridge, ref passed, ref failed);
                EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                typeof(BoatBuoyancy).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boat, null);
                var body = boat.BoatRb; boat.enabled = false;
                var model = boat.transform.Find("RealDragonBoat");
                var combined = new List<CombineInstance>();
                foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
                    if (filter.sharedMesh != null && filter.GetComponent<Renderer>() != null && filter.GetComponent<Renderer>().enabled)
                        combined.Add(new CombineInstance { mesh = filter.sharedMesh,
                            transform = boat.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix });
                referenceMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                referenceMesh.CombineMeshes(combined.ToArray(), true, true, false);
                reference = new GameObject("Unmodified visible wood ray reference");
                reference.transform.SetPositionAndRotation(boat.transform.position, boat.transform.rotation);
                var visible = reference.AddComponent<MeshCollider>(); visible.sharedMesh = referenceMesh;
                var oldBox = boat.GetComponent<BoxCollider>();
                var report = new GeometryReport { inertiaBefore = body.inertiaTensor, centreBefore = body.centerOfMass };
                var cabinRay = new Ray(boat.transform.TransformPoint(new Vector3(0f, 0.8f, 0f)), -boat.transform.up);
                Physics.SyncTransforms();
                bool oldCabinHit = oldBox.Raycast(cabinRay, out RaycastHit oldHit, 2f);
                bool visualCabinHit = visible.Raycast(cabinRay, out RaycastHit woodHit, 2f);
                Require(oldCabinHit && visualCabinHit,
                    "Known cabin surface witness did not hit the two models.");
                report.oldBoxCabinGapM = Vector3.Distance(oldHit.point, woodHit.point);
                Check("known box witness shows the hidden cabin lid", () => Require(report.oldBoxCabinGapM > 0.15f,
                    "Fixture does not detect the superseded contact mismatch."), ref passed, ref failed);
                report.bodyPositionBefore = body.position; report.bodyRotationBefore = body.rotation;
                var shape = BoatHullCollisionShape.Install(boat);
                report.bodyPositionAfter = body.position; report.bodyRotationAfter = body.rotation;
                Check("installing contact geometry preserves the initial body pose", () =>
                {
                    Require(Vector3.Distance(report.bodyPositionBefore, report.bodyPositionAfter) < 1e-5f,
                        "Installing colliders moved the body by " + Vector3.Distance(report.bodyPositionBefore, report.bodyPositionAfter) + " m.");
                    Quaternion before = report.bodyRotationBefore, after = report.bodyRotationAfter;
                    Require(Mathf.Abs(Quaternion.Dot(after, after) - 1f) < 1e-5f
                        && new Vector4(after.x - before.x, after.y - before.y, after.z - before.z, after.w - before.w).magnitude < 1e-5f,
                        "Installing colliders changed the initial unit rotation.");
                }, ref passed, ref failed);
                report.parts = shape.Colliders.Length; report.vertices = shape.CollisionVertices; report.triangles = shape.CollisionTriangles;
                Check("all generated convex parts are cooked and use the same body", () =>
                {
                    Require(shape.Ready && shape.Source.schemaVersion == 2, "Compound source missing.");
                    foreach (var collider in shape.Colliders)
                        Require(collider.enabled && collider.convex && collider.attachedRigidbody == body
                            && collider.sharedMesh.vertexCount <= 120 && collider.sharedMesh.triangles.Length / 3 <= 236
                            && collider.bounds.size.sqrMagnitude > 1e-10f, "Missing or unbounded native contact part.");
                }, ref passed, ref failed);
                Check("cabin contact follows the visible floor instead of a lid", () =>
                {
                    Require(shape.Raycast(cabinRay, out RaycastHit hit, 2f), "Compound missed the cabin floor.");
                    report.newCabinGapM = Vector3.Distance(hit.point, woodHit.point);
                    Require(report.newCabinGapM <= 0.04f, "Native cabin surface differs by " + report.newCabinGapM + " m.");
                }, ref passed, ref failed);
                Check("station side contact follows the visible wood", () =>
                {
                    var ray = new Ray(boat.transform.TransformPoint(new Vector3(0.9f, 0.065f, 0.06f)), -boat.transform.right);
                    bool visualSideHit = visible.Raycast(ray, out RaycastHit visual, 2f);
                    bool collisionSideHit = shape.Raycast(ray, out RaycastHit collision, 2f);
                    Require(visualSideHit && collisionSideHit,
                        "Station side query missed the wood.");
                    report.stationSideGapM = Vector3.Distance(visual.point, collision.point);
                    Require(report.stationSideGapM <= 0.025f, "Native station side differs by " + report.stationSideGapM + " m.");
                }, ref passed, ref failed);
                Check("shared convex cuts cannot drop the visible cabin floor", () =>
                {
                    var ray = new Ray(boat.transform.TransformPoint(new Vector3(-0.15f, 0.8f, 0.25f)), -boat.transform.up);
                    bool wood = visible.Raycast(ray, out RaycastHit visual, 2f);
                    bool native = shape.Raycast(ray, out RaycastHit collision, 2f);
                    Require(wood && native, "Known convex partition witness missed the wood.");
                    report.partitionFloorGapM = Vector3.Distance(visual.point, collision.point);
                    Require(report.partitionFloorGapM <= 0.04f, "Shared cut lost the visible floor by " + report.partitionFloorGapM + " m.");
                }, ref passed, ref failed);
                for (int z = 0; z <= 56; z++)
                {
                    float along = -3.5f + z * 0.125f;
                    for (int h = 0; h <= 10; h++)
                        for (int side = -1; side <= 1; side += 2)
                            Observe(new Vector3(side * 0.9f, -0.25f + h * 0.05f, along), Vector3.left * side, boat.transform, visible, shape, report);
                    for (int x = 0; x <= 12; x++)
                        Observe(new Vector3(-0.45f + x * 0.075f, 0.8f, along), Vector3.down, boat.transform, visible, shape, report);
                }
                // The nonconvex reference exists for queries only, and must leave
                // the physics scene before any real rigidbody step is simulated.
                UnityEngine.Object.DestroyImmediate(reference); reference = null;
                body.useGravity = false; body.velocity = body.angularVelocity = Vector3.zero;
                for (int i = 0; i < 4; i++) Physics.Simulate(0.01f);
                report.inertiaAfter = body.inertiaTensor; report.centreAfter = body.centerOfMass;
                Check("equivalent centre and inertia survive actual physics steps", () => Require(
                    Vector3.Distance(report.inertiaBefore, report.inertiaAfter) < 1e-4f
                    && Vector3.Distance(report.centreBefore, report.centreAfter) < 1e-6f,
                    "Contact replacement changed the independent mass model."), ref passed, ref failed);
                string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-logFile");
                if (index >= 0 && index + 1 < args.Length && args[index + 1] != "-")
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1])), "collision_geometry.json"),
                        JsonUtility.ToJson(report, true));
                Debug.Log($"[BOAT_CONTACT] parts={report.parts} stationGapM={report.stationSideGapM:G8} cabinGapM={report.newCabinGapM:G8} partitionGapM={report.partitionFloorGapM:G8} rays={report.rays.Count}");
            }
            catch (Exception e) { failed++; Debug.LogError("[BOAT_CONTACT] setup: " + e); }
            finally
            {
                if (reference != null) UnityEngine.Object.DestroyImmediate(reference);
                if (referenceMesh != null) UnityEngine.Object.DestroyImmediate(referenceMesh);
                Physics.simulationMode = mode; Physics.queriesHitBackfaces = backfaces;
            }
            Debug.Log($"[BOAT_CONTACT] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[BOAT_CONTACT] RESULT=PASS" : "[BOAT_CONTACT] RESULT=FAIL");
        }

        static void Observe(Vector3 origin, Vector3 direction, Transform boat, MeshCollider visible, BoatHullCollisionShape shape, GeometryReport report)
        {
            var ray = new Ray(boat.TransformPoint(origin), boat.TransformDirection(direction));
            var row = new RayObservation { originLocal = origin, directionLocal = direction };
            row.visualHit = visible.Raycast(ray, out RaycastHit wood, 2f);
            row.collisionHit = shape.Raycast(ray, out RaycastHit hit, 2f);
            if (row.visualHit) row.visualPointLocal = boat.InverseTransformPoint(wood.point);
            if (row.collisionHit) row.collisionPointLocal = boat.InverseTransformPoint(hit.point);
            report.rays.Add(row);
        }
        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try { test(); passed++; Debug.Log("[BOAT_CONTACT] " + name + "=True"); }
            catch (Exception e) { failed++; Debug.LogError("[BOAT_CONTACT] " + name + ": " + e.Message); }
        }
        static void CheckNativeBridge()
        {
            // Exercise the actual plugin ABI and result ownership on a known
            // metre-sized solid, without regenerating the full scanned boat.
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f),
                    new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f),
                    new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f),
                    new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f) },
                triangles = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
                    3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 }
            };
            List<Mesh> parts = null;
            try
            {
                parts = CoacdNative.Decompose(mesh, 0.025f, 24, 20261028);
                Require(parts.Count == 1 && parts[0].vertexCount == 8 && parts[0].triangles.Length == 36,
                    "Native plugin did not return the known convex solid.");
                Require(Vector3.Distance(parts[0].bounds.min, -Vector3.one * 0.5f) < 1e-5f
                    && Vector3.Distance(parts[0].bounds.max, Vector3.one * 0.5f) < 1e-5f,
                    "Native plugin changed the known metre coordinates.");
            }
            finally
            {
                if (parts != null) foreach (var part in parts) UnityEngine.Object.DestroyImmediate(part);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}
