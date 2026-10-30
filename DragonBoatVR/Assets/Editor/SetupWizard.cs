using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using DragonBoatVR;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 一键工程初始化：物理步长 → URP 管线 → 水面材质 → 演示场景（船/水/桨/孪生/双视窗全挂好）。
    ///
    /// 菜单：DragonBoatVR → ① 一键初始化（URP + 演示场景）
    /// 幂等：重复执行会覆盖重建场景与材质，不会报错。
    ///
    /// 场景结构（与工程规划的"一主两副"布局对应）：
    ///   WaveField            唯一水面真相源（Gerstner）
    ///   Water                同源水面视觉（220m/448 段，跟随船）
    ///   Boat                 1.2×0.6×8m、150kg、六点浮力 + 各向异性水阻
    ///   Paddle               桨叶水动力 + 推进反作用力
    ///   HardwareBridge       划桨力反馈链路（9870/9871）
    ///   MotionTelemetry      六轴平台遥测（9880）
    ///   CableRig             三电机牵引绳数字孪生（9880 之外，独立 9872）
    ///   StewartPlatformRig   六轴平台数字孪生（洗出 + 滞后 + 6 缸逆解）
    ///   MainCamera           左视窗：桨手第一人称
    ///   TwinCamera           右视窗：跟随龙舟的第三视角
    ///   RideHud              量化反馈面板
    /// </summary>
    public static class SetupWizard
    {
        const string SettingsDir = "Assets/Settings";
        const string ScenePath = "Assets/Scenes/Demo.unity";
        const string UrpAssetPath = SettingsDir + "/DragonBoatVR-URP.asset";
        const string WaterMatPath = SettingsDir + "/Water.mat";
        const string StatuePhotoPath = "Assets/VisualReferences/Juzizhou_Mao_Statue_Cutout.png";

        // ---- 物理基准（来自工程规划的"参数基准表"，改之前先看注释）----
        const float BoatWidth = 1.2f;
        const float BoatHeight = 0.6f;
        const float BoatLength = 8.0f;
        const float BoatMass = 150f;          // 折算到单桨手的等效质量，不是真实 22 人船重
        const float DesignDraft = 0.18f;      // 平衡吃水（浮力倍率 2.5 反算得到）

        [MenuItem("DragonBoatVR/① 一键初始化（URP + 演示场景）", priority = 0)]
        public static void Setup()
        {
            EnsureFolder(SettingsDir);
            EnsureFolder("Assets/Scenes");
            EnsureFolder("Assets/VisualReferences");

            ApplyPhysicsSettings();
            ConfigureStatuePhoto();
            ConfigureUrp();
            var waterMat = CreateWaterMaterial();
            BuildDemoScene(waterMat);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[DragonBoatVR] 初始化完成 → 打开 Assets/Scenes/Demo.unity 按 Play。" +
                      "无硬件时桨由虚拟划桨手自动驱动；有硬件时 F4 切换成 UDP 直驱。");
        }

        /// <summary>
        /// 物理步长必须是 1/100s。理由：水动力是平方项，50Hz 下高桨速时积分误差大；
        /// 而且后续硬件同步都在 100Hz 以上，物理帧低于它会出现"拍频"抖动。
        /// </summary>
        static void ApplyPhysicsSettings()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TimeManager.asset");
            if (assets != null && assets.Length > 0)
            {
                var so = new SerializedObject(assets[0]);
                var prop = so.FindProperty("Fixed Timestep");
                if (prop != null)
                {
                    prop.floatValue = 0.01f;
                    so.ApplyModifiedProperties();
                }
                var maxProp = so.FindProperty("Maximum Allowed Timestep");
                if (maxProp != null) maxProp.floatValue = 0.05f;
                so.ApplyModifiedProperties();
            }
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // The independent UDP plant keeps running when the desktop window
            // loses focus. Keep its host loop and watchdog consumer running too.
            PlayerSettings.runInBackground = true;
            Debug.Log("[DragonBoatVR] 物理步长已设为 0.01s（100Hz）。");
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            var leaf = System.IO.Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // ---------- URP ----------
        static void ConfigureUrp()
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                AssetDatabase.CreateAsset(asset, UrpAssetPath);

                if (!TryAttachRendererData(asset))
                {
                    Debug.LogWarning("[DragonBoatVR] 自动补 Renderer Data 失败。若画面发粉，请手动创建 URP Asset (with Universal Renderer) 并拖到 Project Settings → Graphics。");
                }
            }

            GraphicsSettings.defaultRenderPipeline = asset;
            QualitySettings.renderPipeline = asset;
            asset.msaaSampleCount = 4;
            asset.renderScale = 1f;
            AssetDatabase.SaveAssets();
            Debug.Log("[DragonBoatVR] URP 已设为默认管线。");
        }

        static void ConfigureStatuePhoto()
        {
            var importer = AssetImporter.GetAtPath(StatuePhotoPath) as TextureImporter;
            if (importer == null) return;
            importer.maxTextureSize = 4096;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.compressionQuality = 100;
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 8;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }

        static bool TryAttachRendererData(UniversalRenderPipelineAsset asset)
        {
            try
            {
                var rendererDataType = System.Type.GetType(
                    "UnityEngine.Rendering.Universal.UniversalRendererData, Unity.RenderPipelines.Universal.Runtime");
                if (rendererDataType == null) return false;

                var data = ScriptableObject.CreateInstance(rendererDataType);
                if (data == null) return false;
                AssetDatabase.CreateAsset(data, SettingsDir + "/DragonBoatVR-Renderer.asset");

                var field = typeof(UniversalRenderPipelineAsset).GetField(
                    "m_RendererDataList",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field == null) return false;

                field.SetValue(asset, new ScriptableRendererData[] { data as ScriptableRendererData });
                EditorUtility.SetDirty(asset);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[DragonBoatVR] 补 Renderer Data 异常: " + e.Message);
                return false;
            }
        }

        // ---------- 材质 ----------
        static Material CreateWaterMaterial()
        {
            var shader = Shader.Find("DragonBoatVR/WaveFieldWater");
            if (shader == null)
            {
                Debug.LogError("[DragonBoatVR] 找不到 shader 'DragonBoatVR/WaveFieldWater'。");
                return null;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, WaterMatPath);
            }
            else
            {
                mat.shader = shader;
            }

            mat.SetFloat("_WaveCount", 4);
            // 波长/幅度与 WaveField 物理源一致，视觉层倍率由 WaterSurface 下发。
            mat.SetVectorArray("_Waves", new Vector4[]
            {
                new Vector4(1f, 0.2f, 8f, 0.045f),
                new Vector4(0.7f, 1f, 4f, 0.022f),
                new Vector4(-0.4f, 1f, 2.5f, 0.008f),
                new Vector4(0.9f, -0.5f, 16f, 0.015f),
                Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero,
            });
            mat.SetFloatArray("_PhaseOffsets", new float[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f });
            mat.SetFloatArray("_Steepness", new float[] { 0.32f, 0.28f, 0.22f, 0.24f, 0f, 0f, 0f, 0f });
            mat.SetFloat("_Intensity", 1f);
            mat.SetFloat("_Horizontal", 1f);
            mat.SetFloat("_DepthOcclusionBias", 0.018f);
            mat.SetFloat("_NormalStrength", 0.36f);
            mat.SetFloat("_NormalScaleA", 2.2f);
            mat.SetFloat("_NormalScaleB", 0.9f);
            mat.SetVector("_NormalScrollA", new Vector4(0.028f, 0.021f, 0f, 0f));
            mat.SetFloat("_FoamThreshold", 0.14f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material MakeLitMaterial(string name, Color color)
        {
            var path = $"{SettingsDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.shader != shader) mat.shader = shader;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.18f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", name.Contains("Trim") ? 0.28f : 0.02f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------- 场景 ----------
        /// <summary>
        /// 程序化天空盒。不挂天空盒时 URP 会给一个纯色底色，
        /// 河面反射和远景雾色对不上，画面会显得"贴纸感"很重。
        /// </summary>
        static void SetupSkybox()
        {
            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
            {
                Debug.LogWarning("[DragonBoatVR] 找不到 Skybox/Procedural，跳过天空盒");
                return;
            }

            var path = $"{SettingsDir}/DragonBoatSky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (sky == null)
            {
                sky = new Material(shader);
                AssetDatabase.CreateAsset(sky, path);
            }
            if (sky.shader != shader) sky.shader = shader;

            if (sky.HasProperty("_SunSize")) sky.SetFloat("_SunSize", 0.045f);
            if (sky.HasProperty("_SunSizeConvergence")) sky.SetFloat("_SunSizeConvergence", 4f);
            // 大气厚度给到 1.05 时，地平线附近会积出很明显的黄绿色带，
            // 河景看起来像被污染了。降到 0.75 并把地面色压向蓝灰，接近珠三角水乡的溽热天光。
            if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", 0.75f);
            if (sky.HasProperty("_SkyTint")) sky.SetColor("_SkyTint", new Color(0.50f, 0.64f, 0.82f));
            if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.30f, 0.34f, 0.38f));
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 1.05f);

            RenderSettings.skybox = sky;
            EditorUtility.SetDirty(sky);
        }

        static void BuildDemoScene(Material waterMat)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- 光照 ----
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.color = new Color(1f, 0.96f, 0.88f);
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.68f, 0.80f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.48f, 0.52f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.22f, 0.22f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.0045f;
            RenderSettings.fogColor = new Color(0.72f, 0.80f, 0.85f);

            // ---- 波场（唯一真相源）----
            var wfGo = new GameObject("WaveField");
            wfGo.transform.position = Vector3.zero;
            var wave = wfGo.AddComponent<WaveField>();

            // ---- 船体 ----
            var boat = BuildBoat(out var buoy, out var rb);
            // 平衡吃水位置：半高 − 吃水
            boat.transform.position = new Vector3(0f, BoatHeight * 0.5f - DesignDraft, 0f);

            // ---- 水面 ----
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water";
            water.transform.position = Vector3.zero;
            water.transform.localScale = Vector3.one;   // 网格尺寸由 WaterSurface 控制，不能再叠缩放
            var waterRenderer = water.GetComponent<MeshRenderer>();
            if (waterMat != null) waterRenderer.sharedMaterial = waterMat;
            // 水面只是视觉，不能有碰撞体。Plane 自带 MeshCollider 会像地板一样把船托住，
            // 浮力模型拿不到浸深（实测船会稳在"船底贴水面、吃水 0"）。
            Object.DestroyImmediate(water.GetComponent<Collider>());
            var surf = water.AddComponent<WaterSurface>();
            surf.waveField = wave;
            // 水面跟随船，边长不需要覆盖整条河道；把网格预算用在近场细节。
            // 220m/448 段 ≈ 0.49m 一格，能平滑表现 2.5m 级短波。
            surf.size = 220f;
            surf.segments = 448;
            surf.baseHeight = 0f;
            surf.followTarget = boat.transform;

            RiverCourseBuilder.Build(null, RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);
            var fengjian = RiverCourseBuilder.Build(null, RiverEnvironmentController.SceneProfile.ShundeFengjian);
            fengjian.SetActive(false);
            SetupSkybox();

            // ---- 桨 ----
            var paddle = BuildPaddle(buoy, out var hydro);

            // ---- 通信桥（划桨力反馈）----
            var bridgeGo = new GameObject("HardwareBridge");
            var bridge = bridgeGo.AddComponent<HardwareUdpBridge>();
            bridge.boatTransform = boat.transform;
            bridge.paddle = hydro;

            // ---- 驱动器：默认虚拟划桨手，无硬件也能完整跑通 ----
            var driver = paddle.AddComponent<PaddleDriver>();
            driver.bridge = bridge;
            driver.boat = buoy;
            driver.mode = PaddleDriver.Mode.VirtualPaddler;
            driver.pivotLocalPos = new Vector3(0.60f, 0.20f, 0f); // 右舷支点样机位置，未实测

            // 桨的划水几何在这里显式写死，不依赖脚本字段默认值 ——
            // 场景一旦保存，字段默认值再改也不会同步，自检会拿旧值跑，白折腾半天。
            driver.strokesPerMinute = 45f;
            driver.sweepAmplitude = 0.45f;    // 水中行程 0.9m，接近真实龙舟桨程
            driver.bladeDepth = 0.75f;        // 对应实际浸没约 0.43m
            driver.bladeAirHeight = 0.20f;
            driver.sideOffset = 0.16f;       // 当前导入船宽的桨叶边缘间隙；未实测，可调
            driver.shaftToBlade = 1.0f;      // 可替换样机参考点→桨叶中心；不是实测值
            driver.paddleInBoatSpace = true;  // 桨跟船走，否则划十几秒人和桨就分离

            // 编码器/协议仿真：虚拟桨先生成上游位姿，再经量化、CRC、序号校验和
            // 船体局部坐标解算后才进入水动力。分辨率是未实测样机值，可替换。
            var encoder = paddle.AddComponent<PaddleEncoderPipeline>();
            encoder.driver = driver;
            encoder.boat = buoy;
            encoder.measured = false;

            // ---- 六轴平台遥测（给真平台）----
            var motionGo = new GameObject("MotionPlatformTelemetry");
            var motion = motionGo.AddComponent<MotionTelemetry>();
            motion.boatTransform = boat.transform;
            motion.sendRate = 90f;

            // ---- 三电机牵引绳数字孪生 ----
            var cableGo = new GameObject("CableRig");
            var cable = cableGo.AddComponent<CableDriveTwin>();
            cable.paddle = hydro;
            cable.boat = buoy;
            // 出绳口显式写入：三点要能张成三维，否则三绳合力还原不出总水阻。
            // 实际安装位置以硬件组机械图为准，这里是仿真侧的合理布置。
            cable.anchorLocal = new Vector3[]
            {
                new Vector3(1.40f,  0.50f,  1.60f),   // 前上外
                new Vector3(1.40f,  0.50f, -1.20f),   // 后上外
                new Vector3(0.60f, -1.40f,  0.20f),   // 内下（水下侧）
            };
            // 机构在真实装置外部，用户视角不显示；求解、遥测和力反馈仍运行。
            cable.showCables = false;
            cable.showMotorUnits = false;

            // ---- 六轴平台数字孪生（与船体同轴安装，平台承载船体）----
            var stewartGo = new GameObject("StewartPlatformRig");
            var stewart = stewartGo.AddComponent<StewartPlatformTwin>();
            stewart.sourceBoat = buoy;
            // 船体中心约 y=0.12m、船底约 y=-0.18m；默认中位平台顶板落在船底，
            // 平台与船体保持同一世界 x/z。所有尺寸均为未实测样机值。
            stewart.standPosition = new Vector3(0f,
                boat.transform.position.y - BoatHeight * 0.5f - stewart.neutralHeight - 0.04f, 0f);
            stewart.followBoatForAssemblyDisplay = true;
            stewart.autoBuildModel = true;

            // ---- 相机：桨手第一人称 + 龙舟第三视角 ----
            var camGo = new GameObject("MainCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 70f;
            cam.allowMSAA = true;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 800f;
            camGo.AddComponent<AudioListener>();
            var seatView = camGo.AddComponent<BoatSeatCamera>();
            seatView.boat = boat.transform;
            seatView.seatLocalPosition = new Vector3(0f, 1.12f, -1.35f);
            seatView.seatLocalEulerAngles = Vector3.zero;

            var twinCamGo = new GameObject("TwinCamera");
            var twinCam = twinCamGo.AddComponent<Camera>();
            twinCam.fieldOfView = 52f;
            twinCam.nearClipPlane = 0.05f;
            twinCam.farClipPlane = 500f;
            twinCam.clearFlags = CameraClearFlags.Skybox;
            twinCam.backgroundColor = new Color(0.09f, 0.11f, 0.14f);
            twinCam.transform.position = boat.transform.position + new Vector3(10f, 5.5f, -12f);
            var thirdPerson = twinCamGo.AddComponent<DebugFollowCamera>();
            thirdPerson.target = boat.transform;
            thirdPerson.distance = 8.5f;
            thirdPerson.pitch = 12f;
            thirdPerson.followSmoothing = 0.12f;
            thirdPerson.followBobbing = false;

            var splitGo = new GameObject("ViewLayout");
            var split = splitGo.AddComponent<SplitViewController>();
            split.mainCamera = cam;
            split.twinCamera = twinCam;
            split.layout = SplitViewController.Layout.Split;
            split.mainWidthRatio = 0.68f;

            // ---- 空间配准（VR 阶段用，先留位）----
            var calib = camGo.AddComponent<SpatialCalibrator>();
            calib.headCamera = camGo.transform;
            calib.boatRoot = boat.transform;

            // ---- 量化反馈 HUD ----
            var hudGo = new GameObject("RideHud");
            var hud = hudGo.AddComponent<RideHud>();
            hud.paddle = hydro;
            hud.boat = buoy;
            hud.bridge = bridge;
            hud.driver = driver;
            hud.cable = cable;
            hud.stewart = stewart;

            // ---- 水声、划桨声和岸上环境声（程序化未实测样机，可替换素材） ----
            var audioGo = new GameObject("RiverAudio");
            audioGo.AddComponent<RiverAudioController>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            ConfigureBuildSettings();
            Debug.Log("[DragonBoatVR] 演示场景已生成: " + ScenePath);
        }

        static void ConfigureBuildSettings()
        {
            var sceneGuid = AssetDatabase.AssetPathToGUID(ScenePath);
            if (string.IsNullOrEmpty(sceneGuid))
            {
                Debug.LogWarning("[DragonBoatVR] 无法为演示场景生成 Build Settings 条目: " + ScenePath);
                return;
            }

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true),
            };
            Debug.Log("[DragonBoatVR] Build Settings 已加入可启动场景: " + ScenePath);
        }

        /// <summary>
        /// 船体：GameObject 的 Scale 必须是 1。
        /// 因为浮力采样点写的是"船体本地坐标"，而 TransformPoint 会乘上 Scale。
        /// 若按规划表的写法把 Scale 设成 (1.2,0.6,8)，采样点会被放大成 (−0.6,−1.8,24)，
        /// 直接飞出船外几十米，浮力全乱 —— 这是最容易踩的坑。
        /// 正确做法：Scale=1，尺寸交给 BoxCollider.size 和视觉子物体。
        /// </summary>
        static GameObject BuildBoat(out BoatBuoyancy buoy, out Rigidbody rb)
        {
            var boat = new GameObject("Boat");
            boat.transform.rotation = Quaternion.identity;

            var box = boat.AddComponent<BoxCollider>();
            box.size = new Vector3(BoatWidth, BoatHeight, BoatLength);
            box.center = Vector3.zero;

            rb = boat.AddComponent<Rigidbody>();
            rb.mass = BoatMass;
            rb.drag = 0.05f;              // 基准表：空气/基础阻力交给物性，水阻由脚本算
            rb.angularDrag = 0.5f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            buoy = boat.AddComponent<BoatBuoyancy>();

            // 湿面积按 3m 级等效小船写死（约 2.1m²），不沿用大龙舟的 17m²。
            // 这个值直接决定水阻大小，写错会让"推力 15N、船速 0.1m/s"这种怪现象出现。
            buoy.wetAreaPerPoint = 0.35f;

            var hullMat = MakeLitMaterial("BoatHull", new Color(0.30f, 0.055f, 0.045f));
            var hull = new GameObject("HullVisual");
            hull.transform.SetParent(boat.transform, false);
            hull.AddComponent<MeshFilter>().sharedMesh = CreateHullMesh();
            hull.AddComponent<MeshRenderer>().sharedMaterial = hullMat;

            var trimMat = MakeLitMaterial("BoatTrim", new Color(0.68f, 0.39f, 0.12f));
            AddGunwaleTrim(boat.transform, trimMat);

            var interior = MakeLitMaterial("BoatInterior", new Color(0.17f, 0.08f, 0.055f));
            CreateBoatShape(boat.transform, PrimitiveType.Cube, "InnerFloor", new Vector3(0f, -0.19f, 0f),
                new Vector3(0.72f, 0.06f, BoatLength - 1.0f), interior, Quaternion.identity);
            CreateBoatShape(boat.transform, PrimitiveType.Cube, "SingleSeat", new Vector3(0f, 0.06f, -0.35f),
                new Vector3(0.53f, 0.12f, 0.42f), interior, Quaternion.identity);

            var headMat = MakeLitMaterial("BoatHead", new Color(0.64f, 0.37f, 0.12f));
            var darkMat = MakeLitMaterial("BoatHeadDetails", new Color(0.12f, 0.045f, 0.025f));
            var head = new GameObject("DragonHead");
            head.transform.SetParent(boat.transform, false);
            head.transform.localPosition = new Vector3(0f, 0.31f, BoatLength * 0.5f - 0.25f);
            CreateBoatShape(head.transform, PrimitiveType.Sphere, "Crown", new Vector3(0f, 0.12f, 0.03f),
                new Vector3(0.55f, 0.45f, 0.56f), headMat, Quaternion.identity);
            CreateBoatShape(head.transform, PrimitiveType.Sphere, "Muzzle", new Vector3(0f, -0.04f, 0.34f),
                new Vector3(0.42f, 0.23f, 0.48f), headMat, Quaternion.identity);
            CreateBoatShape(head.transform, PrimitiveType.Sphere, "LowerJaw", new Vector3(0f, -0.17f, 0.34f),
                new Vector3(0.34f, 0.11f, 0.38f), headMat, Quaternion.identity);
            CreateBoatShape(head.transform, PrimitiveType.Cube, "MouthLine", new Vector3(0f, -0.17f, 0.57f),
                new Vector3(0.22f, 0.018f, 0.025f), darkMat, Quaternion.identity);
            for (int side = -1; side <= 1; side += 2)
            {
                CreateBoatShape(head.transform, PrimitiveType.Sphere, "EyeSocket", new Vector3(side * 0.205f, 0.13f, 0.27f),
                    new Vector3(0.13f, 0.15f, 0.12f), darkMat, Quaternion.identity);
                CreateBoatShape(head.transform, PrimitiveType.Sphere, "Eye", new Vector3(side * 0.215f, 0.14f, 0.33f),
                    new Vector3(0.072f, 0.083f, 0.07f), trimMat, Quaternion.identity);
                CreateBoatShape(head.transform, PrimitiveType.Sphere, "Nostril", new Vector3(side * 0.082f, -0.035f, 0.545f),
                    new Vector3(0.035f, 0.026f, 0.025f), darkMat, Quaternion.identity);
                CreateBoatShape(head.transform, PrimitiveType.Capsule, "Horn", new Vector3(side * 0.18f, 0.36f, 0.02f),
                    new Vector3(0.10f, 0.26f, 0.10f), headMat, Quaternion.Euler(0f, 0f, -side * 24f));
            }

            CreateBoatShape(boat.transform, PrimitiveType.Capsule, "DragonTail",
                new Vector3(0f, 0.20f, -BoatLength * 0.5f + 0.16f), new Vector3(0.23f, 0.28f, 0.38f),
                headMat, Quaternion.Euler(-22f, 0f, 0f));

            return boat;
        }

        static Mesh CreateHullMesh()
        {
            const int rings = 49;
            const int sections = 7;
            float[] across = { -1f, -0.84f, -0.55f, 0f, 0.55f, 0.84f, 1f };
            float[] height = { 0.18f, 0.01f, -0.17f, -0.28f, -0.17f, 0.01f, 0.18f };
            var vertices = new Vector3[rings * sections];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[(rings - 1) * (sections - 1) * 6];
            for (int r = 0; r < rings; r++)
            {
                float t = r / (float)(rings - 1);
                float z = Mathf.Lerp(-BoatLength * 0.5f, BoatLength * 0.5f, t);
                float centreT = 1f - Mathf.Clamp01(Mathf.Abs(z) / (BoatLength * 0.5f));
                float width = BoatWidth * 0.5f * Mathf.Lerp(0.12f, 1f, Mathf.SmoothStep(0f, 1f, centreT));
                float bowLift = Mathf.Clamp01((z - (BoatLength * 0.5f - 1.8f)) / 1.8f) * 0.12f;
                for (int s = 0; s < sections; s++)
                {
                    int i = r * sections + s;
                    vertices[i] = new Vector3(across[s] * width, height[s] + bowLift, z);
                    uv[i] = new Vector2(s / (float)(sections - 1), t);
                }
            }
            for (int r = 0, i = 0; r < rings - 1; r++)
                for (int s = 0; s < sections - 1; s++, i += 6)
                {
                    int a = r * sections + s;
                    int b = a + 1;
                    int c = a + sections;
                    int d = c + 1;
                    triangles[i] = a; triangles[i + 1] = b; triangles[i + 2] = c;
                    triangles[i + 3] = b; triangles[i + 4] = d; triangles[i + 5] = c;
                }
            var mesh = new Mesh { name = "DragonBoatHullPrototype" };
            mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        static void AddGunwaleTrim(Transform boat, Material material)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                var go = new GameObject(side < 0 ? "GunwaleTrimPort" : "GunwaleTrimStarboard");
                go.transform.SetParent(boat, false);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false; line.positionCount = 41; line.widthMultiplier = 0.025f;
                line.sharedMaterial = material; line.numCapVertices = 3; line.numCornerVertices = 3;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                for (int i = 0; i < line.positionCount; i++)
                {
                    float z = Mathf.Lerp(-BoatLength * 0.47f, BoatLength * 0.45f, i / 40f);
                    float taper = Mathf.Lerp(0.12f, 1f,
                        Mathf.SmoothStep(0f, 1f, 1f - Mathf.Abs(z) / (BoatLength * 0.5f)));
                    line.SetPosition(i, new Vector3(side * BoatWidth * 0.5f * taper, 0.19f, z));
                }
            }
        }

        static GameObject CreateBoatShape(Transform parent, PrimitiveType type, string name,
            Vector3 position, Vector3 scale, Material material, Quaternion rotation)
        {
            var shape = GameObject.CreatePrimitive(type);
            shape.name = name;
            shape.transform.SetParent(parent, false);
            shape.transform.localPosition = position;
            shape.transform.localRotation = rotation;
            shape.transform.localScale = scale;
            Object.DestroyImmediate(shape.GetComponent<Collider>());
            shape.GetComponent<MeshRenderer>().sharedMaterial = material;
            return shape;
        }

        /// <summary>
        /// 桨：原点必须是桨叶中心（水动力采样约定），桨杆沿 local +Y，桨叶面法线 local +Z。
        /// 初始桨叶中心与虚拟划桨相位 0 一致；船舷支点约 (0.6, 0.2, 0)，
        /// 按当前桨叶边缘间隙，桨叶横向约 0.76m。
        /// 这些是可替换样机值，未实测。
        /// </summary>
        static GameObject BuildPaddle(BoatBuoyancy boat, out PaddleHydrodynamics hydro)
        {
            var paddle = new GameObject("Paddle");
            // These are provisional, unmeasured prototype dimensions. Keep the
            // object unparented: incoming external/UDP poses are world poses and
            // must not be moved a second time by the boat transform.
            Vector3 localBlade = new Vector3(0.76f, 0.45f, 0.45f);
            paddle.transform.position = boat.transform.TransformPoint(localBlade);

            hydro = paddle.AddComponent<PaddleHydrodynamics>();
            hydro.targetBoat = boat;
            hydro.applyPropulsion = true;

            // Keep the imported wood maps and their color-space/UV
            // settings when rebuilding the scene; plain colors are a fallback.
            var shaftMat = AssetDatabase.LoadAssetAtPath<Material>($"{SettingsDir}/PaddleShaft.mat");
            if (shaftMat == null)
                shaftMat = MakeLitMaterial("PaddleShaft", new Color(0.40f, 0.24f, 0.12f));
            var bladeMat = AssetDatabase.LoadAssetAtPath<Material>($"{SettingsDir}/PaddleBlade.mat");
            if (bladeMat == null)
                bladeMat = MakeLitMaterial("PaddleBlade", new Color(0.48f, 0.17f, 0.075f));

            // 桨杆：从桨叶中心向上
            CreateBoatShape(paddle.transform, PrimitiveType.Cylinder, "Shaft", new Vector3(0f, 0.50f, 0f),
                new Vector3(0.045f, 0.50f, 0.045f), shaftMat, Quaternion.identity);

            // 桨叶：原点就在这里，桨面法线 = +Z
            CreateBoatShape(paddle.transform, PrimitiveType.Capsule, "Blade", Vector3.zero,
                new Vector3(hydro.BladeWidth, hydro.bladeLength, hydro.bladeThickness), bladeMat, Quaternion.identity);

            return paddle;
        }
    }
}
