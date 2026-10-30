using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using DragonBoatVR;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Checks both sample river profiles, the Orange Isle statue photo, and scene switching.</summary>
    public static class RiverScenerySelfTest
    {
        const int SpectatorCount = 12;
        const int BackgroundBoatCount = 3;

        [MenuItem("DragonBoatVR/⑤ 河道场景气氛自检", priority = 5)]
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject orange = null;
            GameObject fengjian = null;
            GameObject selector = null;
            GameObject sphere = null;
            GameObject cube = null;
            try
            {
                sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                int sphereVertices = sphere.GetComponent<MeshFilter>().sharedMesh.vertexCount;
                int cubeVertices = cube.GetComponent<MeshFilter>().sharedMesh.vertexCount;

                orange = RiverCourseBuilder.Build(null, RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
                fengjian = RiverCourseBuilder.Build(null, RiverEnvironmentController.SceneProfile.ShundeFengjian);
                fengjian.SetActive(false);
                bool visualConstruction = Colliders(orange) == 0 && Colliders(fengjian) == 0;

                bool orangeProfile = MeshVertices(orange, "XiangjiangCity") >= cubeVertices * 9
                    && MeshVertices(orange, "HouseWalls") == 0
                    && MeshVertices(orange, "SpectatorHeads") >= sphereVertices * SpectatorCount
                    && MeshVertices(orange, "BackgroundBoatHulls") >= sphereVertices * BackgroundBoatCount;
                bool fengjianProfile = MeshVertices(fengjian, "HouseWalls") >= cubeVertices * 20
                    && MeshVertices(fengjian, "HouseRoofs") >= cubeVertices * 20
                    && MeshVertices(fengjian, "Railing") >= cubeVertices * 50
                    && MeshVertices(fengjian, "XiangjiangCity") == 0
                    && MeshVertices(fengjian, "SpectatorLegs") >= cubeVertices * SpectatorCount * 2;

                Texture2D statuePhoto = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/VisualReferences/Juzizhou_Mao_Statue_Cutout.png");
                Transform statue = orange.transform.Find("MaoZedongStatue");
                MeshRenderer statueRenderer = statue != null ? statue.GetComponent<MeshRenderer>() : null;
                MeshFilter statueFilter = statue != null ? statue.GetComponent<MeshFilter>() : null;
                TextureImporter statueImporter = AssetImporter.GetAtPath(
                    "Assets/VisualReferences/Juzizhou_Mao_Statue_Cutout.png") as TextureImporter;
                Vector3 towardRiverStart = statue != null
                    ? new Vector3(-statue.localPosition.x, 0f, -statue.localPosition.z).normalized : Vector3.zero;
                Vector3 photoNormal = statueFilter != null && statueFilter.sharedMesh.normals.Length > 0
                    ? statue.TransformDirection(statueFilter.sharedMesh.normals[0]) : Vector3.zero;
                photoNormal.y = 0f;
                if (photoNormal.sqrMagnitude > 1e-6f) photoNormal.Normalize();
                bool statueLandmark = statuePhoto != null && statueRenderer != null
                    && statueRenderer.sharedMaterial != null
                    && statueRenderer.sharedMaterial.mainTexture == statuePhoto
                    && statueImporter != null && statueImporter.alphaIsTransparency
                    && statueRenderer.sharedMaterial.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent
                    && statueRenderer.sharedMaterial.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")
                    && statue.GetComponent<Collider>() == null
                    && statue.transform.localScale.x >= 32f
                    && statue.transform.localPosition.x < 0f
                    && statue.transform.localPosition.z > 0f
                    && statue.transform.localPosition.z < RiverCourseBuilder.CourseLength
                    && Vector3.Dot(photoNormal, towardRiverStart) > 0.95f;

                selector = new GameObject("RiverSceneSelector_Test");
                var controller = selector.AddComponent<RiverEnvironmentController>();
                controller.xiangjiangScene = orange;
                controller.fengjianScene = fengjian;
                controller.SetScene(RiverEnvironmentController.SceneProfile.ShundeFengjian);
                bool switchedToFengjian = !orange.activeSelf && fengjian.activeSelf;
                controller.SetScene(RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
                bool switchedToOrange = orange.activeSelf && !fengjian.activeSelf;

                bool lightweight = Renderers(orange) <= 20 && Renderers(fengjian) <= 20
                    && StaticSolidsMatch(orange, 4) && StaticSolidsMatch(fengjian, 5);
                // The required bank binding must be present. This negative
                // control prevents an all-visual scene from passing accidentally.
                Collider bank = orange.transform.Find("Bank").GetComponent<Collider>();
                bank.enabled = false;
                bool missingBankRejected = !StaticSolidsMatch(orange, 4);
                bank.enabled = true;
                bool pass = orangeProfile && fengjianProfile && statueLandmark
                    && switchedToFengjian && switchedToOrange && visualConstruction && lightweight && missingBankRejected;

                Debug.Log($"[SCENERY] Orange city={MeshVertices(orange, "XiangjiangCity")} vertices; "
                    + $"statuePhoto={(statueLandmark ? "present" : "missing")}; "
                    + $"Fengjian houses={MeshVertices(fengjian, "HouseWalls")} vertices, "
                    + $"bridge rails={MeshVertices(fengjian, "Railing")} vertices");
                Debug.Log($"[SCENERY] switching orange->Fengjian={switchedToFengjian}, "
                    + $"Fengjian->orange={switchedToOrange}; renderers={Renderers(orange)}/{Renderers(fengjian)}; "
                    + $"colliders={Colliders(orange)}/{Colliders(fengjian)}");
                Debug.Log($"[SCENERY] visualConstruction={visualConstruction} exactStaticSolids={lightweight} missingBankRejected={missingBankRejected}");
                Debug.Log(pass
                    ? "[SCENERY] RESULT=PASS sample scenes, statue landmark, switching, matching static banks and visual-only decorations verified"
                    : "[SCENERY] RESULT=FAIL one or more scene, statue, switching, or static-scenery contract checks failed");
            }
            finally
            {
                if (selector != null) Object.DestroyImmediate(selector);
                if (orange != null) Object.DestroyImmediate(orange);
                if (fengjian != null) Object.DestroyImmediate(fengjian);
                if (sphere != null) Object.DestroyImmediate(sphere);
                if (cube != null) Object.DestroyImmediate(cube);
            }
        }

        static int MeshVertices(GameObject root, string childName)
        {
            Transform child = root != null ? root.transform.Find(childName) : null;
            MeshFilter filter = child != null ? child.GetComponent<MeshFilter>() : null;
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.vertexCount : 0;
        }

        static int Renderers(GameObject root) => root != null
            ? root.GetComponentsInChildren<MeshRenderer>(true).Length : 0;

        static int Colliders(GameObject root) => root != null
            ? root.GetComponentsInChildren<Collider>(true).Length : 0;

        static bool StaticSolidsMatch(GameObject root, int expectedCount)
        {
            var colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders.Length != expectedCount || root.GetComponentsInChildren<Rigidbody>(true).Length != 0) return false;
            foreach (Collider collider in colliders)
            {
                bool expected = collider.transform.parent == root.transform
                    && RiverEnvironmentController.SolidSceneMeshes.Contains(collider.name);
                var mesh = collider as MeshCollider;
                var displayed = collider.GetComponent<MeshFilter>();
                if (!expected || mesh == null || !mesh.enabled || mesh.convex || mesh.isTrigger
                    || mesh.attachedRigidbody != null || displayed == null || mesh.sharedMesh != displayed.sharedMesh) return false;
            }
            return true;
        }
    }
}
