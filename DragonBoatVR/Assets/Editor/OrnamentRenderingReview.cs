using System;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    // Unsaved counterfactual renders distinguish shading from mesh/collision defects.
    public static class OrnamentRenderingReview
    {
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string folder = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1]);
            foreach (string id in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
            {
                EditorSceneManager.OpenScene("Assets/Scenes/" + BoatModelChoice.SceneName(id) + ".unity", OpenSceneMode.Single);
                var boat = Object.FindObjectOfType<BoatBuoyancy>();
                var seat = Object.FindObjectOfType<BoatSeatCamera>();
                var model = boat.transform.Find("RealDragonBoat");
                foreach (var renderer in model.GetComponentsInChildren<Renderer>()) renderer.gameObject.layer = 30;
                var probe = RenderSettings.ambientProbe;
                float energy = 0f;
                for (int c = 0; c < 3; c++) for (int n = 0; n < 9; n++) energy += probe[c, n] * probe[c, n];
                Debug.Log("[AMBIENT_REVIEW] " + id + " mode=" + RenderSettings.ambientMode + " SH squared=" + energy.ToString("R"));
                foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
                {
                    var mesh = filter.sharedMesh;
                    var vertices = mesh.vertices; var normals = mesh.normals; var triangles = mesh.triangles;
                    int zero = 0, opposed = 0;
                    foreach (var n in normals) if (n.sqrMagnitude < .5f) zero++;
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                        var face = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                        if (normals.Length == vertices.Length && Vector3.Dot(face, normals[a] + normals[b] + normals[c]) < 0f) opposed++;
                    }
                    Debug.Log("[NORMAL_REVIEW] " + id + "/" + filter.name + " vertices=" + vertices.Length
                        + " normals=" + normals.Length + " zero=" + zero + " opposingFaces=" + opposed);
                }
                var go = new GameObject("Unsaved shading comparison camera");
                var camera = go.AddComponent<Camera>();
                camera.cullingMask = 1 << 30;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.10f, .12f, .15f);
                camera.fieldOfView = 55f;
                camera.transform.position = boat.transform.TransformPoint(seat.DefaultLocalEyePosition);
                camera.transform.LookAt(boat.transform.TransformPoint(seat.DefaultLocalEyePosition + new Vector3(.2f, -.3f, 1f)));
                var target = new RenderTexture(1280, 760, 24);
                var pixels = new Texture2D(1280, 760, TextureFormat.RGB24, false);
                var previous = RenderTexture.active;
                try
                {
                    void Shot(string name)
                    {
                        camera.targetTexture = target;
                        camera.Render(); RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, 1280, 760), 0, 0); pixels.Apply();
                        File.WriteAllBytes(Path.Combine(folder, id + "_" + name + ".png"), pixels.EncodeToPNG());
                    }
                    Shot("original");
                    foreach (var light in Object.FindObjectsOfType<Light>()) light.shadows = LightShadows.None;
                    Shot("no_shadows");
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(.5f, .5f, .5f);
                    Shot("flat_ambient");
                    var sh = new SphericalHarmonicsL2(); sh.AddAmbientLight(new Color(.5f, .5f, .5f));
                    RenderSettings.ambientProbe = sh;
                    Shot("explicit_probe");
                }
                finally
                {
                    RenderTexture.active = previous; camera.targetTexture = null;
                    Object.DestroyImmediate(pixels); Object.DestroyImmediate(target); Object.DestroyImmediate(go);
                }
            }
            Debug.Log("[SHADING_REVIEW] Unsaved diagnostic renders only; no asset or lighting settings saved.");
        }
    }
}
