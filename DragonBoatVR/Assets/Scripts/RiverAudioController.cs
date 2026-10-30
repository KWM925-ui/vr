using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// Recorded river/wood timbres follow completed physical motion and contacts.
    /// Audio never feeds back into water, boat or device physics.
    /// </summary>
    public sealed partial class RiverAudioController : MonoBehaviour
    {
        [Header("原型音频（未实测，可替换）")]
        public string provenance = "UNMEASURED_RECORDED_RIVER_AND_WOOD_AUDIO";
        public bool measured;
        public bool enabledInPrototype = true;
        [Range(0f, 1f)] public float waterAmbientVolume = 0.12f;
        [Range(0f, 1f)] public float shoreAmbientVolume = 0.035f;
        [Range(0f, 1f)] public float paddleSplashVolume = 0.22f;
        [Range(0f, 1f)] public float paddleDripVolume = 0.06f;

        public bool AudioReady { get; private set; }
        public int SplashSounds { get; private set; }
        public int DripSounds { get; private set; }

        SimulationSession _session;
        WaterInteractionVisuals _effects;
        AudioSource _waterSource, _shoreSource, _paddleSource;
        AudioClip _waterClip, _shoreClip, _splashClip, _dripClip;
        Transform _paddleEmitter;
        int _lastEntries, _lastExits;
        readonly float[] _paddleOutput = new float[256];

        // Diagnostic observation of the audio source's mixed output. Calling PlayOneShot
        // alone does not establish that the enabled audio engine produced any samples.
        public float SamplePaddleOutputRms()
        {
            if (!AudioReady || _paddleSource == null) return 0f;
            _paddleSource.GetOutputData(_paddleOutput, 0);
            float sum = 0f;
            foreach (float sample in _paddleOutput) sum += sample * sample;
            return Mathf.Sqrt(sum / _paddleOutput.Length);
        }

        public void Bind(SimulationSession session)
        {
            if (session == null) return;
            bool changedSession = _session != session;
            if (changedSession)
            {
                if (_session != null) _session.TimelineReset -= ResetEventCursor;
                _session = session;
                _session.TimelineReset += ResetEventCursor;
            }
            var effects = session.GetComponent<WaterInteractionVisuals>();
            if (changedSession || _effects != effects)
            {
                if (_effects != null) _effects.PhysicsObserved -= ObserveMotion;
                _effects = effects;
                if (_effects != null) _effects.PhysicsObserved += ObserveMotion;
                ResetEventCursor();
                BindWoodenHull();
            }
            if (_paddleEmitter == null) EnsureAudio();
        }

        void Start()
        {
            if (!enabledInPrototype) return;
            EnsureAudio();
            TryBind();
        }

        void Update()
        {
            if (!enabledInPrototype) { StopAllAudio(); return; }
            TryBind();
            if (_session == null || _effects == null || !AudioReady) return;

            if (_session.ExperienceEnded)
            {
                StopAllAudio();
                return;
            }
            _waterSource.volume = Mathf.Clamp01(waterAmbientVolume);
            _shoreSource.volume = Mathf.Clamp01(shoreAmbientVolume);
            if (!_waterSource.isPlaying) _waterSource.Play();
            if (!_shoreSource.isPlaying) _shoreSource.Play();
            UpdateMotionAudio();

            if (_session.drivers != null && _session.drivers.Length > 0 && _paddleEmitter != null)
                _paddleEmitter.position = _session.drivers[0].transform.position;

            int entries = _effects.EntryBursts;
            if (entries < _lastEntries) _lastEntries = entries;
            if (entries > _lastEntries)
            {
                int count = entries - _lastEntries;
                for (int i = 0; i < count; i++) PlaySplash(_effects.LastEntryStrength);
                _lastEntries = entries;
            }
            int exits = _effects.ExitEvents;
            if (exits < _lastExits) _lastExits = exits;
            if (exits > _lastExits)
            {
                int count = exits - _lastExits;
                for (int i = 0; i < count; i++) PlayDrip();
                _lastExits = exits;
            }
        }

        void TryBind()
        {
            if (_session == null)
            {
                var session = FindObjectOfType<SimulationSession>();
                if (session != null) Bind(session);
            }
            else if (_effects == null) Bind(_session);
        }

        void ResetEventCursor()
        {
            // Zero events is a valid bound state. Only a new source or timeline may
            // establish a baseline; doing so every Update would swallow its first splash.
            _lastEntries = _effects != null ? _effects.EntryBursts : 0;
            _lastExits = _effects != null ? _effects.ExitEvents : 0;
            if (_paddleSource != null) _paddleSource.Stop();
            ResetMotionAudio();
        }

        void EnsureAudio()
        {
            if (AudioReady) return;
            CreateRecordedWaterBank();

            _waterSource = gameObject.AddComponent<AudioSource>();
            ConfigureAmbient(_waterSource, _waterClip, waterAmbientVolume);
            _shoreSource = gameObject.AddComponent<AudioSource>();
            ConfigureAmbient(_shoreSource, _shoreClip, shoreAmbientVolume);

            var emitter = new GameObject("PaddleAudioEmitter");
            emitter.transform.SetParent(transform, true);
            _paddleEmitter = emitter.transform;
            _paddleSource = emitter.AddComponent<AudioSource>();
            _paddleSource.playOnAwake = false;
            _paddleSource.loop = false;
            _paddleSource.volume = 1f;
            _paddleSource.spatialBlend = 0.75f;
            _paddleSource.minDistance = 0.8f;
            _paddleSource.maxDistance = 18f;
            _paddleSource.rolloffMode = AudioRolloffMode.Linear;
            _paddleSource.dopplerLevel = 0f;
            EnsureMotionAudio();
            AudioReady = ClipReady(_waterClip) && ClipReady(_shoreClip) && ClipReady(_splashClip) && ClipReady(_dripClip)
                && ClipReady(_strokeWaterClip);
            if (AudioReady)
            {
                _waterSource.Play();
                _shoreSource.Play();
            }
        }

        static bool ClipReady(AudioClip clip) => clip != null && clip.samples > 0
            && clip.channels == 1 && clip.frequency == 22050 && clip.length > 0f;

        void ConfigureAmbient(AudioSource source, AudioClip clip, float volume)
        {
            source.playOnAwake = false;
            source.loop = true;
            source.clip = clip;
            source.volume = Mathf.Clamp01(volume);
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
        }

        void PlaySplash(float strength)
        {
            if (_paddleSource == null || _splashClip == null) return;
            _paddleSource.PlayOneShot(_splashVariants[SplashSounds % _splashVariants.Length],
                Mathf.Clamp01(paddleSplashVolume) * Mathf.Clamp(0.55f + strength, 0.55f, 1.5f));
            SplashSounds++;
        }

        void PlayDrip()
        {
            if (_paddleSource == null || _dripClip == null) return;
            _paddleSource.PlayOneShot(_dripClip, Mathf.Clamp01(paddleDripVolume));
            DripSounds++;
        }

        void OnDestroy()
        {
            DestroyMotionAudio();
            if (_session != null) _session.TimelineReset -= ResetEventCursor;
            if (_waterClip != null) Destroy(_waterClip);
            if (_shoreClip != null) Destroy(_shoreClip);
            if (_splashClip != null) Destroy(_splashClip);
            if (_dripClip != null) Destroy(_dripClip);
        }
    }
}
