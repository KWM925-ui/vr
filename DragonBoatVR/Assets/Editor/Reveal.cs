using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 打开工程时直接落在 Demo 场景并自动 Play。
    /// 命令行：Unity.exe -projectPath ... -executeMethod DragonBoatVR.EditorTools.Reveal.OpenDemoAndPlay
    /// 目的只有一个：老大双击/命令行启动后，窗口一出来就是能看能玩的画面，不用再找场景、按 Play。
    /// </summary>
    public static class Reveal
    {
        public static void OpenDemoAndPlay()
        {
            // executeMethod 在编辑器刚起来时执行，此时场景系统可能还没就绪，
            // 用 delayCall 推到第一帧之后，避免 OpenScene 报 "scene not loaded"。
            EditorApplication.delayCall += () =>
            {
                var path = "Assets/Scenes/Demo.unity";
                if (System.IO.File.Exists(path))
                {
                    EditorSceneManager.OpenScene(path);
                    Debug.Log("[Reveal] 已打开 Demo 场景，正在进入 Play");
                    EditorApplication.isPlaying = true;
                }
                else
                {
                    Debug.LogWarning("[Reveal] 找不到 " + path + "，请先跑菜单 ① 一键初始化");
                }
            };
        }
    }
}
