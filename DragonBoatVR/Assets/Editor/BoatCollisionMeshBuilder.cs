using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DragonBoatVR.EditorTools
{
    public static class BoatCollisionMeshBuilder
    {
        const string LibraryPath = "Assets/Editor/ThirdParty/CoACD/lib_coacd.dll";
        const string LibraryHash = "a32e801d260b774ebc723edfc05e45e179c09d0d2355592c64cbff490698e806";
        const float ThresholdM = 0.025f;
        const int PreprocessResolution = 200, Seed = 20261028, MaximumVerticesPerPart = 120;
        const float SharedFaceOverlapM = 0.005f;
        const bool MergeParts = true;

        [Serializable] sealed class CachedPart { public Vector3[] vertices; public int[] triangles; }
        [Serializable] sealed class CachedDecomposition
        {
            public string engine, nativeLibrarySha256, sourceObjSha256, sourceImportSha256;
            public string sourceSetSha256, grouping;
            public Vector3 modelLocalPosition, modelLocalScale;
            public Quaternion modelLocalRotation;
            public float thresholdM, sharedFaceOverlapM;
            public int preprocessResolution, seed, maximumVerticesPerPart;
            public bool mergeParts;
            public CachedPart[] parts;
        }

        [MenuItem("DragonBoatVR/生成显示模型的船体碰撞网格")]
        public static void Run()
        {
            if (Hash(LibraryPath) != LibraryHash) throw new InvalidDataException("CoACD library identity differs from the pinned package.");
            string[] cliArgs = Environment.GetCommandLineArgs();
            string scenePath = "Assets/Scenes/Demo.unity";
            int sceneIdx = Array.IndexOf(cliArgs, "-collisionScene");
            if (sceneIdx >= 0 && sceneIdx + 1 < cliArgs.Length) scenePath = cliArgs[sceneIdx + 1];
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            // 双船防呆:场景身份必须与 BoatModelChoice 一致,输出/源对象键由场景组件声明
            var choiceCheck = UnityEngine.Object.FindObjectOfType<BoatModelChoice>();
            if (choiceCheck == null) throw new InvalidOperationException("Target scene has no BoatModelChoice identity.");
            if (scenePath.Contains("Ornament") && choiceCheck.modelId != BoatModelChoice.Ornament)
                throw new InvalidOperationException("Ornament scene does not declare modelId=ornament.");
            if (!scenePath.Contains("Ornament") && choiceCheck.modelId != BoatModelChoice.Majesty)
                throw new InvalidOperationException("Majesty scene does not declare modelId=majesty.");
            var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            Transform model = boat != null ? boat.transform.Find("RealDragonBoat") : null;
            if (model == null) throw new InvalidOperationException("Displayed boat model is missing.");
            var sources = new List<CombineInstance>();
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
                sources.Add(new CombineInstance { mesh = filter.sharedMesh,
                    transform = boat.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix });
            }
            if (sources.Count == 0) throw new InvalidOperationException("No displayed hull meshes found.");
            var combined = new Mesh { indexFormat = IndexFormat.UInt32 };
            combined.CombineMeshes(sources.ToArray(), true, true, false);
            // Every displayed part participates, including the bow and stern.
            // A separate liner must not silently replace the visible hull.
            Mesh decomposeSource = combined;
            string sourceKind = "DISPLAYED_MESH_ALL";
            string[] sourceAssets = model.GetComponentsInChildren<MeshFilter>()
                .Where(f => f.sharedMesh != null && f.GetComponent<Renderer>() != null && f.GetComponent<Renderer>().enabled)
                .Select(f => AssetDatabase.GetAssetPath(f.sharedMesh)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (sourceAssets.Any(p => string.IsNullOrEmpty(p) || !File.Exists(p) || !File.Exists(p + ".meta")))
                throw new InvalidDataException("Every displayed collision source must have a persistent asset and importer identity.");
            string sourceSetText = string.Concat(sourceAssets.Select(p => p + ":" + Hash(p) + ":" + Hash(p + ".meta") + "\n"));
            string sourceSetHash;
            using (var sha = SHA256.Create())
                sourceSetHash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sourceSetText)))
                    .Replace("-", "").ToLowerInvariant();
            var choice = boat.GetComponent<BoatModelChoice>() ?? throw new InvalidOperationException("BoatModelChoice identity missing.");
            var metadata = new BoatCollisionMeshSource {
                nativeLibrarySha256 = LibraryHash, convexHullCommit = "6134f1c380dec3170376bf3f4d37d19a6ce0e880",
                sourceObjSha256 = Hash(choice.sourceObjAsset),
                sourceImportSha256 = Hash(choice.sourceObjAsset + ".meta"),
                sourceSetSha256 = sourceSetHash,
                decompositionThresholdM = ThresholdM, preprocessResolution = PreprocessResolution, randomSeed = Seed,
                maximumVerticesPerPart = MaximumVerticesPerPart, mergeParts = MergeParts, sharedFaceOverlapM = SharedFaceOverlapM,
                sourceVertices = combined.vertexCount, sourceTriangles = combined.triangles.Length / 3,
                sourceMin = combined.bounds.min, sourceMax = combined.bounds.max,
                modelLocalPosition = model.localPosition, modelLocalRotation = model.localRotation, modelLocalScale = model.localScale,
                collisionSourceKind = sourceKind };
            var parts = new List<Mesh>(); List<Mesh> decomposed = null;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                int cacheIndex = Array.IndexOf(args, "-collisionDecomposition");
                if (cacheIndex >= 0)
                {
                    if (cacheIndex + 1 >= args.Length) throw new ArgumentException("Missing cached decomposition path.");
                    var cache = JsonUtility.FromJson<CachedDecomposition>(File.ReadAllText(args[cacheIndex + 1]));
                    if (cache == null || cache.engine != metadata.decompositionEngine || cache.nativeLibrarySha256 != LibraryHash
                        || cache.sourceObjSha256 != metadata.sourceObjSha256 || cache.sourceImportSha256 != metadata.sourceImportSha256
                        || Vector3.Distance(cache.modelLocalPosition, model.localPosition) > 1e-6f
                        || Vector3.Distance(cache.modelLocalScale, model.localScale) > 1e-6f
                        || Quaternion.Angle(cache.modelLocalRotation, model.localRotation) > 1e-5f
                        || Mathf.Abs(cache.thresholdM - ThresholdM) > 1e-7f || cache.preprocessResolution != PreprocessResolution || cache.seed != Seed
                        || cache.maximumVerticesPerPart != MaximumVerticesPerPart || cache.mergeParts != MergeParts
                        || Mathf.Abs(cache.sharedFaceOverlapM - SharedFaceOverlapM) > 1e-7f)
                        throw new InvalidDataException("Cached CoACD result differs from the displayed model or generation recipe.");
                    // Legacy majesty caches contained only the one primary OBJ.
                    // New hulls or auxiliary meshes require the complete source
                    // set, including importer changes, to match before reuse.
                    if ((choice.modelId == BoatModelChoice.Ornament || sourceAssets.Length > 1 || !string.IsNullOrEmpty(cache.sourceSetSha256))
                        && cache.sourceSetSha256 != metadata.sourceSetSha256)
                        throw new InvalidDataException("Cached decomposition differs from the full displayed asset set.");
                    string grouping = string.IsNullOrEmpty(cache.grouping) ? "COMBINED" : cache.grouping;
                    if (grouping != "COMBINED" && !(choice.modelId == BoatModelChoice.Ornament && grouping == "DISPLAY_MATERIAL_GROUPS"))
                        throw new InvalidDataException("Unknown decomposition partition recipe.");
                    metadata.decompositionGrouping = grouping;
                    if (cache.parts == null || cache.parts.Length < 2 || cache.parts.Length > BoatHullCollisionShape.MaximumPartsFor(choice))
                        throw new InvalidDataException("Cached decomposition has no bounded compound.");
                    decomposed = new List<Mesh>();
                    foreach (var part in cache.parts)
                    {
                        if (part.vertices == null || part.vertices.Length < 4 || part.vertices.Length > 120 || part.triangles == null)
                            throw new InvalidDataException("Invalid cached convex part.");
                        decomposed.Add(new Mesh { indexFormat = IndexFormat.UInt32, vertices = part.vertices, triangles = part.triangles });
                    }
                }
                else decomposed = CoacdNative.Decompose(decomposeSource, ThresholdM, PreprocessResolution, Seed,
                    MaximumVerticesPerPart, MergeParts, SharedFaceOverlapM);

                foreach (var part in decomposed)
                {
                    if (part.vertices.Any(v => !SimulationValue.Finite(v))) throw new InvalidDataException("Nonfinite collision vertex.");
                    // Reconstruct complete convex faces after CoACD's reduction.
                    // Each part remains inside PhysX's documented polygon limit.
                    Mesh mesh = CompleteHull(part.vertices);
                    mesh.name = "DragonBoatCollision_" + parts.Count.ToString("0000");
                    parts.Add(mesh);
                    if (mesh.vertexCount < 4 || mesh.vertexCount > 120 || mesh.triangles.Length / 3 > 236)
                        throw new InvalidDataException("Complete convex part exceeds the native polygon budget.");
                }
                if (parts.Count < 2 || parts.Count > BoatHullCollisionShape.MaximumPartsFor(choice))
                    throw new InvalidDataException("Unbounded collision compound.");
                metadata.parts = parts.Select(p => new BoatCollisionMeshSource.Part
                    { name = p.name, vertices = p.vertexCount, triangles = p.triangles.Length / 3 }).ToArray();
                metadata.collisionVertices = metadata.parts.Sum(p => p.vertices);
                metadata.collisionTriangles = metadata.parts.Sum(p => p.triangles);
                string resRoot = "Assets/Resources/Collision";
                string assetRel = "Collision/DragonBoatHull", sourceRel = "Collision/DragonBoatHullSource";
                if (choice != null && !string.IsNullOrWhiteSpace(choice.collisionResource))
                {
                    assetRel = choice.collisionResource; sourceRel = choice.collisionSourceResource;
                    resRoot = "Assets/Resources/" + assetRel.Substring(0, assetRel.LastIndexOf('/'));
                    if (!AssetDatabase.IsValidFolder(resRoot))
                    {
                        Directory.CreateDirectory(resRoot);
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    }
                }
                string AssetPath = resRoot + "/DragonBoatHull.asset";
                string SourcePathOut = resRoot + "/DragonBoatHullSource.json";
                Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(AssetPath);
                var oldAssets = AssetDatabase.LoadAllAssetsAtPath(AssetPath);
                if (oldAssets.Any(asset => asset != null && !(asset is Mesh) && asset.GetType().FullName != "UnityEditor.AssetImporters.ImportLog"))
                    throw new InvalidDataException("Collision asset contains unrelated objects: "
                    + string.Join(", ", oldAssets.Select(asset => asset == null ? "null" : asset.name + " [" + asset.GetType().FullName + "]")));
                if (existing == null)
                {
                    AssetDatabase.CreateAsset(parts[0], AssetPath);
                    // Creating the main asset assigns the file stem as its
                    // object name. Restore the declared part identity before
                    // saving, just as CopySerialized does on later rebuilds.
                    parts[0].name = metadata.parts[0].name;
                    EditorUtility.SetDirty(parts[0]);
                    parts[0] = null;
                }
                else
                {
                    foreach (var prior in oldAssets.OfType<Mesh>()) if (prior != existing) UnityEngine.Object.DestroyImmediate(prior, true);
                    EditorUtility.CopySerialized(parts[0], existing); EditorUtility.SetDirty(existing);
                }
                for (int i = 1; i < parts.Count; i++) { AssetDatabase.AddObjectToAsset(parts[i], AssetPath); parts[i] = null; }
                File.WriteAllText(SourcePathOut, JsonUtility.ToJson(metadata, true) + "\n");
                AssetDatabase.ImportAsset(SourcePathOut, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
                var imported = AssetDatabase.LoadAllAssetsAtPath(AssetPath).OfType<Mesh>()
                    .OrderBy(p => p.name, StringComparer.Ordinal).ToArray();
                if (imported.Length != metadata.parts.Length || imported.Where((p, i) =>
                    p.name != metadata.parts[i].name || p.vertexCount != metadata.parts[i].vertices
                    || p.triangles.Length / 3 != metadata.parts[i].triangles).Any())
                    throw new InvalidDataException("Imported collision subassets differ from their declared identities.");
                Debug.Log($"[HULLMESH] parts={metadata.parts.Length} vertices={metadata.collisionVertices} triangles={metadata.collisionTriangles}");
                Debug.Log("[HULLMESH] RESULT=PASS generated compound; runtime contact and motion still require verification");
            }
            finally
            {
                foreach (var mesh in parts) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                if (decomposed != null) foreach (var mesh in decomposed) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(combined);
            }
        }

        static string Hash(string path)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        static Mesh CompleteHull(Vector3[] vertices)
        {
            var input = vertices.Select(v => new MIConvexHull.DefaultVertex
                { Position = new[] { (double)v.x, v.y, v.z } }).ToList();
            var result = MIConvexHull.ConvexHull.Create(input);
            if (result.Outcome != MIConvexHull.ConvexHullCreationResultOutcome.Success || result.Result == null)
                throw new InvalidOperationException("Complete source hull generation failed: " + result.Outcome);
            var points = result.Result.Points.ToArray();
            var indices = new Dictionary<MIConvexHull.DefaultVertex, int>();
            var meshVertices = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                indices.Add(points[i], i);
                meshVertices[i] = new Vector3((float)points[i].Position[0], (float)points[i].Position[1], (float)points[i].Position[2]);
            }
            var triangles = new List<int>();
            foreach (var face in result.Result.Faces)
            {
                if (face.Vertices.Length != 3) throw new InvalidOperationException("Non-triangular source hull face.");
                int a = indices[face.Vertices[0]], b = indices[face.Vertices[1]], c = indices[face.Vertices[2]];
                var normal = new Vector3((float)face.Normal[0], (float)face.Normal[1], (float)face.Normal[2]);
                if (Vector3.Dot(Vector3.Cross(meshVertices[b] - meshVertices[a], meshVertices[c] - meshVertices[a]), normal) < 0f)
                { int swap = b; b = c; c = swap; }
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, vertices = meshVertices, triangles = triangles.ToArray() };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
