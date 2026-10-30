using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// Ornament(龙舟摆件)船专用:把结构化内衬挂入新船场景 RealDragonBoat/HullLiner。
    /// 内衬=封闭 loft 舱壁(tmp/dragonboat_sldprt/build_liner_v3.py 产物,零穿出),
    /// 视觉上填充镂空鳞片内侧,并作为 BoatCollisionMeshBuilder 的碰撞源。
    /// 只写 Assets/Art/Boat/HullLiner.* 与 DragonBoatOrnament.unity,不碰 Demo/公共代码。
    /// </summary>
    public static class OrnamentBoatLinerTool
    {
        const string ScenePath = "Assets/Scenes/DragonBoatOrnament.unity";
        const string LinerObj = "Assets/Art/Boat/HullLiner.obj";
        const string LinerMat = "Assets/Art/Boat/Mats/OrnamentLiner.mat";

        [MenuItem("DragonBoatVR/Ornament 内衬挂接")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            if (boat == null) throw new InvalidOperationException("DragonBoatOrnament.unity has no BoatBuoyancy.");
            var model = boat.transform.Find("RealDragonBoat");
            if (model == null) throw new InvalidOperationException("RealDragonBoat missing in ornament scene.");

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(LinerObj);
            if (mesh == null) { AssetDatabase.ImportAsset(LinerObj, ImportAssetOptions.ForceSynchronousImport); mesh = AssetDatabase.LoadAssetAtPath<Mesh>(LinerObj); }
            if (mesh == null) throw new InvalidOperationException("HullLiner mesh import failed.");

            // 深木双面材质(座舱内壁;URP Render Face Both)
            Directory.CreateDirectory("Assets/Art/Boat/Mats");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(LinerMat);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.SetColor("_BaseColor", new Color(0.36f, 0.25f, 0.16f, 1f));
                mat.SetFloat("_Smoothness", 0.30f);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);   // URP Lit 双面:舱内相机看到内壁背面
                AssetDatabase.CreateAsset(mat, LinerMat);
            }

            var existing = model.Find("HullLiner");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
            var node = new GameObject("HullLiner");
            node.transform.SetParent(model, false);
            node.transform.localPosition = Vector3.zero;
            node.transform.localRotation = Quaternion.identity;
            node.transform.localScale = Vector3.one;
            var filter = node.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            var renderer = node.AddComponent<MeshRenderer>(); renderer.sharedMaterial = mat;
            // 内衬是封闭碰撞源,不投射阴影(外饰镂空是视觉主体)
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ORNAMENT_LINER] installed faces={mesh.vertexCount}v/{mesh.triangles.Length / 3}f at RealDragonBoat/HullLiner; mat={LinerMat}");
            Debug.Log("[ORNAMENT_LINER] RESULT=PASS liner installed in DragonBoatOrnament.unity");
        }
    }
}
