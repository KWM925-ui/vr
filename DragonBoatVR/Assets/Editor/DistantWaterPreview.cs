using System;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Same camera/time/material comparison without saving scene assets.
    /// A visual review artifact, not a substitute for normal Player acceptance.</summary>
    public static class DistantWaterPreview
    {
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-waterPreviewDir");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("-waterPreviewDir required");
            string directory = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(directory);
            EditorSceneManager.OpenScene("Assets/Scenes/Demo.unity", OpenSceneMode.Single);
            var wave = UnityEngine.Object.FindObjectOfType<WaveField>();
            var surface = UnityEngine.Object.FindObjectOfType<WaterSurface>();
            var planar = UnityEngine.Object.FindObjectOfType<PlanarWaterReflection>();
            var seat = UnityEngine.Object.FindObjectOfType<BoatSeatCamera>();
            Camera camera = seat.GetComponent<Camera>();
            Invoke(wave, "Awake");
            wave.Step(12.5f);
            Invoke(surface, "Awake");
            Invoke(surface, "LateUpdate");
            camera.transform.SetPositionAndRotation(seat.boat.TransformPoint(seat.seatLocalPosition),
                seat.boat.rotation * Quaternion.Euler(seat.seatLocalEulerAngles));
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.aspect = 1280f / 760f;
            var renderer = surface.GetComponent<Renderer>();
            Material original = renderer.sharedMaterial;
            Material temporary = new Material(original);
            renderer.sharedMaterial = temporary;
            var target = new RenderTexture(1280, 760, 24, RenderTextureFormat.ARGB32);
            RenderTexture prior = RenderTexture.active;
            camera.targetTexture = target;
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            try
            {
                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                    throw new InvalidOperationException("URP preview request unsupported");
                // Warm shader, reflection and post-processing resources before both captures.
                for (int i = 0; i < 3; i++)
                {
                    planar.RenderNow(camera);
                    RenderPipeline.SubmitRenderRequest(camera, request);
                }
                foreach (bool softened in new[] { false, true })
                {
                    temporary.SetFloat("_DistantReflectionRoughness", softened ? 1f : 0f);
                    planar.RenderNow(camera);
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    RenderTexture.active = target;
                    var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    image.Apply();
                    File.WriteAllBytes(Path.Combine(directory, softened ? "after.png" : "before.png"), image.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(image);
                }
                Debug.Log("[DISTANT_WATER] same pose/time; near=5m; transition=5..22m eased early; maxMip=4.5; planar retention=0.75; broad world-space normal ripples");
                Debug.Log("[DISTANT_WATER] RESULT=PASS captures written; human visual review still required");
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = prior;
                renderer.sharedMaterial = original;
                UnityEngine.Object.DestroyImmediate(temporary);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        static void Invoke(object target, string method)
        {
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Invoke(target, null);
        }
    }
}
