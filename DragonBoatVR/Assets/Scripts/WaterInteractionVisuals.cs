using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Local visual water response to completed physics observations. These
    /// prototype effects never modify the wave field, paddle pose or device requests.</summary>
    public sealed class WaterInteractionVisuals : MonoBehaviour
    {
        public float entryScale = 1f;
        public float dripDurationSeconds = 0.55f;
        public float rippleLifetimeSeconds = 1.25f;
        public int EntryBursts { get; private set; }
        public int SplashParticles { get; private set; }
        public int ExitEvents { get; private set; }
        public int DripParticles { get; private set; }
        public int PaddleRipples { get; private set; }
        public int WakeRipples { get; private set; }
        public int ActiveParticles => _drops != null ? _drops.particleCount : 0;
        public int ActiveRipples { get; private set; }
        public int VisibleRenderFrames { get; private set; }
        public float LastEntryStrength { get; private set; }
        public float WakeSpeedMps { get; private set; }
        public Vector3 LastContactWorld { get; private set; }
        public event System.Action<RiverAudioMotionSample> PhysicsObserved;

        // Presentation-only admission: a microscopic grazing sliver must not
        // start a full splash/drip pair. Once admitted, retain contact until
        // the physical blade is dry; all hydrodynamic values stay unchanged.
        const float MinimumBurstWetFraction = 0.001f;
        const int RingCount = 48, ShaderRippleCount = 24;
        sealed class Ring
        {
            public Vector3 centre, forward;
            public float age, life, radius, expansion, strength, stretch;
            public bool active, wake;
        }
        readonly Ring[] _rings = new Ring[RingCount];
        readonly Vector4[] _shaderRipples = new Vector4[ShaderRippleCount];
        readonly Vector4[] _shaderRippleShapes = new Vector4[ShaderRippleCount];
        SimulationSession _session;
        ParticleSystem _drops;
        Material _material;
        int _nextRing;
        bool _hadObservation, _wasWet;
        float _wetness, _dripRemaining, _dripAccumulator, _paddleTimer, _wakeTimer;
        uint _random = 0x8d4129b1u;

        public void Bind(SimulationSession session)
        {
            if (_session == session) return;
            if (_session != null) _session.FrameCompleted -= Observe;
            _session = session;
            if (_material == null) BuildEffects();
            _session.FrameCompleted += Observe;
        }

        public void ClearEffects()
        {
            _hadObservation = _wasWet = false;
            _wetness = _dripRemaining = _dripAccumulator = _paddleTimer = _wakeTimer = 0f;
            EntryBursts = SplashParticles = ExitEvents = DripParticles = PaddleRipples = WakeRipples = 0;
            ActiveRipples = VisibleRenderFrames = 0;
            LastEntryStrength = WakeSpeedMps = 0f;
            LastContactWorld = Vector3.zero;
            if (_drops != null) _drops.Clear();
            foreach (Ring ring in _rings)
            {
                if (ring == null) continue;
                ring.active = false;
            }
        }

        void BuildEffects()
        {
            Shader shader = Shader.Find("DragonBoatVR/WaterDroplet");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            _material = new Material(shader) { name = "LocalWaterResponse" };
            var rippleColor = new Color(0.68f, 0.86f, 0.9f, 0.6f);
            // Ripples are a transparent water response, not white debug geometry.
            if (_material.HasProperty("_Surface")) _material.SetFloat("_Surface", 1f);
            if (_material.HasProperty("_Blend")) _material.SetFloat("_Blend", 0f);
            if (_material.HasProperty("_SrcBlend")) _material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (_material.HasProperty("_DstBlend")) _material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (_material.HasProperty("_ZWrite")) _material.SetFloat("_ZWrite", 0f);
            _material.renderQueue = 3012;
            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", rippleColor);
            _material.SetColor("_Color", rippleColor);
            _material.color = rippleColor;
            var dropObject = new GameObject("PaddleSplashAndDrops");
            dropObject.transform.SetParent(transform, false);
            _drops = dropObject.AddComponent<ParticleSystem>();
            var main = _drops.main;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 512;
            main.startLifetime = 0.5f;
            main.startSize = 0.006f;
            main.gravityModifier = 1f;
            var emission = _drops.emission; emission.enabled = false;
            var shape = _drops.shape; shape.enabled = false;
            var renderer = _drops.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _material;
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 1.15f;
            renderer.velocityScale = 0.02f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _drops.Play();
            for (int i = 0; i < RingCount; i++)
            {
                _rings[i] = new Ring();
            }
        }

        float Random01()
        {
            _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5;
            return (_random & 0xffffffu) / 16777216f;
        }

        void EmitDrops(Vector3 point, Vector3 velocity, int count, bool splash, float strength)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = Random01() * 2f * Mathf.PI;
                float spread = splash ? 0.2f + Random01() * 0.65f * strength : 0.07f;
                var parameters = new ParticleSystem.EmitParams
                {
                    position = point + new Vector3((Random01() - 0.5f) * 0.12f, 0.01f, (Random01() - 0.5f) * 0.12f),
                    velocity = velocity + new Vector3(Mathf.Cos(angle) * spread,
                        splash ? 0.55f + Random01() * 1.2f * strength : -0.12f, Mathf.Sin(angle) * spread),
                    startLifetime = splash ? 0.28f + Random01() * 0.25f : 0.28f,
                    startSize = splash ? 0.003f + Random01() * 0.008f : 0.0045f,
                    startColor = new Color(0.8f, 0.94f, 1f, 1f)
                };
                _drops.Emit(parameters, 1);
            }
            if (splash) SplashParticles += count; else DripParticles += count;
        }

        void AddRipple(Vector3 centre, float strength, bool wake, Vector3 forward)
        {
            Ring ring = _rings[_nextRing]; _nextRing = (_nextRing + 1) % RingCount;
            ring.active = true; ring.wake = wake;
            ring.centre = centre; ring.forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            ring.age = 0f; ring.life = Mathf.Max(0.1f, rippleLifetimeSeconds);
            ring.radius = wake ? 0.18f : 0.04f;
            ring.expansion = wake ? 0.6f : 0.5f;
            ring.stretch = wake ? 1.45f : 1f;
            ring.strength = Mathf.Clamp(strength, 0.1f, 1f);
            if (wake) WakeRipples++; else PaddleRipples++;
        }

        void Observe(SimulationFrame frame)
        {
            if (_session == null || _session.hydrodynamics.Length == 0) return;
            float dt = frame.fixedStepSeconds;
            if (!SimulationValue.Finite(dt) || dt <= 0) return;
            foreach (Ring ring in _rings)
            {
                if (!ring.active) continue;
                ring.age += dt;
                if (ring.age >= ring.life) ring.active = false;
                ring.centre += new Vector3(_session.wave.backgroundCurrentMps.x, 0f,
                    _session.wave.backgroundCurrentMps.y) * dt;
            }
            var hydro = _session.hydrodynamics[0];
            bool valid = hydro.InputValid;
            bool wet = valid && hydro.BladeInWater;
            bool burstWet = wet && (_wasWet || hydro.WettedFraction >= MinimumBurstWetFraction);
            Vector3 contact = hydro.BladePosition;
            var water = _session.wave.Sample(contact.x, contact.z);
            contact.y = water.height + 0.012f;
            LastContactWorld = contact;
            float speed = hydro.RelativeSpeed;
            if (_hadObservation && valid && burstWet && !_wasWet)
            {
                float strength = Mathf.Clamp(speed * 0.65f * entryScale, 0f, 1.5f);
                LastEntryStrength = strength;
                if (strength > 0.015f)
                {
                    EntryBursts++;
                    EmitDrops(contact, Vector3.ProjectOnPlane(hydro.BladeVelocity - hydro.WaterVelocity, Vector3.up) * 0.2f,
                        Mathf.CeilToInt(12 + strength * 22), true, strength);
                    AddRipple(contact, strength, false, Vector3.forward);
                }
                _dripRemaining = 0f;
                _dripAccumulator = 0f;
                _wetness = 0f;
            }
            // Remember the water carried during this contact. The final wet frame
            // has almost zero area during a gradual exit, but the blade is still wet.
            if (wet) _wetness = Mathf.Max(_wetness, Mathf.Clamp01(hydro.WettedFraction));
            if (_hadObservation && valid && !burstWet && _wasWet)
            {
                ExitEvents++;
                _dripRemaining = Mathf.Max(0f, dripDurationSeconds) * _wetness;
                _dripAccumulator = 0f;
            }
            _paddleTimer += dt;
            if (wet && speed > 0.03f && _paddleTimer >= 0.08f)
            {
                AddRipple(contact, speed * hydro.WettedFraction, false, Vector3.forward);
                _paddleTimer = 0f;
            }
            if (!valid) { _dripRemaining = 0f; _dripAccumulator = 0f; _wetness = 0f; }
            if (valid && !wet && _dripRemaining > 0f)
            {
                float drainFraction = _dripRemaining / Mathf.Max(0.001f, dripDurationSeconds);
                _dripAccumulator += Mathf.Min(dt, _dripRemaining) * drainFraction;
                _dripRemaining = Mathf.Max(0f, _dripRemaining - dt);
                if (_dripAccumulator >= 0.035f)
                {
                    Vector3 tip = hydro.BladePosition - hydro.transform.up * (hydro.bladeLength * 0.5f);
                    tip.y = Mathf.Max(tip.y, water.height + 0.015f);
                    EmitDrops(tip, hydro.BladeVelocity * 0.12f, 2, false, 1f);
                    _dripAccumulator -= 0.035f;
                }
                if (_dripRemaining <= 0f) _wetness = 0f;
            }
            _wasWet = burstWet; _hadObservation = valid;

            // The boat continues coasting after paddle input stops; its wake follows
            // relative water motion rather than the presence of fresh paddle packets.
            var rb = _session.boat.BoatRb;
            Vector3 boatPosition = rb.position;
            Vector3 relative = rb.velocity - _session.wave.Sample(boatPosition.x, boatPosition.z).velocity;
            relative.y = 0f; WakeSpeedMps = relative.magnitude;
            _wakeTimer += dt;
            if (WakeSpeedMps > 0.08f && _wakeTimer >= 0.12f)
            {
                Vector3 stern = _session.boat.transform.TransformPoint(new Vector3(0f, -0.2f, -3.8f));
                Vector3 right = Vector3.Cross(Vector3.up, relative.normalized);
                AddRipple(stern + right * 0.42f, WakeSpeedMps * 0.5f, true, relative);
                AddRipple(stern - right * 0.42f, WakeSpeedMps * 0.5f, true, relative);
                _wakeTimer = 0f;
            }
            if (PhysicsObserved != null && frame.paddles != null && frame.paddles.Length > 0)
                PhysicsObserved(new RiverAudioMotionSample { timeSeconds = frame.timeSeconds, stepSeconds = dt,
                    paddle = frame.paddles[0], wetFraction = valid ? hydro.WettedFraction : 0f,
                    paddleRelativeSpeed = valid ? speed : 0f, hullRelativeSpeed = WakeSpeedMps,
                    paddleLoadN = valid ? hydro.CurrentDrag : 0f, waterContact = contact,
                    boatPosition = frame.boatPosition, boatRotation = frame.boatRotation,
                    boatVelocity = frame.boatVelocity, boatAngularVelocity = frame.boatAngularVelocity,
                    boatLocalAcceleration = _session.boat.localLinearAcceleration });
        }

        void LateUpdate()
        {
            if (_session == null) return;
            ActiveRipples = 0;
            foreach (Ring ring in _rings)
            {
                if (!ring.active) continue;
                ActiveRipples++;
            }
            if (ActiveRipples > 0 || ActiveParticles > 0) VisibleRenderFrames++;
        }

        // The water shader changes the reflected/refracted surface normal. The
        // event remains tied to completed physics, without visible line geometry.
        public void WriteRippleProperties(MaterialPropertyBlock properties)
        {
            int count = 0;
            for (int i = 0; i < RingCount && count < ShaderRippleCount; i++)
            {
                Ring ring = _rings[(_nextRing - 1 - i + RingCount * 2) % RingCount];
                if (ring == null || !ring.active) continue;
                float fade = Mathf.Clamp01(1f - ring.age / Mathf.Max(0.001f, ring.life));
                _shaderRipples[count] = new Vector4(ring.centre.x, ring.centre.z,
                    ring.radius + ring.expansion * ring.age, 0.09f + ring.age * 0.035f);
                _shaderRippleShapes[count] = new Vector4(ring.forward.x, ring.forward.z,
                    ring.wake ? -ring.stretch : ring.stretch, 0.0025f * ring.strength * fade * fade);
                count++;
            }
            properties.SetInt("_InteractionRippleCount", count);
            properties.SetVectorArray("_InteractionRipples", _shaderRipples);
            properties.SetVectorArray("_InteractionRippleShapes", _shaderRippleShapes);
        }

        void OnDestroy()
        {
            if (_session != null) _session.FrameCompleted -= Observe;
            if (_material != null) Destroy(_material);
        }
    }
}
