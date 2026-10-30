// Narrow, water-conforming hull foam using Crest's MIT texture and shading.
// See Assets/Resources/Water/Crest-NOTICE.txt for source and license.
Shader "DragonBoatVR/BoatFoam"
{
    Properties
    {
        _MainTex ("泡沫图", 2D) = "black" {}
        _CrestFoamTexture ("Crest Foam", 2D) = "black" {}
        _Tint ("染色", Color) = (0.94, 0.97, 1.0, 1.0)
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+10"
            "RenderPipeline" = "UniversalPipeline"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "BoatFoamForward"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "CrestFoamLighting.hlsl"
            #include "BoatInteriorWaterClip.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _SimTime, _Intensity, _Horizontal;
                int _WaveCount;
                int _InverseIterations;
                float4 _Waves[8];
                float _PhaseOffsets[8];
                float _Steepness[8];
            CBUFFER_END
            float2 _FoamFlow;
            #include "WaterSurfaceDisplacement.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR0;
                float  fogCoord : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                float compression;
                OUT.positionWS = PlaceWaterSurfaceRibbon(TransformObjectToWorld(IN.positionOS.xyz), OUT.normalWS, compression);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                if (IsInsideBoatWaterClip(IN.positionWS)) discard;
                half edge = smoothstep(0.0, 0.22, IN.uv.x) * (1.0 - smoothstep(0.78, 1.0, IN.uv.x));
                edge *= smoothstep(0.0, 0.07, IN.uv.y);
                half amount = IN.color.a * _Tint.a * edge * 0.8;
                half3 N = normalize(IN.normalWS);
                half3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));
                half4 col = CrestFoamSurface(IN.positionWS.xz, _FoamFlow, _SimTime, amount,
                    N, V, normalize(_MainLightPosition.xyz), _Tint.rgb);
                col.rgb = MixFog(col.rgb, IN.fogCoord);
                return col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
