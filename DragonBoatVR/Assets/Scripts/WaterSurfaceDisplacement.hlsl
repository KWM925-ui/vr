// Shared Gerstner geometry for water and foam, with WaveField's full coefficients
// and clock. Parametric displacement follows GPU Gems / Unity BoatAttack;
// world-position queries use the same inverse map as the existing CPU physics.
#ifndef DRAGONBOAT_WATER_DISPLACEMENT_INCLUDED
#define DRAGONBOAT_WATER_DISPLACEMENT_INCLUDED
#ifndef MAX_WAVES
#define MAX_WAVES 8
#endif
#define GRAVITY 9.81
#define TAU 6.28318530718

void Gerstner(float2 xz0, out float3 disp, out float3 dPdx, out float3 dPdz)
{
    disp = float3(0, 0, 0);
    dPdx = float3(1, 0, 0);
    dPdz = float3(0, 0, 1);
    int n = clamp(_WaveCount, 0, MAX_WAVES);
    [loop] for (int i = 0; i < n; i++)
    {
        float2 w2 = _Waves[i].xy;
        float wl = _Waves[i].z;
        if (_Waves[i].w <= 0.0 || wl <= 0.0) continue;
        float amp = _Waves[i].w * _Intensity;
        float2 dir = w2 / max(length(w2), 1e-5);
        float k = TAU / wl;
        float omega = sqrt(GRAVITY * k);
        float q = _Steepness[i] * _Horizontal;
        float theta = k * dot(dir, xz0) - omega * _SimTime + _PhaseOffsets[i];
        float s = sin(theta), c = cos(theta);
        float kA = k * amp, qkA = q * kA;
        disp.x += q * amp * dir.x * c;
        disp.z += q * amp * dir.y * c;
        disp.y += amp * s;
        dPdx.x -= qkA * dir.x * dir.x * s;
        dPdx.y += kA * dir.x * c;
        dPdx.z -= qkA * dir.x * dir.y * s;
        dPdz.x -= qkA * dir.x * dir.y * s;
        dPdz.y += kA * dir.y * c;
        dPdz.z -= qkA * dir.y * dir.y * s;
    }
}

void WaterSurfaceFrame(float3 dPdx, float3 dPdz, out float3 normal, out float compression)
{
    float3 n = cross(dPdz, dPdx);
    normal = dot(n, n) < 1e-8 ? float3(0, 1, 0) : normalize(n.y < 0 ? -n : n);
    compression = dPdx.x * dPdz.z - dPdx.z * dPdz.x;
}

float3 DisplaceWaterVertex(float3 position, out float3 normal, out float compression)
{
    // A water mesh vertex starts in the rest plane. Displace it once; applying
    // an inverse map first and then moving X/Z again changes the sampled surface.
    float3 disp, dPdx, dPdz;
    Gerstner(position.xz, disp, dPdx, dPdz);
    WaterSurfaceFrame(dPdx, dPdz, normal, compression);
    return position + disp;
}

float3 PlaceWaterSurfaceRibbon(float3 position, out float3 normal, out float compression)
{
    // A hull ribbon already names its desired world X/Z. Keep those coordinates
    // fixed and solve for the corresponding rest point, as WaveField.Sample does.
    float2 restXZ = position.xz;
    [loop] for (int j = 0; j < max(_InverseIterations, 1); j++)
    {
        float3 displacement, dx, dz;
        Gerstner(restXZ, displacement, dx, dz);
        float2 nextXZ = position.xz - displacement.xz;
        float error = abs(nextXZ.x - restXZ.x) + abs(nextXZ.y - restXZ.y);
        restXZ = nextXZ;
        if (error < 1e-4) break;
    }
    float3 disp, dPdx, dPdz;
    Gerstner(restXZ, disp, dPdx, dPdz);
    WaterSurfaceFrame(dPdx, dPdz, normal, compression);
    position.y += disp.y;
    return position;
}
#endif
