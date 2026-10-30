using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
public class CaptureScreenshot {
    [MenuItem("DragonBoatVR/📸 Capture Screenshot Now3")]
    public static void Run() {
        var scenePath = "Assets/Scenes/Demo.unity";
        var scene = EditorSceneManager.OpenScene(scenePath);
        if (!scene.isLoaded) {
            Debug.LogError("Failed to load scene " + scenePath);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.update += CaptureAndExit;
    }
    static void CaptureAndExit() {
        EditorApplication.update -= CaptureAndExit;
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VRBoat_Screenshot.png");
        ScreenCapture.CaptureScreenshot(tempPath);
        Debug.Log("Screenshot saved to " + tempPath);
        EditorApplication.Exit(0);
    }
}
