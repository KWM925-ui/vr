using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DragonBoatVR.EditorTools
{
    public static class OrnamentBoatPreparation
    {
        [Serializable] sealed class WaterExclusionPlan
        {
            public string sourceObjSha256;
            public float heightM;
            public Vector3 center;
            public Vector3[] boundary;
            public int[] missingRays;
            public int radialCount;
        }

        public static void InstallWaterExclusion()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-maskPlan");
            if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("A reviewed interior mask plan is required.");
            var plan = JsonUtility.FromJson<WaterExclusionPlan>(File.ReadAllText(args[index + 1]));
            const string obj = "Assets/Art/Boat/DragonBoatOrnament.obj";
            using (var sha = SHA256.Create())
                if (plan == null || plan.sourceObjSha256 != BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(obj))).Replace("-", "").ToLowerInvariant())
                    throw new InvalidOperationException("Interior mask plan uses another hull.");
            if (plan.boundary == null || plan.boundary.Length != 1024 || plan.radialCount != plan.boundary.Length
                || plan.missingRays == null || plan.missingRays.Length != 0 || !SimulationValue.Finite(plan.center)
                || plan.heightM != 0.05f || plan.center.y != plan.heightM)
                throw new InvalidOperationException("Interior mask plan is incomplete.");
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/DragonBoatOrnament.unity", OpenSceneMode.Single);
            var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            var choice = boat != null ? boat.GetComponent<BoatModelChoice>() : null;
            if (choice == null || choice.modelId != BoatModelChoice.Ornament || choice.sourceObjAsset != obj)
                throw new InvalidOperationException("Refusing to mask another hull.");
            var vertices = new Vector3[plan.boundary.Length + 1];
            var triangles = new int[plan.boundary.Length * 3];
            vertices[0] = plan.center;
            for (int i = 0; i < plan.boundary.Length; i++)
            {
                if (!SimulationValue.Finite(plan.boundary[i]) || Mathf.Abs(plan.boundary[i].y - plan.heightM) > 1e-6f)
                    throw new InvalidOperationException("Nonplanar or invalid water exclusion boundary.");
                vertices[i + 1] = plan.boundary[i];
                triangles[3 * i] = 0;
                triangles[3 * i + 1] = (i + 1) % plan.boundary.Length + 1;
                triangles[3 * i + 2] = i + 1;
            }
            const string meshPath = "Assets/Art/Boat/OrnamentInteriorWaterExclusion.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null) { mesh = new Mesh(); AssetDatabase.CreateAsset(mesh, meshPath); }
            mesh.Clear(); mesh.name = "OrnamentInteriorWaterExclusion";
            mesh.vertices = vertices; mesh.triangles = triangles; mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            const string materialPath = "Assets/Art/Boat/Mats/OrnamentInteriorWaterExclusion.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            var shader = Shader.Find("DragonBoatVR/InteriorWaterExclusion");
            if (shader == null) throw new InvalidOperationException("Interior exclusion shader is missing.");
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, materialPath); }
            else if (material.shader != shader) throw new InvalidOperationException("Unexpected exclusion material.");
            var existing = boat.transform.Find("InteriorWaterExclusion");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
            var cap = new GameObject("InteriorWaterExclusion");
            cap.transform.SetParent(boat.transform, false);
            cap.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = cap.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the new boat's visual water exclusion.");
            Debug.Log("[INTERIOR_WATER] 1024 inner-wall rays; depth-only cap; no collider, physical roof, or changes to wave/buoyancy.");
        }

        public static void Run()
        {
            const string path = "Assets/Scenes/DragonBoatOrnament.unity";
            const string obj = "Assets/Art/Boat/DragonBoatOrnament.obj";
            var importer = AssetImporter.GetAtPath(obj) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("Ornament importer is missing.");
            // The product uses the actual imported vertices for contact identity,
            // rail sampling and view protection in standalone Players as well.
            if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            var choice = boat != null ? boat.GetComponent<BoatModelChoice>() : null;
            if (choice == null || choice.modelId != BoatModelChoice.Ornament || choice.sourceObjAsset != obj
                || choice.collisionResource != "Collision/Ornament/DragonBoatHull")
                throw new InvalidOperationException("Refusing to change another hull.");
            var model = boat.transform.Find("RealDragonBoat");
            if (model == null) throw new InvalidOperationException("Ornament model is missing.");
            // The CAD already contains the inner floor and side walls. The
            // experimental full-circle loft forms a roof across the eye space;
            // its asset and research remain saved, but it is not a cabin liner.
            var experimental = model.Find("HullLiner");
            if (experimental != null) UnityEngine.Object.DestroyImmediate(experimental.gameObject);
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
                if (filter.sharedMesh == null || !filter.sharedMesh.isReadable)
                    throw new InvalidOperationException("An ornament mesh cannot be sampled by the Player.");
            BindMaterials(model);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save ornament scene.");
            AssetDatabase.SaveAssets();
            Debug.Log("[ORNAMENT_PREP] RESULT=PASS readable CAD hull retained; experimental roof removed; contact validation pending.");
        }

        static void BindMaterials(Transform model)
        {
            var colours = new Dictionary<string, Color>();
            string materialName = null;
            foreach (string line in File.ReadAllLines("Assets/Art/Boat/material_ornament.mtl"))
            {
                string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 2 && words[0] == "newmtl") materialName = words[1];
                if (words.Length == 4 && words[0] == "Kd" && materialName != null)
                    colours[materialName] = new Color(float.Parse(words[1], CultureInfo.InvariantCulture),
                        float.Parse(words[2], CultureInfo.InvariantCulture), float.Parse(words[3], CultureInfo.InvariantCulture), 1f);
            }
            // OBJ groups are separate renderers. Slot zero of every renderer
            // is not slot zero of the entire OBJ's material list.
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>())
            {
                if (!colours.TryGetValue(renderer.name, out Color colour))
                    throw new InvalidOperationException("Unknown ornament material group: " + renderer.name);
                string path = "Assets/Art/Boat/Mats/" + renderer.name + ".mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    material.SetColor("_BaseColor", colour);
                    material.SetFloat("_Smoothness", .32f);
                    material.SetFloat("_Metallic", .05f);
                    AssetDatabase.CreateAsset(material, path);
                }
                if (renderer.sharedMaterials.Length != 1)
                    throw new InvalidOperationException("Unexpected material slots for " + renderer.name);
                renderer.sharedMaterial = material;
                Debug.Log("[ORNAMENT_MATERIAL] " + renderer.name + " => " + path);
            }
        }

        public static void Review()
        {
            Run();
            var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            var model = boat.transform.Find("RealDragonBoat");
            foreach (var item in model.GetComponentsInChildren<Transform>()) item.gameObject.layer = 30;
            var seat = UnityEngine.Object.FindObjectOfType<BoatSeatCamera>();
            string[] args = Environment.GetCommandLineArgs();
            int log = Array.IndexOf(args, "-logFile");
            string folder = Path.GetDirectoryName(args[log + 1]);
            var go = new GameObject("Temporary ornament art review camera");
            var camera = go.AddComponent<Camera>();
            camera.CopyFrom(seat.GetComponent<Camera>());
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.cullingMask = 1 << 30;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.10f, .12f, .15f);
            camera.fieldOfView = 55f;
            var target = new RenderTexture(1280, 760, 24);
            var previous = RenderTexture.active;
            var pixels = new Texture2D(1280, 760, TextureFormat.RGB24, false);
            try
            {
                void Shot(string name, Vector3 eye, Vector3 look)
                {
                    camera.transform.position = boat.transform.TransformPoint(eye);
                    camera.transform.LookAt(boat.transform.TransformPoint(look));
                    camera.targetTexture = target;
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 1280, 760), 0, 0);
                    pixels.Apply();
                    File.WriteAllBytes(Path.Combine(folder, name + ".png"), pixels.EncodeToPNG());
                }
                Shot("ornament_overview", new Vector3(7f, 3.5f, -7f), new Vector3(0f, .8f, 0f));
                Shot("ornament_cabin", seat.DefaultLocalEyePosition, seat.DefaultLocalEyePosition + new Vector3(.2f, -.3f, 1f));
                Shot("ornament_top", new Vector3(0f, 10f, -.3f), new Vector3(0f, 0f, 0f));
                Debug.Log("[ORNAMENT_ART_REVIEW] Three direct mesh previews; not runtime or collision acceptance.");
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(pixels);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
