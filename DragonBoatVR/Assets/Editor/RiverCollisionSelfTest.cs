using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class RiverCollisionSelfTest
    {
        [Serializable] sealed class SceneryMesh
        {
            public string boat, scene, name;
            public Bounds renderedBounds;
            public int vertices, triangleIndices, staticFlags;
            public bool readable, hasCollider, matchingCollider;
        }
        [Serializable] sealed class SceneryRay
        {
            public string boat, scene, mesh;
            public Vector3 origin, direction;
            public bool visibleHit, nativeHit;
            public float visibleDistance, nativeDistance;
        }
        [Serializable] sealed class SceneryInspection
        {
            public string scope = "Read the actual serialized scenery in both boat scenes; compare independently intersected rendered terrain triangles with native collider rays. No scene save, boat movement or water/feedback changes.";
            public int passed, failed;
            public List<SceneryMesh> meshes = new List<SceneryMesh>();
            public List<SceneryRay> rays = new List<SceneryRay>();
            public List<TreeSupport> treeSupports = new List<TreeSupport>();
        }
        [Serializable] sealed class TreeSupport
        {
            public string boat, scene, tree;
            public Vector3 anchor;
            public float visibleBottom, visibleTop, groundY;
            public bool nativeGround, grounded;
        }

        // Diagnose the late ordinary-Player screenshot defect using the original
        // scenery, including waterline terrain beyond the finite bank endpoints.
        public static void InspectScenery()
        {
            SceneryInspection report = InspectSceneryData();
            Debug.Log("[RIVER_COLLISION] scenery inspection passed=" + report.passed + " failed=" + report.failed);
            Debug.Log(report.failed == 0 ? "[RIVER_COLLISION] RESULT=PASS" : "[RIVER_COLLISION] RESULT=FAIL");
        }

        static SceneryInspection InspectSceneryData()
        {
            var report = new SceneryInspection();
            foreach (string boat in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
            {
                EditorSceneManager.OpenScene("Assets/Scenes/" + BoatModelChoice.SceneName(boat) + ".unity", OpenSceneMode.Single);
                var environment = UnityEngine.Object.FindObjectOfType<RiverEnvironmentController>();
                if (environment == null) environment = new GameObject("Read-only scenery binding").AddComponent<RiverEnvironmentController>();
                foreach (RiverEnvironmentController.SceneProfile profile in Enum.GetValues(typeof(RiverEnvironmentController.SceneProfile)))
                {
                    environment.SetScene(profile);
                    var root = profile == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland
                        ? environment.xiangjiangScene : environment.fengjianScene;
                    Physics.SyncTransforms();
                    foreach (string name in new[] { "Bank", "Walkway", "Railing", "FarHills", "ShoreGround" })
                    {
                        var part = root != null ? root.transform.Find(name) : null;
                        if (part == null) continue;
                        var filter = part.GetComponent<MeshFilter>();
                        var renderer = part.GetComponent<Renderer>();
                        var collider = part.GetComponent<MeshCollider>();
                        Mesh mesh = filter != null ? filter.sharedMesh : null;
                        bool matching = mesh != null && collider != null && collider.enabled && !collider.isTrigger
                            && !collider.convex && collider.attachedRigidbody == null && collider.sharedMesh == mesh;
                        report.meshes.Add(new SceneryMesh { boat = boat, scene = profile.ToString(), name = name,
                            renderedBounds = renderer != null ? renderer.bounds : new Bounds(),
                            vertices = mesh != null ? mesh.vertexCount : 0,
                            triangleIndices = mesh != null && mesh.isReadable ? mesh.triangles.Length : 0,
                            staticFlags = (int)GameObjectUtility.GetStaticEditorFlags(part.gameObject),
                            readable = mesh != null && mesh.isReadable, hasCollider = collider != null, matchingCollider = matching });
                        if (matching) report.passed++; else report.failed++;
                        if (mesh == null || !mesh.isReadable) continue;
                        Vector3[] vertices = mesh.vertices; int[] indices = mesh.triangles;
                        Matrix4x4 toWorld = part.localToWorldMatrix;
                        for (int i = 0; i < vertices.Length; i++) vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);
                        foreach (float z in new[] { -120f, -90f, 0f, 140f, 225f, 380f, 490f, 520f })
                            foreach (int side in new[] { -1, 1 })
                            {
                                Ray ray = new Ray(new Vector3(0f, 0.4f, z), Vector3.right * side);
                                float nearest = float.PositiveInfinity;
                                for (int i = 0; i < indices.Length; i += 3)
                                    if (RenderedTriangleHit(ray, vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], out float distance))
                                        nearest = Mathf.Min(nearest, distance);
                                bool visible = nearest < 300f;
                                RaycastHit native = default;
                                bool hit = collider != null && collider.Raycast(ray, out native, 300f);
                                float nativeDistance = hit ? native.distance : 0f;
                                report.rays.Add(new SceneryRay { boat = boat, scene = profile.ToString(), mesh = name,
                                    origin = ray.origin, direction = ray.direction, visibleHit = visible, nativeHit = hit,
                                    visibleDistance = visible ? nearest : 0f, nativeDistance = nativeDistance });
                                if (visible && (!hit || Mathf.Abs(nearest - nativeDistance) > 0.001f)) report.failed++;
                                else report.passed++;
                            }
                    }
                    var nature = GameObject.Find("NatureProps");
                    if (nature != null)
                        foreach (Transform tree in nature.transform)
                        {
                            if (!tree.name.Contains("Tree")) continue;
                            float lowest = float.PositiveInfinity, highest = float.NegativeInfinity;
                            foreach (Renderer renderer in tree.GetComponentsInChildren<Renderer>())
                                if (renderer.enabled)
                                { lowest = Mathf.Min(lowest, renderer.bounds.min.y); highest = Mathf.Max(highest, renderer.bounds.max.y); }
                            Ray ray = new Ray(tree.position + Vector3.up * 5f, Vector3.down);
                            float nearest = float.PositiveInfinity, groundY = 0f;
                            foreach (string name in RiverEnvironmentController.SolidSceneMeshes)
                            {
                                var part = root.transform.Find(name);
                                var collider = part != null ? part.GetComponent<MeshCollider>() : null;
                                if (collider != null && collider.Raycast(ray, out RaycastHit hit, 50f) && hit.distance < nearest)
                                { nearest = hit.distance; groundY = hit.point.y; }
                            }
                            bool hitGround = !float.IsPositiveInfinity(nearest);
                            // A trunk may be planted slightly below the raised
                            // walkway. Require support and a visible canopy,
                            // rather than requiring an exposed, tangent root.
                            bool grounded = hitGround && lowest <= groundY + 0.01f && highest > groundY + 0.01f;
                            report.treeSupports.Add(new TreeSupport { boat = boat, scene = profile.ToString(), tree = tree.name,
                                anchor = tree.position, visibleBottom = lowest, visibleTop = highest, groundY = groundY,
                                nativeGround = hitGround, grounded = grounded });
                            if (grounded) report.passed++; else report.failed++;
                        }
                }
            }
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-logFile");
            if (index >= 0 && index + 1 < args.Length)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1])), "scenery_inspection.json"), JsonUtility.ToJson(report, true));
            return report;
        }

        static bool RenderedTriangleHit(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            Vector3 edge1 = b - a, edge2 = c - a, p = Vector3.Cross(ray.direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            distance = 0f;
            if (Mathf.Abs(determinant) < 0.000001f) return false;
            float inverse = 1f / determinant;
            Vector3 t = ray.origin - a;
            float u = Vector3.Dot(t, p) * inverse;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(t, edge1);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(edge2, q) * inverse;
            return distance >= 0f;
        }

        [Serializable] sealed class Trial
        {
            public string scene;
            public int side, steps;
            public Vector3 start, end, initialVelocity, endVelocity;
            public float maxOutwardHullExtent, bankFace = 32f;
            public bool preventedPenetration;
        }
        [Serializable] sealed class Report
        {
            public string scope = "Actual serialized terrain and both existing boat compounds; native PhysX at 0.01 s plus independent rendered-triangle/collider comparisons. No water/propulsion forces in the isolated contact trials.";
            public int passed, failed;
            public List<Trial> trials = new List<Trial>();
            public List<string> checks = new List<string>();
        }
        public static void Run()
        {
            var report = new Report();
            SimulationMode originalMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                typeof(BoatBuoyancy).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boat, null);
                var body = boat.BoatRb;
                var shape = BoatHullCollisionShape.Install(boat);
                Require(shape != null && shape.Ready, "Existing native boat compound is absent.");
                var environment = UnityEngine.Object.FindObjectOfType<RiverEnvironmentController>();
                if (environment == null) environment = new GameObject("Contact fixture environment binding").AddComponent<RiverEnvironmentController>();
                body.isKinematic = false;
                body.useGravity = false;
                body.drag = body.angularDrag = 0f;
                body.interpolation = RigidbodyInterpolation.None;
                body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotation;
                foreach (RiverEnvironmentController.SceneProfile scene in Enum.GetValues(typeof(RiverEnvironmentController.SceneProfile)))
                {
                    environment.SetScene(scene);
                    GameObject root = scene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland ? environment.xiangjiangScene : environment.fengjianScene;
                    Require(root != null, "Actual serialized river root is absent.");
                    var bank = root.transform.Find("Bank");
                    Require(bank != null && bank.GetComponent<MeshFilter>().sharedMesh != null, "Bank display mesh is absent.");
                    int initialColliderCount = root.GetComponentsInChildren<MeshCollider>(true).Length;
                    Check(report, scene + " rendered bank has matching static collision", () =>
                    {
                        var c = bank.GetComponent<MeshCollider>();
                        Require(c != null && c.enabled && !c.convex && !c.isTrigger && c.attachedRigidbody == null
                            && c.sharedMesh == bank.GetComponent<MeshFilter>().sharedMesh,
                            "Bank must collide using the existing rendered mesh.");
                    });
                    foreach (int side in new[] {-1, 1})
                    {
                        Check(report, scene + " bank ray side " + side, () =>
                        {
                            var collider = bank.GetComponent<MeshCollider>();
                            Require(collider != null, "No native bank collider.");
                            Physics.SyncTransforms();
                            Require(collider.Raycast(new Ray(new Vector3(0f, 1f, 0f), Vector3.right * side), out RaycastHit hit, 41f)
                                && Mathf.Abs(hit.point.x - side * 32f) < 0.0001f,
                                "Native bank does not meet the existing visible face at 32 m.");
                        });
                        var trial = new Trial { scene = scene.ToString(), side = side,
                            start = new Vector3(side * 26f, 0.4f, 0f), initialVelocity = Vector3.right * (side * 2f),
                            maxOutwardHullExtent = float.NegativeInfinity };
                        body.position = trial.start; body.rotation = Quaternion.identity;
                        body.velocity = trial.initialVelocity; body.angularVelocity = Vector3.zero;
                        Physics.SyncTransforms();
                        for (int i = 0; i < 400; i++)
                        {
                            Physics.Simulate(0.01f);
                            Bounds bounds = shape.WorldBounds;
                            float outward = side < 0 ? -bounds.min.x : bounds.max.x;
                            trial.maxOutwardHullExtent = Mathf.Max(trial.maxOutwardHullExtent, outward);
                            trial.steps++;
                        }
                        trial.end = body.position; trial.endVelocity = body.velocity;
                        trial.preventedPenetration = trial.maxOutwardHullExtent <= 32.001f;
                        report.trials.Add(trial);
                        Check(report, scene + " native boat cannot cross bank side " + side, () =>
                        {
                            Require((trial.end - trial.start).magnitude > 1f, "Body did not approach the bank; inert fixture cannot pass.");
                            Require(trial.preventedPenetration, "Boat crossed the rendered bank: outward extent=" + trial.maxOutwardHullExtent);
                            Require(side * trial.endVelocity.x < 0.1f, "Native contact did not stop outward velocity.");
                        });
                    }
                    environment.SetScene(scene);
                    Check(report, scene + " rebinding does not duplicate colliders", () => Require(
                        root.GetComponentsInChildren<MeshCollider>(true).Length == initialColliderCount, "Repeated selection added duplicate collision shapes."));
                    if (scene == RiverEnvironmentController.SceneProfile.ShundeFengjian)
                    {
                        Check(report, "Fengjian bridge pier uses existing bank geometry", () =>
                        {
                            var c = bank.GetComponent<MeshCollider>();
                            Require(c != null && c.Raycast(new Ray(new Vector3(17.6f, 0.5f, 140f), Vector3.forward), out RaycastHit hit, 15f)
                                && Mathf.Abs(hit.point.z - 147.2f) < 0.001f, "Visible bridge pier is missing from actual collision.");
                        });
                        Check(report, "Fengjian river remains open beneath bridge centre", () =>
                        {
                            var c = bank.GetComponent<MeshCollider>();
                            Require(c != null && !c.Raycast(new Ray(new Vector3(0f, 0.5f, 140f), Vector3.forward), out _, 20f),
                                "Collision incorrectly fills the bridge opening.");
                        });
                    }
                }
                environment.SetScene(RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
                Check(report, "inactive scene colliders leave the native physics world", () =>
                {
                    Require(!environment.fengjianScene.activeInHierarchy, "Inactive river root remains active.");
                    var c = environment.fengjianScene.transform.Find("Bank").GetComponent<MeshCollider>();
                    Require(c != null && c.bounds.size.sqrMagnitude == 0f, "Inactive river still contributes contact bounds.");
                });
            }
            catch (Exception e) { report.failed++; Debug.LogError("[RIVER_COLLISION] setup: " + e); }
            finally { Physics.simulationMode = originalMode; }
            try
            {
                SceneryInspection scenery = InspectSceneryData();
                report.passed += scenery.passed; report.failed += scenery.failed;
                ExteriorBankTrials(report);
            }
            catch (Exception e) { report.failed++; Debug.LogError("[RIVER_COLLISION] exterior: " + e); }
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-logFile");
            if (index >= 0 && index + 1 < args.Length)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1])), "river_collision.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[RIVER_COLLISION] passed=" + report.passed + " failed=" + report.failed);
            Debug.Log(report.failed == 0 ? "[RIVER_COLLISION] RESULT=PASS" : "[RIVER_COLLISION] RESULT=FAIL");
        }

        static void ExteriorBankTrials(Report report)
        {
            SimulationMode originalMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                foreach (string model in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
                {
                    EditorSceneManager.OpenScene("Assets/Scenes/" + BoatModelChoice.SceneName(model) + ".unity", OpenSceneMode.Single);
                    var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                    typeof(BoatBuoyancy).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boat, null);
                    var body = boat.BoatRb;
                    var shape = BoatHullCollisionShape.Install(boat);
                    Require(shape != null && shape.Ready, "Existing boat compound is absent: " + model);
                    body.isKinematic = false; body.useGravity = false; body.drag = body.angularDrag = 0f;
                    body.interpolation = RigidbodyInterpolation.None;
                    body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotation;
                    var environment = UnityEngine.Object.FindObjectOfType<RiverEnvironmentController>();
                    if (environment == null) environment = new GameObject("Exterior contact fixture binding").AddComponent<RiverEnvironmentController>();
                    foreach (RiverEnvironmentController.SceneProfile scene in Enum.GetValues(typeof(RiverEnvironmentController.SceneProfile)))
                    {
                        environment.SetScene(scene);
                        GameObject root = scene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland
                            ? environment.xiangjiangScene : environment.fengjianScene;
                        var ground = root.transform.Find("ShoreGround")?.GetComponent<MeshCollider>();
                        Require(ground != null && ground.enabled, "Missing visible shore ground collision.");
                        foreach (float z in new[] { -120f, 520f })
                            foreach (int side in new[] { -1, 1 })
                            {
                                Physics.SyncTransforms();
                                Ray ray = new Ray(new Vector3(0f, 0.4f, z), Vector3.right * side);
                                Require(ground.Raycast(ray, out RaycastHit face, 300f), "No actual exterior shore face.");
                                foreach (bool disabledControl in new[] { false, true })
                                {
                                    ground.enabled = !disabledControl;
                                    var trial = new Trial { scene = model + "/" + scene + (disabledControl ? "/collision-disabled-control" : ""), side = side,
                                        start = new Vector3(side * 31f, 0.4f, z), initialVelocity = Vector3.right * (side * 2f),
                                        bankFace = side * face.point.x, maxOutwardHullExtent = float.NegativeInfinity };
                                    body.position = trial.start; body.rotation = Quaternion.identity;
                                    body.velocity = trial.initialVelocity; body.angularVelocity = Vector3.zero;
                                    Physics.SyncTransforms();
                                    for (int i = 0; i < 600; i++)
                                    {
                                        Physics.Simulate(0.01f);
                                        Bounds bounds = shape.WorldBounds;
                                        trial.maxOutwardHullExtent = Mathf.Max(trial.maxOutwardHullExtent,
                                            side < 0 ? -bounds.min.x : bounds.max.x);
                                        trial.steps++;
                                    }
                                    trial.end = body.position; trial.endVelocity = body.velocity;
                                    trial.preventedPenetration = trial.maxOutwardHullExtent <= trial.bankFace + 0.001f;
                                    report.trials.Add(trial);
                                    Check(report, trial.scene + " exterior shore z=" + z + " side=" + side, () =>
                                    {
                                        Require((trial.end - trial.start).magnitude > 1f, "Inert exterior contact fixture.");
                                        if (disabledControl) Require(!trial.preventedPenetration && side * trial.endVelocity.x > 1f,
                                            "Disabling actual terrain did not recover the old crossing.");
                                        else Require(trial.preventedPenetration && side * trial.endVelocity.x < 0.1f,
                                            "Native boat crossed or failed to stop at its rendered exterior shore.");
                                    });
                                    ground.enabled = true;
                                }
                            }
                    }
                }
            }
            finally { Physics.simulationMode = originalMode; }
        }
        static void Check(Report report, string name, Action test)
        {
            try { test(); report.passed++; report.checks.Add(name + " PASS"); Debug.Log("[RIVER_COLLISION] " + name + "=True"); }
            catch (Exception e) { report.failed++; report.checks.Add(name + " FAIL " + e.Message); Debug.LogError("[RIVER_COLLISION] " + name + ": " + e.Message); }
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}
