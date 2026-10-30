using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 同源水面视觉 + 无限水面网格。
    ///
    /// 两件事：
    ///   1. 网格：运行时生成一张高细分平面。河道训练场景的最短波长约 3m，
    ///      网格间距要保持在半米左右，否则波峰会被拉成不自然的大折面。
    ///   2. 参数：把 CPU 侧 WaveField 的波形与时间逐帧下发给 Shader，
    ///      保证"画面上的浪"与"物理采样的浪"出自同一公式、同一时钟（红线：水面同源）。
    ///
    /// 跟随：把水面物体按网格步长量化后跟着船走，视觉上等效于无限水面，
    /// 且浪形在世界空间绝对固定（不会随相机漂移）。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class WaterSurface : MonoBehaviour
    {
        [Header("波形来源（默认场景唯一 WaveField）")]
        public WaveField waveField;

        [Header("网格（性能与精度的平衡点）")]
        [Tooltip("水面边长（米）。水面跟随船，不需要覆盖整条河道")]
        public float size = 220f;
        [Tooltip("每边分段数。220m/448段≈0.49m/格，能平滑表现 2.5m 级短波")]
        [Range(16, 512)] public int segments = 448;

        [Header("跟随（无限水面）")]
        [Tooltip("跟随目标，一般填船。留空则水面固定在原地")]
        public Transform followTarget;
        [Tooltip("静水面高度（世界 Y）")]
        public float baseHeight = 0f;

        [Header("材质参数名（与 shader 保持默认即可）")]
        public string timeProp = "_SimTime";
        public string wavesProp = "_Waves";
        public string intensityProp = "_Intensity";
        public string phaseProp = "_PhaseOffsets";
        public string steepnessProp = "_Steepness";

        static readonly int TimeId = Shader.PropertyToID("_SimTime");
        static readonly int WavesId = Shader.PropertyToID("_Waves");
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        static readonly int PhaseId = Shader.PropertyToID("_PhaseOffsets");
        static readonly int SteepnessId = Shader.PropertyToID("_Steepness");
        static readonly int HorizontalId = Shader.PropertyToID("_Horizontal");
        static readonly int WaveCountId = Shader.PropertyToID("_WaveCount");
        static readonly int InverseIterationsId = Shader.PropertyToID("_InverseIterations");
        static readonly int TrailTexId = Shader.PropertyToID("_FoamTrailTex");
        static readonly int TrailAnchorId = Shader.PropertyToID("_FoamTrailAnchor");
        static readonly int FoamDetailId = Shader.PropertyToID("_CrestFoamTexture");
        static readonly int FoamFlowId = Shader.PropertyToID("_FoamFlow");
        Texture2D _foamDetail;
        WaterInteractionVisuals _interactions;
        BoatInteriorWaterProfile _interior;

        Renderer _rend;
        MaterialPropertyBlock _mpb;
        readonly Vector4[] _waveVecs = new Vector4[MaxWaves];
        readonly float[] _phases = new float[MaxWaves];
        readonly float[] _steeps = new float[MaxWaves];
        Mesh _mesh;

        // Shader 数组长度上限（与 WaveFieldWater.shader 的 MAX_WAVES 一致）
        const int MaxWaves = 8;

        void Awake()
        {
            if (_rend == null) _rend = GetComponent<Renderer>();
            _mpb = new MaterialPropertyBlock();
            _foamDetail = Resources.Load<Texture2D>("Water/CrestFoam");
            if (waveField == null) waveField = WaveField.Instance;
            BuildMesh();
        }

        void Start()
        {
            if (waveField == null) waveField = WaveField.Instance;
            if (waveField == null) Debug.LogWarning("[WaterSurface] 没有 WaveField，水面将是平面。");
            if (followTarget == null)
            {
                var boat = FindObjectOfType<BoatBuoyancy>(true);
                if (boat != null) followTarget = boat.transform;
            }
        }

        /// <summary>
        /// 生成水面网格。必须是密集网格：Gerstner 的水平位移会"挤压"顶点，
        /// 段数不足时浪尖会被拉成锯齿，甚至出现反向面。
        /// </summary>
        public void BuildMesh()
        {
            var mf = GetComponent<MeshFilter>();
            if (mf == null) return;

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "WaveFieldWaterMesh" };
                _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                _mesh.MarkDynamic();
            }

            int seg = Mathf.Clamp(segments, 16, 512);
            int vCount = (seg + 1) * (seg + 1);
            var verts = new Vector3[vCount];
            var uvs = new Vector2[vCount];
            var tris = new int[seg * seg * 6];

            float half = size * 0.5f;
            float step = size / seg;

            for (int z = 0, i = 0; z <= seg; z++)
            {
                float fz = -half + z * step;
                for (int x = 0; x <= seg; x++, i++)
                {
                    float fx = -half + x * step;
                    verts[i] = new Vector3(fx, 0f, fz);
                    uvs[i] = new Vector2((float)x / seg, (float)z / seg);
                }
            }

            for (int z = 0, t = 0; z < seg; z++)
            {
                for (int x = 0; x < seg; x++, t += 6)
                {
                    int bl = z * (seg + 1) + x;
                    int br = bl + 1;
                    int tl = bl + seg + 1;
                    int tr = tl + 1;

                    tris[t + 0] = bl; tris[t + 1] = tl; tris[t + 2] = br;
                    tris[t + 3] = br; tris[t + 4] = tl; tris[t + 5] = tr;
                }
            }

            _mesh.Clear();
            _mesh.vertices = verts;
            _mesh.uv = uvs;
            _mesh.triangles = tris;
            _mesh.RecalculateNormals();   // 运行时被 shader 覆盖，但剔除/光照兜底需要
            _mesh.RecalculateBounds();
            mf.sharedMesh = _mesh;

            // 水面很大且会跟随移动，关掉剔除更稳（避免 bounds 边缘闪一下）
            if (_rend != null) _rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void LateUpdate()
        {
            if (_rend == null) _rend = GetComponent<Renderer>();
            if (_rend == null) return;

            // 跟随：按网格步长量化，避免顶点随相机连续滑动产生"爬行"感
            if (followTarget != null)
            {
                float snap = size / Mathf.Max(16, segments);
                Vector3 t = followTarget.position;
                transform.position = new Vector3(
                    Mathf.Round(t.x / snap) * snap,
                    baseHeight,
                    Mathf.Round(t.z / snap) * snap);
            }

            if (waveField == null) waveField = WaveField.Instance;
            if (waveField == null) return;

            if (_interior == null && followTarget != null)
            {
                var choice = followTarget.GetComponent<BoatModelChoice>();
                if (choice != null) _interior = new BoatInteriorWaterProfile(choice);
            }
            _interior?.Publish();

            int n = Mathf.Min(waveField.waves.Length, MaxWaves);
            for (int i = 0; i < n; i++)
            {
                var w = waveField.waves[i];
                Vector2 dir = w.direction.sqrMagnitude > 1e-6f ? w.direction.normalized : Vector2.right;
                _waveVecs[i] = new Vector4(dir.x, dir.y, w.wavelength, w.amplitude);
                _phases[i] = w.phaseOffset;
                _steeps[i] = w.steepness;
            }
            // 未使用的槽位清零，防止数组残留上一帧的波形
            for (int i = n; i < MaxWaves; i++)
            {
                _waveVecs[i] = Vector4.zero;
                _phases[i] = 0f;
                _steeps[i] = 0f;
            }

            _rend.GetPropertyBlock(_mpb);
            _mpb.SetFloat(TimeId, waveField.SimTime);
            _mpb.SetVectorArray(WavesId, _waveVecs);
            _mpb.SetFloatArray(PhaseId, _phases);
            _mpb.SetFloatArray(SteepnessId, _steeps);
            _mpb.SetFloat(IntensityId, waveField.intensity);
            _mpb.SetFloat(HorizontalId, waveField.horizontalDisplacement ? 1f : 0f);
            _mpb.SetInt(WaveCountId, n);
            _mpb.SetInt(InverseIterationsId, Mathf.Max(1, waveField.inverseIterations));
            _mpb.SetTexture(FoamDetailId, _foamDetail != null ? _foamDetail : Texture2D.blackTexture);
            _mpb.SetVector(FoamFlowId, new Vector4(waveField.backgroundCurrentMps.x, waveField.backgroundCurrentMps.y, 0f, 0f));
            if (_interactions == null) _interactions = FindObjectOfType<WaterInteractionVisuals>();
            if (_interactions != null) _interactions.WriteRippleProperties(_mpb);
            else _mpb.SetInt("_InteractionRippleCount", 0);

            // 交互泡沫/尾迹累积层(InteractionFoamTrail;无实例时强度归零关闭)
            var trail = InteractionFoamTrail.Instance;
            if (trail != null && trail.FoamTexture != null)
            {
                _mpb.SetTexture(TrailTexId, trail.FoamTexture);
                _mpb.SetVector(TrailAnchorId, trail.AnchorParams);
            }
            else
            {
                _mpb.SetVector(TrailAnchorId, new Vector4(0f, 48f, 0f, 0f));
            }
            _rend.SetPropertyBlock(_mpb);
        }

        // 编辑器里改 size / segments 时实时重建，方便调参
        void OnValidate()
        {
            if (!Application.isPlaying) return;
            if (_mesh != null) BuildMesh();
        }

        void OnDestroy()
        {
            _interior?.Dispose();
            _interior = null;
        }
    }
}
