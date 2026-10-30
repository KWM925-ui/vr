using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Two-view presentation and cabin camera movement against the
    /// actual imported hull. Real keyboard/mouse acceptance is a separate Player run.</summary>
    public static class PlatformViewSelfTest
    {
        static int _passed, _failed;
        static void Check(bool condition, string name)
        {
            if (condition) _passed++; else _failed++;
            Debug.Log("[PLATFORMVIEW] " + name + "=" + condition);
        }

        static void CheckFollowingTurns(Camera camera, BoatBuoyancy boat)
        {
            var follow = camera.GetComponent<DebugFollowCamera>();
            var hull = boat.transform.Find("RealDragonBoat");
            var meshes = hull.GetComponentsInChildren<MeshFilter>()
                .Where(filter => filter.sharedMesh != null && filter.GetComponent<Renderer>() != null
                    && filter.GetComponent<Renderer>().enabled)
                .Select(filter => new { filter.transform, vertices = filter.sharedMesh.vertices }).ToArray();
            Quaternion originalRotation = boat.BoatRb.rotation;
            Quaternion originalTransformRotation = boat.transform.rotation;
            var originalInterpolation = boat.BoatRb.interpolation;
            float originalAspect = camera.aspect;
            float largestHeadingError = 0f;
            float largestAcosError = 0f;
            bool headingsApplied = true;
            int outside = 0, examined = 0;
            try
            {
                // The shipped narrow right pane, independently of batchmode's
                // screen size. Use every displayed hull vertex, not a box whose
                // empty stern corners can lie outside an otherwise valid view.
                camera.aspect = 1280f * camera.rect.width / 760f;
                // An Editor call has no intervening render interpolation step.
                // Change the displayed pose explicitly and verify the fixture,
                // rather than mistaking an unchanged Transform for a passed turn.
                boat.BoatRb.interpolation = RigidbodyInterpolation.None;
                follow.SendMessage("Start");
                for (int heading = 0; heading <= 360; heading++)
                {
                    var desired = Quaternion.Euler(0f, heading, 0f);
                    boat.BoatRb.rotation = desired;
                    boat.transform.rotation = desired;
                    Physics.SyncTransforms();
                    headingsApplied &= Quaternion.Angle(boat.transform.rotation, desired) < 0.01f
                        && Quaternion.Angle(boat.BoatRb.rotation, desired) < 0.01f;
                    follow.SendMessage("LateUpdate");
                    Vector3 viewForward = camera.transform.forward, boatForward = boat.transform.forward;
                    largestAcosError = Mathf.Max(largestAcosError, Vector3.Angle(
                        Vector3.ProjectOnPlane(viewForward, Vector3.up), Vector3.ProjectOnPlane(boatForward, Vector3.up)));
                    // acos(dot) in float rounds tiny errors to about 0.0198 deg.
                    // atan2(cross,dot) resolves them without relaxing 0.01 deg.
                    double cross = (double)viewForward.x * boatForward.z - (double)viewForward.z * boatForward.x;
                    double dot = (double)viewForward.x * boatForward.x + (double)viewForward.z * boatForward.z;
                    largestHeadingError = Mathf.Max(largestHeadingError, (float)(Math.Abs(Math.Atan2(cross, dot)) * 180.0 / Math.PI));
                    // Every degree exercises heading continuity; full mesh
                    // projection includes the cardinal poses and the 0/360 seam.
                    if (heading % 90 != 0 && heading != 1 && heading != 359) continue;
                    foreach (var mesh in meshes)
                        foreach (var vertex in mesh.vertices)
                        {
                            var point = camera.WorldToViewportPoint(mesh.transform.TransformPoint(vertex));
                            examined++;
                            if (point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f
                                || point.z < camera.nearClipPlane || point.z > camera.farClipPlane) outside++;
                        }
                }
            }
            finally
            {
                boat.BoatRb.rotation = originalRotation;
                boat.transform.rotation = originalTransformRotation;
                boat.BoatRb.interpolation = originalInterpolation;
                camera.aspect = originalAspect;
                Physics.SyncTransforms();
            }
            Check(headingsApplied, "turn fixture actually applies every requested displayed and physical heading");
            Check(headingsApplied && largestHeadingError < 0.01f, "default third-person view follows boat turns and heading wrap");
            Check(headingsApplied && examined > 0 && outside == 0, "entire displayed hull fits the default narrow third-person pane after turns");
            Debug.Log($"[PLATFORMVIEW] thirdPersonHeadingError={largestHeadingError:F4}deg hullVerticesOutside={outside}/{examined}");
            Debug.Log($"[PLATFORMVIEW] headingAtan2Max={largestHeadingError:E6}deg headingFloatAcosMax={largestAcosError:E6}deg");
        }

        public static void Run()
        {
            _passed = _failed = 0;
            try
            {
                EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                var main = GameObject.Find("MainCamera").GetComponent<Camera>();
                var twin = GameObject.Find("TwinCamera").GetComponent<Camera>();
                var seat = main.GetComponent<BoatSeatCamera>();
                var split = Object.FindObjectOfType<SplitViewController>();
                var boat = Object.FindObjectOfType<BoatBuoyancy>();
                var platform = GameObject.Find("StewartPlatformRig");
                var cable = GameObject.Find("CableRig");
                if (boat.BoatRb == null) boat.SendMessage("Awake");
                BoatHullCollisionShape.Install(boat);
                Physics.SyncTransforms();

                var body = boat.BoatRb;
                Vector3 bodyPosition = body.position, bodyVelocity = body.velocity;
                Quaternion bodyRotation = body.rotation;
                int originalColliders = Object.FindObjectsOfType<Collider>().Length;
                var plate = new GameObject("VisibilityRegressionPlate");
                plate.transform.SetParent(platform.transform, false);
                var renderer = plate.AddComponent<MeshRenderer>();
                renderer.enabled = true;
                var legacy = new GameObject("RigCamera").AddComponent<Camera>();
                legacy.enabled = true;
                split.layout = (SplitViewController.Layout)3; // Obsolete saved layout.
                split.Apply();
                twin.GetComponent<PlatformDebugView>().HideDevices();
                Check(main.enabled && twin.enabled && !legacy.enabled
                    && Object.FindObjectsOfType<Camera>().Count(c => c.enabled && c.targetTexture == null) == 2,
                    "only two presentation cameras, including an obsolete saved layout");
                Check(Mathf.Approximately(main.rect.x, 0f) && Mathf.Approximately(main.rect.y, 0f)
                    && Mathf.Approximately(twin.rect.x, main.rect.width)
                    && Mathf.Approximately(main.rect.width + twin.rect.width, 1f)
                    && Mathf.Approximately(main.rect.height, 1f) && Mathf.Approximately(twin.rect.height, 1f),
                    "two complete nonoverlapping viewports");
                Check(!renderer.enabled && plate.activeInHierarchy && platform.activeInHierarchy && cable.activeInHierarchy
                    && (main.cullingMask & (1 << 2)) == 0 && (twin.cullingMask & (1 << 2)) == 0,
                    "devices remain active but are hidden from both views");
                Object.DestroyImmediate(plate);
                Object.DestroyImmediate(legacy.gameObject);

                Vector3 home = seat.DefaultLocalEyePosition;
                var boundary = seat.CabinBoundary;
                Check(boundary != null && boundary.Ready && boundary.Contains(new Vector2(home.x, home.z))
                    && boundary.MaximumZ - boundary.MinimumZ > 3f, "usable cabin derived from displayed hull");
                if (boundary == null || !boundary.Ready) throw new InvalidOperationException("Cabin unavailable.");

                for (int i = 0; i < 120; i++) seat.MoveInCabin(new Vector2(0f, 0.1f));
                Vector3 bow = seat.LocalEyePosition;
                Check(bow.z - home.z > 2f && bow.z < boundary.MaximumZ - 0.1f
                    && Mathf.Abs(bow.y - home.y) < 1e-6f, "forward travel reaches cabin end without leaving it");
                for (int i = 0; i < 200; i++) seat.MoveInCabin(new Vector2(0f, -0.1f));
                Vector3 stern = seat.LocalEyePosition;
                Check(home.z - stern.z > 2f && stern.z > boundary.MinimumZ + 0.1f
                    && bow.z - stern.z > 4f, "backward travel spans the cabin with end clearance");

                // Real W/S input uses the default gaze, which is angled toward
                // the paddle. Pure boat-axis movement missed a rail-sliding bug.
                seat.ResetView();
                Vector3 gazeForward = Vector3.ProjectOnPlane(seat.LocalViewRotation * Vector3.forward, Vector3.up).normalized;
                Vector2 walkingStep = new Vector2(gazeForward.x, gazeForward.z) * 0.025f;
                for (int i = 0; i < 400; i++) seat.MoveInCabin(walkingStep);
                Vector3 walkingBow = seat.LocalEyePosition;
                for (int i = 0; i < 480; i++) seat.MoveInCabin(-walkingStep);
                Vector3 walkingStern = seat.LocalEyePosition;
                Check(walkingBow.z - home.z > 3f && home.z - walkingStern.z > 2f
                    && walkingBow.z - walkingStern.z > 6.5f,
                    "default gaze movement follows curved rails across the cabin");
                Debug.Log($"[PLATFORMVIEW] gazeWalkZ=[{walkingStern.z:F3},{walkingBow.z:F3}]");

                seat.ResetView();
                for (int i = 0; i < 40; i++) seat.MoveInCabin(new Vector2(0.05f, 0f));
                float right = seat.LocalEyePosition.x;
                seat.ResetView();
                for (int i = 0; i < 40; i++) seat.MoveInCabin(new Vector2(-0.05f, 0f));
                float left = seat.LocalEyePosition.x;
                var geometry = BoatHullGeometry.FindOrCreate(boat);
                Check(right - home.x > 0.1f && home.x - left > 0.1f
                    && right < geometry.HalfBeam - 0.1f && left > -geometry.HalfBeam + 0.1f,
                    "both gunwales contain lateral movement");
                Vector3 beforeInvalid = seat.LocalEyePosition;
                seat.MoveInCabin(new Vector2(float.NaN, 1f));
                Check(Vector3.Distance(beforeInvalid, seat.LocalEyePosition) < 1e-6f, "nonfinite movement rejected");

                seat.ResetView();
                var obstruction = new GameObject("CameraHeadroomRegression");
                obstruction.transform.SetPositionAndRotation(boat.transform.TransformPoint(home + Vector3.forward * 0.6f), boat.transform.rotation);
                obstruction.AddComponent<BoxCollider>().size = new Vector3(1f, 0.8f, 0.1f);
                Physics.SyncTransforms();
                seat.MoveInCabin(new Vector2(0f, 3f));
                float blockedTravel = seat.LocalEyePosition.z - home.z;
                Check(blockedTravel > 0f && blockedTravel < 0.4f,
                    "large movement cannot cross a real head-height collider");
                Object.DestroyImmediate(obstruction);
                Physics.SyncTransforms();

                seat.ResetView();
                Check(Vector3.Distance(home, seat.LocalEyePosition) < 1e-6f && !seat.AtCabinBoundary,
                    "reset returns to the original paddling position");
                seat.MoveInCabin(new Vector2(0.1f, 0.5f));
                seat.SendMessage("LateUpdate");
                Check(Vector3.Distance(main.transform.position, boat.transform.TransformPoint(seat.LocalEyePosition)) < 1e-5f
                    && Quaternion.Angle(main.transform.rotation, boat.transform.rotation * seat.LocalViewRotation) < 1e-3f,
                    "moving view remains attached to the same boat transform");
                Check(Vector3.Distance(body.position, bodyPosition) < 1e-6f
                    && Quaternion.Angle(body.rotation, bodyRotation) < 1e-3f
                    && Vector3.Distance(body.velocity, bodyVelocity) < 1e-6f
                    && Object.FindObjectsOfType<Collider>().Length == originalColliders,
                    "camera navigation adds no collider or boat motion");
                seat.ResetView();
                Debug.Log($"[PLATFORMVIEW] cabinZ=[{boundary.MinimumZ:F3},{boundary.MaximumZ:F3}] " +
                    $"actualZ=[{stern.z:F3},{bow.z:F3}] actualX=[{left:F3},{right:F3}] blockedTravel={blockedTravel:F3}m");
                CheckFollowingTurns(twin, boat);
            }
            catch (Exception error) { _failed++; Debug.LogException(error); }
            Debug.Log($"[PLATFORMVIEW] passed={_passed} failed={_failed}");
            Debug.Log("[PLATFORMVIEW] RESULT=" + (_failed == 0 ? "PASS" : "FAIL")
                + " desktop cabin geometry and two-view presentation; keyboard/mouse verified separately");
            EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }
    }
}
