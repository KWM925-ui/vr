using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    public static class BoatSelectionSceneBuilder
    {
        const string Demo = "Assets/Scenes/Demo.unity";
        const string OrnamentScene = "Assets/Scenes/DragonBoatOrnament.unity";
        const string BoatAssets = "Assets/Art/Boat/";
        const string Original = "../tmp/boat_selection_20261030/majesty_baseline/Assets/";

        public static void Run()
        {
            if (File.Exists(OrnamentScene)) throw new InvalidOperationException("Boat split is already present; do not regenerate over scene edits.");
            // Keep the complete new import in a separate asset before restoring
            // the original GUID/path. Both scenes then have stable references.
            string obj = File.ReadAllText(BoatAssets + "DragonBoat.obj");
            if (!obj.Contains("g head_gold") || !obj.Contains("usemtl head_gold")
                || !obj.Contains("usemtl hull_red") || !obj.Contains("usemtl deco_gold"))
                throw new InvalidDataException("Expected the documented ornament import before splitting scenes.");
            // The current import binds its materials explicitly and has no
            // mtllib declaration. Add the isolated library without changing
            // vertices, indices, group names or the scene material bindings.
            string isolated = obj.Contains("mtllib material.mtl")
                ? obj.Replace("mtllib material.mtl", "mtllib material_ornament.mtl")
                : "mtllib material_ornament.mtl\n" + obj;
            File.WriteAllText(BoatAssets + "DragonBoatOrnament.obj", isolated);
            File.Copy(BoatAssets + "material.mtl", BoatAssets + "material_ornament.mtl", false);
            AssetDatabase.ImportAsset(BoatAssets + "material_ornament.mtl", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(BoatAssets + "DragonBoatOrnament.obj", ImportAssetOptions.ForceSynchronousImport);

            var scene = EditorSceneManager.OpenScene(Demo, OpenSceneMode.Single);
            var boat = Object.FindObjectOfType<BoatBuoyancy>();
            Transform prior = boat != null ? boat.transform.Find("RealDragonBoat") : null;
            if (prior == null) throw new InvalidOperationException("Current new boat is missing.");
            var sourceRenderers = prior.GetComponentsInChildren<MeshRenderer>().ToDictionary(
                r => AnimationUtility.CalculateTransformPath(r.transform, prior));
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(BoatAssets + "DragonBoatOrnament.obj");
            if (model == null) throw new InvalidDataException("Separate ornament asset was not imported.");
            var replacement = Object.Instantiate(model, boat.transform);
            replacement.transform.localPosition = prior.localPosition;
            replacement.transform.localRotation = prior.localRotation;
            replacement.transform.localScale = prior.localScale;
            int checkedMeshes = 0;
            foreach (var renderer in replacement.GetComponentsInChildren<MeshRenderer>())
            {
                string key = AnimationUtility.CalculateTransformPath(renderer.transform, replacement.transform);
                if (!sourceRenderers.TryGetValue(key, out MeshRenderer source))
                    throw new InvalidDataException("Separate import changed the ornament hierarchy: " + key);
                var before = source.GetComponent<MeshFilter>().sharedMesh;
                var after = renderer.GetComponent<MeshFilter>().sharedMesh;
                if (!before.vertices.SequenceEqual(after.vertices) || !before.triangles.SequenceEqual(after.triangles))
                    throw new InvalidDataException("Separate import changed ornament geometry: " + key);
                renderer.sharedMaterials = source.sharedMaterials;
                renderer.enabled = source.enabled; checkedMeshes++;
            }
            if (checkedMeshes != sourceRenderers.Count) throw new InvalidDataException("Ornament renderer coverage differs.");
            Object.DestroyImmediate(prior.gameObject); replacement.name = "RealDragonBoat";
            var choice = boat.gameObject.AddComponent<BoatModelChoice>();
            choice.modelId = BoatModelChoice.Ornament;
            choice.collisionResource = "Collision/Ornament/DragonBoatHull";
            choice.collisionSourceResource = "Collision/Ornament/DragonBoatHullSource";
            choice.sourceObjAsset = BoatAssets + "DragonBoatOrnament.obj";
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, OrnamentScene, true)) throw new IOException("Could not preserve ornament scene.");

            // Restore original bytes and use its recorded local transform. Other
            // current scene repairs (including all 32 rock colliders) are kept.
            File.Copy(Original + "Art/Boat/DragonBoat.obj", BoatAssets + "DragonBoat.obj", true);
            File.Copy(Original + "Art/Boat/material.mtl", BoatAssets + "material.mtl", true);
            AssetDatabase.ImportAsset(BoatAssets + "material.mtl", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(BoatAssets + "DragonBoat.obj", ImportAssetOptions.ForceSynchronousImport);
            var original = JsonUtility.FromJson<BoatCollisionMeshSource>(File.ReadAllText(Original + "Resources/Collision/DragonBoatHullSource.json"));
            var classicAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BoatAssets + "DragonBoat.obj");
            var classicMaterial = AssetDatabase.LoadAssetAtPath<Material>(BoatAssets + "Mats/material_0.mat");
            if (classicAsset == null || classicMaterial == null) throw new InvalidDataException("Original boat assets are unavailable.");
            var classic = Object.Instantiate(classicAsset, boat.transform);
            classic.transform.localPosition = original.modelLocalPosition;
            classic.transform.localRotation = original.modelLocalRotation;
            classic.transform.localScale = original.modelLocalScale;
            foreach (var renderer in classic.GetComponentsInChildren<MeshRenderer>())
                renderer.sharedMaterials = Enumerable.Repeat(classicMaterial, renderer.GetComponent<MeshFilter>().sharedMesh.subMeshCount).ToArray();
            Object.DestroyImmediate(replacement); classic.name = "RealDragonBoat";
            choice.modelId = BoatModelChoice.Majesty;
            choice.collisionResource = "Collision/DragonBoatHull";
            choice.collisionSourceResource = "Collision/DragonBoatHullSource";
            choice.sourceObjAsset = BoatAssets + "DragonBoat.obj";
            Object.FindObjectOfType<PaddleDriver>().pivotLocalPos = new Vector3(0.44755954f, 0.2f, 0f);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, Demo)) throw new IOException("Could not restore classic boat scene.");
            AssetDatabase.SaveAssets();
            Debug.Log("[BOAT_SELECTION_SPLIT] preserved ornament meshes=" + checkedMeshes + "; restored original boat and paddle station. New boat collision is still pending.");
            EditorApplication.Exit(0);
        }
    }
}
