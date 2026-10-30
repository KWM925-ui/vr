using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR.EditorTools
{
    public static class ExperienceConfigurationTool
    {
        public static void AdaptOrnamentPaddleRestPose()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/DragonBoatOrnament.unity", OpenSceneMode.Single);
            var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            var driver = UnityEngine.Object.FindObjectOfType<PaddleDriver>();
            var choice = boat != null ? boat.GetComponent<BoatModelChoice>() : null;
            if (choice == null || choice.modelId != BoatModelChoice.Ornament || driver == null
                || driver.mode != PaddleDriver.Mode.VirtualPaddler)
                throw new InvalidOperationException("Ornament scripted paddle station is missing.");
            driver.boat = boat;
            // The scene pose remains visible while the independent controller
            // starts, and is restored by F11. Match its existing raised start
            // phase using this hull's geometry; never substitute for encoder poses.
            var existingGeometry = boat.GetComponent<BoatHullGeometry>();
            float previousPhase = driver.Phase01;
            Vector3 blade;
            Quaternion rotation;
            try
            {
                driver.SetPhase01(0.75f);
                UdpClosedLoopClient.SampleScriptedIntent(driver, out blade, out rotation);
            }
            finally
            {
                driver.SetPhase01(previousPhase);
                if (existingGeometry == null)
                {
                    var sampledGeometry = boat.GetComponent<BoatHullGeometry>();
                    if (sampledGeometry != null) UnityEngine.Object.DestroyImmediate(sampledGeometry);
                }
            }
            driver.transform.SetPositionAndRotation(boat.transform.TransformPoint(blade), boat.transform.rotation * rotation);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save ornament paddle rest pose.");
            Debug.Log("[ORNAMENT_PADDLE_REST] local blade=" + blade.ToString("R")
                + "; rotation=" + rotation.ToString("R") + "; initial display only.");
        }

        public static void AdaptOrnamentSeat()
        {
            // Keep the declared lateral eye-to-paddle-station distance from
            // the existing boat. A wider hull moves both the user's station
            // and its viewpoint; it must not add 28 cm to the implied reach.
            // This only sets the new scene's desktop observation origin.
            EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
            var referenceDriver = UnityEngine.Object.FindObjectOfType<PaddleDriver>();
            var referenceSeat = UnityEngine.Object.FindObjectOfType<BoatSeatCamera>();
            var referenceBoat = UnityEngine.Object.FindObjectOfType<BoatModelChoice>();
            if (referenceBoat == null || referenceBoat.modelId != BoatModelChoice.Majesty
                || referenceDriver == null || referenceSeat == null)
                throw new InvalidOperationException("Original boat station reference is missing.");
            float referenceSide = referenceDriver.leftSide ? -1f : 1f;
            float eyeToStation = Mathf.Abs(referenceDriver.pivotLocalPos.x) - referenceSide * referenceSeat.seatLocalPosition.x;
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/DragonBoatOrnament.unity", OpenSceneMode.Single);
            var choice = UnityEngine.Object.FindObjectOfType<BoatModelChoice>();
            var driver = UnityEngine.Object.FindObjectOfType<PaddleDriver>();
            var seat = UnityEngine.Object.FindObjectOfType<BoatSeatCamera>();
            if (choice == null || choice.modelId != BoatModelChoice.Ornament || driver == null || seat == null)
                throw new InvalidOperationException("Ornament boat station is missing.");
            Vector3 position = seat.seatLocalPosition;
            position.x = (driver.leftSide ? -1f : 1f) * (Mathf.Abs(driver.pivotLocalPos.x) - eyeToStation);
            seat.seatLocalPosition = position;
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save ornament viewpoint.");
            Debug.Log($"[ORNAMENT_SEAT] local eye x={position.x:R}m; reference eye-to-station={eyeToStation:R}m; viewpoint only, not body input.");
        }

        public static void Run()
        {
            string[] paths = { "Assets/Scenes/Demo.unity", "Assets/Scenes/DragonBoatOrnament.unity" };
            foreach (string path in paths)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
                var seat = UnityEngine.Object.FindObjectOfType<BoatSeatCamera>();
                if (boat == null || seat == null || boat.GetComponent<BoatModelChoice>() == null)
                    throw new InvalidOperationException("Experience needs a named hull and a seat camera: " + path);
                foreach (var volume in UnityEngine.Object.FindObjectsOfType<Volume>())
                    if (volume.gameObject.layer == ComfortVignette.VolumeLayer)
                        throw new InvalidOperationException("Comfort volume layer is already used.");
                foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>())
                    if ((camera.GetUniversalAdditionalCameraData().volumeLayerMask.value & (1 << ComfortVignette.VolumeLayer)) != 0)
                        throw new InvalidOperationException("The runtime comfort layer must be reserved for its seat camera.");
                if (boat.GetComponent<SimulatedPaddlerLoad>() == null) boat.gameObject.AddComponent<SimulatedPaddlerLoad>();
                if (seat.GetComponent<ComfortVignette>() == null) seat.gameObject.AddComponent<ComfortVignette>();
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save " + path);
                Debug.Log("[EXPERIENCE_SETUP] " + path + ": occupant first-moment model; optional comfort, initially off.");
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(paths[0], true), new EditorBuildSettingsScene(paths[1], true) };
            AssetDatabase.SaveAssets();
        }
    }
}
