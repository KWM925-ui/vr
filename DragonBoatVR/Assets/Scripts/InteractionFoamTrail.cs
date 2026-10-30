using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 交互泡沫/尾迹累积层(Crest 泡沫模拟的桌面缩小版):
    /// 一张跟随船的 48m 窗口累积贴图——船壳接触线、船尾尾迹、桨叶入水/划水
    /// 每帧向里"画",整张贴图按数秒时间常数指数衰减。WaveFieldWater.shader
    /// 采样它叠加泡沫并轻微压平法线(翻腾的浑水)。纯视觉层,不碰物理。
    ///
    /// 历史水痕固定在世界坐标;窗口移动时搬移像素,不随船平移。
    /// </summary>
    public class InteractionFoamTrail : MonoBehaviour
    {
        public const int Size = 256;
        public const float Window = 48f;          // 世界窗口边长(米)
        public float decaySeconds = 6f;           // 泡沫消散时间常数(用户 2026-10-25 拍板加强)
        public float paintPerMeter = 1.2f;        // 每米航程的船壳/尾迹注入强度(用户 2026-10-25 拍板加强)

        public static InteractionFoamTrail Instance { get; private set; }

        Texture2D _tex;
        Color32[] _px;
        float[] _density, _shifted;
        Vector2 _anchor, _publishedAnchor;
        SimulationSession _session;
        WaterInteractionVisuals _visuals;
        int _lastEntryBursts;
        float _applyAccumulator;
        bool _dirty;
        public int ObservedFrames { get; private set; }
        public int PaddlePaintEvents { get; private set; }
        public int WakePaintEvents { get; private set; }
        public int TimelineEpoch { get; private set; }
        public int TextureUploads { get; private set; }
        public double SourceObservationTime { get; private set; }
        public const float TexelMeters = Window / Size;

        /// <summary>x=锚点X, z=锚点Z, y=窗口, w=强度(供 shader 直接用)</summary>
        public Vector4 AnchorParams => new Vector4(
            _publishedAnchor.x,
            Window, _publishedAnchor.y,
            _hasPublishedEpoch ? 1f : 0f);

        public Texture2D FoamTexture => _tex;

        void Awake()
        {
            Instance = this;
            _tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            _px = new Color32[Size * Size];
            _density = new float[Size * Size];
            _shifted = new float[Size * Size];
            ClearTrail();
            Publish();
        }

        public void Bind(SimulationSession session)
        {
            if (_session == session) return;
            if (_session != null)
            {
                _session.FrameCompleted -= Observe;
                _session.TimelineReset -= ClearTrail;
            }
            _session = session;
            _visuals = session != null ? session.GetComponent<WaterInteractionVisuals>() : null;
            ClearTrail();
            if (_session != null)
            {
                _session.FrameCompleted += Observe;
                _session.TimelineReset += ClearTrail;
            }
        }

        void Update()
        {
            if (_session == null) Bind(FindObjectOfType<SimulationSession>());
            UploadIfDue(Time.unscaledDeltaTime);
        }

        void UploadIfDue(float dt)
        {
            // Texture and its world mapping are published together, at most 30 Hz.
            _applyAccumulator += dt;
            if (_dirty && _applyAccumulator >= 1f / 30f)
            {
                _applyAccumulator = 0f;
                Publish();
            }
        }

        void Observe(SimulationFrame frame)
        {
            if (_session == null || !_session.ExperienceActive || frame == null) return;
            if (_visuals == null) _visuals = _session.GetComponent<WaterInteractionVisuals>();
            float dt = frame.fixedStepSeconds;
            if (!SimulationValue.Finite(dt) || dt <= 0f) return;
            Recenter(frame.boatPosition);
            ObservedFrames++;
            SourceObservationTime = frame.timeSeconds;

            // 1) 整图指数衰减(泡沫消散)
            Decay(dt);

            // 2) 注入:船壳接触线 + 船尾尾迹(速度驱动)
            Vector3 relative = frame.boatVelocity - _session.wave.Sample(frame.boatPosition.x, frame.boatPosition.z).velocity;
            relative.y = 0f;
            float speed = relative.magnitude;
            if (speed > 0.05f)
            {
                WakePaintEvents++;
                float intensity = Mathf.Clamp01(speed / 2.2f) * paintPerMeter * speed * dt * 4f;
                var boat = Matrix4x4.TRS(frame.boatPosition, frame.boatRotation, Vector3.one);
                for (float z = -3.9f; z <= 4.0f; z += 0.35f)
                {
                    // 船壳两舷接触线(头尾画重些)
                    float taper = Mathf.Clamp01(1f - Mathf.Abs(z) / 4.6f);
                    Paint(boat.MultiplyPoint3x4(new Vector3( 0.50f, 0f, z)), intensity * (0.5f + taper));
                    Paint(boat.MultiplyPoint3x4(new Vector3(-0.50f, 0f, z)), intensity * (0.5f + taper));
                }
                // 尾迹:船尾向外张的楔形,强度∝速度²
                float wake = intensity * speed * 0.5f;
                for (float back = 0.3f; back <= 3.2f; back += 0.4f)
                {
                    float spread = 0.45f + back * 0.30f;
                    Paint(boat.MultiplyPoint3x4(new Vector3( spread * 0.6f, 0f, -4.1f - back)), wake);
                    Paint(boat.MultiplyPoint3x4(new Vector3(-spread * 0.6f, 0f, -4.1f - back)), wake);
                }
            }

            // 3) 注入:桨叶入水/划水(事件+持续)
            if (_session != null && _session.hydrodynamics != null && _session.hydrodynamics.Length > 0)
            {
                var hydro = _session.hydrodynamics[0];
                if (hydro.InputValid && hydro.BladeInWater && SimulationValue.Finite(hydro.RelativeSpeed)
                    && hydro.RelativeSpeed > 0.03f)
                {
                    PaddlePaintEvents++;
                    float wet = Mathf.Clamp01(hydro.WettedFraction);
                    // Churn follows relative travel through water, including pure
                    // rotation. A submerged stationary blade cannot manufacture foam.
                    float travel = Mathf.Min(hydro.RelativeSpeed, 3f) * dt;
                    Paint(frame.paddles[0].bladePosition, wet * travel * 1.6f, radius: 1.6f);
                }
                // 入水爆花事件(WaterInteractionVisuals 的计数器,只增不减)
                if (_visuals != null)
                {
                    if (_visuals.EntryBursts > _lastEntryBursts)
                    {
                        float strength = Mathf.Clamp01(_visuals.LastEntryStrength / 1.5f);
                        Paint(frame.paddles[0].bladePosition, 0.55f * strength,
                            radius: Mathf.Lerp(1.6f, 2.8f, strength));
                    }
                    _lastEntryBursts = _visuals.EntryBursts;
                }
            }

            _dirty = true;
        }

        void Decay(float dt)
        {
            float factor = Mathf.Exp(-dt / Mathf.Max(0.2f, decaySeconds));
            // Quantize only on upload. Byte multiplication rounded away small amounts
            // every frame, making the declared six-second decay depend on frame rate.
            for (int i = 0; i < _density.Length; i++) _density[i] *= factor;
        }

        void Recenter(Vector3 position)
        {
            var next = new Vector2(Mathf.Round(position.x / TexelMeters) * TexelMeters,
                Mathf.Round(position.z / TexelMeters) * TexelMeters);
            int dx = Mathf.RoundToInt((next.x - _anchor.x) / TexelMeters);
            int dz = Mathf.RoundToInt((next.y - _anchor.y) / TexelMeters);
            if (dx == 0 && dz == 0) return;
            System.Array.Clear(_shifted, 0, _shifted.Length);
            if (Mathf.Abs(dx) < Size && Mathf.Abs(dz) < Size)
            {
                int begin = Mathf.Max(0, -dx), end = Mathf.Min(Size, Size - dx);
                for (int z = Mathf.Max(0, -dz); z < Mathf.Min(Size, Size - dz); z++)
                    System.Array.Copy(_density, (z + dz) * Size + begin + dx,
                        _shifted, z * Size + begin, end - begin);
            }
            var prior = _density; _density = _shifted; _shifted = prior;
            _anchor = next;
            _dirty = true;
        }

        void ClearTrail()
        {
            if (_density == null) return;
            System.Array.Clear(_density, 0, _density.Length);
            _anchor = new Vector2(Mathf.Round(transform.position.x / TexelMeters) * TexelMeters,
                Mathf.Round(transform.position.z / TexelMeters) * TexelMeters);
            _lastEntryBursts = _visuals != null ? _visuals.EntryBursts : 0;
            ObservedFrames = PaddlePaintEvents = WakePaintEvents = 0;
            SourceObservationTime = 0;
            TimelineEpoch++;
            // Hide the old epoch immediately; the blank/new texture still uploads
            // through the normal 30 Hz limit, never paired with a new anchor early.
            _publishedAnchor = _anchor;
            _dirty = true;
            _hasPublishedEpoch = false;
        }

        bool _hasPublishedEpoch;
        void Publish()
        {
            for (int i = 0; i < _px.Length; i++)
                _px[i] = new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(_density[i]) * 255f), 0, 0, 255);
            _tex.SetPixels32(_px); _tex.Apply(false);
            TextureUploads++;
            _publishedAnchor = _anchor;
            _hasPublishedEpoch = true;
            _dirty = false;
        }

        /// <summary>世界坐标 → 窗口像素,叠加强度(带钳制)</summary>
        void Paint(Vector3 world, float strength, float radius = 1.6f)
        {
            Vector2 rel = new Vector2(world.x - _anchor.x, world.z - _anchor.y);
            float px = (rel.x / Window + 0.5f) * Size;
            float py = (rel.y / Window + 0.5f) * Size;
            int r = Mathf.CeilToInt(radius);
            for (int dy = -r; dy <= r; dy++)
            {
                int y = Mathf.FloorToInt(py) + dy; if (y < 0 || y >= Size) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = Mathf.FloorToInt(px) + dx; if (x < 0 || x >= Size) continue;
                    float fall = 1f - Mathf.Sqrt(dx * dx + dy * dy) / (r + 0.001f);
                    if (fall <= 0) continue;
                    int i = y * Size + x;
                    _density[i] = Mathf.Clamp01(_density[i] + strength * fall);
                }
            }
        }

        void OnDestroy()
        {
            if (_session != null)
            {
                _session.FrameCompleted -= Observe;
                _session.TimelineReset -= ClearTrail;
            }
            if (Instance == this) Instance = null;
            if (_tex != null)
            {
                if (Application.isPlaying) Destroy(_tex); else DestroyImmediate(_tex);
            }
        }
    }
}
