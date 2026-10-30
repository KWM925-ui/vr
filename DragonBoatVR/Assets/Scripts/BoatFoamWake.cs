using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 随速船壳泡沫(纯视觉层):细船首浪和船尾泡沫丝。
    /// 使用 Crest MIT 泡沫图，复用 WaterSurface 的波形、时钟及位移着色代码。
    /// 强度只来自已完成物理帧的水平相对水速，不由渲染时刻重新推断运动。
    /// </summary>
    [DefaultExecutionOrder(50)] // Read the water's current render properties after WaterSurface.
    public class BoatFoamWake : MonoBehaviour
    {
        [Tooltip("船的刚体(显示跟随),留空则找同层级 Rigidbody；速度由已完成的物理帧提供")]
        public Rigidbody boatBody;
        [Tooltip("水面真相源,留空则用 WaveField.Instance")]
        public WaveField wave;
        [Tooltip("泡沫材质(向导注入;空则运行时从 shader 构建,仅供编辑器")]
        public Material foamMaterial;

        [Tooltip("起显速度(m/s)")]
        public float fadeInMps = 0.35f;
        [Tooltip("满强度速度(m/s)")]
        public float fullMps = 2.2f;
        [Range(0f, 1f)] public float maxOpacity = 0.8f;

        [Tooltip("船尾拖尾长(米,满强度时)")]
        public float wakeLength = 3.2f;
        [Tooltip("船首八字单片长(米)")]
        public float bowLength = 1.4f;
        [Tooltip("泡沫贴水抬升(米),防 z-fight")]
        public float liftAboveWater = 0.025f;

        MaterialPropertyBlock _mpb;
        Material _mat;
        Mesh _mesh;
        MeshRenderer _renderer;
        Texture2D _texture;
        Renderer _waterRenderer;
        SimulationSession _session;
        float _horizontalWaterRelativeSpeed;
        static readonly int TintAndAlpha = Shader.PropertyToID("_Tint");

        void Awake()
        {
            if (boatBody == null) boatBody = GetComponentInParent<Rigidbody>();
            if (wave == null) wave = WaveField.Instance;

            // 用注入材质的副本(运行时贴纹理不弄脏资产);引用链保证 shader 进包
            _texture = Resources.Load<Texture2D>("Water/CrestFoam");
            _mat = foamMaterial != null ? new Material(foamMaterial) : new Material(Shader.Find("DragonBoatVR/BoatFoam"));
            _mat.mainTexture = _texture;
            _mat.SetTexture("_CrestFoamTexture", _texture != null ? _texture : Texture2D.blackTexture);
            _mat.hideFlags = HideFlags.DontSave;
            var surface = FindObjectOfType<WaterSurface>();
            if (surface != null) _waterRenderer = surface.GetComponent<Renderer>();

            _mesh = BuildMesh();
            var mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = _mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _mpb = new MaterialPropertyBlock();
            Bind(GetComponentInParent<SimulationSession>() ?? FindObjectOfType<SimulationSession>());
            ClearMotion();
        }

        void Bind(SimulationSession session)
        {
            if (_session == session) return;
            if (_session != null)
            {
                _session.FrameCompleted -= Observe;
                _session.TimelineReset -= ClearMotion;
            }
            _session = session;
            ClearMotion();
            if (_session != null)
            {
                _session.FrameCompleted += Observe;
                _session.TimelineReset += ClearMotion;
            }
        }

        void Observe(SimulationFrame frame)
        {
            if (_session == null || !_session.ExperienceActive || _session.wave == null || frame == null
                || !SimulationValue.Finite(frame.boatPosition) || !SimulationValue.Finite(frame.boatVelocity))
            {
                ClearMotion();
                return;
            }
            // Match the world-anchored trail: shared current and vertical bobbing
            // do not produce a travelling hull wake. Do not resample velocity at render time.
            Vector3 relative = frame.boatVelocity - _session.wave.Sample(frame.boatPosition.x, frame.boatPosition.z).velocity;
            relative.y = 0f;
            _horizontalWaterRelativeSpeed = SimulationValue.Finite(relative) ? relative.magnitude : 0f;
        }

        void ClearMotion()
        {
            _horizontalWaterRelativeSpeed = 0f;
            if (_mpb == null || _renderer == null) return;
            _mpb.SetColor(TintAndAlpha, new Color(1f, 1f, 1f, 0f));
            _renderer.SetPropertyBlock(_mpb);
        }

        void OnDisable() => Bind(null);

        void OnDestroy()
        {
            Bind(null);
            // Resources.Load returns a shared asset, not an owned runtime texture.
            Release(_mat); Release(_mesh);
            _texture = null; _mat = null; _mesh = null;
        }

        static void Release(Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) Destroy(resource);
            else DestroyImmediate(resource);
        }

        void LateUpdate()
        {
            if (_session == null) Bind(GetComponentInParent<SimulationSession>() ?? FindObjectOfType<SimulationSession>());
            if (boatBody == null || wave == null) return;

            float speed = _horizontalWaterRelativeSpeed;
            float t = Mathf.InverseLerp(fadeInMps, fullMps, speed);
            float alpha = maxOpacity * t * t;   // 二次:低速几乎看不见,冲刺才铺开

            // The shader samples the physical surface at the ribbon's world X/Z,
            // so its edge follows the hull while sharing the water's full wave.
            Vector3 p = boatBody.position;
            transform.SetPositionAndRotation(
                new Vector3(p.x, wave.BaseHeight + Mathf.Min(liftAboveWater, 0.008f), p.z),
                Quaternion.Euler(0f, boatBody.rotation.eulerAngles.y, 0f));
            transform.localScale = Vector3.one;

            if (_waterRenderer != null) _waterRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(TintAndAlpha, new Color(1f, 1f, 1f, alpha));
            _renderer.SetPropertyBlock(_mpb);
        }

        // Subdivided narrow ribbons start at the actual hull ends (+/-3.8m).
        // Soft transverse edges and longitudinal decay are applied in the shader.
        Mesh BuildMesh()
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var cols = new System.Collections.Generic.List<Color>();
            var tris = new System.Collections.Generic.List<int>();

            void Ribbon(float side, bool bow)
            {
                const int segments = 16;
                int start = verts.Count;
                for (int i = 0; i <= segments; i++)
                {
                    float t = i / (float)segments;
                    float x = side * (bow ? Mathf.Lerp(0.22f, 0.95f, t) : Mathf.Lerp(0.28f, 0.78f, t));
                    float z = bow ? 3.6f - t * bowLength : -3.8f - t * wakeLength;
                    float width = bow ? Mathf.Lerp(0.055f, 0.21f, t) : Mathf.Lerp(0.17f, 0.32f, t);
                    var color = new Color(1f, 1f, 1f, Mathf.Pow(1f - t, 1.4f));
                    verts.Add(new Vector3(x - width, 0f, z));
                    verts.Add(new Vector3(x + width, 0f, z));
                    uvs.Add(new Vector2(0f, t)); uvs.Add(new Vector2(1f, t));
                    cols.Add(color); cols.Add(color);
                    if (i == segments) continue;
                    int a = start + i * 2;
                    tris.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
                }
            }

            Ribbon(1f, true); Ribbon(-1f, true);
            Ribbon(1f, false); Ribbon(-1f, false);

            var m = new Mesh { name = "BoatFoamWake" };
            m.SetVertices(verts); m.SetUVs(0, uvs); m.SetColors(cols);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            m.bounds = new Bounds(m.bounds.center, m.bounds.size + new Vector3(0.5f, 1f, 0.5f));
            return m;
        }
    }
}
