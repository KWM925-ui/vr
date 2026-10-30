using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Short, read-only runtime scene inspection for visual iteration.</summary>
    public static class RuntimeSceneDump
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";
        static int _frames;

        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[RUNTIME-DUMP] Demo scene could not be opened."); EditorApplication.Exit(1); return; }
            _frames = 0;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.isPlaying = true;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { EditorApplication.update -= Tick; EditorApplication.Exit(1); return; }
            if (++_frames < 45) return;
            EditorApplication.update -= Tick;
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                var b = renderer.bounds;
                Debug.Log($"[RUNTIME-DUMP] RENDERER name={renderer.name} parent={(renderer.transform.parent != null ? renderer.transform.parent.name : "-")} pos={renderer.transform.position:F2} size={b.size:F2} mat={(renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "NULL")}");
            }
            foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                Debug.Log($"[RUNTIME-DUMP] CAMERA name={camera.name} enabled={camera.enabled} pos={camera.transform.position:F2} rot={camera.transform.eulerAngles:F2} rect={camera.rect} fov={camera.fieldOfView}");
            Debug.Log("[RUNTIME-DUMP] stopping after runtime inspection");
            EditorApplication.isPlaying = false;
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }
    }
}
