using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Export the declared paddle trajectory and cabin outline before
    /// contact integration. Independent OBJ queries review these observations;
    /// this does not install a candidate compound or certify loaded motion.</summary>
    public static class BoatPlacementProbe
    {
        [Serializable] sealed class Pose
        {
            public Vector3 bladeLocal;
            public Quaternion rotationLocal;
            public bool valid;
            public double sampleTime;
        }
        [Serializable] sealed class Variant
        {
            public string model, sceneSha256, objSha256;
            public Vector3 eyeLocal, pivotLocal;
            public float clearanceM, shaftLengthM, halfBeamM;
            public bool outlineReady;
            public List<Vector2> outline = new List<Vector2>();
            public List<Pose> poses = new List<Pose>();
            public List<Vector3> displayFloorPoints = new List<Vector3>();
        }
        [Serializable] sealed class Report
        {
            public string scope = "PLACEMENT_PREPARATION_ONLY: declared unloaded trajectory and outline, no saved scene, no compound installed, no runtime acceptance";
            public List<Variant> variants = new List<Variant>();
        }

        public static void Run()
        {
            var report = new Report();
            foreach (string id in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
            {
                string scene = "Assets/Scenes/" + BoatModelChoice.SceneName(id) + ".unity";
                EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
                var boat = Object.FindObjectOfType<BoatBuoyancy>();
                var driver = Object.FindObjectOfType<PaddleDriver>();
                var seat = Object.FindObjectOfType<BoatSeatCamera>();
                var choice = boat.GetComponent<BoatModelChoice>();
                if (choice == null || choice.modelId != id) throw new InvalidDataException("Boat identity differs.");
                typeof(BoatBuoyancy).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boat, null);
                driver.boat = boat; seat.boat = boat.transform;
                driver.externallyStepped = true;
                driver.ResetInputTimeline();
                driver.SetMode(PaddleDriver.Mode.VirtualPaddler);
                var geometry = BoatHullGeometry.FindOrCreate(boat);
                var boundary = seat.CabinBoundary;
                var result = new Variant { model = id, sceneSha256 = Hash(scene), objSha256 = Hash(choice.sourceObjAsset),
                    eyeLocal = seat.DefaultLocalEyePosition, pivotLocal = driver.pivotLocalPos,
                    clearanceM = seat.viewClearanceMeters, shaftLengthM = driver.shaftToBlade,
                    halfBeamM = geometry.HalfBeam, outlineReady = boundary.Ready };
                if (boundary.Ready) result.outline.AddRange(boundary.Outline);
                var model = boat.transform.Find("RealDragonBoat");
                var visualMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                visualMesh.CombineMeshes(model.GetComponentsInChildren<MeshFilter>()
                    .Where(f => f.sharedMesh != null && f.GetComponent<Renderer>() != null && f.GetComponent<Renderer>().enabled)
                    .Select(f => new CombineInstance { mesh = f.sharedMesh,
                        transform = boat.transform.worldToLocalMatrix * f.transform.localToWorldMatrix }).ToArray(), true, true, false);
                var reference = new GameObject("Independent display floor query");
                reference.layer = 2;
                reference.transform.SetPositionAndRotation(boat.transform.position, boat.transform.rotation);
                bool priorBackfaces = Physics.queriesHitBackfaces;
                try
                {
                    Physics.queriesHitBackfaces = true;
                    var collider = reference.AddComponent<MeshCollider>(); collider.sharedMesh = visualMesh;
                    Physics.SyncTransforms();
                    foreach (float dx in new[] { 0f, -.001f, .001f })
                    {
                        var ray = new Ray(boat.transform.TransformPoint(result.eyeLocal + Vector3.right * dx),
                            boat.transform.TransformDirection(Vector3.down));
                        if (!collider.Raycast(ray, out RaycastHit hit, 4f)) throw new InvalidDataException("Display floor ray missed.");
                        result.displayFloorPoints.Add(boat.transform.InverseTransformPoint(hit.point));
                    }
                }
                finally
                {
                    Physics.queriesHitBackfaces = priorBackfaces;
                    Object.DestroyImmediate(reference); Object.DestroyImmediate(visualMesh);
                }
                // Two cycles at 300 uniformly spaced phases, using the actual
                // configured trajectory generator. No physics or controller runs.
                for (int step = 0; step < 600; step++)
                {
                    driver.SetPhase01((step % 300) / 300f);
                    driver.Tick(.000001f);
                    result.poses.Add(new Pose {
                        bladeLocal = boat.transform.InverseTransformPoint(driver.transform.position),
                        rotationLocal = Quaternion.Inverse(boat.transform.rotation) * driver.transform.rotation,
                        valid = driver.Sample.valid, sampleTime = driver.Sample.timeSeconds });
                }
                report.variants.Add(result);
            }
            string[] args = Environment.GetCommandLineArgs(); int log = Array.IndexOf(args, "-logFile");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[log + 1]), "placement_observations.json"),
                JsonUtility.ToJson(report, true));
            Debug.Log("[PLACEMENT_PROBE] OBSERVED_ONLY; collision capacity and product assets unchanged.");
        }

        static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
    }
}
