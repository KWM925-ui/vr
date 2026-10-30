using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 橘子洲与顺德逢简水乡的轻量场景生成器。
    ///
    /// 场景只提供自由划行所需的河道与岸景，不含比赛浮标或门架。
    /// 河宽、岸线、建筑与地标摆放都是可替换样机值，尚未实测。
    ///
    /// 为什么全部用程序化合并网格：
    ///   两岸要铺 600m，逐棵树、逐根栏杆建对象会有四五百个 GameObject，
    ///   在 VR 里光 Draw Call 就把帧率吃掉一半。这里按材质各合并成一个 Mesh，
    ///   最终只有 10 个左右渲染对象，顶点数不到 5 万。
    ///
    /// 所有景观网格按材质合并，减少渲染对象数量。
    /// </summary>
    public static class RiverCourseBuilder
    {
        public const float RiverHalfWidth = 32f;    // 样机河道半宽（m），未实测
        public const float CourseLength = 600f;     // 样机景观长度（m），未实测
        public const float ZStart = -100f;          // 样机景观起点（m），未实测
        public const float BankTop = 2.25f;         // 样机岸顶高度（m），未实测

        const float BankWidth = 8f;                 // 样机值，未实测
        const float ZCenter = ZStart + CourseLength * 0.5f;
        const string MaoPhotoPath = "Assets/VisualReferences/Juzizhou_Mao_Statue_Cutout.png";

        // 网格缓存：CreatePrimitive 的内置网格是共享资源，取完立刻销毁临时对象即可
        static Mesh _cube, _cylinder, _sphere;

        /// <summary>入口：生成整条河道。由 SetupWizard 在场景生成后调用。</summary>
        public static GameObject Build(Transform parent)
            => Build(parent, RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland);

        public static GameObject Build(Transform parent, RiverEnvironmentController.SceneProfile profile)
        {
            if (!System.Enum.IsDefined(typeof(RiverEnvironmentController.SceneProfile), profile))
                throw new System.ArgumentOutOfRangeException(nameof(profile));

            bool orangeIsland = profile == RiverEnvironmentController.SceneProfile.XiangjiangOrangeIsland;
            string prefix = orangeIsland ? "Xiangjiang" : "Fengjian";
            var root = new GameObject(orangeIsland ? "RiverCourse_XiangjiangOrangeIsland" : "RiverCourse_ShunDeFengjian");
            if (parent != null) root.transform.SetParent(parent, false);

            var cube = GetPrimitive(PrimitiveType.Cube, ref _cube);
            var cyl = GetPrimitive(PrimitiveType.Cylinder, ref _cylinder);
            var sph = GetPrimitive(PrimitiveType.Sphere, ref _sphere);

            var matBank = Mat(prefix + "RiverBankStone", new Color(0.43f, 0.43f, 0.40f), 0.16f);
            var matWalk = Mat(prefix + "RiverWalkway", new Color(0.62f, 0.60f, 0.55f), 0.20f);
            var matRail = Mat(prefix + "BridgeRail", new Color(0.23f, 0.25f, 0.24f), 0.25f);
            var matTrunk = Mat(prefix + "TreeTrunk", new Color(0.22f, 0.16f, 0.12f), 0.32f);
            var matLeaf = Mat(prefix + "TreeCanopy", new Color(0.16f, 0.27f, 0.17f), 0.28f);
            var matLeaf2 = Mat(prefix + "TreeCanopyLight", new Color(0.23f, 0.34f, 0.21f), 0.28f);
            var matWall = Mat(prefix + "HouseWall", new Color(0.72f, 0.70f, 0.65f), 0.14f);
            var matRoof = Mat(prefix + "HouseRoof", new Color(0.20f, 0.22f, 0.22f), 0.18f);
            var matWindow = Mat(prefix + "HouseWindow", new Color(0.12f, 0.19f, 0.19f), 0.32f);
            var matWood = Mat(prefix + "HouseWood", new Color(0.29f, 0.18f, 0.11f), 0.22f);
            var matCity = Mat(prefix + "CityFacade", new Color(0.48f, 0.51f, 0.50f), 0.18f);
            var matGlass = Mat(prefix + "CityWindows", new Color(0.19f, 0.27f, 0.30f), 0.42f);
            var matHill = Mat(prefix + "FarHill", new Color(0.28f, 0.37f, 0.32f), 0.12f);
            var matSpectator = Mat(prefix + "SpectatorClothes", new Color(0.24f, 0.31f, 0.30f), 0.14f);
            var matSpectatorLegs = Mat(prefix + "SpectatorLegs", new Color(0.13f, 0.15f, 0.15f), 0.12f);
            var matSpectatorSkin = Mat(prefix + "SpectatorSkin", new Color(0.52f, 0.36f, 0.27f), 0.12f);
            var matBackgroundBoat = Mat(prefix + "BackgroundBoatHull", new Color(0.20f, 0.17f, 0.13f), 0.20f);
            var matBackgroundCabin = Mat(prefix + "BackgroundBoatCabin", new Color(0.48f, 0.43f, 0.34f), 0.18f);

            var bank = new List<CombineInstance>();
            var walk = new List<CombineInstance>();
            var rail = new List<CombineInstance>();
            var trunk = new List<CombineInstance>();
            var leaf = new List<CombineInstance>();
            var leaf2 = new List<CombineInstance>();
            var wall = new List<CombineInstance>();
            var roof = new List<CombineInstance>();
            var windows = new List<CombineInstance>();
            var doors = new List<CombineInstance>();
            var city = new List<CombineInstance>();
            var glass = new List<CombineInstance>();
            var hill = new List<CombineInstance>();
            var spectatorBodies = new List<CombineInstance>();
            var spectatorHeads = new List<CombineInstance>();
            var spectatorLegs = new List<CombineInstance>();
            var backgroundBoatHulls = new List<CombineInstance>();
            var backgroundBoatCabins = new List<CombineInstance>();

            // ---------------- 两岸堤身 + 堤顶步道 ----------------
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * (RiverHalfWidth + BankWidth * 0.5f);
                AddBox(bank, cube, new Vector3(x, BankTop * 0.5f, ZCenter),
                       new Vector3(BankWidth, BankTop, CourseLength));
                AddBox(walk, cube, new Vector3(x, BankTop + 0.07f, ZCenter),
                       new Vector3(BankWidth - 1.2f, 0.14f, CourseLength));
            }

            // ---------------- 榕树（水乡标志）----------------
            // 交替排列，树冠做两层不同深浅，近看有层次
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * (RiverHalfWidth + 6.2f);
                int idx = 0;
                for (float z = ZStart + 11f; z < ZStart + CourseLength; z += 17f, idx++)
                {
                    // 用确定性伪随机让高矮粗细有变化，但每次生成结果一致
                    float jitter = Mathf.Sin(z * 1.37f + side * 2.1f);
                    float h = 4.6f + jitter * 0.9f;
                    float baseY = BankTop;
                    AddCylinder(trunk, cyl, new Vector3(x, baseY + h * 0.5f, z),
                                new Vector3(0.55f, h * 0.5f, 0.55f));

                    var c1 = new Vector3(x, baseY + h + 0.9f, z);
                    AddSphere(idx % 2 == 0 ? leaf : leaf2, sph, c1,
                              new Vector3(4.6f, 3.4f, 4.6f));
                    AddSphere(idx % 2 == 0 ? leaf2 : leaf, sph, c1 + new Vector3(1.5f, -0.9f, 1.0f),
                              new Vector3(3.2f, 2.5f, 3.2f));
                    AddSphere(leaf, sph, c1 + new Vector3(-1.4f, -0.6f, -1.2f),
                              new Vector3(2.9f, 2.2f, 2.9f));
                }
            }

            // ---------------- 白墙灰瓦民居 ----------------
            if (!orangeIsland)
            {
            for (int side = -1; side <= 1; side += 2)
            {
                float xBase = side * (RiverHalfWidth + 17f);
                int idx = 0;
                for (float z = ZStart + 6f; z < ZStart + CourseLength; z += 24f, idx++)
                {
                    float jitter = Mathf.Cos(z * 0.71f + side);
                    float w = 9f + jitter * 2.0f;      // 沿河长度
                    float d = 8f + jitter * 1.5f;      // 进深
                    float h = 6.5f + ((idx % 3) - 1) * 1.8f;
                    float x = xBase + side * (idx % 2) * 3.5f;

                    AddBox(wall, cube, new Vector3(x, h * 0.5f, z), new Vector3(d, h, w));

                    // 人字坡屋顶：两块斜板对拼，比平顶更像水乡民居
                    float slope = 26f;
                    float halfD = d * 0.5f + 0.5f;
                    float roofLen = halfD / Mathf.Cos(slope * Mathf.Deg2Rad) + 0.3f;
                    float roofThick = 0.32f;
                    Vector3 ridge = new Vector3(x, h + halfD * Mathf.Tan(slope * Mathf.Deg2Rad), z);

                    AddBox(roof, cube, ridge + new Vector3(-halfD * 0.5f, -0f, 0f),
                           new Vector3(roofLen, roofThick, w + 1.2f),
                           Quaternion.Euler(0f, 0f, -slope));
                    AddBox(roof, cube, ridge + new Vector3(halfD * 0.5f, -0f, 0f),
                           new Vector3(roofLen, roofThick, w + 1.2f),
                           Quaternion.Euler(0f, 0f, slope));

                    // 临河一面加个挑檐，遮住墙顶接缝
                    AddBox(roof, cube, new Vector3(x - side * (d * 0.5f + 0.35f), h - 0.15f, z),
                           new Vector3(0.9f, 0.18f, w + 1.6f), Quaternion.Euler(0f, 0f, -side * 12f));

                    float frontX = x - side * (d * 0.5f + 0.055f);
                    AddBox(doors, cube, new Vector3(frontX, 1.0f, z), new Vector3(0.10f, 1.9f, 0.88f));
                    for (int window = -1; window <= 1; window += 2)
                        AddBox(windows, cube, new Vector3(frontX, 2.5f, z + window * w * 0.27f),
                            new Vector3(0.07f, 1.05f, 0.92f));
                }
            }
            }
            else BuildXiangjiangSkyline(cube, city, glass);

            // ---------------- 日常岸边活动与远处背景船 ----------------
            // Static shore visitors and distant boats remain visual-only.
            BuildEverydayRiverLife(cube, sph, spectatorBodies, spectatorHeads, spectatorLegs,
                backgroundBoatHulls, backgroundBoatCabins);

            if (!orangeIsland)
            {
                BuildArchBridge(cube, bank, rail, 150f, RiverHalfWidth);
                BuildArchBridge(cube, bank, rail, 380f, RiverHalfWidth);
            }

            // ---------------- 远山（雾里若隐若现，补天际线）----------------
            for (int i = 0; i < 9; i++)
            {
                float t = i / 8f;
                float z = ZStart + t * CourseLength;
                float h = 16f + Mathf.Sin(i * 2.3f) * 7f;
                AddSphere(hill, sph, new Vector3(-150f - Mathf.Abs(Mathf.Sin(i * 1.1f)) * 40f, -2f, z),
                          new Vector3(120f, h, 90f));
                AddSphere(hill, sph, new Vector3(150f + Mathf.Abs(Mathf.Cos(i * 1.7f)) * 40f, -2f, z),
                          new Vector3(120f, h * 0.9f, 90f));
            }

            // ---------------- 合并输出 ----------------
            Combine(root.transform, "Bank", bank, matBank);
            Combine(root.transform, "Walkway", walk, matWalk);
            Combine(root.transform, "Railing", rail, matRail);
            Combine(root.transform, "TreeTrunks", trunk, matTrunk);
            Combine(root.transform, "TreeCanopyA", leaf, matLeaf);
            Combine(root.transform, "TreeCanopyB", leaf2, matLeaf2);
            Combine(root.transform, "HouseWalls", wall, matWall);
            Combine(root.transform, "HouseRoofs", roof, matRoof);
            Combine(root.transform, "HouseWindows", windows, matWindow);
            Combine(root.transform, "HouseDoors", doors, matWood);
            Combine(root.transform, "XiangjiangCity", city, matCity);
            Combine(root.transform, "XiangjiangWindows", glass, matGlass);
            Combine(root.transform, "FarHills", hill, matHill);
            EnsureShoreGround(root.transform);
            Combine(root.transform, "SpectatorBodies", spectatorBodies, matSpectator);
            Combine(root.transform, "SpectatorHeads", spectatorHeads, matSpectatorSkin);
            Combine(root.transform, "SpectatorLegs", spectatorLegs, matSpectatorLegs);
            Combine(root.transform, "BackgroundBoatHulls", backgroundBoatHulls, matBackgroundBoat);
            Combine(root.transform, "BackgroundBoatCabins", backgroundBoatCabins, matBackgroundCabin);

            if (orangeIsland) BuildMaoPhotoLandmark(root.transform);
            Debug.Log($"[DragonBoatVR] {prefix} river scene generated: sample landscape length={CourseLength}m (unmeasured), " +
                      $"spectators=12, background boats=3, houses={wall.Count}");
            return root;
        }

        static void BuildEverydayRiverLife(Mesh cube, Mesh sphere,
            List<CombineInstance> spectatorBodies, List<CombineInstance> spectatorHeads,
            List<CombineInstance> spectatorLegs, List<CombineInstance> backgroundBoatHulls,
            List<CombineInstance> backgroundBoatCabins)
        {
            // Two small groups on each bank. Walkway top is BankTop + 0.14m;
            // keeping them behind the rail leaves the water edge and course clear.
            float[] groupZ = { 35f, 315f };
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * (RiverHalfWidth + 5.2f);
                Quaternion facing = Quaternion.Euler(0f, side > 0 ? -90f : 90f, 0f);
                for (int group = 0; group < groupZ.Length; group++)
                    for (int person = 0; person < 3; person++)
                    {
                        float z = groupZ[group] + (person - 1) * 1.0f;
                        float baseY = BankTop + 0.14f;
                        Vector3 feet = new Vector3(x, baseY, z);
                        Vector3 leftLeg = feet + facing * new Vector3(-0.09f, 0.31f, 0f);
                        Vector3 rightLeg = feet + facing * new Vector3(0.09f, 0.31f, 0f);
                        Vector3 body = feet + Vector3.up * 0.91f;
                        Vector3 head = feet + Vector3.up * 1.48f;
                        spectatorLegs.Add(new CombineInstance { mesh = cube,
                            transform = Matrix4x4.TRS(leftLeg, facing, new Vector3(0.12f, 0.62f, 0.16f)) });
                        spectatorLegs.Add(new CombineInstance { mesh = cube,
                            transform = Matrix4x4.TRS(rightLeg, facing, new Vector3(0.12f, 0.62f, 0.16f)) });
                        spectatorBodies.Add(new CombineInstance { mesh = cube,
                            transform = Matrix4x4.TRS(body, facing, new Vector3(0.38f, 0.70f, 0.24f)) });
                        spectatorHeads.Add(new CombineInstance { mesh = sphere,
                            transform = Matrix4x4.TRS(head, Quaternion.identity, new Vector3(0.27f, 0.30f, 0.27f)) });
                    }
            }

            // Small scenery boats remain well outside the paddling line. They are
            // static visual background only; no collider or synthetic propulsion.
            Vector2[] boatXZ = { new Vector2(-22f, 145f), new Vector2(23f, 300f), new Vector2(-20f, 445f) };
            for (int i = 0; i < boatXZ.Length; i++)
            {
                Vector3 centre = new Vector3(boatXZ[i].x, 0.02f, boatXZ[i].y);
                float yaw = (i % 2 == 0 ? 1f : -1f) * 8f;
                Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
                backgroundBoatHulls.Add(new CombineInstance { mesh = sphere,
                    transform = Matrix4x4.TRS(centre, rotation, new Vector3(0.9f, 0.18f, 3.2f)) });
                backgroundBoatCabins.Add(new CombineInstance { mesh = cube,
                    transform = Matrix4x4.TRS(centre + Vector3.up * 0.36f, rotation, new Vector3(0.78f, 0.54f, 1.05f)) });
            }
        }

        static void BuildXiangjiangSkyline(Mesh cube, List<CombineInstance> facades, List<CombineInstance> windows)
        {
            for (int i = 0; i < 9; i++)
            {
                float z = ZStart + 25f + i * 63f;
                float height = 28f + (i % 4) * 8f + Mathf.Sin(i * 1.7f) * 3f;
                float width = 19f + (i % 3) * 4f;
                float depth = 12f + (i % 2) * 3f;
                float x = RiverHalfWidth + 56f + (i % 2) * 15f;
                AddBox(facades, cube, new Vector3(x, height * 0.5f, z), new Vector3(depth, height, width));
                for (float y = 3.2f; y < height - 2f; y += 4.2f)
                    AddBox(windows, cube, new Vector3(x - depth * 0.5f - 0.025f, y, z),
                        new Vector3(0.05f, 1.25f, width * 0.72f));
            }
        }

        static void BuildMaoPhotoLandmark(Transform parent)
        {
            Texture2D photo = AssetDatabase.LoadAssetAtPath<Texture2D>(MaoPhotoPath);
            if (photo == null)
            {
                Debug.LogError("[DragonBoatVR] Missing Orange Isle statue photo: " + MaoPhotoPath);
                return;
            }

            const string materialPath = "Assets/Settings/OrangeIsleStatueLandmark.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            if (material.shader != shader) material.shader = shader;
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", photo);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            material.mainTexture = photo;
            EditorUtility.SetDirty(material);

            // Photo landmark proxy; placement and display scale are sample values.
            var landmark = GameObject.CreatePrimitive(PrimitiveType.Quad);
            landmark.name = "MaoZedongStatue";
            landmark.transform.SetParent(parent, false);
            landmark.transform.localPosition = new Vector3(-52f, 16f, 150f);
            Vector3 localNormal = landmark.GetComponent<MeshFilter>().sharedMesh.normals[0];
            Vector3 towardRiverStart = new Vector3(-landmark.transform.localPosition.x, 0f,
                -landmark.transform.localPosition.z).normalized;
            landmark.transform.localRotation = Quaternion.FromToRotation(localNormal, towardRiverStart);
            landmark.transform.localScale = new Vector3(48f, 32f, 1f);
            Object.DestroyImmediate(landmark.GetComponent<Collider>());
            var renderer = landmark.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        // ---------------- 桥 ----------------
        static void BuildArchBridge(Mesh cube, List<CombineInstance> bank, List<CombineInstance> rail,
                                    float z, float halfWidth)
        {
            const int seg = 9;
            float span = halfWidth * 2f + 8f;      // 桥跨比河宽略长，落点在堤上
            float rise = 4.2f;
            float deckW = 5.0f;                    // 桥面宽（沿河方向）
            float deckThick = 0.5f;

            // 桥面用折线逼近拱形：每段一小块板，端点按抛物线抬升
            for (int i = 0; i < seg; i++)
            {
                float u0 = i / (float)seg;
                float u1 = (i + 1) / (float)seg;
                float x0 = -span * 0.5f + span * u0;
                float x1 = -span * 0.5f + span * u1;
                float y0 = BankTop + rise * 4f * u0 * (1f - u0);
                float y1 = BankTop + rise * 4f * u1 * (1f - u1);

                Vector3 a = new Vector3(x0, y0, z);
                Vector3 b = new Vector3(x1, y1, z);
                Vector3 mid = (a + b) * 0.5f;
                float len = Vector3.Distance(a, b);
                float ang = Mathf.Atan2(y1 - y0, x1 - x0) * Mathf.Rad2Deg;

                AddBox(bank, cube, mid, new Vector3(len + 0.05f, deckThick, deckW),
                       Quaternion.Euler(0f, 0f, ang));

                // 桥栏
                AddBox(rail, cube, mid + Vector3.up * 0.9f, new Vector3(len + 0.05f, 0.12f, 0.12f),
                       Quaternion.Euler(0f, 0f, ang));
                AddBox(rail, cube, mid + Vector3.up * 0.9f + Vector3.forward * (deckW * 0.5f - 0.2f),
                       new Vector3(len + 0.05f, 0.12f, 0.12f), Quaternion.Euler(0f, 0f, ang));
                AddBox(rail, cube, mid + Vector3.up * 0.9f - Vector3.forward * (deckW * 0.5f - 0.2f),
                       new Vector3(len + 0.05f, 0.12f, 0.12f), Quaternion.Euler(0f, 0f, ang));
            }

            // 两个桥墩（落在水里，遮住拱脚）
            for (int side = -1; side <= 1; side += 2)
            {
                AddBox(bank, cube, new Vector3(side * halfWidth * 0.55f, 0.4f, z),
                       new Vector3(2.2f, 3.2f, deckW + 0.6f));
            }
        }

        // ---------------- 合并工具 ----------------
        static void AddBox(List<CombineInstance> list, Mesh mesh, Vector3 pos, Vector3 size)
        {
            AddBox(list, mesh, pos, size, Quaternion.identity);
        }

        static void AddBox(List<CombineInstance> list, Mesh mesh, Vector3 pos, Vector3 size, Quaternion rot)
        {
            list.Add(new CombineInstance
            {
                mesh = mesh,
                transform = Matrix4x4.TRS(pos, rot, size)
            });
        }

        static void AddCylinder(List<CombineInstance> list, Mesh mesh, Vector3 pos, Vector3 size)
        {
            // Unity 内置圆柱高度为 2，scale.y 直接就是半高
            list.Add(new CombineInstance
            {
                mesh = mesh,
                transform = Matrix4x4.TRS(pos, Quaternion.identity, size)
            });
        }

        static void AddSphere(List<CombineInstance> list, Mesh mesh, Vector3 pos, Vector3 size)
        {
            list.Add(new CombineInstance
            {
                mesh = mesh,
                transform = Matrix4x4.TRS(pos, Quaternion.identity, size)
            });
        }

        public static bool EnsureShoreGround(Transform root)
        {
            if (root == null || root.Find("ShoreGround") != null) return false;
            var bank = root.Find("Bank")?.GetComponent<MeshFilter>();
            var walkway = root.Find("Walkway")?.GetComponent<MeshFilter>();
            var hills = root.Find("FarHills")?.GetComponent<MeshFilter>();
            var material = bank != null ? bank.GetComponent<Renderer>()?.sharedMaterial : null;
            if (bank?.sharedMesh == null || walkway?.sharedMesh == null || hills?.sharedMesh == null || material == null)
                throw new System.InvalidOperationException("Original river geometry is required to support the shore.");
            Bounds bankBounds = BoundsInRoot(root, bank), hillBounds = BoundsInRoot(root, hills);
            float top = BoundsInRoot(root, walkway).min.y;
            float bottom = Mathf.Min(bankBounds.min.y, hillBounds.min.y);
            float firstZ = Mathf.Min(bankBounds.min.z, hillBounds.min.z);
            float lastZ = Mathf.Max(bankBounds.max.z, hillBounds.max.z);
            float leftWidth = bankBounds.min.x - hillBounds.min.x;
            float rightWidth = hillBounds.max.x - bankBounds.max.x;
            if (leftWidth <= 0f || rightWidth <= 0f || top <= bottom || lastZ <= firstZ)
                throw new System.InvalidOperationException("Invalid original shore envelope.");
            // The second tree row was planted beyond the narrow bank's outer
            // edge. Close that actual land gap and keep the original river face,
            // hill meshes, scenery placements and water geometry unchanged.
            var parts = new List<CombineInstance>();
            Mesh cube = GetPrimitive(PrimitiveType.Cube, ref _cube);
            AddBox(parts, cube, new Vector3((hillBounds.min.x + bankBounds.min.x) * 0.5f,
                (top + bottom) * 0.5f, (firstZ + lastZ) * 0.5f), new Vector3(leftWidth, top - bottom, lastZ - firstZ));
            AddBox(parts, cube, new Vector3((hillBounds.max.x + bankBounds.max.x) * 0.5f,
                (top + bottom) * 0.5f, (firstZ + lastZ) * 0.5f), new Vector3(rightWidth, top - bottom, lastZ - firstZ));
            Combine(root, "ShoreGround", parts, material);
            return true;
        }

        static Bounds BoundsInRoot(Transform root, MeshFilter filter)
        {
            Bounds source = filter.sharedMesh.bounds;
            Matrix4x4 matrix = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            Bounds result = new Bounds(matrix.MultiplyPoint3x4(source.center), Vector3.zero);
            for (int i = 0; i < 8; i++) result.Encapsulate(matrix.MultiplyPoint3x4(new Vector3(
                (i & 1) == 0 ? source.min.x : source.max.x,
                (i & 2) == 0 ? source.min.y : source.max.y,
                (i & 4) == 0 ? source.min.z : source.max.z)));
            return result;
        }

        public static bool PreserveSolidMesh(GameObject go)
        {
            if (go == null || !RiverEnvironmentController.SolidSceneMeshes.Contains(go.name)) return false;
            // Runtime collision and camera queries deliberately reuse these
            // authored meshes. Static batching replaces MeshFilter.sharedMesh
            // with a larger render batch (including other objects/submeshes).
            // Retain every other static flag and the original display geometry.
            var before = GameObjectUtility.GetStaticEditorFlags(go);
            var after = before & ~StaticEditorFlags.BatchingStatic;
            if (before == after) return false;
            GameObjectUtility.SetStaticEditorFlags(go, after);
            return true;
        }

        static void Combine(Transform parent, string name, List<CombineInstance> parts, Material mat)
        {
            if (parts == null || parts.Count == 0) return;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.isStatic = true;
            PreserveSolidMesh(go);

            var mesh = new Mesh { name = name };
            // 一律用 32 位索引。按零件数估算顶点数会算错（球体 515 顶点、圆柱 88，
            // 都不是立方体的 24），算小了会在 CombineMeshes 里直接抛异常炸掉整个生成流程。
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.CombineMeshes(parts.ToArray(), true, true);
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
        }

        static Mesh GetPrimitive(PrimitiveType type, ref Mesh cache)
        {
            if (cache != null) return cache;
            var go = GameObject.CreatePrimitive(type);
            cache = go.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(go);
            return cache;
        }

        static Material Mat(string name, Color color, float smoothness)
        {
            const string dir = "Assets/Settings";
            var path = $"{dir}/River_{name}.mat";
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
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", Mathf.Clamp01(smoothness));
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
