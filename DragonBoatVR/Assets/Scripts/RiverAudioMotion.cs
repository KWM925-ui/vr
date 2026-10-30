using System;
using System.Collections.Generic;
using UnityEngine;

namespace DragonBoatVR
{
    public sealed partial class RiverAudioController
    {
        [Header("同步水流与木质声音")]
        [Range(0f, 1f)] public float strokeWaterVolume = 0.28f;
        [Range(0f, 1f)] public float hullWaterVolume = 0.16f;
        [Range(0f, 1f)] public float woodImpactVolume = 0.30f;
        [Range(0f, 1f)] public float woodCreakVolume = 0.10f;
        public float StrokeWaterLevel { get; private set; }
        public float HullWaterLevel { get; private set; }
        public int WoodImpacts { get; private set; }
        public int WoodCreaks { get; private set; }
        public int MotionSourceFrames { get; private set; }
        public double MotionSourceTime { get; private set; } = -1;
        public double LastWoodImpactTime { get; private set; } = -1;
        public Vector3 LastWoodContactWorld { get; private set; }

        AudioSource _strokeSource, _hullSource, _woodSource, _creakSource;
        AudioClip[] _woodClips, _creakClips, _splashVariants;
        AudioClip _strokeWaterClip;
        WoodHullCollisionAudio _collisionRelay;
        RiverAudioMotionSample _motion;
        double _nextCreakTime, _lastMotionWallTime;
        bool _hasMotion;
        readonly List<WoodenRail> _rails = new List<WoodenRail>();
        readonly float[] _motionOutput = new float[256];

        sealed class WoodenRail
        {
            public LineRenderer renderer;
            public Transform frame;
            public float radius;
            public Vector3[] points;
            public bool touching;
            public double nextImpact;
        }

        void CreateRecordedWaterBank()
        {
            var original = Resources.Load<AudioClip>("Audio/BoatAttackFlowingWater");
            float[] water = RiverAudioClipFactory.ReadMono(original);
            _waterClip = RiverAudioClipFactory.Loop("BoatAttackRiverLoop", water);
            _shoreClip = RiverAudioClipFactory.Loop("DistantRiverLoop", water, 0.25f);
            float[] canoe = RiverAudioClipFactory.ReadMono(Resources.Load<AudioClip>("Audio/EgeexyzCanoePaddling"));
            // Contiguous recorded paddle/water phrases. Keep the large source
            // transients outside these windows; wood impacts have their own events.
            float[] starts = { 0.20f, 2.42f, 3.72f, 7.72f };
            float[] lengths = { 0.58f, 0.62f, 0.62f, 0.39f };
            _splashVariants = new AudioClip[4];
            for (int i = 0; i < _splashVariants.Length; i++)
                _splashVariants[i] = RiverAudioClipFactory.RecordedSlice("CanoeEntry_" + i, canoe, starts[i], lengths[i], 0.72f);
            _splashClip = _splashVariants[0];
            _dripClip = RiverAudioClipFactory.RecordedSlice("CanoeDrain", canoe, 6.65f, 0.48f, 0.58f);
            _strokeWaterClip = RiverAudioClipFactory.Loop("CanoeStrokeWater", RiverAudioClipFactory.Slice(canoe, 2.22f, 1.20f, 0.65f));
            provenance = "BoatAttack river (Unity Companion License); egeexyz canoe water, Kenney wood impacts and Department64 timber creak (CC0); unmeasured mix";
        }

