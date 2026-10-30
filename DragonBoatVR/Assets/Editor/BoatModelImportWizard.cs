using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 真龙舟模型导入(幂等,命令行可用):
    ///   1. 使用 Assets/Art/Boat 中持久保存的 OBJ、MTL 和贴图;
    ///   2. 实例化为 Boat 的子物体 RealDragonBoat:按 HeadYaw 对齐模型长轴
    ///      与工程 Z 轴(龙头朝 +Z),按旧 HullVisual 世界包围盒动态计算缩放与
    ///      对齐(长度对齐、XZ 中心对齐、底面 Y 对齐)——不依赖任何写死比例,
    ///      OBJ 导入器改了单位也自适配;
    ///   3. 停用旧基元和船舷线的 Renderer，保留船体碰撞与六点浮力;
    ///      根据新船宽设置手持桨工位，龙舟桨不使用桨锁;
    ///   4. 保存场景并退出(退出码 0/1)。
    /// 用法:Unity.exe -batchmode -projectPath ... -executeMethod DragonBoatVR.EditorTools.BoatModelImportWizard.Run
    /// </summary>
    public static class BoatModelImportWizard
    {
        const string DstDir = "Assets/Art/Boat";
        const string ScenePath = "Assets/Scenes/Demo.unity";
        const string InstanceName = "RealDragonBoat";

        /// 这份 OBJ 的长轴已经沿模型 Z 轴，龙头朝 +Z；保持零旋转，
        /// 否则船会横跨座舱相机并把第一人称画面完全遮住。
        const float HeadYaw = 0f;

        static readonly string[] DisableRender =
            { "HullVisual", "GunwaleTrimStarboard", "GunwaleTrimPort",
              "DragonHead", "DragonTail", "InnerFloor", "SingleSeat" };

        [MenuItem("DragonBoatVR/⑥ 真龙舟模型导入")]
        public static void Run()
        {
            bool revert = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-revert") >= 0;
            if (revert) { RevertToPrimitiveBoat(); return; }
            ImportModel();
        }

        /// <summary>
        /// 回退到旧程序化龙船:隐藏 RealDragonBoat(保留实例便于再换回),
        /// 重新启用旧基元渲染体。物理常量不动。
        /// </summary>
        static void RevertToPrimitiveBoat()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var boatGo = GameObject.Find("Boat");
            if (boatGo == null)
            { Debug.LogError("[BOATMODEL] 场景里没有 Boat"); EditorApplication.Exit(1); return; }
            var boat = boatGo.transform;

            int restored = 0;
            foreach (var n in DisableRender)
            {
                var tr = boat.Find(n);
                if (tr == null) continue;
                foreach (var r in tr.GetComponentsInChildren<Renderer>())
                    if (!r.enabled) { r.enabled = true; restored++; }
            }

            var real = boat.Find(InstanceName);
            if (real != null) real.gameObject.SetActive(false);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[BOATMODEL] 已回退旧龙船:恢复渲染体={restored}," +
                      $"{InstanceName} 隐藏保留");
            EditorApplication.Exit(0);
        }

        static void ImportModel()
        {
            // Project assets are the source of truth. Reopening this menu must
            // not depend on a deleted download directory or destroy materials
            // and their GUIDs before checking that a replacement model exists.
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(DstDir + "/DragonBoat.obj");
            if (model == null)
            { Debug.LogError("[BOATMODEL] OBJ 导入失败"); EditorApplication.Exit(1); return; }

            // ---- 2. 场景与实例化 ----
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var boatGo = GameObject.Find("Boat");
            if (boatGo == null)
            { Debug.LogError("[BOATMODEL] 场景里没有 Boat"); EditorApplication.Exit(1); return; }
            var boat = boatGo.transform;

            var old = boat.Find(InstanceName);
            if (old != null) Object.DestroyImmediate(old.gameObject);

            var inst = Object.Instantiate(model, boat);
            inst.name = InstanceName;

            var hullRef = boat.Find("HullVisual");
            Bounds refB = hullRef != null && hullRef.GetComponent<Renderer>() != null
                ? hullRef.GetComponent<Renderer>().bounds
                : new Bounds(boat.position, new Vector3(1.2f, 0.6f, 8f));

            // 姿态:先单位缩放置于船根,量旋转后的世界包围盒再算缩放,重对齐。
            inst.transform.SetParent(boat, false);
            inst.transform.SetPositionAndRotation(boat.position, Quaternion.Euler(0f, HeadYaw, 0f));
            inst.transform.localScale = Vector3.one;

            Bounds b = WorldBounds(inst);
            if (b.size.z <= 0.01f)
            { Debug.LogError("[BOATMODEL] 模型包围盒异常"); EditorApplication.Exit(1); return; }

            float scale = refB.size.z / b.size.z;
            inst.transform.localScale = Vector3.one * scale;

            b = WorldBounds(inst);
            Vector3 pos = inst.transform.position;
            pos.x += refB.center.x - b.center.x;
            pos.z += refB.center.z - b.center.z;
            pos.y += refB.min.y - b.min.y;
            inst.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, HeadYaw, 0f));

            // ---- 2b. 显式材质绑定 ----
            // Unity 的 OBJ 导入器在 materialImportMode 未提取时不绑 MTL,船会
            // 整体灰面(dd063e37 诊断)。这里按 MTL 直接建 URP Lit 材质:
            // map_Kd → _BaseMap,Kd → _BaseColor;材质存 DstDir/Mats 幂等复用。
            BindMaterialsFromMtl(inst);

            // ---- 3. 停用旧渲染体，包含船舷的 LineRenderer ----
            int disabled = 0;
            foreach (var n in DisableRender)
            {
                var tr = boat.Find(n);
                if (tr == null) continue;
                foreach (var r in tr.GetComponentsInChildren<Renderer>())
                    if (r.enabled) { r.enabled = false; disabled++; }
            }

            // ---- 3b. 按导入船宽设置手持桨工位 ----
            {
                Bounds nb = WorldBounds(inst);
                float halfBeam = Mathf.Max(Mathf.Abs(nb.extents.x), 0.5f);
                // PaddleDriver 在独立桨根节点上，工位值属于场景实例。
                var drivers = Object.FindObjectOfType<PaddleDriver>() != null
                    ? new[] { Object.FindObjectOfType<PaddleDriver>() } : new PaddleDriver[0];
                foreach (var d in drivers)
                {
                    var pv = d.pivotLocalPos;
                    if (Mathf.Abs(Mathf.Abs(pv.x) - (halfBeam - 0.06f)) > 0.01f)
                    {
                        pv.x = Mathf.Sign(pv.x == 0 ? 1 : pv.x) * (halfBeam - 0.06f);
                        d.pivotLocalPos = pv;
                        EditorUtility.SetDirty(d);
                        Debug.Log($"[BOATMODEL] 桨轴 pivotLocalPos.x 同步到 {pv.x:F3}");
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[BOATMODEL] 完成:scale={scale:F4} 对齐后尺寸={WorldBounds(inst).size} " +
                      $"停用渲染体={disabled};保留船体物理参数");
            EditorApplication.Exit(0);
        }

        static Bounds WorldBounds(GameObject root)
        {
            var result = new Bounds(); bool first = true;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (first) { result = r.bounds; first = false; }
                else result.Encapsulate(r.bounds);
            }
            return result;
        }

        /// <summary>MTL 解析:newmtl 名 → (map_Kd 文件名, Kd 颜色)</summary>
        static System.Collections.Generic.Dictionary<string, (string tex, Color kd)> ParseMtl(string path)
        {
            var map = new System.Collections.Generic.Dictionary<string, (string, Color)>();
            if (!File.Exists(path)) return map;
            string cur = null; var kd = Color.white; string tex = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("newmtl")) { if (cur != null) map[cur] = (tex, kd); cur = line.Substring(6).Trim(); kd = Color.white; tex = null; }
                else if (line.StartsWith("Kd") && cur != null)
                {
                    var p = line.Split(' '); if (p.Length >= 4)
                        kd = new Color(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                                       float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                                       float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture), 1f);
                }
                else if (line.StartsWith("map_Kd") && cur != null) tex = line.Substring(6).Trim();
            }
            if (cur != null) map[cur] = (tex, kd);
            return map;
        }

        /// <summary>OBJ 里 usemtl 的出现顺序(对应 submesh 顺序)</summary>
        static System.Collections.Generic.List<string> ParseUsemtlOrder(string path)
        {
            var order = new System.Collections.Generic.List<string>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("usemtl "))
                {
                    var n = line.Substring(7).Trim();
                    if (!order.Contains(n)) order.Add(n);
                }
            }
            return order;
        }

        static void BindMaterialsFromMtl(GameObject inst)
        {
            var mtlPath = DstDir + "/material.mtl";
            var pairs = ParseMtl(mtlPath);
            var order = ParseUsemtlOrder(DstDir + "/DragonBoat.obj");
            if (pairs.Count == 0) { Debug.LogWarning("[BOATMODEL] MTL 为空,跳过材质绑定"); return; }
            AssetDatabase.Refresh();
            Directory.CreateDirectory(DstDir + "/Mats");
            var lit = Shader.Find("Universal Render Pipeline/Lit");

            System.Func<string, Material> get = name =>
            {
                var assetPath = $"{DstDir}/Mats/{name}.mat";
                var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (existing != null) return existing;
                var m = new Material(lit);
                if (pairs.TryGetValue(name, out var info))
                {
                    if (!string.IsNullOrEmpty(info.tex))
                    {
                        var texPath = $"{DstDir}/{info.tex}";
                        var t2d = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                        if (t2d != null) m.SetTexture("_BaseMap", t2d);
                        else Debug.LogWarning($"[BOATMODEL] 贴图载入失败 {texPath}");
                    }
                    m.SetColor("_BaseColor", info.kd);
                    m.SetFloat("_Smoothness", 0.32f);
                    m.SetFloat("_Metallic", 0.05f);
                }
                AssetDatabase.CreateAsset(m, assetPath);
                return m;
            };

            int rebound = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            {
                var sm = r.sharedMaterials;
                if (sm.Length == 0) continue;
                var mats = new Material[sm.Length];
                for (int i = 0; i < sm.Length; i++)
                {
                    var name = i < order.Count ? order[i] : order.Count > 0 ? order[order.Count - 1] : pairs.Keys.First();
                    mats[i] = get(name);
                }
                r.sharedMaterials = mats;
                rebound++;
            }
            Debug.Log($"[BOATMODEL] 显式材质绑定完成:渲染体={rebound} 材质={pairs.Count}");
        }
    }
}
