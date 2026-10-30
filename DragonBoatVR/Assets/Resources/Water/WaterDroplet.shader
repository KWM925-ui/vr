Shader "DragonBoatVR/WaterDroplet"
{
    Properties { _Color ("Water tint", Color) = (0.68, 0.86, 0.9, 0.6) }
    SubShader
    {
        Tags { "Queue"="Transparent+12" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.5
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END
            struct Input { float4 positionOS : POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Output { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; float fog : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            Output vert(Input i)
            {
                Output o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv; o.color = i.color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }
            half4 frag(Output i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = i.uv * 2.0 - 1.0;
                float radius = dot(p, p);
                float edge = 1.0 - smoothstep(0.45, 1.0, radius);
                float glint = pow(saturate(1.0 - length(p - float2(-0.25, 0.25)) * 1.7), 5.0);
                half3 color = lerp(_Color.rgb * i.color.rgb * 0.65, half3(1,1,1), glint);
                return half4(MixFog(color, i.fog), edge * _Color.a * i.color.a);
            }
            ENDHLSL
        }
    }
}
