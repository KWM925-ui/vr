using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace DragonBoatVR.EditorTools
{
    // Actual production-water renders at recorded windy boat poses. Scenes are
    // never saved; the no-water image is an independent opaque-cabin reference.
    public static class InteriorWaterReview
    {
        [Serializable] sealed class Pose
        {
            public string modelId;
            public float time, intensity;
            public Vector3 position;
            public Quaternion rotation;
        }
        [Serializable] sealed class Plan { public Pose[] poses; }
        [Serializable] sealed class Entry
        {
            public string modelId;
            public float time;
            public int sampledInteriorPixels, changedInteriorPixels, unmaskedInteriorPixels, changedExteriorPixels;
            public int diagnosticWaterPixels, diagnosticUnmaskedInteriorPixels, diagnosticExteriorPixels;
            public string wetImage, dryImage;
        }
        [Serializable] sealed class Report
        {
            public string result = "RUNNING";
            public string scope = "Production water shader and displayed hull at recorded windy poses. Opaque magenta material isolates actual water contributions from small ordinary shading differences; geometry and wave inputs are unchanged.";
            public List<Entry> cases = new List<Entry>();
        }
        static void Call(object value, string name)
        {
            var method = value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null) method.Invoke(value, null);
        }
        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            string folder = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1]);
            string planPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../tmp/water_exclusion_20261030/review_plan.json"));
            var plan = JsonUtility.FromJson<Plan>(File.ReadAllText(planPath));
            var report = new Report();
            foreach (string model in new[] { BoatModelChoice.Majesty, BoatModelChoice.Ornament })
            {
                EditorSceneManager.OpenScene("Assets/Scenes/" + BoatModelChoice.SceneName(model) + ".unity");
                var boat = Object.FindObjectOfType<BoatBuoyancy>();
                var wave = Object.FindObjectOfType<WaveField>();
                var surface = Object.FindObjectOfType<WaterSurface>();
                var reflection = Object.FindObjectOfType<PlanarWaterReflection>();
                if (reflection != null) reflection.enabled = false;
                Shader.SetGlobalFloat("_WaterPlanarOn", 0f);
                Call(wave, "Awake");
                surface.waveField = wave; surface.followTarget = boat.transform;
                Call(surface, "Awake"); Call(surface, "Start");
                var waterRenderer = surface.GetComponent<Renderer>();
                var hull = BoatHullGeometry.FindOrCreate(boat);
                var original = Camera.main;
                var go = new GameObject("Unsaved interior water review camera");
                var camera = go.AddComponent<Camera>(); camera.CopyFrom(original);
                camera.enabled = false; camera.rect = new Rect(0, 0, 1, 1);
                camera.fieldOfView = 72f; camera.aspect = 1f;
                var extra = camera.GetUniversalAdditionalCameraData();
                extra.requiresDepthOption = CameraOverrideOption.On;
                extra.requiresColorOption = CameraOverrideOption.On;
                extra.renderPostProcessing = false;
                var target = new RenderTexture(640, 640, 24, RenderTextureFormat.ARGB32);
                target.Create();
                var pixels = new Texture2D(640, 640, TextureFormat.RGB24, false);
                var previous = RenderTexture.active;
                try
                {
                    Color32[] Shot(string name)
                    {
                        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                        RenderPipeline.SubmitRenderRequest(camera, request);
                        RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, 640, 640), 0, 0); pixels.Apply();
                        File.WriteAllBytes(Path.Combine(folder, name), pixels.EncodeToPNG());
                        return pixels.GetPixels32();
                    }
                    int index = 0;
                    foreach (var pose in plan.poses)
                    {
                        if (pose.modelId != model) continue;
                        boat.transform.SetPositionAndRotation(pose.position, pose.rotation);
                        wave.intensity = pose.intensity; wave.SetTime(pose.time);
                        Call(surface, "LateUpdate");
                        camera.transform.position = boat.transform.TransformPoint(new Vector3(0, 1.6f, -.45f));
                        camera.transform.LookAt(boat.transform.TransformPoint(new Vector3(0, 0, .5f)), boat.transform.up);
                        string stem = model + "_" + index++;
                        var entry = new Entry { modelId = model, time = pose.time,
                            wetImage = stem + "_wet.png", dryImage = stem + "_dry.png" };
                        waterRenderer.enabled = false;
                        var dry = Shot(entry.dryImage);
                        waterRenderer.enabled = true;
                        var wet = Shot(entry.wetImage);
                        Shader.SetGlobalFloat("_BoatInteriorEnabled", 0f);
                        var unmasked = Shot(stem + "_unmasked.png");
                        Call(surface, "LateUpdate");
                        var ordinaryMaterial = waterRenderer.sharedMaterial;
                        var identifiedMaterial = new Material(ordinaryMaterial);
                        var identifier = new Color(16f, 0f, 16f, 1f);
                        identifiedMaterial.SetColor("_BaseColor", identifier);
                        identifiedMaterial.SetColor("_ShallowColor", identifier);
                        identifiedMaterial.SetColor("_FoamColor", identifier);
                        identifiedMaterial.SetFloat("_SkyReflectStrength", 0f);
                        identifiedMaterial.SetFloat("_SpecStrength", 0f);
                        identifiedMaterial.SetFloat("_DepthTint", 0f);
                        waterRenderer.sharedMaterial = identifiedMaterial;
                        var marked = Shot(stem + "_water_identity.png");
                        Shader.SetGlobalFloat("_BoatInteriorEnabled", 0f);
                        var unmaskedMarked = Shot(stem + "_unmasked_water_identity.png");
                        waterRenderer.sharedMaterial = ordinaryMaterial;
                        Object.DestroyImmediate(identifiedMaterial);
                        Call(surface, "LateUpdate");
                        bool Different(Color32 a, Color32 b) => Mathf.Abs(a.r-b.r) > 2 || Mathf.Abs(a.g-b.g) > 2 || Mathf.Abs(a.b-b.b) > 2;
                        // The real water shader can darken/refract the marker.
                        // Identify its two dominant channels, including the
                        // observed dark-purple positive control in the old hull.
                        bool WaterIdentified(Color32 c) => c.r > 80 && c.b > 80 && c.r > 2*c.g && c.b > 2*c.g;
                        // Separate geometry sampling: the displayed rail
                        // envelope, not the generated water profile, defines
                        // points across the floor and near both interior edges.
                        for (int iz = 0; iz < 35; iz++)
                        for (int ix = 0; ix < 11; ix++)
                        {
                            float z = -2.7f+.15f*iz;
                            int nearest = 0;
                            for (int j = 1; j < hull.Port.Length; j++)
                                if (Mathf.Abs(hull.Port[j].z-z) < Mathf.Abs(hull.Port[nearest].z-z)) nearest = j;
                            float x = Mathf.Lerp(hull.Port[nearest].x+.04f, hull.Starboard[nearest].x-.04f, ix/10f);
                            var world = boat.transform.TransformPoint(new Vector3(x, .03f, z));
                            var screen = camera.WorldToViewportPoint(world);
                            if (screen.z <= 0 || screen.x < .02f || screen.x > .98f || screen.y < .02f || screen.y > .98f) continue;
                            int p = Mathf.Clamp(Mathf.RoundToInt(screen.y*639), 0, 639)*640
                                + Mathf.Clamp(Mathf.RoundToInt(screen.x*639), 0, 639);
                            entry.sampledInteriorPixels++;
                            if (Different(wet[p], dry[p])) entry.changedInteriorPixels++;
                            if (Different(unmasked[p], dry[p])) entry.unmaskedInteriorPixels++;
                            if (WaterIdentified(marked[p])) entry.diagnosticWaterPixels++;
                            if (WaterIdentified(unmaskedMarked[p])) entry.diagnosticUnmaskedInteriorPixels++;
                        }
                        // Pixel rays wholly outside the boat's actual vertex
                        // bounds must retain the identical unmasked water.
                        float outsideX = hull.HalfBeam+.12f;
                        for (int iz = 0; iz < 35; iz++)
                        foreach (float x in new[] { -outsideX, outsideX })
                        {
                            Vector3 point = boat.transform.TransformPoint(new Vector3(x, 0, -2.7f+.15f*iz));
                            point.y = wave.Sample(point.x, point.z).height;
                            var screen = camera.WorldToViewportPoint(point);
                            if (screen.z <= 0 || screen.x < .02f || screen.x > .98f || screen.y < .02f || screen.y > .98f) continue;
                            int p = Mathf.Clamp(Mathf.RoundToInt(screen.y*639), 0, 639)*640
                                + Mathf.Clamp(Mathf.RoundToInt(screen.x*639), 0, 639);
                            if (Different(wet[p], unmasked[p])) entry.changedExteriorPixels++;
                        }
                        // These reviewed corner pixels show open river in both
                        // cameras. World points just beyond the CAD hull can
                        // project behind its tall ornaments and are not a
                        // reliable positive control for visible exterior water.
                        foreach (int y in new[] { 8, 24, 40, 56, 72 })
                        foreach (int x in new[] { 8, 24, 40, 56, 72, 567, 583, 599, 615, 631 })
                        {
                            int p = (639-y)*640+x;
                            if (WaterIdentified(marked[p]) && WaterIdentified(unmaskedMarked[p]))
                                entry.diagnosticExteriorPixels++;
                        }
                        report.cases.Add(entry);
                        Debug.Log("[INTERIOR_WATER] " + stem + " time=" + pose.time
                            + " changed=" + entry.changedInteriorPixels + "/" + entry.sampledInteriorPixels
                            + " unmasked=" + entry.unmaskedInteriorPixels + " exterior=" + entry.changedExteriorPixels
                            + " waterIdentity=" + entry.diagnosticWaterPixels + " unmaskedIdentity=" + entry.diagnosticUnmaskedInteriorPixels
                            + " exteriorIdentity=" + entry.diagnosticExteriorPixels);
                    }
                }
                finally
                {
                    RenderTexture.active = previous;
                    Object.DestroyImmediate(pixels); target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(go);
                }
            }
            bool bothControls = report.cases.Exists(c => c.modelId == BoatModelChoice.Majesty && c.diagnosticUnmaskedInteriorPixels > 0)
                && report.cases.Exists(c => c.modelId == BoatModelChoice.Ornament && c.diagnosticUnmaskedInteriorPixels > 0);
            report.result = report.cases.Count >= 8 && bothControls
                && report.cases.TrueForAll(c => c.sampledInteriorPixels >= 100 && c.diagnosticWaterPixels == 0
                    && c.diagnosticExteriorPixels == 50) ? "PASS" : "FAIL";
            File.WriteAllText(Path.Combine(folder, "interior_water_review.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[INTERIOR_WATER] RESULT=" + report.result);
        }
    }
}
