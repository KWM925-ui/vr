// Editor-only GPU readback of the exact production water displacement include.
Shader "Hidden/DragonBoatVR/WaterSurfaceProbe"
{
    Properties { _MainTex ("Known test positions", 2D) = "black" {} }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            Texture2D<float4> _MainTex;
            float _SimTime, _Intensity, _Horizontal;
            int _WaveCount, _ProbeKind;
            int _InverseIterations;
            float4 _Waves[8];
            float _PhaseOffsets[8], _Steepness[8];
            #include "../Scripts/WaterSurfaceDisplacement.hlsl"
            float4 frag(v2f_img input) : SV_Target
            {
                float3 origin = _MainTex.Load(int3(int2(input.pos.xy), 0)).xyz;
                if (_ProbeKind == 2) return float4(origin, 1.0);
                float3 normal; float compression;
                float3 position;
                if (_ProbeKind >= 3) position = PlaceWaterSurfaceRibbon(origin, normal, compression);
                else position = DisplaceWaterVertex(origin, normal, compression);
                return _ProbeKind == 0 || _ProbeKind == 3 ? float4(position, compression) : float4(normal, compression);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
