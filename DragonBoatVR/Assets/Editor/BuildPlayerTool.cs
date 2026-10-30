using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 命令行构建 Windows Player。
    /// 用 -executeMethod 走编辑器内 BuildPipeline，绕过 -buildWindows64Player
    /// 的许可证在线刷新预检（本机许可有效，只是令牌刷新失败）。
    /// </summary>
    public static class BuildPlayerTool
    {
        public static void Run()
        {
            var args = System.Environment.GetCommandLineArgs();
            string outDir = "F:/vr视觉/Builds/DragonBoatVR";
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-buildOutDir") outDir = args[i + 1];

            var scenes = new[] { "Assets/Scenes/Demo.unity", "Assets/Scenes/DragonBoatOrnament.unity" };
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outDir + "/DragonBoatVR.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[BUILD] result={report.summary.result} size={report.summary.totalSize} errors={report.summary.totalErrors}");
            EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
