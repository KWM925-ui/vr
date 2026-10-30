using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class PaddlePlacementSelfTest
    {
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("rendered blade bounds equal the hydrodynamic blade", BladeBounds, ref passed, ref failed);
            Check("shaft starts at blade and ends at the measured reference", ShaftBounds, ref passed, ref failed);
            Check("handheld stroke never inverts the grip below the blade", UprightStroke, ref passed, ref failed);
            Check("stopped script has the same fixed and rendered pose", StoppedPose, ref passed, ref failed);
            Check("direct stroke clears the actual imported hull mesh", () => HullClearance(false), ref passed, ref failed);
            Check("independent intention clears the actual imported hull mesh", () => HullClearance(true), ref passed, ref failed);
            Check("upper grip remains reachable from the paddle seat", GripReach, ref passed, ref failed);
            Check("recovery centre height uses the declared boat coordinates", RecoveryHeightFrame, ref passed, ref failed);
            Check("deepest stroke uses its instantaneous rigid shaft reach", InstantaneousImmersionReach, ref passed, ref failed);
            Check("model gunwales replace the legacy floating trim and oarlock", ModelRails, ref passed, ref failed);
            Debug.Log($"[PADDLE_PLACEMENT] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[PADDLE_PLACEMENT] RESULT=PASS" : "[PADDLE_PLACEMENT] RESULT=FAIL");
        }

        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try { test(); passed++; Debug.Log($"[PADDLE_PLACEMENT] {name}=True"); }
            catch (Exception e) { failed++; Debug.LogError($"[PADDLE_PLACEMENT] {name}: {e.Message}"); }
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        static PaddleHydrodynamics Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity");
            var paddle = UnityEngine.Object.FindObjectOfType<PaddleHydrodynamics>();
            Require(paddle != null, "Demo paddle missing");
            paddle.input = paddle.GetComponent<PaddleDriver>();
            paddle.SynchronizeVisualGeometry();
            return paddle;
        }
        static Bounds MeshBounds(PaddleHydrodynamics paddle, string name)
        {
            var part = paddle.transform.Find(name);
            var mesh = part.GetComponent<MeshFilter>().sharedMesh;
            var bounds = mesh.bounds;
            var result = new Bounds(paddle.transform.InverseTransformPoint(part.TransformPoint(bounds.min)), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                result.Encapsulate(paddle.transform.InverseTransformPoint(part.TransformPoint(corner)));
            }
            return result;
        }
        static void BladeBounds()
        {
            var paddle = Load();
            var bounds = MeshBounds(paddle, "Blade");
            var expected = new Vector3(paddle.BladeWidth, paddle.bladeLength, paddle.bladeThickness);
            Require(Vector3.Distance(bounds.size, expected) < 0.00002f && bounds.center.magnitude < 0.00002f,
                $"actual size={bounds.size.ToString("F5")} expected={expected.ToString("F5")} centre={bounds.center}");
        }
        static void ShaftBounds()
        {
            var paddle = Load();
            var bounds = MeshBounds(paddle, "Shaft");
            Require(Mathf.Abs(bounds.min.y) < 0.00002f && Mathf.Abs(bounds.max.y - paddle.input.shaftToBlade) < 0.00002f,
                $"shaft y=[{bounds.min.y:F5},{bounds.max.y:F5}], reference={paddle.input.shaftToBlade:F5}");
        }
        static void UprightStroke()
        {
            var paddle = Load(); var driver = paddle.input;
            driver.SetMode(PaddleDriver.Mode.VirtualPaddler);
            float minimum = 1f;
            for (int i = 0; i < 720; i++)
            {
                driver.SetPhase01(i / 720f); driver.Tick(0.001f);
                minimum = Mathf.Min(minimum, Vector3.Dot(driver.transform.up, driver.boat.transform.up));
            }
            Require(minimum > 0.55f, $"grip-to-blade upright cosine fell to {minimum:F5}");
        }
        static void StoppedPose()
        {
            var paddle = Load(); var driver = paddle.input;
            driver.SetMode(PaddleDriver.Mode.VirtualPaddler); driver.strokesPerMinute = 0f;
            driver.SetPhase01(0.31f); driver.Tick(0.01f);
            var position = driver.transform.position; var rotation = driver.transform.rotation;
            typeof(PaddleDriver).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);
            Require(Vector3.Distance(position, driver.transform.position) < 0.00002f
                && Quaternion.Angle(rotation, driver.transform.rotation) < 0.02f, "LateUpdate moved the stopped paddle");
        }

        static void HullClearance(bool independent)
        {
            var paddle = Load(); var driver = paddle.input;
            driver.SetMode(PaddleDriver.Mode.VirtualPaddler);
            var model = driver.boat.transform.Find("RealDragonBoat");
            Require(model != null && model.gameObject.activeInHierarchy, "Actual imported hull missing");
            var triangles = new List<HullTriangle>();
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var vertices = filter.sharedMesh.vertices; var indices = filter.sharedMesh.triangles;
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = driver.boat.transform.InverseTransformPoint(filter.transform.TransformPoint(vertices[i]));
                for (int i = 0; i < indices.Length; i += 3)
                    triangles.Add(new HullTriangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]));
            }
            // Positive witness prevents an uncooked/disabled physics query from
            // silently reporting an empty hull. These checks use mesh triangles
            // directly, including both faces, rather than the boat's box collider.
            bool witness = false;
            foreach (var triangle in triangles)
                witness |= triangle.Intersects(new Vector3(-2f, -0.1f, 0f), new Vector3(2f, -0.1f, 0f));
            Require(witness, "Imported mesh intersection witness failed");
            int overlaps = 0; float firstPhase = -1f;
            var corners = new Vector3[8];
            for (int side = 0; side < 2; side++)
            {
                driver.leftSide = side != 0;
                for (int i = 0; i < 360; i++)
                {
                    driver.SetPhase01(i / 360f);
                    Vector3 position; Quaternion rotation;
                    if (independent) UdpClosedLoopClient.SampleScriptedIntent(driver, out position, out rotation);
                    else
                    {
                        driver.Tick(0.0001f);
                        position = driver.boat.transform.InverseTransformPoint(driver.transform.position);
                        rotation = Quaternion.Inverse(driver.boat.transform.rotation) * driver.transform.rotation;
                    }
                    Vector3 grip = position + rotation * Vector3.up * driver.shaftToBlade;
                    var bound = new Bounds(position, Vector3.zero); bound.Encapsulate(grip); bound.Expand(0.045f);
                    for (int k = 0; k < 8; k++)
                    {
                        corners[k] = position + rotation * new Vector3((k & 1) == 0 ? -paddle.BladeWidth / 2 : paddle.BladeWidth / 2,
                            (k & 2) == 0 ? -paddle.bladeLength / 2 : paddle.bladeLength / 2,
                            (k & 4) == 0 ? -paddle.bladeThickness / 2 : paddle.bladeThickness / 2);
                        bound.Encapsulate(corners[k]);
                    }
                    bool hit = false;
                    foreach (var triangle in triangles)
                    {
                        if (!bound.Intersects(triangle.bounds)) continue;
                        hit |= triangle.Intersects(position, grip);
                        for (int k = 0; k < 4 && !hit; k++)
                        {
                            Vector3 offset = rotation * (k < 2 ? Vector3.right : Vector3.forward) * (k % 2 == 0 ? 0.0225f : -0.0225f);
                            hit |= triangle.Intersects(position + offset, grip + offset);
                        }
                        for (int k = 0; k < 8 && !hit; k++)
                            for (int bit = 1; bit <= 4; bit *= 2)
                                if ((k & bit) == 0) hit |= triangle.Intersects(corners[k], corners[k | bit]);
                        if (hit) break;
                    }
                    if (hit) { overlaps++; if (firstPhase < 0f) firstPhase = i / 360f; }
                }
            }
            Require(overlaps == 0, $"whole-stroke mesh overlaps={overlaps}/720; first phase={firstPhase:F5}");
        }

        readonly struct HullTriangle
        {
            readonly Vector3 a, ab, ac;
            public readonly Bounds bounds;
            public HullTriangle(Vector3 first, Vector3 second, Vector3 third)
            { a = first; ab = second - first; ac = third - first; bounds = new Bounds(first, Vector3.zero); bounds.Encapsulate(second); bounds.Encapsulate(third); }
            public bool Intersects(Vector3 from, Vector3 to)
            {
                Vector3 delta = to - from, h = Vector3.Cross(delta, ac); float determinant = Vector3.Dot(ab, h);
                if (Mathf.Abs(determinant) < 1e-9f) return false;
                Vector3 s = from - a; float u = Vector3.Dot(s, h) / determinant;
                if (u < 0f || u > 1f) return false;
                Vector3 q = Vector3.Cross(s, ab); float v = Vector3.Dot(delta, q) / determinant;
                if (v < 0f || u + v > 1f) return false;
                float t = Vector3.Dot(ac, q) / determinant;
                return t >= 0f && t <= 1f;
            }
        }

        static void GripReach()
        {
            var paddle = Load(); var driver = paddle.input;
            var seat = UnityEngine.Object.FindObjectOfType<BoatSeatCamera>();
            Require(seat != null, "Paddle seat camera missing");
            float distance = 0f, lowest = float.PositiveInfinity;
            float front = float.NegativeInfinity, back = float.PositiveInfinity, highest = float.NegativeInfinity;
            for (int i = 0; i < 720; i++)
            {
                driver.SetPhase01(i / 720f);
                UdpClosedLoopClient.SampleScriptedIntent(driver, out Vector3 blade, out Quaternion rotation);
                Vector3 grip = blade + rotation * Vector3.up * driver.shaftToBlade;
                Require(Mathf.Abs(grip.x) >= Mathf.Abs(driver.pivotLocalPos.x) - 0.001f,
                    "Upper hand moved inboard of the paddle station");
                lowest = Mathf.Min(lowest, grip.y);
                highest = Mathf.Max(highest, grip.y);
                front = Mathf.Max(front, grip.z); back = Mathf.Min(back, grip.z);
                distance = Mathf.Max(distance, Vector3.Distance(grip, seat.LocalEyePosition));
            }
            Require(lowest >= driver.pivotLocalPos.y + 0.34f && distance < 1f,
                $"lowest upper grip={lowest:F4}m; maximum eye-to-grip reach={distance:F4}m");
            Debug.Log($"[PADDLE_PLACEMENT] upper grip minimum={lowest:F4}m maximum={highest:F4}m "
                + $"fore/aft span={front - back:F4}m maximum eye-to-grip={distance:F4}m");
        }

        static void RecoveryHeightFrame()
        {
            var driver = Load().input;
            driver.SetPhase01(0.75f);
            UdpClosedLoopClient.SampleScriptedIntent(driver, out Vector3 blade, out Quaternion rotation);
            Require(Mathf.Abs(blade.y - driver.bladeAirHeight) < 0.00002f,
                $"boat-local blade centre={blade.y:F5}, declared recovery centre={driver.bladeAirHeight:F5}; rail height must not be added twice");
        }

        static void InstantaneousImmersionReach()
        {
            var driver = Load().input;
            driver.SetPhase01(0.25f);
            UdpClosedLoopClient.SampleScriptedIntent(driver, out Vector3 blade, out Quaternion rotation);
            // Rigid kinematics: grip = blade + R * up * L. The existing minimum
            // upper-hand height constrains this pose, not an unrelated end pose.
            float reachY = (rotation * Vector3.up).y * driver.shaftToBlade;
            float lowestAllowed = Mathf.Max(driver.pivotLocalPos.y - driver.bladeDepth,
                driver.pivotLocalPos.y + 0.35f - reachY);
            Require(Mathf.Abs(blade.y - lowestAllowed) < 0.00002f,
                $"deepest blade={blade.y:F5}, reachable depth with current orientation={lowestAllowed:F5}; shaft vertical reach={reachY:F5}");
        }

        static void ModelRails()
        {
            var paddle = Load(); var hull = BoatHullGeometry.FindOrCreate(paddle.input.boat);
            Require(hull != null && hull.HalfBeam > 0.49f && hull.HalfBeam < 0.52f, "Model beam was not sampled");
            foreach (Transform child in hull.transform)
                if (child.name.StartsWith("GunwaleTrim") || child.name.StartsWith("Oarlock"))
                    foreach (var renderer in child.GetComponentsInChildren<Renderer>(true))
                        Require(!renderer.enabled, "Legacy geometry remains visible: " + child.name);
            float top = hull.RailTop(-0.45f, 0.45f);
            Require(top > 0.10f && top < 0.16f, $"Rail profile doesn't match this mesh's edge: y={top:F4}");
        }
    }
}
