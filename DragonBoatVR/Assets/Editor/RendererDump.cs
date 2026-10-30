using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    /// <summary>临时诊断：把场景里每个渲染器实际用的材质和基色打出来。</summary>
    public static class RendererDump
    {
        [MenuItem("DragonBoatVR/诊断/材质检查", priority = 90)]
        public static void Dump()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity");

            var mat = GraphicsSettingsPipeline();
            Debug.Log($"[DUMP] 当前渲染管线资产 = {(mat != null ? mat.name : "None(Built-in)")}");

            // Include every Renderer subtype. The imported OBJ can contain a
            // prefab child whose renderer is not a MeshRenderer (and a visual
            // that is black in the game view is otherwise invisible to this
            // diagnostic).
            foreach (var mr in Object.FindObjectsOfType<Renderer>(true))
            {
                if (mr == null || !mr.enabled) continue;
                var m = mr.sharedMaterial;
                string col = "n/a";
                if (m != null)
                {
                    if (m.HasProperty("_BaseColor")) col = m.GetColor("_BaseColor").ToString("F2");
                    else if (m.HasProperty("_Color")) col = m.GetColor("_Color").ToString("F2");
                }
                var b = mr.bounds;
                var filter = mr.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                Debug.Log($"[DUMP] {mr.GetType().Name} path={HierarchyPath(mr.transform)}  mat={(m != null ? m.name : "NULL")}  shader={(m != null && m.shader != null ? m.shader.name : "-")}  color={col}  verts={(mesh != null ? mesh.vertexCount : 0)}  pos={mr.transform.position:F2}  size={b.size:F2}");
            }

            foreach (var cam in Object.FindObjectsOfType<Camera>(true))
                Debug.Log($"[DUMP] CAMERA {cam.name} enabled={cam.enabled} rect={cam.rect} fov={cam.fieldOfView} pos={cam.transform.position:F2} rot={cam.transform.eulerAngles:F2} parent={(cam.transform.parent != null ? cam.transform.parent.name : "-")}");

            var light = Object.FindObjectOfType<Light>();
            if (light != null)
                Debug.Log($"[DUMP] 主光 intensity={light.intensity} color={light.color} rot={light.transform.eulerAngles}");

            Debug.Log($"[DUMP] ambientMode={RenderSettings.ambientMode} sky={RenderSettings.ambientSkyColor} " +
                      $"equator={RenderSettings.ambientEquatorColor} ground={RenderSettings.ambientGroundColor} " +
                      $"skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "NULL")} fog={RenderSettings.fog}");
        }

        static string HierarchyPath(Transform t)
        {
            string path = t != null ? t.name : "-";
            while (t != null && t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        static Object GraphicsSettingsPipeline()
        {
            var rp = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
            return rp;
        }
    }
}
