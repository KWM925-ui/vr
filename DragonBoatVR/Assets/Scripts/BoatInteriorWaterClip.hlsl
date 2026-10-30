// Evaluate the displaced water fragment in the actual boat frame. A flat
// screen-depth cap cannot enclose a moving, curved water surface at its edges.
#ifndef DRAGONBOAT_INTERIOR_WATER_CLIP
#define DRAGONBOAT_INTERIOR_WATER_CLIP
TEXTURE2D(_BoatInteriorProfile);
SAMPLER(sampler_BoatInteriorProfile);
float _BoatInteriorEnabled;
float4x4 _BoatInteriorWorldToLocal;
float4 _BoatInteriorDomain, _BoatInteriorHeights, _BoatInteriorTexel;

bool IsInsideBoatWaterClip(float3 worldPosition)
{
    if (_BoatInteriorEnabled < 0.5) return false;
    float3 p = mul(_BoatInteriorWorldToLocal, float4(worldPosition, 1.0)).xyz;
    if (p.x < _BoatInteriorDomain.x || p.x > _BoatInteriorDomain.y
        || p.z < _BoatInteriorDomain.z || p.z > _BoatInteriorDomain.w
        || p.y < _BoatInteriorHeights.x) return false;
    float2 uv = float2((p.z-_BoatInteriorDomain.z)/(_BoatInteriorDomain.w-_BoatInteriorDomain.z),
        saturate((p.y-_BoatInteriorHeights.x)/(_BoatInteriorHeights.y-_BoatInteriorHeights.x)));
    uv = uv*(1.0-_BoatInteriorTexel.xy)+0.5*_BoatInteriorTexel.xy;
    float2 sides = SAMPLE_TEXTURE2D(_BoatInteriorProfile, sampler_BoatInteriorProfile, uv).rg;
    return sides.x <= p.x && p.x <= sides.y;
}
#endif