        void EnsureMotionAudio()
        {
            _woodClips = Resources.LoadAll<AudioClip>("Audio/Kenney");
            Array.Sort(_woodClips, (a, b) => string.CompareOrdinal(a.name, b.name));
            if (_woodClips.Length != 9) throw new InvalidOperationException("Nine licensed wood impact variations are required.");
            var timberCreak = Resources.Load<AudioClip>("Audio/Department64TreeCreak");
            _creakClips = new AudioClip[3];
            for (int i = 0; i < _creakClips.Length; i++) _creakClips[i] = RiverAudioClipFactory.Creak(timberCreak, i);
            _strokeSource = SpatialSource("PaddleWaterRush", _paddleEmitter);
            _strokeSource.clip = _strokeWaterClip; _strokeSource.loop = true;
            _strokeSource.gameObject.AddComponent<AudioLowPassFilter>().cutoffFrequency = 3200f;
            _hullSource = SpatialSource("WoodenHullWater", transform);
            _hullSource.clip = _waterClip; _hullSource.loop = true;
            _hullSource.gameObject.AddComponent<AudioLowPassFilter>().cutoffFrequency = 1600f;
            _woodSource = SpatialSource("WoodContact", transform);
            _creakSource = SpatialSource("WoodFlex", transform);
        }

        static AudioSource SpatialSource(string name, Transform parent)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false; source.volume = 0f; source.spatialBlend = 1f;
            source.dopplerLevel = 0f; source.minDistance = 0.65f; source.maxDistance = 14f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            return source;
        }

        void BindWoodenHull()
        {
            _rails.Clear();
            if (_collisionRelay != null && _collisionRelay.owner == this) _collisionRelay.owner = null;
            _collisionRelay = null;
            if (_session == null || _session.boat == null) return;
            var hull = BoatHullGeometry.FindOrCreate(_session.boat);
            if (hull != null)
            {
                _rails.Add(new WoodenRail { frame = hull.transform, points = hull.Starboard, radius = BoatHullGeometry.RailRadius });
                _rails.Add(new WoodenRail { frame = hull.transform, points = hull.Port, radius = BoatHullGeometry.RailRadius });
            }
            else foreach (var line in _session.boat.GetComponentsInChildren<LineRenderer>(true))
                if (line.enabled && line.name.StartsWith("GunwaleTrim", StringComparison.Ordinal) && line.positionCount > 1)
                {
                    var rail = new WoodenRail { renderer = line, frame = line.transform,
                        radius = line.widthMultiplier * 0.5f, points = new Vector3[line.positionCount] };
                    line.GetPositions(rail.points); _rails.Add(rail);
                }
            _collisionRelay = _session.boat.GetComponent<WoodHullCollisionAudio>();
            if (_collisionRelay == null) _collisionRelay = _session.boat.gameObject.AddComponent<WoodHullCollisionAudio>();
            _collisionRelay.owner = this;
        }

        void ObserveMotion(RiverAudioMotionSample sample)
        {
            if (!enabledInPrototype || !isActiveAndEnabled || !AudioReady || _session == null || _session.ExperienceEnded
                || !SimulationValue.Finite(sample.stepSeconds) || sample.stepSeconds <= 0f
                || double.IsNaN(sample.timeSeconds) || double.IsInfinity(sample.timeSeconds)
                || _hasMotion && sample.timeSeconds <= MotionSourceTime) return;
            bool previous = _hasMotion;
            _motion = sample; _hasMotion = true; MotionSourceFrames++; MotionSourceTime = sample.timeSeconds;
            _lastMotionWallTime = Time.realtimeSinceStartupAsDouble;
            StrokeWaterLevel = sample.paddle.valid ? Mathf.Clamp01(sample.wetFraction)
                * Mathf.SmoothStep(0f, 1f, Mathf.Max(0f, sample.paddleRelativeSpeed - 0.025f) / 2.2f) : 0f;
            HullWaterLevel = Mathf.SmoothStep(0f, 1f, Mathf.Max(0f, sample.hullRelativeSpeed - 0.025f) / 2.5f);
            ObserveWoodContacts(sample, previous);
            Vector3 angular = Quaternion.Inverse(sample.boatRotation) * sample.boatAngularVelocity;
            float strain = Mathf.Abs(angular.x) + Mathf.Abs(angular.z)
                + 0.035f * Mathf.Abs(sample.boatLocalAcceleration.y)
                + (sample.paddle.valid ? Mathf.Min(0.45f, sample.paddleLoadN / 450f) : 0f);
            if (strain > 0.08f && sample.timeSeconds >= _nextCreakTime)
            {
                _creakSource.transform.position = sample.boatPosition;
                _creakSource.volume = 1f; _creakSource.pitch = 0.91f + 0.06f * (WoodCreaks % 3);
                _creakSource.PlayOneShot(_creakClips[WoodCreaks % _creakClips.Length],
                    woodCreakVolume * Mathf.Clamp01(strain));
                WoodCreaks++;
                _nextCreakTime = sample.timeSeconds + 1.15 + 0.21 * (WoodCreaks % 4);
            }
        }

