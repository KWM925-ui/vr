using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 离线渲染预览图：不进 Play 模式，直接把场景渲到 RenderTexture 存 PNG。
    ///
    /// 为什么需要它：
    ///   这个工程是靠 batchmode 命令行迭代的，看不见画面就没法判断"两岸是不是太空"
    ///   "水面色对不对""船的尺度合不合适"。每次手动打开编辑器按 Play 截屏，
    ///   一轮验证要两分钟，改十次就是二十分钟。这里把验证压到十几秒。
    ///
    /// 注意：必须**不带** -nographics 启动 Unity，否则没有图形设备，渲染出来是全黑的。
    /// </summary>
    public static class ScreenshotTool
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";

        [MenuItem("DragonBoatVR/④ 渲染预览图", priority = 3)]
        public static void Capture()
        {
            var outPath = ArgValue("-shotPath") ?? "F:/vr视觉/tools/shot_course.png";
            float camX = 7f, camY = 6.5f, camZ = -20f;
            float lookX = 0f, lookY = 0.5f, lookZ = 30f;
            float fov = 55f, simTime = 12.5f;

            var scene = EditorSceneManager.OpenScene(ScenePath);
            if (!scene.IsValid())
            {
                Debug.LogError("[SHOT] 打不开场景");
                return;
            }

            var wave = Object.FindObjectOfType<WaveField>();
            var surf = Object.FindObjectOfType<WaterSurface>();

            // 非 Play 模式下 Awake 不会自动跑，水面网格要靠它生成
            if (wave != null) Invoke(wave, "Awake");
            var simField = typeof(WaveField).GetField("<SimTime>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (simField != null && wave != null) simField.SetValue(wave, simTime);
            if (surf != null) Invoke(surf, "Awake");

            var camGo = new GameObject("CaptureCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 900f;
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.transform.position = new Vector3(camX, camY, camZ);
            camGo.transform.LookAt(new Vector3(lookX, lookY, lookZ));

            const int w = 1920, h = 1080;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var prev = RenderTexture.active;

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outPath, tex.EncodeToPNG());

                Object.DestroyImmediate(tex);
                Debug.Log($"[SHOT] 已保存 {outPath} ({w}x{h}, fov={fov}, cam=({camX},{camY},{camZ}))");
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = prev;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
            }
        }

        static string ArgValue(string key)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return null;
        }

        static void Invoke(object target, string method)
        {
            if (target == null) return;
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null) m.Invoke(target, null);
        }
    }
}
