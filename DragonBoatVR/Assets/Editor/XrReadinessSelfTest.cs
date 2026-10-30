using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Software and GPU fixtures only. Explicit eye matrices do not
    /// validate a headset, XR runtime, tracking latency or native eye submission.</summary>
    public static class XrReadinessSelfTest
    {
        [Serializable] sealed class Result
        {
            public string result, scope = "SOFTWARE_AND_EXPLICIT_EYE_GPU_FIXTURES_ONLY", graphicsDevice;
            public int passed, failed, stereoShaderVariants;
            public int instancedMatchingEyePixels;
            public float leftRedProjectionError, rightRedProjectionError, eyeCentroidDifference;
            public Color leftWaterPixel, rightWaterPixel;
            public List<string> checks = new List<string>();
        }
        static Result _result;
        static string _output;
        static readonly Vector3 Origin = new Vector3(1000f, 0f, 0f);

        [MenuItem("DragonBoatVR/XR软件与逐眼GPU准备自检")]
        public static void Run()
        {
            Require(Application.isBatchMode && !Application.isPlaying, "Use a dedicated batch Editor in Edit mode");
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-xrReadinessArtifacts");
            if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("-xrReadinessArtifacts is required");
            _output = Path.GetFullPath(args[at + 1]);
            Directory.CreateDirectory(_output);
            if (File.Exists(Path.Combine(_output, "xr_readiness.json"))) throw new IOException("Use a new evidence directory");
            _result = new Result { graphicsDevice = SystemInfo.graphicsDeviceName };
            string previousScenePath = SceneManager.GetActiveScene().path;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                Check("explicit per-eye GPU reflection uses each supplied view and asymmetric projection", StereoReflection);
                Check("water fragment selects the correct eye's reflection texture", WaterEyeSampling);
                Check("single-pass instanced GPU draw routes eye instances into separate texture layers", InstancedEyeLayers);
                Check("water/foam/droplet/interior-mask instanced stereo variants compile", ShaderVariants);
                Check("provider rejects stale identities and camera has one pose owner", ProviderAndPoseOwner);
                Check("external monitor camera cannot render into the HMD", MonitorOwnership);
            }
            finally
            {
                if (!string.IsNullOrEmpty(previousScenePath)) EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                _result.result = _result.failed == 0 ? "PASS" : "FAIL";
                File.WriteAllText(Path.Combine(_output, "xr_readiness.json"), JsonUtility.ToJson(_result, true));
                Debug.Log($"[XR_READINESS] passed={_result.passed} failed={_result.failed}");
                Debug.Log("[XR_READINESS] RESULT=" + _result.result + " software fixtures; HMD/runtime unverified");
            }
            if (_result.failed != 0) throw new InvalidOperationException("XR software readiness fixture failed");
        }

        static void Check(string name, Action test)
        {
            try { test(); _result.passed++; _result.checks.Add(name + "=PASS"); Debug.Log("[XR_READINESS] " + name + "=PASS"); }
            catch (Exception error) { _result.failed++; _result.checks.Add(name + "=FAIL: " + error.Message); Debug.LogError("[XR_READINESS] " + name + ": " + error); }
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        sealed class Fixture : IDisposable
        {
            public readonly GameObject root = new GameObject("XR explicit-eye fixture");
            public readonly Camera camera;
            public readonly GameObject water;
            public readonly PlanarWaterReflection planar;
            public readonly RenderTexture target = new RenderTexture(256, 256, 24);
            public readonly Material waterMaterial;
            readonly List<Material> _materials = new List<Material>();
            public Fixture()
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                root.transform.position = Origin;
                camera = Child("source").AddComponent<Camera>();
                camera.transform.SetPositionAndRotation(Origin + new Vector3(0f, 1.6f, -3f),
                    Quaternion.LookRotation(new Vector3(0f, -1.6f, 7f)));
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.nearClipPlane = 0.05f; camera.farClipPlane = 50f; camera.fieldOfView = 58f;
                camera.aspect = 1f; camera.cullingMask = 1 << 30; camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.allowHDR = false; camera.allowMSAA = false;
                camera.GetUniversalAdditionalCameraData().allowXRRendering = false;
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                target.Create(); camera.targetTexture = target;
                water = GameObject.CreatePrimitive(PrimitiveType.Plane);
                water.name = "water"; water.layer = 30; water.transform.SetParent(root.transform, false);
                water.transform.localPosition = new Vector3(0, 0, 4); water.transform.localScale = new Vector3(1, 1, 2);
                UnityEngine.Object.DestroyImmediate(water.GetComponent<Collider>());
                waterMaterial = new Material(Shader.Find("DragonBoatVR/WaveFieldWater")); _materials.Add(waterMaterial);
                water.GetComponent<Renderer>().sharedMaterial = waterMaterial;
                waterMaterial.SetInt("_WaveCount", 0); waterMaterial.SetFloat("_Intensity", 1);
                waterMaterial.SetFloat("_NormalStrength", 0); waterMaterial.SetFloat("_PlanarDistortion", 0);
                waterMaterial.SetFloat("_DistantReflectionRoughness", 0); waterMaterial.SetFloat("_SpecStrength", 0);
                waterMaterial.SetFloat("_PlanarStrength", 1); waterMaterial.SetFloat("_SkyReflectStrength", 1);
                waterMaterial.SetFloat("_FresnelPower", 0.1f); waterMaterial.SetColor("_SkyReflectTint", Color.white);
                waterMaterial.SetColor("_BaseColor", new Color(0, 0, 0, 1)); waterMaterial.SetColor("_ShallowColor", Color.black);
                planar = water.AddComponent<PlanarWaterReflection>(); planar.waterLevel = 0;
                Marker("red", new Vector3(-0.6f, 0.8f, 4f), Color.red);
                Marker("green", new Vector3(0.6f, 0.8f, 4f), Color.green);
            }
            GameObject Child(string name)
            { var go = new GameObject(name); go.transform.SetParent(root.transform, false); go.layer = 30; return go; }
            void Marker(string name, Vector3 local, Color color)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name; go.layer = 30;
                go.transform.SetParent(root.transform, false); go.transform.localPosition = local; go.transform.localScale = Vector3.one * 0.35f;
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); _materials.Add(material);
                material.SetColor("_BaseColor", color); go.GetComponent<Renderer>().sharedMaterial = material;
            }
            public Matrix4x4 View(float x)
                => Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(
                    Origin + new Vector3(x, 1.6f, -3f), camera.transform.rotation, Vector3.one).inverse;
            public Matrix4x4 Projection(float offset)
            { var p = Matrix4x4.Perspective(58, 1, 0.05f, 50); p.m02 = offset; return p; }
            public void Dispose()
            {
                camera.targetTexture = null; target.Release(); UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(root);
                foreach (var material in _materials) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        static Texture2D Read(RenderTexture target, string name)
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try { RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); }
            finally { RenderTexture.active = previous; }
            File.WriteAllBytes(Path.Combine(_output, name + ".png"), image.EncodeToPNG());
            return image;
        }
        static Vector2 RedCenter(Texture2D image)
        {
            var pixels = image.GetPixels32(); double x = 0, y = 0; int count = 0;
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].r > 100 && pixels[i].r > pixels[i].g + 60 && pixels[i].r > pixels[i].b + 60)
                { x += i % image.width + 0.5; y += i / image.width + 0.5; count++; }
            Require(count > 10, "reflection did not contain the real red marker");
            return new Vector2((float)(x / count / image.width), (float)(y / count / image.height));
        }
        static Vector2 ExpectedCenter(Matrix4x4 view, Matrix4x4 projection)
        {
            var marker = Origin + new Vector3(-0.6f, -0.8f, 4f); // independently reflect the actual cube center
            Vector4 clip = projection * view * new Vector4(marker.x, marker.y, marker.z, 1);
            return new Vector2(clip.x, clip.y) / clip.w * 0.5f + Vector2.one * 0.5f;
        }
        static void StereoReflection()
        {
            using (var f = new Fixture())
            {
                var lv = f.View(-0.032f); var rv = f.View(0.032f); var lp = f.Projection(-0.06f); var rp = f.Projection(0.06f);
                f.planar.RenderStereoNow(f.camera, lv, lp, rv, rp);
                var left = Shader.GetGlobalTexture("_WaterPlanarReflection") as RenderTexture;
                var right = Shader.GetGlobalTexture("_WaterPlanarReflectionRight") as RenderTexture;
                Require(left != null && right != null && left != right && left.IsCreated() && right.IsCreated(), "eyes do not own distinct completed RTs");
                Require(f.planar.LastRenderedEyeCount == 2 && f.planar.LastSourceCameraId == f.camera.GetInstanceID()
                    && f.planar.LastRenderedFrame == Time.frameCount, "completion evidence lost source/eye identity");
                var li = Read(left, "reflection_left"); var ri = Read(right, "reflection_right");
                try
                {
                    var lc = RedCenter(li); var rc = RedCenter(ri);
                    _result.leftRedProjectionError = Vector2.Distance(lc, ExpectedCenter(lv, lp));
                    _result.rightRedProjectionError = Vector2.Distance(rc, ExpectedCenter(rv, rp));
                    _result.eyeCentroidDifference = Vector2.Distance(lc, rc);
                    Require(_result.leftRedProjectionError < 0.03f && _result.rightRedProjectionError < 0.03f,
                        "GPU markers disagree with independent per-eye projections");
                    Require(_result.eyeCentroidDifference > 0.04f, "right eye repeated the left eye's reflection");
                }
                finally { UnityEngine.Object.DestroyImmediate(li); UnityEngine.Object.DestroyImmediate(ri); }
            }
        }
        static void WaterEyeSampling()
        {
            using (var f = new Fixture())
            {
                f.planar.RenderStereoNow(f.camera, f.View(-0.032f), f.Projection(-0.06f), f.View(0.032f), f.Projection(0.06f));
                var red = new Texture2D(1, 1); red.SetPixel(0, 0, Color.red); red.Apply();
                var green = new Texture2D(1, 1); green.SetPixel(0, 0, Color.green); green.Apply();
                try
                {
                    Shader.SetGlobalTexture("_WaterPlanarReflection", red); Shader.SetGlobalTexture("_WaterPlanarReflectionRight", green);
                    for (int eye = 0; eye < 2; eye++)
                    {
                        Shader.SetGlobalFloat("_WaterPlanarActiveEye", eye);
                        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = f.target };
                        Require(RenderPipeline.SupportsRenderRequest(f.camera, request), "URP does not support explicit GPU fixture rendering");
                        RenderPipeline.SubmitRenderRequest(f.camera, request);
                        var image = Read(f.target, eye == 0 ? "water_left_sampler" : "water_right_sampler");
                        try
                        {
                            var pixel = image.GetPixel(128, 64);
                            if (eye == 0) _result.leftWaterPixel = pixel; else _result.rightWaterPixel = pixel;
                        }
                        finally { UnityEngine.Object.DestroyImmediate(image); }
                    }
                    Require(_result.leftWaterPixel.r > _result.leftWaterPixel.g + 0.15f
                        && _result.rightWaterPixel.g > _result.rightWaterPixel.r + 0.15f,
                        "water shader sampled the wrong eye's bound texture");
                }
                finally { UnityEngine.Object.DestroyImmediate(red); UnityEngine.Object.DestroyImmediate(green); }
            }
        }
        static void InstancedEyeLayers()
        {
            using (var f = new Fixture())
            {
                var descriptor = new RenderTextureDescriptor(256, 256, RenderTextureFormat.ARGB32, 0)
                    { dimension = TextureDimension.Tex2DArray, volumeDepth = 2, msaaSamples = 1, sRGB = false };
                var target = new RenderTexture(descriptor); target.Create();
                descriptor.dimension = TextureDimension.Tex2D; descriptor.volumeDepth = 1;
                var slice = new RenderTexture(descriptor); slice.Create();
                var depth = new Texture2DArray(1, 1, 2, TextureFormat.RFloat, false, true);
                var opaque = new Texture2DArray(1, 1, 2, TextureFormat.RGBA32, false, true);
                var red = new Texture2D(1, 1); red.SetPixel(0, 0, Color.red); red.Apply();
                var green = new Texture2D(1, 1); green.SetPixel(0, 0, Color.green); green.Apply();
                var command = new CommandBuffer { name = "Explicit stereo instancing GPU fixture (no HMD)" };
                Texture2D left = null, right = null;
                bool previousStereoKeyword = Shader.IsKeywordEnabled("STEREO_INSTANCING_ON");
                try
                {
                    Require(SystemInfo.supports2DArrayTextures && SystemInfo.supportsInstancing,
                        "actual texture-array/instancing support is required");
                    for (int eye = 0; eye < 2; eye++)
                    {
                        // The actual array samplers are bound, rather than supplying
                        // ordinary 2D textures to an instanced stereo shader variant.
                        depth.SetPixels(new[] { Color.clear }, eye);
                        opaque.SetPixels(new[] { Color.black }, eye);
                    }
                    depth.Apply(); opaque.Apply();
                    var views = new[] { f.View(-0.032f), f.View(0.032f) };
                    var projections = new[] { GL.GetGPUProjectionMatrix(f.Projection(-0.06f), true),
                        GL.GetGPUProjectionMatrix(f.Projection(0.06f), true) };
                    var inverseViews = new[] { views[0].inverse, views[1].inverse };
                    var vp = new[] { projections[0] * views[0], projections[1] * views[1] };
                    command.SetRenderTarget(target, 0, CubemapFace.Unknown, -1);
                    command.ClearRenderTarget(false, true, Color.black);
                    command.SetViewport(new Rect(0, 0, 256, 256));
                    command.SetGlobalMatrixArray("unity_StereoMatrixV", views);
                    command.SetGlobalMatrixArray("unity_StereoMatrixP", projections);
                    command.SetGlobalMatrixArray("unity_StereoMatrixInvV", inverseViews);
                    command.SetGlobalMatrixArray("unity_StereoMatrixInvP", new[] { projections[0].inverse, projections[1].inverse });
                    command.SetGlobalMatrixArray("unity_StereoMatrixVP", vp);
                    command.SetGlobalMatrixArray("unity_StereoMatrixInvVP", new[] { vp[0].inverse, vp[1].inverse });
                    command.SetGlobalVectorArray("unity_StereoWorldSpaceCameraPos", new[] {
                        inverseViews[0].GetColumn(3), inverseViews[1].GetColumn(3) });
                    command.SetGlobalVector("_ScaledScreenParams", new Vector4(256, 256, 1f + 1f / 256, 1f + 1f / 256));
                    float ratio = 50f / 0.05f;
                    command.SetGlobalVector("_ZBufferParams", SystemInfo.usesReversedZBuffer
                        ? new Vector4(ratio - 1f, 1f, (ratio - 1f) / 50f, 1f / 50f)
                        : new Vector4(1f - ratio, ratio, (1f - ratio) / 50f, ratio / 50f));
                    command.SetGlobalTexture("_CameraDepthTexture", depth);
                    command.SetGlobalTexture("_CameraOpaqueTexture", opaque);
                    command.SetGlobalTexture("_WaterPlanarReflection", red);
                    command.SetGlobalTexture("_WaterPlanarReflectionRight", green);
                    command.SetGlobalFloat("_WaterPlanarOn", 1);
                    command.SetGlobalFloat("_WaterPlanarStereo", 1);
                    // Deliberately keep the multi-pass override on the LEFT eye.
                    // The right layer must come from its real instance eye index.
                    command.SetGlobalFloat("_WaterPlanarActiveEye", 0);
                    command.SetGlobalMatrix("_WaterPlanarWorldToUV", Matrix4x4.identity);
                    command.SetGlobalMatrix("_WaterPlanarWorldToUVRight", Matrix4x4.identity);
                    command.SetGlobalVector("_MainLightPosition", new Vector4(0, 1, 0, 0));
                    command.SetGlobalVector("_MainLightColor", Vector4.zero);
                    command.SetGlobalVector("_FoamTrailAnchor", new Vector4(0, 48, 0, 0));
                    command.SetGlobalInt("_InteractionRippleCount", 0);
                    command.EnableShaderKeyword("STEREO_INSTANCING_ON");
                    f.waterMaterial.enableInstancing = true;
                    // This is the same instance multiplier used by URP XRPass.
                    command.SetInstanceMultiplier(2);
                    command.DrawMeshInstanced(f.water.GetComponent<MeshFilter>().sharedMesh, 0, f.waterMaterial, 0,
                        new[] { f.water.transform.localToWorldMatrix }, 1);
                    command.SetInstanceMultiplier(1);
                    if (!previousStereoKeyword) command.DisableShaderKeyword("STEREO_INSTANCING_ON");
                    Graphics.ExecuteCommandBuffer(command);
                    Graphics.CopyTexture(target, 0, 0, slice, 0, 0); left = Read(slice, "instanced_left");
                    Graphics.CopyTexture(target, 1, 0, slice, 0, 0); right = Read(slice, "instanced_right");
                    var l = left.GetPixels32(); var r = right.GetPixels32(); int matching = 0;
                    for (int i = 0; i < l.Length; i++)
                        if (l[i].r > 70 && l[i].r > l[i].g + 50 && r[i].g > 70 && r[i].g > r[i].r + 50) matching++;
                    _result.instancedMatchingEyePixels = matching;
                    Require(matching > 500, "GPU eye instances did not produce red LEFT and green RIGHT layers");
                }
                finally
                {
                    if (!previousStereoKeyword) Shader.DisableKeyword("STEREO_INSTANCING_ON");
                    command.Release();
                    if (left != null) UnityEngine.Object.DestroyImmediate(left);
                    if (right != null) UnityEngine.Object.DestroyImmediate(right);
                    target.Release(); slice.Release();
                    foreach (var value in new UnityEngine.Object[] { target, slice, depth, opaque, red, green })
                        UnityEngine.Object.DestroyImmediate(value);
                }
            }
        }

        static void ShaderVariants()
        {
            var variants = new ShaderVariantCollection();
            foreach (var name in new[] { "DragonBoatVR/WaveFieldWater", "DragonBoatVR/BoatFoam", "DragonBoatVR/WaterDroplet", "DragonBoatVR/InteriorWaterExclusion" })
            {
                var shader = Shader.Find(name); Require(shader != null && shader.isSupported, "unsupported shader: " + name);
                // The invisible cap explicitly selects SRPDefaultUnlit. The
                // other custom passes have no LightMode and are Normal passes.
                var pass = name == "DragonBoatVR/InteriorWaterExclusion"
                    ? PassType.ScriptableRenderPipelineDefaultUnlit : PassType.Normal;
                variants.Add(new ShaderVariantCollection.ShaderVariant(shader, pass,
                    new[] { "INSTANCING_ON", "STEREO_INSTANCING_ON" }));
            }
            variants.WarmUp(); _result.stereoShaderVariants = variants.variantCount;
            foreach (var name in new[] { "DragonBoatVR/WaveFieldWater", "DragonBoatVR/BoatFoam", "DragonBoatVR/WaterDroplet", "DragonBoatVR/InteriorWaterExclusion" })
                Require(!ShaderUtil.ShaderHasError(Shader.Find(name)), "stereo shader compiler error: " + name);
            UnityEngine.Object.DestroyImmediate(variants);
            Require(_result.stereoShaderVariants == 4, "stereo variants were not requested for all four shaders");
        }
        static void ProviderAndPoseOwner()
        {
            var root = new GameObject("head input boundary fixture"); var boat = new GameObject("boat fixture");
            try
            {
                var provider = root.AddComponent<UnityXrHeadTrackingProvider>();
                var attachment = root.GetComponent<HeadPoseAttachment>();
                Require(!attachment.useTrackedPose && !attachment.allowUnmeasuredDesktopPreview && !provider.DeviceCaptureTimeAvailable,
                    "provider implicitly authorized a measured camera or invented capture time");
                Require(provider.SubmitPlatformObservation(Matrix4x4.identity, 100, HeadTrackingSource.ExternalTracking, true), "fresh explicit platform observation rejected");
                Require(!provider.SubmitPlatformObservation(Matrix4x4.identity, 100, HeadTrackingSource.ExternalTracking, true)
                    && !provider.SubmitPlatformObservation(Matrix4x4.identity, 99, HeadTrackingSource.ExternalTracking, true), "platform duplicates/order not checked");
                Require(!provider.SubmitPlatformObservation(Matrix4x4.identity, 101, HeadTrackingSource.DesktopSimulation, true), "software platform cannot pose as external tracking");
                var seat = root.AddComponent<BoatSeatCamera>(); seat.boat = boat.transform;
                root.transform.position = new Vector3(1, 2, 3); attachment.useTrackedPose = true;
                typeof(BoatSeatCamera).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(seat, null);
                Require(root.transform.position == new Vector3(1, 2, 3), "desktop pose overwrote tracked output during input loss");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(boat); }
        }
        static void MonitorOwnership()
        {
            var root = new GameObject("XR monitor ownership fixture");
            try
            {
                var main = new GameObject("main"); main.transform.SetParent(root.transform); var eye = main.AddComponent<Camera>();
                var outside = new GameObject("outside"); outside.transform.SetParent(root.transform); var monitor = outside.AddComponent<Camera>();
                var split = root.AddComponent<SplitViewController>(); split.mainCamera = eye; split.twinCamera = monitor; split.Apply();
                Require(eye.stereoTargetEye == StereoTargetEyeMask.Both && monitor.stereoTargetEye == StereoTargetEyeMask.None
                    && eye.GetUniversalAdditionalCameraData().allowXRRendering && !monitor.GetUniversalAdditionalCameraData().allowXRRendering,
                    "outside follow camera can take over a headset eye");
                Require(eye.enabled && monitor.enabled, "two presentation cameras were not retained");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
