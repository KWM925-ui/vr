using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
public class FixCameraSetup {
    [MenuItem("DragonBoatVR/⚙️ Fix Camera Layout (Auto)")]
    public static void Run() {
        var scenePath = "Assets/Scenes/Demo.unity";
        if (!EditorSceneManager.OpenScene(scenePath).IsValid()) {
            Debug.LogError("Cannot open scene " + scenePath);
            return;
        }
        var seatCam = GameObject.Find("BoatSeatCamera");
        var mainCam = GameObject.Find("Main Camera");
        if (seatCam != null && mainCam != null) {
            mainCam.transform.SetParent(seatCam.transform, false);
            Debug.Log("Main Camera reparented to BoatSeatCamera");
        } else {
            Debug.LogWarning("SeatCam or MainCam not found");
        }
        var twinCam = GameObject.Find("TwinCamera");
        var boat = GameObject.Find("Boat");
        if (twinCam != null && boat != null) {
            twinCam.transform.SetParent(boat.transform, false);
            Debug.Log("TwinCamera reparented to Boat");
        } else {
            Debug.LogWarning("TwinCam or Boat not found");
        }
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("Scene saved after camera fix.");
    }
}
