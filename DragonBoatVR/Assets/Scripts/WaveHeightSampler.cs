using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>Captured wave coefficients for many visual height queries at one instant.
    /// Uses WaveField's exact displacement and inverse-map iteration, without unused
    /// normals/particle velocities. Capture again after any wave/time/height change.</summary>
    public sealed class WaveHeightSampler
    {
        struct Component
        {
            public Vector2 direction;
            public float waveNumber, amplitude, steepness, omega, phaseOffset;
            public bool enabled;
        }
        Component[] _components = new Component[0];
        float _baseHeight;
        float _time;
        int _iterations;

        public void Capture(WaveField wave)
        {
            _baseHeight = wave.BaseHeight;
            _time = wave.SimTime;
            _iterations = Mathf.Max(1, wave.inverseIterations);
            int count = wave.waves == null ? 0 : wave.waves.Length;
            if (_components.Length != count) _components = new Component[count];
            for (int i = 0; i < count; i++)
            {
                var source = wave.waves[i];
                float k = 2f * Mathf.PI / source.wavelength;
                float amplitude = source.amplitude * wave.intensity;
                _components[i] = new Component
                {
                    enabled = source.amplitude > 0f && source.wavelength > 0f,
                    direction = source.direction.sqrMagnitude > 1e-6f ? source.direction.normalized : Vector2.right,
                    waveNumber = k, amplitude = amplitude,
                    steepness = wave.horizontalDisplacement ? source.steepness : 0f,
                    omega = Mathf.Sqrt(9.81f * k),
                    phaseOffset = source.phaseOffset
                };
            }
        }

        float Phase(in Component wave, float x, float z)
            => wave.waveNumber * Vector2.Dot(wave.direction, new Vector2(x, z)) - wave.omega * _time + wave.phaseOffset;

        public float GetHeight(float x, float z)
        {
            float x0 = x, z0 = z;
            for (int iteration = 0; iteration < _iterations; iteration++)
            {
                float dx = 0f, dz = 0f;
                foreach (var wave in _components)
                {
                    if (!wave.enabled) continue;
                    float cosine = Mathf.Cos(Phase(wave, x0, z0));
                    dx += wave.steepness * wave.amplitude * wave.direction.x * cosine;
                    dz += wave.steepness * wave.amplitude * wave.direction.y * cosine;
                }
                float nextX = x - dx, nextZ = z - dz;
                float error = Mathf.Abs(nextX - x0) + Mathf.Abs(nextZ - z0);
                x0 = nextX; z0 = nextZ;
                if (error < 1e-4f) break;
            }
            float elevation = 0f;
            foreach (var wave in _components)
                if (wave.enabled) elevation += wave.amplitude * Mathf.Sin(Phase(wave, x0, z0));
            return _baseHeight + elevation;
        }
    }
}
