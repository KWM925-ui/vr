using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Checks the reflection geometry without rendering or changing scene assets.
    /// Actual per-camera RT rendering is verified separately in the normal Player.</summary>
    public static class PlanarReflectionMathSelfTest
    {
        const string Marker = "[PLANAR_REFLECTION]";
        const float Tolerance = 0.0001f;

        [MenuItem("DragonBoatVR/平面反射数学自检")]
        public static void Run()
        {
            int passed = 0, failed = 0;
            Check("mirror points, plane, determinant and involution", TestReflection, ref passed, ref failed);
            Check("reflected view retains the improper basis", TestView, ref passed, ref failed);
            Check("oblique near plane retains only geometry above water", TestClipping, ref passed, ref failed);
            Check("each split camera projects into its own full RT", TestProjection, ref passed, ref failed);
            Check("water visibility restoration retains disabled renderers", TestVisibility, ref passed, ref failed);
            Debug.Log($"{Marker} passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? Marker + " RESULT=PASS reflection math and state restoration"
                : Marker + " RESULT=FAIL see individual checks");
        }

        static void Check(string name, Action test, ref int passed, ref int failed)
        {
            try { test(); passed++; Debug.Log($"{Marker} {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"{Marker} {name}: {error}"); }
        }

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        static object Invoke(PlanarWaterReflection target, string name, params object[] arguments)
        {
            MethodInfo method = typeof(PlanarWaterReflection).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Require(method != null, "reflection method missing: " + name);
            return method.Invoke(target, arguments);
        }

        static void Near(Vector3 actual, Vector3 expected, string message)
        { Require(Vector3.Distance(actual, expected) < Tolerance, message + $": {actual} versus {expected}"); }

        static Vector3 Divide(Vector4 value)
        {
            Require(Mathf.Abs(value.w) > 0.00001f, "fixture projected onto a singular plane");
            return new Vector3(value.x, value.y, value.z) / value.w;
        }

        static void MatrixNear(Matrix4x4 actual, Matrix4x4 expected, string message)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    Require(Mathf.Abs(actual[row, col] - expected[row, col]) < Tolerance,
                        message + $": entry {row},{col}");
        }

        sealed class Fixture : IDisposable
        {
            readonly GameObject root = new GameObject("PlanarReflectionMathSelfTest (no rendering)");
            public readonly PlanarWaterReflection planar;
            public readonly Camera source;
            public readonly Camera reflected;

            public Fixture()
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                planar = root.AddComponent<PlanarWaterReflection>();
                planar.enabled = false;
                source = CameraChild("source");
                reflected = CameraChild("reflection");
                source.transform.SetPositionAndRotation(new Vector3(0.7f, 1.7f, -2f),
                    Quaternion.Euler(12f, 5f, 3f));
                source.fieldOfView = 60f;
                source.nearClipPlane = 0.05f;
                source.farClipPlane = 1200f;
                source.aspect = 8f / 9f;
            }

            Camera CameraChild(string name)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                Camera camera = go.AddComponent<Camera>();
                camera.enabled = false;
                return camera;
            }

            public MeshRenderer WaterChild(string name, bool enabled)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.enabled = enabled;
                return renderer;
            }

            public void Mirror() => Invoke(planar, "MirrorCamera", source, reflected);
            public Matrix4x4 WorldToTexture() => (Matrix4x4)Invoke(null, "WorldToTextureMatrix", reflected);
            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void TestReflection()
        {
            foreach (float height in new[] { -1.3f, 0f, 2.4f })
            {
                Matrix4x4 mirror = (Matrix4x4)Invoke(null, "ReflectionMatrix", height);
                Vector3 point = new Vector3(2.1f, height + 0.8f, -4.2f);
                Near(mirror.MultiplyPoint3x4(point), new Vector3(point.x, height - 0.8f, point.z),
                    "mirrored point has wrong water height");
                Near(mirror.MultiplyPoint3x4(new Vector3(5f, height, -7f)), new Vector3(5f, height, -7f),
                    "water plane is not fixed");
                Require(Mathf.Abs(mirror.determinant + 1f) < Tolerance, "mirror did not change handedness");
                MatrixNear(mirror * mirror, Matrix4x4.identity, "two reflections did not restore identity");
            }
        }

        static void TestView()
        {
            using (var f = new Fixture())
            {
                f.planar.waterLevel = 0.3f;
                f.Mirror();
                Vector3 world = new Vector3(-1.1f, 2.6f, 8f);
                Vector3 mirroredWorld = new Vector3(world.x, 0.6f - world.y, world.z);
                Near(f.reflected.worldToCameraMatrix.MultiplyPoint3x4(world),
                    f.source.worldToCameraMatrix.MultiplyPoint3x4(mirroredWorld),
                    "view does not match an independently mirrored world point");
                Vector3 position = f.source.transform.position;
                Near(f.reflected.transform.position, new Vector3(position.x, 0.6f - position.y, position.z),
                    "reflection camera position is wrong");
                Require(f.source.worldToCameraMatrix.determinant * f.reflected.worldToCameraMatrix.determinant < 0f,
                    "reflected view has the original handedness");

                // This is the previous defect: LookRotation must rebuild a proper
                // rotation, whose right axis opposes the actual mirrored right axis.
                Vector3 right = f.source.transform.right; right.y = -right.y;
                Require(Vector3.Dot(f.reflected.transform.right, right) < -0.999f,
                    "fixture did not expose quaternion reconstruction's horizontal reversal");
                Matrix4x4 quaternionView = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                    * f.reflected.transform.worldToLocalMatrix;
                Require(Vector3.Distance(quaternionView.MultiplyPoint3x4(world),
                    f.reflected.worldToCameraMatrix.MultiplyPoint3x4(world)) > 0.5f,
                    "old quaternion view was not distinguished from the corrected view");
            }
        }

        static void TestClipping()
        {
            using (var f = new Fixture())
            {
                f.planar.waterLevel = 0.3f;
                f.Mirror();
                Matrix4x4 view = f.reflected.worldToCameraMatrix;
                Vector4 worldPlane = new Vector4(0f, 1f, 0f, -f.planar.waterLevel);
                Vector4 cameraPlane = view.inverse.transpose * worldPlane;
                Matrix4x4 clip = f.reflected.projectionMatrix * view;
                foreach (float elevation in new[] { -0.2f, 0f, 0.2f })
                {
                    Vector4 point = new Vector4(1.1f, f.planar.waterLevel + elevation, 6f, 1f);
                    Require(Mathf.Abs(Vector4.Dot(cameraPlane, view * point) - elevation) < Tolerance,
                        "plane covector transformation changed the retained side");
                    Vector4 projected = clip * point;
                    Require(projected.w > 0f, "clip fixture is behind the camera");
                    float nearDistance = projected.z + projected.w;
                    if (elevation == 0f)
                        Require(Mathf.Abs(nearDistance) < Tolerance, "water plane did not become the near clip plane");
                    else
                        Require(nearDistance * elevation > 0f, "oblique clip retained the wrong side of the water");
                }
                MatrixNear(f.reflected.cullingMatrix, clip, "culling and rendering use different frusta");
            }
        }

        static void TestProjection()
        {
            using (var first = new Fixture())
            using (var second = new Fixture())
            {
                first.source.rect = new Rect(0f, 0f, 0.5f, 1f);
                second.source.rect = new Rect(0.5f, 0f, 0.5f, 1f);
                first.source.aspect = second.source.aspect = 8f / 9f;
                second.source.transform.position = new Vector3(-5f, 5.8f, -12f);
                second.source.transform.rotation = Quaternion.LookRotation(
                    new Vector3(0f, 0f, 3f) - second.source.transform.position);
                second.source.fieldOfView = 50f;
                Vector3 world = new Vector3(1.1f, 0f, 7f);
                Vector3 firstUv = CheckProjection(first, world);
                Vector3 secondUv = CheckProjection(second, world);
                Require(Vector2.Distance(new Vector2(firstUv.x, firstUv.y),
                    new Vector2(secondUv.x, secondUv.y)) > 0.05f,
                    "fixture did not distinguish the two camera projections");

                // A different screen rectangle must not crop or offset this RT.
                Matrix4x4 before = second.WorldToTexture();
                second.source.rect = new Rect(0.1f, 0.2f, 0.3f, 0.6f);
                second.source.aspect = 8f / 9f;
                second.Mirror();
                MatrixNear(second.WorldToTexture(), before, "screen viewport leaked into reflection texture UVs");
            }
        }

        static Vector3 CheckProjection(Fixture f, Vector3 world)
        {
            f.Mirror();
            Vector3 actual = Divide(f.WorldToTexture() * new Vector4(world.x, world.y, world.z, 1f));
            Vector3 expected = f.source.WorldToViewportPoint(world);
            Require(Mathf.Abs(actual.x - expected.x) < Tolerance
                && Mathf.Abs(actual.y - expected.y) < Tolerance,
                $"water-plane RT projection differs from camera-local viewport: {actual} versus {expected}");
            return actual;
        }

        static void TestVisibility()
        {
            using (var f = new Fixture())
            {
                MeshRenderer visible = f.WaterChild("visible water", true);
                MeshRenderer hidden = f.WaterChild("already hidden water", false);
                Invoke(f.planar, "HideWater");
                Require(!visible.enabled && !hidden.enabled, "water remained visible in the reflection pass");
                Invoke(f.planar, "RestoreWater");
                Require(visible.enabled && !hidden.enabled, "reflection pass changed pre-existing renderer visibility");
            }
        }
    }
}
