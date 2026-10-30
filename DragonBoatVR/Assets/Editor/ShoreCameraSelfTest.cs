using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class ShoreCameraSelfTest
    {
        [Serializable] sealed class Case
        {
            public string scene;
            public Vector3 boatPosition, cameraPosition;
            public float boatYaw, obstructionDistance;
            public bool actualHullInsideRiver, occluded, hullFits;
            public int examined, outside;
        }
        [Serializable] sealed class Report
        {
            public string scope = "Actual Unity displayed poses and cooked terrain colliders, including both boats outside the finite bank endpoints; controlled fixture poses are not desktop-input acceptance.";
            public int passed, failed;
            public List<Case> cases = new List<Case>();
            public List<ExtendedCase> extended = new List<ExtendedCase>();
            public List<ExtendedCase> exterior = new List<ExtendedCase>();
            public List<FramingResult> framingTransition = new List<FramingResult>();
            public int independentVertexRays, maximumQueries;
            public float maximumSolveMilliseconds, continuityMaximumAngleStep, continuityMaximumEyeStep;
            public int hullWitnessVertices, continuityLargestStepIndex;
            public Vector3 continuityLargestStepBefore, continuityLargestStepAfter;
        }
        [Serializable] sealed class ExtendedCase
        {
            public string scene;
            public Vector3 boatPosition, boatEuler, eye;
            public float requestYaw, requestPitch, requestDistance, appliedYaw, appliedPitch, appliedDistance;
            public int outside, occludedVertices;
            public bool requestedFits, adjusted, clear, physicallyUnchanged;
        }

        [Serializable] sealed class FramingSample
        {
            public int frame;
            public double wallTime;
            public Vector3 boatPosition, twinLookAt;
            public Quaternion boatRotation;
            public float twinYaw, twinPitch, twinDistance, twinAspect, twinFieldOfView, twinNearClipPlane;
        }
        [Serializable] sealed class FramingFixture { public string rawSha256; public FramingSample[] samples; }
        [Serializable] sealed class FramingResult
        {
            public int frame, requestedOutside, appliedOutside;
            public bool adjusted, clear;
        }

        static int OutsideVertices(Vector3[] vertices, Transform boat, Camera camera)
        {
            int outside = 0;
            foreach (Vector3 local in vertices)
            {
                Vector3 p = camera.WorldToViewportPoint(boat.TransformPoint(local));
                if (p.x < 0f || p.x > 1f || p.y < 0f || p.y > 1f || p.z < camera.nearClipPlane) outside++;
            }
            return outside;
        }

        static void RecordedFramingChecks(Report report, BoatBuoyancy boat, Camera camera, RiverEnvironmentController environment)
        {
            var fixture = JsonUtility.FromJson<FramingFixture>(File.ReadAllText("Assets/Editor/ShoreCameraFramingFixture.json"));
            if (fixture.samples == null || fixture.samples.Length < 2 || string.IsNullOrEmpty(fixture.rawSha256))
                throw new InvalidOperationException("Recorded camera-framing fixture is missing.");
            environment.SetScene(RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
            var protection = new BoatViewOcclusion(boat.transform, camera);
            Vector3[] vertices = HullVertices(boat);
            int fittingRequests = 0, croppedRequests = 0, adjusted = 0, lostFraming = 0;
            bool allClear = true;
            for (int i = 0; i < fixture.samples.Length; i++)
            {
                var sample = fixture.samples[i];
                boat.BoatRb.position = sample.boatPosition; boat.BoatRb.rotation = sample.boatRotation;
                boat.transform.SetPositionAndRotation(sample.boatPosition, sample.boatRotation); Physics.SyncTransforms();
                camera.aspect = sample.twinAspect; camera.fieldOfView = sample.twinFieldOfView; camera.nearClipPlane = sample.twinNearClipPlane;
                Vector3 forward = boat.transform.forward;
                float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                Quaternion requested = Quaternion.Euler(sample.twinPitch, heading + sample.twinYaw, 0f);
                camera.transform.SetPositionAndRotation(sample.twinLookAt - requested * Vector3.forward * sample.twinDistance, requested);
                var row = new FramingResult { frame = sample.frame, requestedOutside = OutsideVertices(vertices, boat.transform, camera) };
                if (row.requestedOutside == 0) fittingRequests++; else croppedRequests++;
                float dt = i == 0 ? .02f : (float)(sample.wallTime - fixture.samples[i - 1].wallTime);
                var applied = protection.Resolve(sample.twinLookAt, heading, sample.twinYaw, sample.twinPitch,
                    sample.twinDistance, dt, i == 0, environment);
                camera.transform.SetPositionAndRotation(applied.Eye(sample.twinLookAt, heading), applied.Rotation(heading));
                row.appliedOutside = OutsideVertices(vertices, boat.transform, camera);
                row.adjusted = protection.Adjusted; row.clear = protection.Clear;
                if (row.adjusted) adjusted++;
                if (row.requestedOutside == 0 && row.appliedOutside > 0) lostFraming++;
                allClear &= row.clear; report.framingTransition.Add(row);
            }
            Check(report, fittingRequests > 0 && croppedRequests > 0 && adjusted > 0,
                "recorded bank orbit exercises both intentional crop and whole-hull requests");
            Check(report, allClear, "recorded bank orbit retains clear applied sightlines");
            Check(report, lostFraming == 0, "recorded whole-hull request never becomes cropped by protection or its smoothing");
            Debug.Log("[SHORE_CAMERA] recorded framing input=" + fixture.rawSha256 + " fittingRequests=" + fittingRequests
                + " croppedRequests=" + croppedRequests + " adjusted=" + adjusted + " lostFraming=" + lostFraming);
        }

        static Vector3[] HullVertices(BoatBuoyancy boat)
        {
            var result = new List<Vector3>();
            foreach (var mesh in boat.transform.Find("RealDragonBoat").GetComponentsInChildren<MeshFilter>())
            {
                var renderer = mesh.GetComponent<Renderer>();
                if (mesh.sharedMesh == null || renderer == null || !renderer.enabled) continue;
                Matrix4x4 matrix = boat.transform.worldToLocalMatrix * mesh.transform.localToWorldMatrix;
                foreach (Vector3 vertex in mesh.sharedMesh.vertices) result.Add(matrix.MultiplyPoint3x4(vertex));
            }
            return result.ToArray();
        }

        static bool FullVertexVisibility(Vector3[] vertices, Transform boat, Camera camera, Collider[] solids,
            Report report, out int outside, out int occluded)
        {
            outside = occluded = 0;
            Matrix4x4 matrix = boat.localToWorldMatrix;
            foreach (Vector3 local in vertices)
            {
                Vector3 world = matrix.MultiplyPoint3x4(local);
                Vector3 viewport = camera.WorldToViewportPoint(world);
                if (viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f || viewport.z < camera.nearClipPlane) outside++;
                Vector3 path = camera.transform.position - world;
                bool hitSolid = false;
                foreach (Collider solid in solids)
                {
                    report.independentVertexRays++;
                    if (solid.Raycast(new Ray(world, path.normalized), out RaycastHit hit, path.magnitude)) { hitSolid = true; break; }
                }
                if (hitSolid) occluded++;
            }
            return occluded == 0;
        }

        static void ExtendedChecks(Report report, BoatBuoyancy boat, Camera camera,
            DebugFollowCamera follow, RiverEnvironmentController environment)
        {
            Vector3[] vertices = HullVertices(boat);
            FieldInfo yawField = typeof(DebugFollowCamera).GetField("_yaw", BindingFlags.Instance | BindingFlags.NonPublic);
            int adjusted = 0;
            foreach (RiverEnvironmentController.SceneProfile scene in Enum.GetValues(typeof(RiverEnvironmentController.SceneProfile)))
            {
                environment.SetScene(scene);
                GameObject root = scene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland ? environment.xiangjiangScene : environment.fengjianScene;
                var sceneSolids = new List<Collider>();
                foreach (string name in new[] { "Bank", "Walkway", "Railing", "FarHills", "ShoreGround" })
                {
                    Transform part = root.transform.Find(name);
                    if (part == null) continue; // Empty groups are not emitted by RiverCourseBuilder.
                    Collider collider = part.GetComponent<Collider>();
                    if (collider == null) throw new InvalidOperationException("Existing scenery mesh has no collider: " + name);
                    sceneSolids.Add(collider);
                }
                if (root.transform.Find("Bank") == null) throw new InvalidOperationException("Bank geometry missing.");
                Collider[] solids = sceneSolids.ToArray();
                foreach (int side in new[] { -1, 1 }) for (int index = 0; index < 8; index++)
                {
                    float yaw = index * 45f;
                    Vector3 euler = new Vector3(index % 2 == 0 ? 8f : -8f, yaw, index % 3 == 0 ? 12f : -12f);
                    Quaternion rotation = Quaternion.Euler(euler);
                    float extreme = float.NegativeInfinity;
                    foreach (Vector3 vertex in vertices) extreme = Mathf.Max(extreme, side * (rotation * vertex).x);
                    Vector3 position = new Vector3(side * (32f - 0.25f - extreme), .12f, 0f);
                    boat.BoatRb.position = position; boat.BoatRb.rotation = rotation;
                    boat.transform.SetPositionAndRotation(position, rotation); Physics.SyncTransforms();
                    float requestedYaw = index % 4 == 0 ? -side * 90f : index % 4 == 1 ? 0f : index % 4 == 2 ? 180f : side * 90f;
                    follow.distance = index % 3 == 0 ? 4f : index % 3 == 1 ? 8.5f : 20f;
                    follow.pitch = index % 3 == 0 ? 0f : index % 3 == 1 ? 12f : 40f;
                    yawField.SetValue(follow, requestedYaw);
                    var row = new ExtendedCase { scene = scene.ToString(), boatPosition = position, boatEuler = euler,
                        requestYaw = requestedYaw, requestPitch = follow.pitch, requestDistance = follow.distance, requestedFits = true };
                    Vector3 forward = boat.transform.forward;
                    float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                    Vector3 focus = position; focus.y = Mathf.Lerp(focus.y, .5f, .9f);
                    Quaternion requestedRotation = Quaternion.Euler(follow.pitch, heading + requestedYaw, 0f);
                    camera.transform.SetPositionAndRotation(focus - requestedRotation * Vector3.forward * follow.distance, requestedRotation);
                    foreach (Vector3 local in vertices)
                    {
                        Vector3 point = camera.WorldToViewportPoint(boat.transform.TransformPoint(local));
                        row.requestedFits &= point.x >= 0f && point.x <= 1f && point.y >= 0f && point.y <= 1f && point.z >= camera.nearClipPlane;
                    }
                    var rb = boat.BoatRb;
                    Vector3 physicalPosition = rb.position; Quaternion physicalRotation = rb.rotation;
                    Vector3 velocity = rb.velocity, angularVelocity = rb.angularVelocity, inertia = rb.inertiaTensor, center = rb.centerOfMass;
                    float mass = rb.mass;
                    follow.SendMessage("ResetFollow"); follow.SendMessage("LateUpdate");
                    row.physicallyUnchanged = rb.position.Equals(physicalPosition) && rb.rotation.Equals(physicalRotation)
                        && rb.velocity.Equals(velocity) && rb.angularVelocity.Equals(angularVelocity) && rb.inertiaTensor.Equals(inertia)
                        && rb.centerOfMass.Equals(center) && rb.mass == mass;
                    row.eye = camera.transform.position; row.appliedYaw = follow.AppliedYaw;
                    row.appliedPitch = follow.AppliedPitch; row.appliedDistance = follow.AppliedDistance;
                    row.adjusted = follow.OcclusionAdjusted; row.clear = follow.OcclusionClear;
                    if (row.adjusted) adjusted++;
                    bool visible = FullVertexVisibility(vertices, boat.transform, camera, solids, report, out row.outside, out row.occludedVertices);
                    string label = scene + " side " + side + " tilted case " + index;
                    Check(report, visible && follow.OcclusionAvailable && row.clear, label + " all actual hull vertices have clear terrain sightlines");
                    Check(report, !row.requestedFits || row.outside == 0, label + " preserves whole-hull framing when requested view fits it");
                    Check(report, row.physicallyUnchanged, label + " camera leaves physical state unchanged");
                    Check(report, follow.distance == row.requestDistance && follow.pitch == row.requestPitch
                        && follow.OrbitYaw == row.requestYaw, label + " all requested settings retained");
                    report.maximumQueries = Mathf.Max(report.maximumQueries, follow.OcclusionQueries);
                    report.maximumSolveMilliseconds = Mathf.Max(report.maximumSolveMilliseconds, follow.OcclusionMilliseconds);
                    report.extended.Add(row);
                }
            }
            Check(report, adjusted >= 8, "tilted cases actually exercise obstruction adjustment");

            environment.SetScene(RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
            var protection = new BoatViewOcclusion(boat.transform, camera);
            report.hullWitnessVertices = protection.WitnessCount;
            Quaternion previousRotation = Quaternion.identity;
            Vector3 previousEye = Vector3.zero;
            bool allClear = true;
            BoatViewOcclusion.Pose applied = default;
            const int steps = 620;
            for (int i = 0; i <= steps * 2 + 180; i++)
            {
                float x = i <= steps ? -31f * i / steps : i <= steps * 2 ? -31f * (steps * 2 - i) / steps : 0f;
                Vector3 position = new Vector3(x, .12f, 0f);
                boat.BoatRb.position = position; boat.BoatRb.rotation = Quaternion.identity;
                boat.transform.SetPositionAndRotation(position, Quaternion.identity); Physics.SyncTransforms();
                Vector3 focus = position; focus.y = Mathf.Lerp(focus.y, .5f, .9f);
                applied = protection.Resolve(focus, 0f, 90f, 12f, 8.5f, .02f, i == 0, environment);
                Vector3 eye = applied.Eye(focus, 0f); Quaternion rotation = applied.Rotation(0f);
                allClear &= protection.Available && protection.Clear;
                if (i > 0)
                {
                    report.continuityMaximumAngleStep = Mathf.Max(report.continuityMaximumAngleStep, Quaternion.Angle(previousRotation, rotation));
                    float eyeStep = Vector3.Distance(previousEye, eye);
                    if (eyeStep > report.continuityMaximumEyeStep)
                    {
                        report.continuityMaximumEyeStep = eyeStep; report.continuityLargestStepIndex = i;
                        report.continuityLargestStepBefore = previousEye; report.continuityLargestStepAfter = eye;
                    }
                }
                previousRotation = rotation; previousEye = eye;
            }
            Check(report, allClear, "continuous approach and departure keep the queried sightlines clear");
            Check(report, report.continuityMaximumAngleStep < 20f && report.continuityMaximumEyeStep < 2f,
                "controlled 50 mm approach has no large orbit flip or camera jump");
            Check(report, Mathf.Abs(Mathf.DeltaAngle(applied.yaw, 90f)) < .001f && Mathf.Abs(applied.pitch - 12f) < .001f
                && Mathf.Abs(applied.distance - 8.5f) < .001f && !protection.Adjusted, "departure returns to the requested view");
            Debug.Log("[SHORE_CAMERA] continuity angleStep=" + report.continuityMaximumAngleStep
                + " eyeStep=" + report.continuityMaximumEyeStep + " maxQueries=" + report.maximumQueries
                + " maxSolveMs=" + report.maximumSolveMilliseconds + " independentVertexRays=" + report.independentVertexRays);
        }
        static void ExteriorChecks(Report report)
        {
            foreach (string model in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
            {
                EditorSceneManager.OpenScene("Assets/Scenes/" + BoatModelChoice.SceneName(model) + ".unity", OpenSceneMode.Single);
                var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                boat.SendMessage("Awake"); BoatHullCollisionShape.Install(boat);
                boat.BoatRb.interpolation = RigidbodyInterpolation.None;
                var camera = GameObject.Find("TwinCamera").GetComponent<Camera>();
                var follow = camera.GetComponent<DebugFollowCamera>();
                camera.aspect = 1280f * camera.rect.width / 760f;
                var environment = UnityEngine.Object.FindObjectOfType<RiverEnvironmentController>();
                if (environment == null) environment = new GameObject("Exterior camera fixture binding").AddComponent<RiverEnvironmentController>();
                follow.SendMessage("Start");
                Vector3[] vertices = HullVertices(boat);
                FieldInfo yawField = typeof(DebugFollowCamera).GetField("_yaw", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (RiverEnvironmentController.SceneProfile scene in Enum.GetValues(typeof(RiverEnvironmentController.SceneProfile)))
                {
                    environment.SetScene(scene);
                    GameObject root = scene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland
                        ? environment.xiangjiangScene : environment.fengjianScene;
                    var solids = new List<Collider>();
                    // Explicitly enumerate the displayed terrain for the
                    // independent full-vertex oracle, separate from the solver.
                    foreach (string name in new[] { "Bank", "Walkway", "Railing", "FarHills", "ShoreGround" })
                    {
                        Transform part = root.transform.Find(name);
                        if (part == null)
                        {
                            if (name != "Railing") throw new InvalidOperationException("Missing displayed terrain: " + name);
                            continue;
                        }
                        var collider = part.GetComponent<MeshCollider>();
                        if (collider == null || !collider.enabled) throw new InvalidOperationException("Missing native terrain: " + name);
                        solids.Add(collider);
                    }
                    foreach (float z in new[] { -120f, 520f })
                        foreach (int side in new[] { -1, 1 })
                            foreach (float heading in new[] { 0f, 90f })
                            {
                                Quaternion rotation = Quaternion.Euler(0f, heading, 0f);
                                float extent = float.NegativeInfinity;
                                foreach (Vector3 vertex in vertices) extent = Mathf.Max(extent, side * (rotation * vertex).x);
                                var ground = root.transform.Find("ShoreGround").GetComponent<MeshCollider>();
                                Physics.SyncTransforms();
                                if (!ground.Raycast(new Ray(new Vector3(0f, .12f, z), Vector3.right * side), out RaycastHit face, 300f))
                                    throw new InvalidOperationException("Missing actual shore face outside the original bank.");
                                Vector3 position = new Vector3(face.point.x - side * (extent + .25f), .12f, z);
                                var rb = boat.BoatRb;
                                rb.position = position; rb.rotation = rotation;
                                boat.transform.SetPositionAndRotation(position, rotation); Physics.SyncTransforms();
                                float requestedYaw = -side * 90f - heading;
                                follow.distance = 20f; follow.pitch = 12f; yawField.SetValue(follow, requestedYaw);
                                Vector3 focus = position; focus.y = Mathf.Lerp(focus.y, .5f, .9f);
                                Quaternion requested = Quaternion.Euler(12f, heading + requestedYaw, 0f);
                                camera.transform.SetPositionAndRotation(focus - requested * Vector3.forward * 20f, requested);
                                var row = new ExtendedCase { scene = model + "/" + scene, boatPosition = position,
                                    boatEuler = rotation.eulerAngles, requestYaw = requestedYaw, requestPitch = 12f, requestDistance = 20f,
                                    requestedFits = OutsideVertices(vertices, boat.transform, camera) == 0 };
                                Vector3 physicalPosition = rb.position, velocity = rb.velocity, angularVelocity = rb.angularVelocity;
                                Vector3 inertia = rb.inertiaTensor, center = rb.centerOfMass; Quaternion physicalRotation = rb.rotation;
                                float mass = rb.mass;
                                follow.SendMessage("ResetFollow"); follow.SendMessage("LateUpdate");
                                row.eye = camera.transform.position; row.appliedYaw = follow.AppliedYaw;
                                row.appliedPitch = follow.AppliedPitch; row.appliedDistance = follow.AppliedDistance;
                                row.adjusted = follow.OcclusionAdjusted; row.clear = follow.OcclusionClear;
                                row.physicallyUnchanged = rb.position.Equals(physicalPosition) && rb.rotation.Equals(physicalRotation)
                                    && rb.velocity.Equals(velocity) && rb.angularVelocity.Equals(angularVelocity)
                                    && rb.inertiaTensor.Equals(inertia) && rb.centerOfMass.Equals(center) && rb.mass == mass;
                                bool visible = FullVertexVisibility(vertices, boat.transform, camera, solids.ToArray(), report,
                                    out row.outside, out row.occludedVertices);
                                string label = row.scene + " outside bank z=" + z + " side=" + side + " heading=" + heading;
                                Check(report, visible && follow.OcclusionAvailable && row.clear, label + " all actual hull vertices clear every displayed terrain mesh");
                                Check(report, row.requestedFits && row.outside == 0, label + " whole-hull request remains fully framed");
                                Check(report, row.physicallyUnchanged && follow.distance == 20f && follow.pitch == 12f
                                    && follow.OrbitYaw == requestedYaw, label + " physical state and requested settings remain unchanged");
                                report.maximumQueries = Mathf.Max(report.maximumQueries, follow.OcclusionQueries);
                                report.maximumSolveMilliseconds = Mathf.Max(report.maximumSolveMilliseconds, follow.OcclusionMilliseconds);
                                report.exterior.Add(row);
                            }
                }
            }
        }

        public static void Run()
        {
            var report = new Report();
            try
            {
                EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                boat.SendMessage("Awake");
                BoatHullCollisionShape.Install(boat);
                var camera = GameObject.Find("TwinCamera").GetComponent<Camera>();
                var follow = camera.GetComponent<DebugFollowCamera>();
                var environment = UnityEngine.Object.FindObjectOfType<RiverEnvironmentController>();
                if (environment == null) environment = new GameObject("Shore camera fixture binding").AddComponent<RiverEnvironmentController>();
                camera.aspect = 1280f * camera.rect.width / 760f;
                follow.SendMessage("Start");
                boat.BoatRb.interpolation = RigidbodyInterpolation.None;
                var meshes = boat.transform.Find("RealDragonBoat").GetComponentsInChildren<MeshFilter>();
                foreach (RiverEnvironmentController.SceneProfile scene in Enum.GetValues(typeof(RiverEnvironmentController.SceneProfile)))
                {
                    environment.SetScene(scene);
                    GameObject root = scene == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland ? environment.xiangjiangScene : environment.fengjianScene;
                    var bank = root.transform.Find("Bank").GetComponent<MeshCollider>();
                    foreach (int side in new[] {-1, 1})
                    {
                        var row = new Case { scene = scene.ToString(), boatPosition = new Vector3(side * 26f, .12f, 0f), boatYaw = -side * 90f, actualHullInsideRiver = true };
                        boat.BoatRb.position = row.boatPosition; boat.BoatRb.rotation = Quaternion.Euler(0f, row.boatYaw, 0f);
                        boat.transform.SetPositionAndRotation(boat.BoatRb.position, boat.BoatRb.rotation);
                        Physics.SyncTransforms();
                        follow.SendMessage("ResetFollow"); follow.SendMessage("LateUpdate");
                        row.cameraPosition = camera.transform.position;
                        Vector3 focus = boat.transform.position; focus.y = Mathf.Lerp(focus.y, .5f, .9f);
                        Vector3 path = camera.transform.position - focus;
                        row.occluded = bank.Raycast(new Ray(focus, path.normalized), out RaycastHit hit, path.magnitude);
                        row.obstructionDistance = row.occluded ? hit.distance : -1f;
                        foreach (var mesh in meshes)
                        {
                            var renderer = mesh.GetComponent<Renderer>();
                            if (mesh.sharedMesh == null || renderer == null || !renderer.enabled) continue;
                            foreach (Vector3 vertex in mesh.sharedMesh.vertices)
                            {
                                Vector3 world = mesh.transform.TransformPoint(vertex);
                                row.actualHullInsideRiver &= Mathf.Abs(world.x) < 32f;
                                Vector3 p = camera.WorldToViewportPoint(world); row.examined++;
                                if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1 || p.z < camera.nearClipPlane) row.outside++;
                            }
                        }
                        row.hullFits = row.outside == 0 && row.examined > 0;
                        Check(report, row.actualHullInsideRiver, scene + " side " + side + " actual boat stays in water");
                        Check(report, !row.occluded, scene + " side " + side + " camera is not behind or inside bank");
                        Check(report, row.hullFits, scene + " side " + side + " actual entire hull fits");
                        Check(report, Mathf.Abs(follow.distance - 8.5f) < 1e-5f && Mathf.Abs(follow.pitch - 12f) < 1e-5f && Mathf.Abs(follow.OrbitYaw) < 1e-5f,
                            scene + " side " + side + " user settings retained");
                        report.cases.Add(row);
                    }
                }
                ExtendedChecks(report, boat, camera, follow, environment);
                RecordedFramingChecks(report, boat, camera, environment);
                ExteriorChecks(report);
            }
            catch (Exception error) { report.failed++; Debug.LogError("[SHORE_CAMERA] setup: " + error); }
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-logFile");
            if (index >= 0 && index + 1 < args.Length)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1])), "shore_camera.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[SHORE_CAMERA] passed=" + report.passed + " failed=" + report.failed);
            Debug.Log(report.failed == 0 ? "[SHORE_CAMERA] RESULT=PASS" : "[SHORE_CAMERA] RESULT=FAIL");
        }
        static void Check(Report report, bool condition, string name)
        {
            if (condition) report.passed++; else report.failed++;
            Debug.Log("[SHORE_CAMERA] " + name + "=" + condition);
        }
    }
}
