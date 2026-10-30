using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR
{
    /// <summary>
    /// 平面镜反射:每台相机在自身渲染前更新独立的反射 RT，立体相机逐眼计算，
    /// 并将纹理及世界坐标到该 RT 的投影矩阵绑定给水面 shader。
    ///
    /// 这是水面"逼真感"最重要的一环:岸、树、船会被镜像映进水里,
    /// 而天空盒反射(unity_SpecCube0)只能映出天空。
    ///
    /// 性能:每台屏幕相机额外渲染一次半分辨率场景;实际开销需运行测量。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1100)] // After seat/follow cameras and HeadPoseAttachment (1000).
    public class PlanarWaterReflection : MonoBehaviour
    {
        [Tooltip("静水面世界高度(WaveField 的 Y)")]
        public float waterLevel = 0f;

        [Range(0.2f, 1f)]
        [Tooltip("反射RT相对屏幕的比例")]
        public float resolutionScale = 0.5f;

        sealed class ReflectionResources
        {
            public Camera source;
            public Camera camera;
            public RenderTexture texture;
            public readonly UniversalRenderPipeline.SingleCameraRequest request =
                new UniversalRenderPipeline.SingleCameraRequest();
        }

        readonly Dictionary<long, ReflectionResources> _resources =
            new Dictionary<long, ReflectionResources>();
        readonly List<long> _removedSources = new List<long>();
        Renderer[] _waterRenderers;
        bool[] _waterRendererEnabled;
        bool _rendering;

        // The screen camera's endCameraRendering observer checks these values.
        // They describe a completed render and binding, not a scheduled request.
        public int LastSourceCameraId { get; private set; }
        public int LastRenderedFrame { get; private set; } = -1;
        public int LastRenderedEyeCount { get; private set; }

        static readonly int RtId = Shader.PropertyToID("_WaterPlanarReflection");
        static readonly int OnId = Shader.PropertyToID("_WaterPlanarOn");
        static readonly int WorldToUvId = Shader.PropertyToID("_WaterPlanarWorldToUV");
        static readonly int RightRtId = Shader.PropertyToID("_WaterPlanarReflectionRight");
        static readonly int RightWorldToUvId = Shader.PropertyToID("_WaterPlanarWorldToUVRight");
        static readonly int StereoId = Shader.PropertyToID("_WaterPlanarStereo");
        static readonly int ActiveEyeId = Shader.PropertyToID("_WaterPlanarActiveEye");

        void OnEnable()
        {
            DisableReflection();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            Camera.onPreCull += OnBuiltinCameraPreCull;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Camera.onPreCull -= OnBuiltinCameraPreCull;
            ReleaseAllResources();
        }

        void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Camera.onPreCull -= OnBuiltinCameraPreCull;
            ReleaseAllResources();
        }

        static bool IsScreenCamera(Camera source)
        {
            return source != null && source.isActiveAndEnabled
                && source.cameraType == CameraType.Game && source.targetTexture == null;
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera source)
        {
            if (_rendering || !IsScreenCamera(source)) return;
            // Avoid allocating a full reflection for a diagnostic thumbnail.
            if (source.pixelWidth < 256 || source.pixelHeight < 256)
            { DisableReflection(); return; }
            RenderForCamera(source, context, true);
        }

        void OnBuiltinCameraPreCull(Camera source)
        {
            if (GraphicsSettings.currentRenderPipeline != null || _rendering
                || !IsScreenCamera(source)) return;
            if (source.pixelWidth < 256 || source.pixelHeight < 256)
            { DisableReflection(); return; }
            RenderForCamera(source, default, false);
        }

        /// <summary>为指定相机渲染一次反射(公开给编辑器预览/批处理调用)。</summary>
        public void RenderNow(Camera source)
        {
            RenderForCamera(source, default, false);
        }

        /// <summary>Diagnostic entry with explicit eye matrices. This exercises the same
        /// per-eye render path without claiming that a headset/runtime supplied these matrices.</summary>
        public void RenderStereoNow(Camera source, Matrix4x4 leftView, Matrix4x4 leftProjection,
            Matrix4x4 rightView, Matrix4x4 rightProjection)
        {
            RenderForCamera(source, default, false, new[] { leftView, rightView },
                new[] { leftProjection, rightProjection });
        }

        void RenderForCamera(Camera source, ScriptableRenderContext context, bool hasContext,
            Matrix4x4[] diagnosticViews = null, Matrix4x4[] diagnosticProjections = null)
        {
            if (_rendering) return;
            DisableReflection();
            if (!isActiveAndEnabled || source == null || !source.isActiveAndEnabled) return;
            // A camera on the clipping plane produces a singular oblique
            // projection (the serialized camera starts at y=0 before its first
            // follow update). This surface shader has no underwater reflection.
            bool stereo = diagnosticViews != null || source.stereoEnabled;
            var leftView = diagnosticViews != null ? diagnosticViews[0] : stereo
                ? source.GetStereoViewMatrix(Camera.StereoscopicEye.Left) : source.worldToCameraMatrix;
            var leftProjection = diagnosticProjections != null ? diagnosticProjections[0] : stereo
                ? source.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) : source.projectionMatrix;
            var rightView = diagnosticViews != null ? diagnosticViews[1] : stereo
                ? source.GetStereoViewMatrix(Camera.StereoscopicEye.Right) : leftView;
            var rightProjection = diagnosticProjections != null ? diagnosticProjections[1] : stereo
                ? source.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) : leftProjection;
            if (leftView.inverse.GetColumn(3).y - waterLevel <= 0.001f
                || rightView.inverse.GetColumn(3).y - waterLevel <= 0.001f) return;
            bool isUrp = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            if (!isUrp && GraphicsSettings.currentRenderPipeline != null) return;

            _rendering = true;
            bool previousInvertCulling = GL.invertCulling;
            RenderTexture previousActive = RenderTexture.active;
            bool waterHidden = false;
            try
            {
                PruneDestroyedSources();
                ReflectionResources resource = EnsureResources(source, 0, stereo);
                if (!resource.texture.IsCreated()) return;
                ReflectionResources rightResource = stereo ? EnsureResources(source, 1, true) : resource;
                if (!rightResource.texture.IsCreated()) return;
                HideWater();
                waterHidden = true;
                // Reflection changes handedness. Restore the caller's state,
                // including when it already had inverted culling enabled.
                GL.invertCulling = !previousInvertCulling;
                for (int eye = 0; eye < (stereo ? 2 : 1); eye++)
                {
                    ReflectionResources current = eye == 0 ? resource : rightResource;
                    MirrorView(current.camera, eye == 0 ? leftView : rightView,
                        eye == 0 ? leftProjection : rightProjection);
                    if (isUrp && hasContext)
                    {
                    // Inside URP's camera callback the supplied context is the
                    // active one; submitting another whole render request here
                    // would start a nested context. URP 14 retains this API.
#pragma warning disable CS0618
                    UniversalRenderPipeline.RenderSingleCamera(context, current.camera);
#pragma warning restore CS0618
                    }
                    else if (isUrp)
                    {
                    // Editor previews have no active ScriptableRenderContext.
                    // A synchronous request supplies one and is recursion-guarded.
                        if (!RenderPipeline.SupportsRenderRequest(current.camera, current.request)) return;
                        RenderPipeline.SubmitRenderRequest(current.camera, current.request);
                    }
                    else current.camera.Render();
                    current.texture.GenerateMips();
                }

                Shader.SetGlobalTexture(RtId, resource.texture);
                Shader.SetGlobalMatrix(WorldToUvId, WorldToTextureMatrix(resource.camera));
                Shader.SetGlobalTexture(RightRtId, rightResource.texture);
                Shader.SetGlobalMatrix(RightWorldToUvId, WorldToTextureMatrix(rightResource.camera));
                Shader.SetGlobalFloat(StereoId, stereo ? 1f : 0f);
                Shader.SetGlobalFloat(ActiveEyeId, source.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1f : 0f);
                Shader.SetGlobalFloat(OnId, 1f);
                LastSourceCameraId = source.GetInstanceID();
                LastRenderedFrame = Time.frameCount;
                LastRenderedEyeCount = stereo ? 2 : 1;
            }
            finally
            {
                GL.invertCulling = previousInvertCulling;
                RenderTexture.active = previousActive;
                if (waterHidden) RestoreWater();
                _rendering = false;
            }
        }

        ReflectionResources EnsureResources(Camera source, int eye, bool stereo)
        {
            long id = ((long)source.GetInstanceID() << 1) | (uint)eye;
            if (!_resources.TryGetValue(id, out ReflectionResources resource))
            {
                var go = new GameObject("WaterReflectionCamera_" + id);
                go.hideFlags = HideFlags.HideAndDontSave;
                var reflectionCamera = go.AddComponent<Camera>();
                reflectionCamera.enabled = false;
                resource = new ReflectionResources { source = source, camera = reflectionCamera };
                _resources.Add(id, resource);
            }

            int width = stereo && source.stereoEnabled && UnityEngine.XR.XRSettings.eyeTextureWidth > 0
                ? UnityEngine.XR.XRSettings.eyeTextureWidth : source.pixelWidth;
            int height = stereo && source.stereoEnabled && UnityEngine.XR.XRSettings.eyeTextureHeight > 0
                ? UnityEngine.XR.XRSettings.eyeTextureHeight : source.pixelHeight;
            int w = Mathf.Max(128, (int)(width * resolutionScale));
            int h = Mathf.Max(128, (int)(height * resolutionScale));
            if (resource.texture == null || resource.texture.width != w || resource.texture.height != h)
            {
                resource.camera.targetTexture = null;
                ReleaseTexture(resource.texture);
                resource.texture = new RenderTexture(w, h, 24)
                {
                    name = "WaterPlanarReflection_" + id,
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    useMipMap = true,
                    autoGenerateMips = false
                };
            }
            if (!resource.texture.IsCreated()) resource.texture.Create();

            Camera reflection = resource.camera;
            reflection.CopyFrom(source);
            reflection.enabled = false; // Render only through this component.
            reflection.cameraType = CameraType.Reflection;
            reflection.clearFlags = CameraClearFlags.Skybox;
            // Layer 2 contains the external force-feedback mechanism.
            reflection.cullingMask = source.cullingMask & ~(1 << 2);
            reflection.useOcclusionCulling = false;
            reflection.allowMSAA = false;
            reflection.allowHDR = false;
            reflection.stereoTargetEye = StereoTargetEyeMask.None;
            reflection.GetUniversalAdditionalCameraData().allowXRRendering = false;
            // A reflection camera renders into its own full-size target.  Do
            // not inherit a split-screen viewport from the source camera.
            reflection.targetTexture = resource.texture;
            // pixelRect also changes the normalized viewport. Assigning it
            // against the previous target (or the startup Game view) shrinks
            // the new target again, producing an invalid culling frustum.
            reflection.rect = new Rect(0f, 0f, 1f, 1f);
            reflection.aspect = source.aspect;
            resource.request.destination = resource.texture;
            return resource;
        }

        static Matrix4x4 ReflectionMatrix(float height)
        {
            Matrix4x4 reflection = Matrix4x4.identity;
            reflection.m11 = -1f;
            reflection.m13 = 2f * height;
            return reflection;
        }

        /// <summary>Use an improper reflection matrix; a quaternion alone loses handedness.</summary>
        void MirrorCamera(Camera source, Camera reflectionCamera)
            => MirrorView(reflectionCamera, source.worldToCameraMatrix, source.projectionMatrix);

        void MirrorView(Camera reflectionCamera, Matrix4x4 sourceView, Matrix4x4 sourceProjection)
        {
            Matrix4x4 reflection = ReflectionMatrix(waterLevel);
            Matrix4x4 worldFromEye = sourceView.inverse;
            reflectionCamera.transform.SetPositionAndRotation(
                reflection.MultiplyPoint3x4(worldFromEye.GetColumn(3)),
                Quaternion.LookRotation(reflection.MultiplyVector(-worldFromEye.GetColumn(2)),
                    reflection.MultiplyVector(worldFromEye.GetColumn(1))));

            // Transform orientation above only supplies ancillary camera data.
            // Geometry must use this exact view, including the reflected basis.
            Matrix4x4 view = sourceView * reflection;
            reflectionCamera.worldToCameraMatrix = view;
            reflectionCamera.projectionMatrix = sourceProjection;
            Vector4 worldPlane = new Vector4(0f, 1f, 0f, -waterLevel);
            Vector4 camPlane = view.inverse.transpose * worldPlane;
            reflectionCamera.projectionMatrix = reflectionCamera.CalculateObliqueMatrix(camPlane);
            reflectionCamera.cullingMatrix = reflectionCamera.projectionMatrix * view;
        }

        static Matrix4x4 WorldToTextureMatrix(Camera reflectionCamera)
        {
            // Convert GPU clip coordinates to this RT's native texture origin.
            // This is independent of the screen camera's viewport and Y flip.
            Matrix4x4 toUv = Matrix4x4.identity;
            toUv.m00 = 0.5f;
            toUv.m03 = 0.5f;
            toUv.m11 = SystemInfo.graphicsUVStartsAtTop ? -0.5f : 0.5f;
            toUv.m13 = 0.5f;
            return toUv * GL.GetGPUProjectionMatrix(reflectionCamera.projectionMatrix, true)
                * reflectionCamera.worldToCameraMatrix;
        }

        void HideWater()
        {
            if (_waterRenderers == null)
            {
                _waterRenderers = GetComponentsInChildren<Renderer>(true);
                _waterRendererEnabled = new bool[_waterRenderers.Length];
            }
            for (int i = 0; i < _waterRenderers.Length; i++)
                if (_waterRenderers[i] != null)
                {
                    _waterRendererEnabled[i] = _waterRenderers[i].enabled;
                    _waterRenderers[i].enabled = false;
                }
        }

        void RestoreWater()
        {
            if (_waterRenderers == null) return;
            for (int i = 0; i < _waterRenderers.Length; i++)
                if (_waterRenderers[i] != null)
                    _waterRenderers[i].enabled = _waterRendererEnabled[i];
        }

        void DisableReflection()
        {
            Shader.SetGlobalFloat(OnId, 0f);
            Shader.SetGlobalTexture(RtId, Texture2D.blackTexture);
            Shader.SetGlobalMatrix(WorldToUvId, Matrix4x4.identity);
            Shader.SetGlobalTexture(RightRtId, Texture2D.blackTexture);
            Shader.SetGlobalMatrix(RightWorldToUvId, Matrix4x4.identity);
            Shader.SetGlobalFloat(StereoId, 0f);
            Shader.SetGlobalFloat(ActiveEyeId, 0f);
            LastSourceCameraId = 0;
            LastRenderedFrame = -1;
            LastRenderedEyeCount = 0;
        }

        void PruneDestroyedSources()
        {
            _removedSources.Clear();
            foreach (var entry in _resources)
                if (entry.Value.source == null) _removedSources.Add(entry.Key);
            foreach (long id in _removedSources)
            {
                ReleaseResources(_resources[id]);
                _resources.Remove(id);
            }
        }

        void ReleaseAllResources()
        {
            DisableReflection();
            foreach (ReflectionResources resource in _resources.Values) ReleaseResources(resource);
            _resources.Clear();
            _removedSources.Clear();
            _waterRenderers = null;
            _waterRendererEnabled = null;
        }

        static void ReleaseResources(ReflectionResources resource)
        {
            if (resource.camera != null) resource.camera.targetTexture = null;
            resource.request.destination = null;
            ReleaseTexture(resource.texture);
            if (resource.camera != null) DestroyOwned(resource.camera.gameObject);
        }

        static void ReleaseTexture(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            DestroyOwned(texture);
        }

        static void DestroyOwned(Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
