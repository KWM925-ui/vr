// Adapted from Crest Ocean System OceanFoam.hlsl, MIT.
// Copyright (c) 2019 Wave Harmonic and contributors.
// Pinned source and full license: Assets/Resources/Water/Crest-NOTICE.txt.
// URP integration: one world-space tile instead of Crest's ocean LOD cascades.
#ifndef DRAGONBOAT_CREST_FOAM_INCLUDED
#define DRAGONBOAT_CREST_FOAM_INCLUDED
TEXTURE2D(_CrestFoamTexture);
SAMPLER(sampler_CrestFoamTexture);

half CrestWhiteFoamTexture(float2 worldXZ, float2 flow, float time, half amount, float2 offset)
{
    // Crest's complementary phases reset the flow UVs without visible stretching.
    float phase0 = fmod(time, 2.0);
    float phase1 = fmod(time + 1.0, 2.0);
    float weight = 1.0 - abs(phase0 - 1.0);
    float2 uvOffset = offset + time / 10.0;
    float2 uv0 = (1.25 * (worldXZ - flow * phase0) + uvOffset) / 1.6;
    float2 uv1 = (1.25 * (worldXZ - flow * phase1) + uvOffset) / 1.6;
    half ft = lerp(SAMPLE_TEXTURE2D(_CrestFoamTexture, sampler_CrestFoamTexture, uv1).r,
                   SAMPLE_TEXTURE2D(_CrestFoamTexture, sampler_CrestFoamTexture, uv0).r, weight);
    // Black-point fading makes old foam break into separate filaments and bubbles.
    half blackPoint = saturate(1.0 - amount);
    return smoothstep(blackPoint, blackPoint + 0.16, ft);
}

half4 CrestFoamSurface(float2 worldXZ, float2 flow, float time, half amount,
                       half3 normal, half3 view, half3 light, half3 tint)
{
    half foam = CrestWhiteFoamTexture(worldXZ, flow, time, amount, 0.0);
    half dx = CrestWhiteFoamTexture(worldXZ, flow, time, amount, float2(0.012, 0.0));
    half dz = CrestWhiteFoamTexture(worldXZ, flow, time, amount, float2(0.0, 0.012));
    half3 foamNormal = normalize(normal + 0.6 * half3(foam - dx, 0.0, foam - dz));
    half3 lighting = SampleSH(foamNormal) + _MainLightColor.rgb * (0.25 + 0.55 * saturate(dot(foamNormal, light)));
    half glint = pow(saturate(dot(reflect(-view, foamNormal), light)), 48.0) * 0.1;
    return half4(tint * lighting + glint * _MainLightColor.rgb, foam * saturate(amount * 4.0));
}

half CrestSubsurfaceBubbles(float2 worldXZ, float2 flow, float time, half amount, half3 normal, half3 view)
{
    // Crest's blurred, displaced bubble layer sits below the sharp surface foam.
    float2 uv = (worldXZ - flow * time + 0.05 * time * float2(0.866, 0.5)) / 1.6;
    uv += 0.08 * normal.xz - 0.015 * view.xz / max(0.2, dot(normal, view));
    return SAMPLE_TEXTURE2D_LOD(_CrestFoamTexture, sampler_CrestFoamTexture, uv * 0.74, 3.0).r * amount;
}
#endif
