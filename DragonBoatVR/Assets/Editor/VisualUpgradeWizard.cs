using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 视觉升级第一梯队：后处理 + HDRI 天空 + 黄昏光照 + 软阴影 + SSAO。
    ///
    /// 幂等：重复执行只会覆盖成同一组设置，不产生重复对象。
    /// 产物：
    ///   Assets/Settings/GlobalPostProcessing.asset     —— 全局后处理配置
    ///   Assets/Settings/ScreenSpaceAO.asset            —— SSAO 渲染特性
    ///   Assets/Settings/Skybox_<hdri名>.mat            —— 全景天空盒材质
    ///   场景内的 GlobalVolume、主光角度、相机后处理开关、雾。
    /// </summary>
    public static class VisualUpgradeWizard
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";
        const string UrpAssetPath = "Assets/Settings/DragonBoatVR-URP.asset";
        const string RendererAssetPath = "Assets/Settings/DragonBoatVR-Renderer.asset";
        const string ProfilePath = "Assets/Settings/GlobalPostProcessing.asset";
        const string SsaoPath = "Assets/Settings/ScreenSpaceAO.asset";

        // 黄昏主光：低角度暖光。方位角在预览后可继续调。
        static readonly Vector3 SunEuler = new Vector3(16f, 232f, 20f);
        static readonly Color SunColor = new Color(1.00f, 0.78f, 0.56f);
        const float SunIntensity = 1.35f;
        // 晴天风格:高角度白光(净水质感参考)
        static readonly Vector3 DaySunEuler = new Vector3(42f, 218f, 10f);
        static readonly Color DaySunColor = new Color(1.00f, 0.97f, 0.90f);
        const float DaySunIntensity = 1.5f;

        /// <summary>-daylight 传入时为 true:白昼光照+净水配色</summary>
        public static bool Daylight { get; private set; }

        [MenuItem("DragonBoatVR/⑤ 视觉升级（后处理/天空/光照）")]
        public static void Run()
        {
            // 默认晴天净水(用户 2026-10-25 拍板);-sunset 切回黄昏氛围
            string hdriName = ArgValue("-hdri") ?? (ArgValue("-sunset") != null ? "venice_sunset_4k" : "shanghai_riverside_2k");
            string outDir = ArgValue("-outDir") ?? "F:/vr视觉/tools/visual_upgrade";
            var args = System.Environment.GetCommandLineArgs();
            Daylight = ArgValue("-sunset") == null;

            ConfigureUrpAsset();
            EnsureSsaoFeature();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("[VISUAL] 打不开场景 " + ScenePath);
                EditorApplication.Exit(1);
                return;
            }

            var skyMat = EnsureSkyboxMaterial(hdriName);
            var profile = EnsurePostProfile();
            EnsureWaterVisuals();
            EnsureBoatVisuals();
            ApplySceneSettings(skyMat, profile);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[VISUAL] 场景与资产已保存");

            CapturePreviews(outDir);
            EditorApplication.Exit(0);
        }

        // ---- URP 资产：开深度/不透明纹理（水面阶段要用）、软阴影 ----
        static void ConfigureUrpAsset()
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (asset == null) { Debug.LogError("[VISUAL] 找不到 URP 资产"); return; }

            var so = new SerializedObject(asset);
            so.FindProperty("m_RequireDepthTexture").boolValue = true;
            so.FindProperty("m_RequireOpaqueTexture").boolValue = true;
            so.FindProperty("m_SoftShadowsSupported").boolValue = true;
            so.FindProperty("m_SoftShadowQuality").intValue = 2; // High
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log("[VISUAL] URP 资产已更新（深度/不透明纹理/高质量软阴影）");
        }

        // ---- 渲染器：挂 SSAO 特性（URP14 中该类是 internal，用反射创建） ----
        static void EnsureSsaoFeature()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererAssetPath);
            if (renderer == null) { Debug.LogError("[VISUAL] 找不到 Renderer 资产"); return; }

            var ssaoType = typeof(UniversalRenderPipelineAsset).Assembly
                .GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
            if (ssaoType == null)
            {
                Debug.LogError("[VISUAL] 当前 URP 版本没有 SSAO 特性");
                return;
            }

            var so = new SerializedObject(renderer);
            var features = so.FindProperty("m_RendererFeatures");
            for (int i = 0; i < features.arraySize; i++)
            {
                var f = features.GetArrayElementAtIndex(i).objectReferenceValue;
                if (f != null && f.GetType() == ssaoType) return; // 已存在，保持幂等
            }

            var ssao = AssetDatabase.LoadAssetAtPath(SsaoPath, ssaoType) as ScriptableRendererFeature;
            if (ssao == null)
            {
                ssao = (ScriptableRendererFeature)ScriptableObject.CreateInstance(ssaoType);
                AssetDatabase.CreateAsset(ssao, SsaoPath);
            }
            var ssaoSo = new SerializedObject(ssao);
            // 风格化写实：中等强度、半分辨率，保持帧率余量（未来 VR 也能留预算）。
            ssaoSo.FindProperty("m_Settings.Downsample").boolValue = true;
            ssaoSo.FindProperty("m_Settings.Intensity").floatValue = 1.0f;
            ssaoSo.FindProperty("m_Settings.Radius").floatValue = 0.3f;
            ssaoSo.FindProperty("m_Settings.DirectLightingStrength").floatValue = 0.25f;
            ssaoSo.ApplyModifiedProperties();
            EditorUtility.SetDirty(ssao);

            features.InsertArrayElementAtIndex(features.arraySize);
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = ssao;
            var map = so.FindProperty("m_RendererFeatureMap");
            map.InsertArrayElementAtIndex(map.arraySize);
            map.GetArrayElementAtIndex(map.arraySize - 1).intValue =
                Shader.PropertyToID(GUID.Generate().ToString());
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();
            Debug.Log("[VISUAL] SSAO 渲染特性已挂载");
        }

        // ---- 天空盒：HDR 全景图 → Skybox/Panoramic 材质 ----
        static Material EnsureSkyboxMaterial(string hdriName)
        {
            string texPath = $"Assets/Art/HDRI/{hdriName}.hdr";
            var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer == null) { Debug.LogError("[VISUAL] 找不到 HDRI " + texPath); return null; }

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;           // HDR 线性数据
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();

            var tex = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
            string matPath = $"Assets/Settings/Skybox_{hdriName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Skybox/Panoramic"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetTexture("_MainTex", tex);
            mat.SetFloat("_Exposure", 1.05f);
            mat.SetFloat("_ImageType", 0f);         // Latlong
            mat.SetFloat("_Layout", 0f);
            mat.SetFloat("_MirrorOnBack", 0f);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VISUAL] 天空盒材质已生成 {matPath}");
            return mat;
        }

        // ---- 水面视觉资源：法线贴图导入 + Water.mat 绑定 + 平面反射组件 ----
        static void EnsureWaterVisuals()
        {
            const string normalPath = "Assets/Art/Water/WaterNormals.png";
            var importer = AssetImporter.GetAtPath(normalPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.sRGBTexture = false;
                importer.SaveAndReimport();
                var normalTex = AssetDatabase.LoadAssetAtPath<Texture>(normalPath);

                var waterMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/Water.mat");
                if (waterMat != null && waterMat.shader != null &&
                    waterMat.shader.name == "DragonBoatVR/WaveFieldWater")
                {
                    waterMat.SetTexture("_NormalMap", normalTex);
                    EditorUtility.SetDirty(waterMat);
                }
            }

            var surf = Object.FindObjectOfType<WaterSurface>();
            if (surf != null)
            {
                var planar = surf.GetComponent<PlanarWaterReflection>();
                if (planar == null)
                {
                    planar = surf.gameObject.AddComponent<PlanarWaterReflection>();
                    var wave = Object.FindObjectOfType<WaveField>();
                    if (wave != null) planar.waterLevel = wave.transform.position.y;
                }
            }

            // 内河训练场景波谱(4 分量,16m→2.5m):
            // 几何波长与 8m 样机船处在同一量级，近场碎纹交给法线层，
            // 避免出现一整片比船还大的“海浪”。
            // 物理内核(色散 ω=√(gk)、尖峰化 Q、CPU/GPU 同源)与 FFT 方案一致。
            // 幂等:数组与目标谱不一致时整体覆盖。
            var wf = Object.FindObjectOfType<WaveField>();
            if (wf != null)
            {
                var spectrum = new WaveField.WaveComponent[]
                {
                    new WaveField.WaveComponent { direction = new Vector2( 0.9f, -0.5f), wavelength = 26f,  amplitude = 0.060f, steepness = 0.32f },
                    new WaveField.WaveComponent { direction = new Vector2( 1.0f,  0.2f), wavelength = 12f,  amplitude = 0.110f, steepness = 0.50f },
                    new WaveField.WaveComponent { direction = new Vector2( 0.8f,  0.6f), wavelength = 7.5f, amplitude = 0.070f, steepness = 0.45f },
                    new WaveField.WaveComponent { direction = new Vector2( 0.7f,  1.0f), wavelength = 6.5f, amplitude = 0.060f, steepness = 0.48f },
                    new WaveField.WaveComponent { direction = new Vector2(-0.3f,  1.0f), wavelength = 4.6f, amplitude = 0.037f, steepness = 0.42f },
                    new WaveField.WaveComponent { direction = new Vector2(-0.4f,  1.0f), wavelength = 3.2f, amplitude = 0.030f, steepness = 0.40f },
                };
                bool changed = wf.waves == null || wf.waves.Length != spectrum.Length;
                if (!changed)
                    for (int i = 0; i < spectrum.Length; i++)
                        if (Mathf.Abs(wf.waves[i].wavelength - spectrum[i].wavelength) > 0.01f ||
                            Mathf.Abs(wf.waves[i].amplitude - spectrum[i].amplitude) > 0.001f ||
                            Mathf.Abs(wf.waves[i].steepness - spectrum[i].steepness) > 0.01f)
                        { changed = true; break; }
                if (changed)
                {
                    wf.waves = spectrum;
                    EditorUtility.SetDirty(wf);
                    Debug.Log("[VISUAL] 已安装内河训练波谱(16m→2.5m,低幅度,物理/视觉分层)");
                }
                var surface = Object.FindObjectOfType<WaterSurface>();
                if (surface != null)
                {
                    surface.size = 220f;
                    surface.segments = 448;
                    EditorUtility.SetDirty(surface);
                }
                wf.ValidateSteepness();
            }

            // 细节法线:已换实拍水纹法线图(three.js waternormals,1024,真实涟漪)。
            // 采样密度必须亚米级才有真实涟漪感——0.08/0.032(12~31m 一格)是
            // "大软包"假感的直接来源;强度适中让近场碎光与反射兼顾。
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/Water.mat");
            if (mat != null && mat.HasProperty("_NormalStrength"))
            {
                mat.SetFloat("_NormalStrength", 0.36f);
                if (mat.HasProperty("_NormalScaleA")) mat.SetFloat("_NormalScaleA", 2.2f);
                if (mat.HasProperty("_NormalScaleB")) mat.SetFloat("_NormalScaleB", 0.9f);
                if (mat.HasProperty("_NormalScrollA")) mat.SetVector("_NormalScrollA", new Vector4(0.028f, 0.021f, 0f, 0f));
                if (Daylight)
                {
                    // 晴天净水参考款:亮蓝渐变、镜面反射主导、涟漪收敛
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.05f, 0.30f, 0.42f, 0.90f));
                    if (mat.HasProperty("_ShallowColor")) mat.SetColor("_ShallowColor", new Color(0.24f, 0.63f, 0.72f, 0.80f));
                    if (mat.HasProperty("_SkyReflectStrength")) mat.SetFloat("_SkyReflectStrength", 0.95f);
                    if (mat.HasProperty("_PlanarStrength")) mat.SetFloat("_PlanarStrength", 0.82f);
                    mat.SetFloat("_NormalStrength", 0.34f);
                    if (mat.HasProperty("_SpecStrength")) mat.SetFloat("_SpecStrength", 0.85f);
                    if (mat.HasProperty("_FoamThreshold")) mat.SetFloat("_FoamThreshold", 0.14f);
                }
                else
                {
                    // 水色往天然河道偏:深水墨绿、浅水橄榄,不再是泳池蓝绿
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.045f, 0.16f, 0.15f, 0.94f));
                    if (mat.HasProperty("_ShallowColor")) mat.SetColor("_ShallowColor", new Color(0.26f, 0.50f, 0.42f, 0.86f));
                }
                // 共用项(风格分支之后不再覆盖上面)
                if (mat.HasProperty("_PlanarDistortion")) mat.SetFloat("_PlanarDistortion", 0.04f);
                if (mat.HasProperty("_ShoreFoamWidth")) mat.SetFloat("_ShoreFoamWidth", 1.5f);
                if (mat.HasProperty("_DepthOcclusionBias")) mat.SetFloat("_DepthOcclusionBias", 0.018f);
                if (!Daylight)
                {
                    if (mat.HasProperty("_PlanarStrength")) mat.SetFloat("_PlanarStrength", 0.72f);
                    if (mat.HasProperty("_SpecStrength")) mat.SetFloat("_SpecStrength", 0.5f);
                    if (mat.HasProperty("_FoamThreshold")) mat.SetFloat("_FoamThreshold", 0.14f);
                }
                EditorUtility.SetDirty(mat);
            }

            // 随速船壳泡沫(船首八字白水+船尾拖尾):挂在 Boat 下的纯视觉层,
            // 幂等;运行时按船速显隐,静态预览不可见属正常
            var boatGo2 = GameObject.Find("Boat");
            if (boatGo2 != null)
            {
                var wakeTr = boatGo2.transform.Find("FoamWake");
                if (wakeTr == null)
                {
                    var wakeGo = new GameObject("FoamWake");
                    wakeGo.transform.SetParent(boatGo2.transform, false);
                    wakeGo.AddComponent<BoatFoamWake>();
                    Debug.Log("[VISUAL] 已挂载随速船壳泡沫 FoamWake");
                }
                // 交互泡沫/尾迹累积层(Crest 泡沫模拟的桌面版)
                InteractionFoamTrail trail = null;
                if (boatGo2.transform.Find("FoamTrail") == null)
                {
                    var trailGo = new GameObject("FoamTrail");
                    trailGo.transform.SetParent(boatGo2.transform, false);
                    trail = trailGo.AddComponent<InteractionFoamTrail>();
                    Debug.Log("[VISUAL] 已挂载交互泡沫尾迹层 FoamTrail");
                }
                else
                {
                    trail = boatGo2.transform.Find("FoamTrail").GetComponent<InteractionFoamTrail>();
                }
                if (trail != null)
                {
                    // 定稿值(用户 2026-10-25 拍板):可见白痕+6s 存留
                    trail.paintPerMeter = 1.2f;
                    trail.decaySeconds = 6f;
                    EditorUtility.SetDirty(trail);
                }
                var fw = (wakeTr != null ? wakeTr : boatGo2.transform.Find("FoamWake"))
                         ?.GetComponent<BoatFoamWake>();
                if (fw != null)
                {
                    if (fw.boatBody == null) fw.boatBody = boatGo2.GetComponent<Rigidbody>();
                    if (fw.wave == null) fw.wave = wf;
                    if (fw.foamMaterial == null)
                    {
                        var foamMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Settings/BoatFoam.mat");
                        if (foamMat == null)
                        {
                            var sh = Shader.Find("DragonBoatVR/BoatFoam");
                            foamMat = new Material(sh);
                            AssetDatabase.CreateAsset(foamMat, "Assets/Settings/BoatFoam.mat");
                        }
                        fw.foamMaterial = foamMat;
                    }
                    EditorUtility.SetDirty(fw);
                }
            }

            // 用户保留第一/第三人称双视窗；外部装置继续仿真，但不进入画面。
            var twinCamGo = GameObject.Find("TwinCamera");
            if (twinCamGo != null && twinCamGo.GetComponent<PlatformDebugView>() == null)
            {
                twinCamGo.AddComponent<PlatformDebugView>();
                Debug.Log("[VISUAL] 已挂载外部装置显示隔离组件");
            }

            // 桨-船相对位置探针(诊断桨逃逸 BUG 用,运行时输出 [PADDLEPROBE] 日志)
            var paddleGo = GameObject.Find("Paddle");
            var boatForProbe = GameObject.Find("Boat");
            if (paddleGo != null && boatForProbe != null && paddleGo.GetComponent<PaddleAttachmentProbe>() == null)
            {
                var probe = paddleGo.AddComponent<PaddleAttachmentProbe>();
                probe.boat = boatForProbe.transform;
                Debug.Log("[VISUAL] 已挂载桨位探针");
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[VISUAL] 水面视觉(法线/平面反射)已配置");
        }

        // The majesty OBJ atlas has unrelated UV islands reading as a black mass
        // in the seat view, so the boat uses a real plank photo texture (Poly
        // Haven brown_planks_04, CC0) instead of the single flat color that read
        // as fake plastic wood. Mesh, physics and paddle interfaces untouched.
        static void EnsureBoatVisuals()
        {
            var real = GameObject.Find("Boat/RealDragonBoat");
            if (real == null) return;

            // 法线贴图要按 NormalMap 导入(否则切线空间错)
            var norImporter = AssetImporter.GetAtPath("Assets/Art/Boat/boat_planks_nor.jpg") as TextureImporter;
            if (norImporter != null && norImporter.textureType != TextureImporterType.NormalMap)
            {
                norImporter.textureType = TextureImporterType.NormalMap;
                norImporter.wrapMode = TextureWrapMode.Repeat;
                norImporter.SaveAndReimport();
            }

            const string materialPath = "Assets/Settings/BoatExterior.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) { Debug.LogWarning("[VISUAL] URP Lit shader unavailable; boat material unchanged"); return; }
                mat = new Material(shader) { name = "BoatExterior" };
                AssetDatabase.CreateAsset(mat, materialPath);
            }

            var diff = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/Boat/boat_planks_diff.jpg");
            var nor = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/Boat/boat_planks_nor.jpg");
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", diff);
            if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", nor);
            if (diff != null)
            {
                // 图集 UV 覆盖整船;×2 平铺让木板读感 ~0.6m/块,近看有纹理不糊
                mat.SetTextureScale("_BaseMap", new Vector2(2f, 2f));
                mat.SetTextureOffset("_BaseMap", Vector2.zero);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.04f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.38f);   // 上漆木头
            EditorUtility.SetDirty(mat);

            int count = 0;
            foreach (var renderer in real.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0) renderer.sharedMaterial = mat;
                else
                {
                    for (int i = 0; i < materials.Length; i++) materials[i] = mat;
                    renderer.sharedMaterials = materials;
                }
                count++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[VISUAL] 真龙舟已绑定干净木质材质:渲染体={count}");
        }

        // ---- 全局后处理配置：ACES + Bloom + 暖白平衡 + 对比度/饱和度 + 暗角 ----
        static VolumeProfile EnsurePostProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }

            var tm = GetOrAdd<Tonemapping>(profile);
            tm.mode.Override(TonemappingMode.ACES);

            var bloom = GetOrAdd<Bloom>(profile);
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(0.62f);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(1f, 0.94f, 0.85f, 1f));

            var ca = GetOrAdd<ColorAdjustments>(profile);
            ca.postExposure.Override(0.12f);
            ca.contrast.Override(10f);
            ca.saturation.Override(14f);

            var wb = GetOrAdd<WhiteBalance>(profile);
            wb.temperature.Override(12f);

            var vig = GetOrAdd<Vignette>(profile);
            vig.intensity.Override(0.26f);
            vig.smoothness.Override(0.42f);

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            Debug.Log("[VISUAL] 后处理配置已生成 " + ProfilePath);
            return profile;
        }

        static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            T comp;
            if (!profile.TryGet(out comp))
            {
                comp = profile.Add<T>();
                AssetDatabase.AddObjectToAsset(comp, profile);
            }
            return comp;
        }

        // ---- 场景：天空/环境光/主光/体积/雾/相机开关 ----
        static void ApplySceneSettings(Material skyMat, VolumeProfile profile)
        {
            // 天空与环境
            RenderSettings.skybox = skyMat;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.0f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionBounces = 1;

            // 黄昏雾：远处岸景融进天色
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0038f;
            RenderSettings.fogColor = new Color(0.82f, 0.70f, 0.62f);

            // 主光：低角度暖光 + 软阴影
            Light sun = null;
            foreach (var light in Object.FindObjectsOfType<Light>())
                if (light.type == LightType.Directional) { sun = light; break; }
            if (sun == null)
            {
                var go = new GameObject("Directional Light");
                sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            sun.transform.rotation = Quaternion.Euler(Daylight ? DaySunEuler : SunEuler);
            sun.color = Daylight ? DaySunColor : SunColor;
            sun.intensity = Daylight ? DaySunIntensity : SunIntensity;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            sun.shadowBias = 0.02f;
            sun.shadowNormalBias = 0.6f;

            // 全局体积
            Volume volume = null;
            var existing = GameObject.Find("GlobalVolume");
            if (existing != null) volume = existing.GetComponent<Volume>();
            if (volume == null)
            {
                var go = new GameObject("GlobalVolume");
                go.transform.position = Vector3.zero;
                volume = go.AddComponent<Volume>();
            }
            volume.isGlobal = true;
            volume.priority = 0;
            volume.sharedProfile = profile;

            // 所有场景相机开启后处理
            int camCount = 0;
            foreach (var cam in Object.FindObjectsOfType<Camera>())
            {
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                camCount++;
            }

            var seat = Object.FindObjectOfType<BoatSeatCamera>();
            if (seat != null)
            {
                seat.seatLocalPosition = new Vector3(0f, 1.12f, -1.35f);
                seat.seatLocalEulerAngles = Vector3.zero;
                EditorUtility.SetDirty(seat);
            }
            var follow = Object.FindObjectOfType<DebugFollowCamera>();
            if (follow != null)
            {
                follow.distance = 8.5f;
                follow.pitch = 12f;
                follow.followSmoothing = 0.12f;
                follow.followBobbing = false;
                EditorUtility.SetDirty(follow);
            }
            var split = Object.FindObjectOfType<SplitViewController>();
            if (split != null)
            {
                // The desktop presentation contains only the cabin and
                // whole-boat panes. Device diagnostics do not create a view.
                split.layout = SplitViewController.Layout.Split;
                split.mainWidthRatio = 0.68f;
                EditorUtility.SetDirty(split);
            }
            Debug.Log($"[VISUAL] 场景设置完成：天空={skyMat?.name}，后处理相机 {camCount} 台");
        }

        // ---- 预览：电影机位 + 主相机(座位) + 副相机(第三人称)各渲一张 ----
        static void CapturePreviews(string outDir)
        {
            Directory.CreateDirectory(outDir);

            // 天空盒换成 HDRI 后刷新环境探针，让水面反射/环境光取到新天色。
            DynamicGI.UpdateEnvironment();

            var wave = Object.FindObjectOfType<WaveField>();
            var surf = Object.FindObjectOfType<WaterSurface>();
            if (wave != null) InvokePrivate(wave, "Awake");
            if (surf != null) InvokePrivate(surf, "Awake");
            var simField = typeof(WaveField).GetField("<SimTime>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (simField != null && wave != null) simField.SetValue(wave, 12.5f);

            // 静态出图没有物理步进，涌浪峰会越过固定不动的船体，看起来像半沉。
            // 这里把船（和不在船层级下的主相机）沿垂直方向对齐到当前浪面的
            // 抬升量——运行时由浮力组件实时跟浪，此偏移只服务于本次截图。
            var boatGo = GameObject.Find("Boat");
            var boatT = boatGo != null ? boatGo.transform : null;
            if (boatT == null)
            {
                var buoy = Object.FindObjectOfType<BoatBuoyancy>();
                if (buoy != null) boatT = buoy.transform;
            }
            var mainGo = GameObject.Find("MainCamera");
            var mainT = mainGo != null ? mainGo.transform : null;
            Vector3 boatSavedPos = Vector3.zero, mainSavedPos = Vector3.zero;
            Quaternion mainSavedRot = Quaternion.identity;
            bool boatMoved = false, mainMoved = false;
            if (wave != null && boatT != null)
            {
                var s = wave.Sample(boatT.position.x, boatT.position.z);
                boatSavedPos = boatT.position;
                boatT.position += Vector3.up * s.elevation;
                boatMoved = true;
                if (mainT != null)
                {
                    mainSavedPos = mainT.position;
                    mainSavedRot = mainT.rotation;
                    mainMoved = true;
                    // Edit-mode preview does not run BoatSeatCamera.LateUpdate.
                    // Rebuild the actual seat pose so screenshots match Play mode.
                    var seat = mainGo.GetComponent<BoatSeatCamera>();
                    if (seat != null && seat.boat != null)
                    {
                        mainT.SetPositionAndRotation(
                            seat.boat.TransformPoint(seat.seatLocalPosition),
                            seat.boat.rotation * Quaternion.Euler(seat.seatLocalEulerAngles));
                    }
                    else if (!mainT.IsChildOf(boatT))
                    {
                        mainT.position += Vector3.up * s.elevation;
                    }
                }
            }

            CaptureOne(outDir + "/pass_cinematic.png",
                new Vector3(7f, 6.5f, -20f), Quaternion.LookRotation(new Vector3(-7f, -6f, 50f)), 55f);

            if (mainT != null)
                CaptureOne(outDir + "/pass_seat.png", mainT.position, mainT.rotation, 60f);

            var twin = GameObject.Find("TwinCamera");
            if (twin != null)
            {
                Vector3 twinSavedPos = twin.transform.position;
                Quaternion twinSavedRot = twin.transform.rotation;
                // DebugFollowCamera also has no LateUpdate in a static preview.
                // Recreate its normal orbit pose before capturing the third person view.
                var follow = twin.GetComponent<DebugFollowCamera>();
                if (follow != null && follow.target != null)
                {
                    float yaw = twin.transform.eulerAngles.y;
                    Quaternion rotation = Quaternion.Euler(follow.pitch, yaw, 0f);
                    Vector3 lookAt = follow.target.position;
                    if (!follow.followBobbing) lookAt.y = 0.5f;
                    twin.transform.SetPositionAndRotation(
                        lookAt - rotation * Vector3.forward * follow.distance, rotation);
                }
                CaptureOne(outDir + "/pass_third.png", twin.transform.position, twin.transform.rotation, 50f);
                twin.transform.SetPositionAndRotation(twinSavedPos, twinSavedRot);
            }

            if (boatMoved) boatT.position = boatSavedPos;
            if (mainMoved) mainT.SetPositionAndRotation(mainSavedPos, mainSavedRot);

            Debug.Log("[VISUAL] 预览已输出到 " + outDir);
        }

        static void CaptureOne(string path, Vector3 pos, Quaternion rot, float fov)
        {
            const int w = 1920, h = 1080;
            var go = new GameObject("VisualPreviewCam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 1200f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            // 让平面反射先按这台预览相机的视角渲染一次
            var planar = Object.FindObjectOfType<PlanarWaterReflection>();
            if (planar != null) planar.RenderNow(cam);

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var prev = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log("[VISUAL] 已保存 " + path);
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = prev;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(go);
            }
        }

        static string ArgValue(string key)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return null;
        }

        static void InvokePrivate(object target, string method)
        {
            var m = target.GetType().GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (m != null) m.Invoke(target, null);
        }
    }
}
