using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 用 Quaternius CC0 植被模型替换两岸的几何体树。
    ///
    /// 做法(与 RiverCourseBuilder 的旧树摆位完全同位):
    ///   1. 停用合并网格 TreeTrunks / TreeCanopyA / TreeCanopyB;
    ///   2. 在原位置(±38.2m, 每 17m, 岸高 2.25)散布 BirchTree/CommonTree/PineTree;
    ///   3. 亲水步道边加灌木,水线旁加岩石;
    ///   4. 统一换成 4 个共享 URP Lit 材质(替代 OBJ 导入的默认材质),利于合批。
    /// 幂等:重跑先删 NatureProps 旧根再重建。
    /// </summary>
    public static class NatureScatterWizard
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";
        const string NatureDir = "Assets/Art/Nature";

        static readonly string[] TreeSpecies =
            { "BirchTree_1", "BirchTree_2", "BirchTree_3", "CommonTree_1", "CommonTree_2",
              "CommonTree_3", "CommonTree_4", "PineTree_1", "PineTree_2", "PineTree_3" };
        static readonly string[] BushSpecies = { "Bush_1", "Bush_2" };
        static readonly string[] RockSpecies = { "Rock_1", "Rock_2", "Rock_3" };

        // 与 RiverCourseBuilder 同源的河道常量
        const float RiverHalfWidth = 32f;
        const float BankTop = 2.25f;
        const float ZStart = -100f;
        const float CourseLength = 600f;

        [MenuItem("DragonBoatVR/⑥ 两岸植被替换")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[NATURE] 打不开场景"); EditorApplication.Exit(1); return; }

            DisablePrimitiveTrees();

            var old = GameObject.Find("NatureProps");
            if (old != null) Object.DestroyImmediate(old);
            var root = new GameObject("NatureProps");

            var mats = CreateSharedMaterials();

            int trees = 0, bushes = 0, rocks = 0;
            // 前排树:与旧树同位;后排树:更远更大,补层次
            for (int side = -1; side <= 1; side += 2)
            {
                int idx = 0;
                for (float z = ZStart + 11f; z < ZStart + CourseLength; z += 17f, idx++)
                {
                    float jitter = Mathf.Sin(z * 1.37f + side * 2.1f);
                    var species = TreeSpecies[(idx * 3 + (side > 0 ? 1 : 4)) % TreeSpecies.Length];
                    trees += Place(root, mats, species,
                        new Vector3(side * (RiverHalfWidth + 6.2f + jitter * 0.8f), BankTop, z),
                        5.2f + jitter * 0.8f, jitter);
                    if (idx % 2 == 0)
                    {
                        var back = TreeSpecies[(idx * 5 + (side > 0 ? 2 : 6)) % TreeSpecies.Length];
                        trees += Place(root, mats, back,
                            new Vector3(side * (RiverHalfWidth + 13f + Mathf.Cos(z * 0.61f) * 1.5f), BankTop, z + 8.5f),
                            7.0f, jitter * 0.7f);
                    }
                }
                int bIdx = 0;
                for (float z = ZStart + 6f; z < ZStart + CourseLength; z += 13f, bIdx++)
                {
                    if ((bIdx + (side > 0 ? 0 : 1)) % 3 == 0) continue;   // 稀疏一点
                    float j = Mathf.Sin(z * 2.3f + side);
                    bushes += Place(root, mats, BushSpecies[bIdx % BushSpecies.Length],
                        new Vector3(side * (RiverHalfWidth + 2.6f + j * 0.6f), BankTop, z), 1.15f, j);
                }
                int rIdx = 0;
                for (float z = ZStart + 9f; z < ZStart + CourseLength; z += 37f, rIdx++)
                {
                    float j = Mathf.Sin(z * 3.1f + side * 1.7f);
                    rocks += Place(root, mats, RockSpecies[rIdx % RockSpecies.Length],
                        new Vector3(side * (RiverHalfWidth - 1.2f - Mathf.Abs(j)), 0.15f, z), 0.8f, j);
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[NATURE] 完成:乔木 {trees}、灌木 {bushes}、岩石 {rocks};已停用几何体树");
            EditorApplication.Exit(0);
        }

        static void DisablePrimitiveTrees()
        {
            int n = 0;
            foreach (var t in Object.FindObjectsOfType<Transform>())
            {
                if (t.name == "TreeTrunks" || t.name == "TreeCanopyA" || t.name == "TreeCanopyB")
                {
                    t.gameObject.SetActive(false);
                    n++;
                }
            }
            Debug.Log($"[NATURE] 已停用几何体树组合 {n} 组");
        }

        static int Place(GameObject root, Dictionary<string, Material> mats, string species, Vector3 pos, float targetHeight, float jitterSeed)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{NatureDir}/{species}/{species}.obj");
            if (prefab == null) { Debug.LogWarning($"[NATURE] 缺模型 {species}"); return 0; }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, jitterSeed * 137.5f, 0f);

            // 按目标高度归一化缩放(用实例自身包围盒实测,天然规避各单位差异)
            float height = 0f;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) height = Mathf.Max(height, r.bounds.size.y);
            if (height > 0.01f) go.transform.localScale *= targetHeight / height;
            else go.transform.localScale = Vector3.one * targetHeight * 0.4f;

            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var src = r.sharedMaterials;
                var dst = new Material[src.Length];
                for (int i = 0; i < src.Length; i++)
                    dst[i] = MapMaterial(mats, src[i] != null ? src[i].name : "");
                r.sharedMaterials = dst;
                StaticEditorBatch(r);
            }
            if (species.StartsWith("Rock_", System.StringComparison.Ordinal))
                ConfigureWaterlineRock(go, mats["rock"]);
            return 1;
        }

        static Material MapMaterial(Dictionary<string, Material> mats, string name)
        {
            if (name.Contains("Rock")) return mats["rock"];
            if (name.Contains("White")) return mats["canopyLight"];
            if (name.Contains("Black")) return mats["trunk"];
            if (name.Contains("Green")) return mats["canopy"];
            if (name.Contains("Gray")) return mats["rock"];
            return mats["canopy"];
        }

        // Repair the existing instances in place: retain their approved model,
        // position, rotation and scale; do not regenerate trees or river meshes.
        [MenuItem("DragonBoatVR/⑥ 水线岩石材质与实体碰撞修复")]
        public static void RepairWaterlineRocks()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var root = GameObject.Find("NatureProps");
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/Nature_Rock.mat");
            if (!scene.IsValid() || root == null || material == null)
                throw new System.InvalidOperationException("Existing nature scene or stone material is missing.");
            int rocks = 0, meshes = 0;
            foreach (Transform child in root.transform)
            {
                if (!child.name.StartsWith("Rock_", System.StringComparison.Ordinal)) continue;
                Matrix4x4 original = child.localToWorldMatrix;
                meshes += ConfigureWaterlineRock(child.gameObject, material);
                if (!original.Equals(child.localToWorldMatrix))
                    throw new System.InvalidOperationException("Rock repair changed an existing pose.");
                rocks++;
            }
            if (rocks != 32 || meshes != 32)
                throw new System.InvalidOperationException("Unexpected serialized waterline rock inventory.");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[NATURE_ROCK_REPAIR] rocks={rocks} meshes={meshes} posesUnchanged=True RESULT=PASS");
        }

        public static int ConfigureWaterlineRock(GameObject root, Material material)
        {
            int count = 0;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<Renderer>();
                if (filter.sharedMesh == null || renderer == null || !renderer.enabled) continue;
                var materials = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < materials.Length; i++) materials[i] = material;
                renderer.sharedMaterials = materials;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                var collider = filter.GetComponent<MeshCollider>();
                if (collider == null) collider = filter.gameObject.AddComponent<MeshCollider>();
                if (collider.attachedRigidbody != null || filter.GetComponents<Collider>().Length != 1)
                    throw new System.InvalidOperationException("Waterline rock must have one static mesh collider.");
                collider.convex = false;
                collider.isTrigger = false;
                collider.sharedMesh = filter.sharedMesh;
                collider.enabled = true;
                count++;
            }
            return count;
        }

        static Dictionary<string, Material> CreateSharedMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            Material Make(string name, Color c)
            {
                string path = $"Assets/Settings/Nature_{name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
                m.SetColor("_BaseColor", c);
                m.SetFloat("_Smoothness", 0.08f);
                EditorUtility.SetDirty(m);
                return m;
            }
            var mats = new Dictionary<string, Material>
            {
                ["trunk"]      = Make("Trunk", new Color(0.32f, 0.22f, 0.15f)),
                ["canopy"]     = Make("Canopy", new Color(0.27f, 0.44f, 0.21f)),
                ["canopyLight"]= Make("CanopyLight", new Color(0.55f, 0.64f, 0.42f)),
                ["rock"]       = Make("Rock", new Color(0.44f, 0.44f, 0.46f)),
            };
            AssetDatabase.SaveAssets();
            return mats;
        }

        static void StaticEditorBatch(Renderer r)
        {
            GameObjectUtility.SetStaticEditorFlags(r.gameObject,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        }
    }
}
