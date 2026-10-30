using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DragonBoatVR.EditorTools
{
    public static class WaterSurfaceConsistencySelfTest
    {
        [Serializable] sealed class Point
        {
            public Vector3 gpuPosition, gpuNormal, cpuNormal;
            public float cpuHeight, heightErrorM, normalErrorDeg;
        }
        [Serializable] sealed class Case
        {
            public string name;
            public float intensity, time, maximumHeightErrorM, maximumNormalErrorDeg;
            public List<Point> points = new List<Point>();
        }
        [Serializable] sealed class Report
        {
            public string scope = "Actual GPU execution of the production shared HLSL, sampled against CPU physics at the resulting world X/Z; not an interpolation or whole-image quality check.";
            public string graphicsDevice;
            public bool knownInputPositionsMatched;
            public List<Case> cases = new List<Case>();
        }
        static void Call(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

        public static void Run()
        {
            int passed = 0, failed = 0;
            var report = new Report { graphicsDevice = SystemInfo.graphicsDeviceType + ": " + SystemInfo.graphicsDeviceName };
            Material material = null; Texture2D positions = null, output = null; RenderTexture target = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                    throw new InvalidOperationException("Actual GPU and float render target are required; do not run with -nographics.");
                EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
                var wave = UnityEngine.Object.FindObjectOfType<WaveField>();
                var surface = UnityEngine.Object.FindObjectOfType<WaterSurface>();
                Call(wave, "Awake"); surface.waveField = wave; surface.followTarget = null; Call(surface, "Awake");
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/WaterSurfaceProbe.shader");
                if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                    throw new InvalidOperationException("Production water probe shader failed to compile on this GPU.");
                material = new Material(shader);
                const int count = 128;
                positions = new Texture2D(count, 1, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
                output = new Texture2D(count, 1, TextureFormat.RGBAFloat, false, true);
                target = new RenderTexture(count, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                target.Create();
                var points = new Color[count];
                for (int i = 0; i < count; i++) points[i] = new Color(-7.5f + (i % 16), wave.BaseHeight, -5.25f + (i / 16) * 1.5f, 1f);
                positions.SetPixels(points); positions.Apply(false, false);
                material.SetTexture("_MainTex", positions);
                material.SetInt("_ProbeKind", 2); Graphics.Blit(positions, target, material);
                RenderTexture.active = target; output.ReadPixels(new Rect(0, 0, count, 1), 0, 0); output.Apply(false, false);
                Color[] echoed = output.GetPixels();
                for (int i = 0; i < count; i++)
                    if (Vector4.Distance(echoed[i], points[i]) > 1e-6f)
                        throw new InvalidOperationException("GPU probe did not receive the known, distinct world coordinates: " + i);
                report.knownInputPositionsMatched = true; passed++;
                Debug.Log("[WATER_SURFACE_GPU] known float input coordinates survive actual GPU transfer=True");
                var block = new MaterialPropertyBlock();
                foreach (int kind in new[] { 0, 3 })
                foreach (float intensity in new[] { 0.12f, 0.65f })
                    foreach (float time in new[] { 0f, 0.7f, 3.11f, 7.49f })
                    {
                        wave.intensity = intensity; wave.SetTime(time); Call(surface, "LateUpdate");
                        surface.GetComponent<Renderer>().GetPropertyBlock(block);
                        foreach (string property in new[] { "_SimTime", "_Intensity", "_Horizontal" }) material.SetFloat(property, block.GetFloat(property));
                        material.SetInt("_WaveCount", block.GetInt("_WaveCount"));
                        material.SetInt("_InverseIterations", block.GetInt("_InverseIterations"));
                        material.SetVectorArray("_Waves", block.GetVectorArray("_Waves"));
                        material.SetFloatArray("_PhaseOffsets", block.GetFloatArray("_PhaseOffsets"));
                        material.SetFloatArray("_Steepness", block.GetFloatArray("_Steepness"));
                        material.SetInt("_ProbeKind", kind); Graphics.Blit(positions, target, material);
                        RenderTexture.active = target; output.ReadPixels(new Rect(0, 0, count, 1), 0, 0); output.Apply(false, false);
                        Color[] gpuPositions = output.GetPixels();
                        material.SetInt("_ProbeKind", kind + 1); Graphics.Blit(positions, target, material);
                        RenderTexture.active = target; output.ReadPixels(new Rect(0, 0, count, 1), 0, 0); output.Apply(false, false);
                        Color[] normals = output.GetPixels();
                        var result = new Case { name = (kind == 0 ? "water_" : "foam_") + (intensity < 0.2f ? "calm_" : "windy_") + time.ToString("F2"), intensity = intensity, time = time };
                        for (int i = 0; i < count; i++)
                        {
                            Vector3 position = new Vector3(gpuPositions[i].r, gpuPositions[i].g, gpuPositions[i].b);
                            var cpu = wave.Sample(position.x, position.z);
                            var normal = new Vector3(normals[i].r, normals[i].g, normals[i].b);
                            if (!SimulationValue.Finite(position) || !SimulationValue.Finite(normal) || normal.sqrMagnitude < 0.99f)
                                throw new InvalidOperationException("Invalid GPU water output.");
                            var point = new Point { gpuPosition = position, gpuNormal = normal, cpuHeight = cpu.height, cpuNormal = cpu.normal,
                                heightErrorM = Mathf.Abs(position.y - cpu.height), normalErrorDeg = Vector3.Angle(normal, cpu.normal) };
                            result.points.Add(point);
                            result.maximumHeightErrorM = Mathf.Max(result.maximumHeightErrorM, point.heightErrorM);
                            result.maximumNormalErrorDeg = Mathf.Max(result.maximumNormalErrorDeg, point.normalErrorDeg);
                        }
                        report.cases.Add(result);
                        bool ok = result.maximumHeightErrorM <= 0.002f && result.maximumNormalErrorDeg <= 0.15f;
                        if (ok) passed++; else failed++;
                        Debug.Log($"[WATER_SURFACE_GPU] {result.name}={ok} maxHeightErrorM={result.maximumHeightErrorM:G8} maxNormalErrorDeg={result.maximumNormalErrorDeg:G8}");
                    }
            }
            catch (Exception error) { failed++; Debug.LogError("[WATER_SURFACE_GPU] setup: " + error); }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (positions != null) UnityEngine.Object.DestroyImmediate(positions);
                if (output != null) UnityEngine.Object.DestroyImmediate(output);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-logFile");
                if (index >= 0 && index + 1 < args.Length && args[index + 1] != "-")
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1])), "water_surface_gpu.json"), JsonUtility.ToJson(report, true));
            }
            Debug.Log($"[WATER_SURFACE_GPU] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[WATER_SURFACE_GPU] RESULT=PASS" : "[WATER_SURFACE_GPU] RESULT=FAIL");
        }
    }
}