        void UpdateMotionAudio()
        {
            if (_strokeSource == null) return;
            // If physical observations cease, do not keep playing a held stroke.
            bool fresh = _hasMotion && Time.realtimeSinceStartupAsDouble - _lastMotionWallTime <= 0.1;
            UpdateLoop(_strokeSource, fresh ? StrokeWaterLevel * strokeWaterVolume : 0f,
                0.82f + 0.24f * Mathf.Clamp01(_motion.paddleRelativeSpeed / 2.5f));
            UpdateLoop(_hullSource, fresh ? HullWaterLevel * hullWaterVolume : 0f,
                0.85f + 0.22f * HullWaterLevel);
            if (_session.boat != null)
            {
                _hullSource.transform.position = _session.boat.transform.position;
                _creakSource.transform.position = _session.boat.transform.position;
            }
        }

        static void UpdateLoop(AudioSource source, float volume, float pitch)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            source.volume = Mathf.MoveTowards(source.volume, Mathf.Clamp01(volume), dt * 3f);
            source.pitch = Mathf.Lerp(source.pitch, pitch, 1f - Mathf.Exp(-dt * 12f));
            if (source.volume > 0.0001f && !source.isPlaying) { source.time = source.clip.length * 0.37f; source.Play(); }
            else if (source.volume <= 0.0001f && source.isPlaying) source.Stop();
        }

        void ObserveWoodContacts(RiverAudioMotionSample sample, bool previous)
        {
            if (!sample.paddle.valid || _session.drivers == null || _session.drivers.Length == 0) return;
            float length = _session.drivers[0].shaftToBlade;
            var hydro = _session.hydrodynamics != null && _session.hydrodynamics.Length > 0 ? _session.hydrodynamics[0] : null;
            Vector3 bladeHalfSize = hydro != null ? new Vector3(hydro.BladeWidth, hydro.bladeLength, hydro.bladeThickness) * 0.5f : Vector3.zero;
            var paddle = sample.paddle;
            Vector3 blade = paddle.bladePosition, grip = blade + paddle.bladeRotation * Vector3.up * length;
            foreach (var rail in _rails)
            {
                float closest = float.MaxValue; Vector3 onPaddle = blade, onRail = blade;
                for (int i = 1; i < rail.points.Length; i++)
                {
                    bool worldSpace = rail.renderer != null && rail.renderer.useWorldSpace;
                    Vector3 a = worldSpace ? rail.points[i - 1] : rail.frame.TransformPoint(rail.points[i - 1]);
                    Vector3 b = worldSpace ? rail.points[i] : rail.frame.TransformPoint(rail.points[i]);
                    float distance = WoodenContactGeometry.SegmentDistance(blade, grip, a, b, out Vector3 p, out Vector3 q);
                    if (distance < closest) { closest = distance; onPaddle = p; onRail = q; }
                    if (hydro != null && WoodenContactGeometry.SegmentTouchesBlade(a, b, blade, paddle.bladeRotation,
                        bladeHalfSize, rail.radius, out p, out q))
                    { closest = 0f; onPaddle = p; onRail = q; }
                }
                float radius = 0.0225f + rail.radius;
                bool touching = closest <= radius;
                if (touching && !rail.touching && previous && sample.timeSeconds >= rail.nextImpact)
                {
                    Vector3 relative = paddle.bladeVelocity + Vector3.Cross(paddle.bladeAngularVelocity, onPaddle - blade)
                        - sample.boatVelocity - Vector3.Cross(sample.boatAngularVelocity, onRail - sample.boatPosition);
                    Vector3 normal = (onPaddle - onRail).normalized;
                    float approach = normal.sqrMagnitude > 0.1f ? -Vector3.Dot(relative, normal) : relative.magnitude;
                    if (approach > 0.07f && EmitWoodImpact(onRail, approach, sample.timeSeconds)) rail.nextImpact = sample.timeSeconds + 0.18;
                }
                if (touching) rail.touching = true;
                else if (closest > radius * 1.35f) rail.touching = false;
            }
        }

        public bool EmitWoodImpact(Vector3 point, float approachSpeedMps, double simulationTime)
        {
            if (!AudioReady || !enabledInPrototype || !isActiveAndEnabled || _session == null || _session.ExperienceEnded
                || !SimulationValue.Finite(point) || !SimulationValue.Finite(approachSpeedMps) || approachSpeedMps < 0.07f) return false;
            float strength = Mathf.Clamp01(approachSpeedMps / 2f);
            string kind = strength > 0.75f ? "heavy" : strength > 0.3f ? "medium" : "light";
            string name = "impactWood_" + kind + "_" + (WoodImpacts % 3).ToString("000");
            AudioClip clip = Array.Find(_woodClips, value => value.name == name);
            if (clip == null) return false;
            _woodSource.transform.position = point; _woodSource.volume = 1f;
            _woodSource.pitch = 0.94f + 0.045f * (WoodImpacts % 4);
            _woodSource.PlayOneShot(clip, woodImpactVolume * (0.15f + 0.85f * strength));
            WoodImpacts++; LastWoodContactWorld = point; LastWoodImpactTime = simulationTime;
            return true;
        }

        public float SampleStrokeOutputRms() => OutputRms(_strokeSource);
        public float SampleWoodImpactOutputRms() => OutputRms(_woodSource);
        public float SampleWoodCreakOutputRms() => OutputRms(_creakSource);
        public float SampleWoodOutputRms() => Mathf.Max(OutputRms(_woodSource), OutputRms(_creakSource));
        float OutputRms(AudioSource source)
        {
            if (source == null || !AudioReady) return 0f;
            source.GetOutputData(_motionOutput, 0); float sum = 0f;
            foreach (float sample in _motionOutput) sum += sample * sample;
            return Mathf.Sqrt(sum / _motionOutput.Length);
        }

        void ResetMotionAudio()
        {
            _hasMotion = false; _motion = default; MotionSourceTime = -1;
            StrokeWaterLevel = HullWaterLevel = 0f; _nextCreakTime = 0.65;
            foreach (var rail in _rails) { rail.touching = false; rail.nextImpact = 0; }
            foreach (var source in new[] { _strokeSource, _hullSource, _woodSource, _creakSource })
                if (source != null) { source.Stop(); source.volume = 0f; }
        }
        void StopAllAudio()
        {
            ResetMotionAudio();
            if (_waterSource != null) _waterSource.Stop();
            if (_shoreSource != null) _shoreSource.Stop();
            if (_paddleSource != null) _paddleSource.Stop();
        }
        void OnDisable() { StopAllAudio(); }
        void DestroyMotionAudio()
        {
            if (_effects != null) _effects.PhysicsObserved -= ObserveMotion;
            if (_collisionRelay != null && _collisionRelay.owner == this) _collisionRelay.owner = null;
            if (_creakClips != null) foreach (var clip in _creakClips) if (clip != null) Destroy(clip);
            if (_splashVariants != null)
                foreach (var clip in _splashVariants) if (clip != null && clip != _splashClip) Destroy(clip);
            if (_strokeWaterClip != null) Destroy(_strokeWaterClip);
        }
    }
}
