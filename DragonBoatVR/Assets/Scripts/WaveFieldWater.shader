// WaveFieldWater.shader — 龙舟 VR 同源水面（Gerstner 版）
//
// 与 CPU 侧 WaveField.cs 逐项同源：
//   θ   = k·dot(D, (x0,z0)) − ω·t + φ       k = 2π/λ,  ω = sqrt(g·k)
//   dx  = Σ Q·A·D.x·cosθ      dz = Σ Q·A·D.z·cosθ      dy = Σ A·sinθ
//   dP/dx0 = (1 − Σ Q·k·A·D.x²·sinθ,  Σ k·A·D.x·cosθ,  −Σ Q·k·A·D.x·D.z·sinθ)
//   dP/dz0 = (−Σ Q·k·A·D.x·D.z·sinθ,  Σ k·A·D.z·cosθ,  1 − Σ Q·k·A·D.z²·sinθ)
//   N = normalize(cross(dP/dz0, dP/dx0))
// 参数由 WaterSurface.cs 每帧通过 MaterialPropertyBlock 下发（同一个 SimTime 时钟）。
//
// 顶点着色器同时做水平位移与垂直位移，因此网格必须够密（见 WaterSurface.BuildMesh）。
Shader "DragonBoatVR/WaveFieldWater"
{
    Properties
    {
        _BaseColor ("深水色", Color) = (0.04, 0.20, 0.28, 0.94)
        _ShallowColor ("浪尖色", Color) = (0.30, 0.66, 0.68, 0.86)
        _FoamColor ("白沫色", Color) = (0.92, 0.96, 0.98, 1.0)
        _FresnelPower ("菲涅尔强度", Range(0.1, 8)) = 3.5
        _SpecStrength ("高光强度", Range(0, 2)) = 0.8
        _FoamThreshold ("浪尖起白沫阈值", Range(0, 1.5)) = 0.12
        _DepthTint ("岸边浅水提亮", Range(0, 1)) = 0.35

        // 视觉层：天空盒反射与交界泡沫（不改变物理同源的 Gerstner 波场）
        _SkyReflectStrength ("天空反射强度", Range(0, 1)) = 0.85
        _SkyReflectTint ("天空反射染色", Color) = (1.0, 0.92, 0.82, 1.0)
        _DepthFadeDistance ("浅水过渡距离(米)", Range(0.5, 8)) = 2.6
        _ShoreFoamWidth ("交界泡沫宽度(米)", Range(0.1, 2)) = 0.55
        _DepthOcclusionBias ("水面遮挡偏移(米)", Range(0, 0.1)) = 0.018

        // 平面反射 + 折射 + 细节法线(全部为视觉层)
        _PlanarStrength ("镜面反射强度", Range(0, 1)) = 0.9
        _PlanarDistortion ("镜面反射扰动", Range(0, 0.3)) = 0.08
        [HideInInspector] _DistantReflectionRoughness ("Distant reflection roughness", Range(0, 1)) = 1
        _RefractionStrength ("折射强度", Range(0, 0.3)) = 0.06
        _NormalMap ("细节法线贴图", 2D) = "bump" {}
        _NormalStrength ("细节法线强度", Range(0, 2)) = 0.36
        _NormalScaleA ("法线层A密度", Range(0.02, 4)) = 2.2
        _NormalScaleB ("法线层B密度", Range(0.02, 4)) = 0.9
        _NormalScrollA ("法线层A滚动(x,z)", Vector) = (0.028, 0.021, 0, 0)

        // 运行时由脚本下发，默认值仅供编辑器预览
        _SimTime ("仿真时间", Float) = 0
        _Intensity ("浪强", Float) = 1
        _Horizontal ("水平位移开关", Float) = 1
        _WaveCount ("波数", Int) = 3
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "WaveFieldForward"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
            #include "CrestFoamLighting.hlsl"
            #include "BoatInteriorWaterClip.hlsl"

            #define MAX_WAVES 8

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ShallowColor;
                float4 _FoamColor;
                float  _FresnelPower;
                float  _SpecStrength;
                float  _FoamThreshold;
                float  _DepthTint;
                float  _SkyReflectStrength;
                float4 _SkyReflectTint;
                float  _DepthFadeDistance;
                float  _ShoreFoamWidth;
                float  _DepthOcclusionBias;
                float  _PlanarStrength;
                float  _PlanarDistortion;
                float  _DistantReflectionRoughness;
                float  _RefractionStrength;
                float  _NormalStrength;
                float  _NormalScaleA;
                float  _NormalScaleB;
                float4 _NormalScrollA;
                float  _SimTime;
                float  _Intensity;
                float  _Horizontal;
                int    _WaveCount;
                int    _InverseIterations;
                float4 _Waves[MAX_WAVES];       // x:dirX y:dirZ z:wavelength w:amplitude
                float  _PhaseOffsets[MAX_WAVES];
                float  _Steepness[MAX_WAVES];
            CBUFFER_END

            #include "WaterSurfaceDisplacement.hlsl"

            // 全局:平面反射RT(PlanarWaterReflection 写入)与不透明纹理(URP)
            TEXTURE2D(_WaterPlanarReflection);
            SAMPLER(sampler_WaterPlanarReflection);
            TEXTURE2D(_WaterPlanarReflectionRight);
            SAMPLER(sampler_WaterPlanarReflectionRight);
            TEXTURE2D_X(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            // 交互泡沫/尾迹累积层(InteractionFoamTrail 写入;x/z=锚点,y=窗口,w=强度)
            TEXTURE2D(_FoamTrailTex);
            SAMPLER(sampler_FoamTrailTex);
            float4 _FoamTrailAnchor;
            float2 _FoamFlow;
            int _InteractionRippleCount;
            float4 _InteractionRipples[24];
            float4 _InteractionRippleShapes[24];
            // PlanarWaterReflection 每帧写入的全局开关(0=无RT,回退天空反射)
            float _WaterPlanarOn;
            float4x4 _WaterPlanarWorldToUV;
            float4x4 _WaterPlanarWorldToUVRight;
            float _WaterPlanarStereo, _WaterPlanarActiveEye;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float  elevation : TEXCOORD2;    // 相对静水面的抬升
                float  worldY : TEXCOORD3;
                float  fogCoord : TEXCOORD4;
                float compression : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // 世界系二维旋转（法线层各向采样 + 逆旋转回世界系）
            float2 rot2(float2 v, float ca, float sa) { return float2(v.x*ca - v.y*sa, v.x*sa + v.y*ca); }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float originalY = posWS.y;
                float3 normal;
                float compression;
                posWS = DisplaceWaterVertex(posWS, normal, compression);

                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);

                OUT.normalWS = normal;
                OUT.elevation = posWS.y - originalY;
                OUT.compression = compression;
                OUT.worldY = posWS.y;
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

        float2 InteractionRippleSlope(float2 worldXZ)
        {
            float2 slope = 0.0;
            [loop] for (int i = 0; i < min(_InteractionRippleCount, 24); i++)
            {
                float4 ripple = _InteractionRipples[i];
                float4 shape = _InteractionRippleShapes[i];
                float2 offset = worldXZ - ripple.xy;
                float2 forward = shape.xy;
                float2 right = float2(forward.y, -forward.x);
                float along = dot(offset, forward), across = dot(offset, right);
                float stretch = abs(shape.z);
                float radius = length(float2(across, along / stretch));
                float delta = radius - ripple.z;
                if (abs(delta) > ripple.w * 3.0 || radius < 0.001) continue;
                float envelope = exp(-delta * delta / (ripple.w * ripple.w));
                float phase = delta * 42.0;
                float derivative = shape.w * envelope * (-2.0 * delta / (ripple.w * ripple.w) * cos(phase) - 42.0 * sin(phase));
                // Boat wakes leave trailing arcs; paddle contacts expand in all directions.
                if (shape.z < 0.0) derivative *= saturate(-along / max(ripple.z, 0.05) * 2.0 + 0.3);
                slope += derivative * (across * right + along / (stretch * stretch) * forward) / radius;
            }
            return clamp(slope, -0.18, 0.18);
        }

        half4 frag(Varyings IN) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
            if (IsInsideBoatWaterClip(IN.positionWS)) discard;
            float3 N = normalize(IN.normalWS);
            float3 V = normalize(_WorldSpaceCameraPos.xyz - IN.positionWS);
            float3 L = normalize(_MainLightPosition.xyz);

            // Fine surface detail is visual only; the shared wave field remains
            // the sole source for buoyancy, paddle drag and platform motion.
            // 两层不同密度/速度的滚动法线替代重复正弦细节波。
            // 各层独立旋转采样系破除平铺感；detail 向量旋转回世界系后再叠加。
            // 距离淡出：远处的贴图级细节压到 0，只留几何 Gerstner 波，避免
            // 远场高频闪烁，也让大尺度起伏读得更清楚。
            float2 p = IN.positionWS.xz;
            const float CA_A = 0.8660, SA_A = 0.5;        //  +30°
            const float CA_B = 0.5736,  SA_B = -0.8192;    //  -55°
            float camDist = length(_WorldSpaceCameraPos.xz - IN.positionWS.xz);
            float distantTransition = smoothstep(5.0, 22.0, camDist);
            float distantRoughness = distantTransition * (2.0 - distantTransition) * _DistantReflectionRoughness;
            float detailFade = exp(-camDist * 0.018);      // ~38m 处衰减到一半以下
            float2 uvA = rot2(p, CA_A, SA_A) * _NormalScaleA + rot2(_NormalScrollA.xy, CA_A, SA_A) * _SimTime * 6.0;
            float2 uvB = rot2(p, CA_B, SA_B) * (-_NormalScaleB) + rot2(_NormalScrollA.yx, CA_B, SA_B) * _SimTime * 4.0;
            // 第三层微涟漪(约0.25m/格,更快滚动):近场碎光来源,随距离最快淡出
            const float CA_C = 0.4540, SA_C = 0.8910;        //  +63°
            float2 uvC = rot2(p, CA_C, SA_C) * 4.0 + rot2(_NormalScrollA.xy, CA_C, SA_C) * _SimTime * 8.5;
            float4 nA4 = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvA);
            float4 nB4 = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvB);
            float4 nC4 = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvC);
            float2 nA = rot2(UnpackNormalScale(nA4, 1.0).xy, CA_A, -SA_A);
            float2 nB = rot2(UnpackNormalScale(nB4, 1.0).xy, CA_B, -SA_B);
            float2 nC = rot2(UnpackNormalScale(nC4, 1.0).xy, CA_C, -SA_C);
            // 微涟漪权重低、淡出更快(平方),只负责近场碎光不抢主波
            float2 detailXZ = (nA + nB + nC * 0.35 * detailFade) * (_NormalStrength * detailFade);
            if (camDist < 24.0) detailXZ -= InteractionRippleSlope(IN.positionWS.xz);
            // At a seated camera height the apparent far-water band begins
            // only a few metres away. Keep resolved, broader ripples there
            // instead of fading every surface slope into a flat mirror.
            // Reuse the photographic normal texture in world coordinates so
            // the pattern stays on the river while cameras and boat move.
            float2 uvFar = rot2(p, CA_B, SA_B) * 0.08
                + rot2(_NormalScrollA.xy, CA_B, SA_B) * _SimTime * 0.7;
            float2 farSlope = rot2(UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvFar), 1.0).xy,
                CA_B, -SA_B);
            detailXZ += farSlope * (0.6 * distantRoughness);
            N = normalize(float3(N.x + detailXZ.x, N.y, N.z + detailXZ.y));

            // 场景深度 → 两个度量：
            //   thickness：视线穿越水体的路径长（吸收/折射用，物理上沿视线）；
            //   belowWater：场景点沉在水下的垂直深度（浅水提亮/交界泡沫用）。
            // 交界泡沫改用垂直度量：低视角时视线厚度在船边会虚高、泡沫环消失；
            // 垂直深度与视角无关，船壳水线和岸线都能稳定起沫。
            float2 screenUV = IN.positionCS.xy / _ScaledScreenParams.xy;
            float sceneRaw = SampleSceneDepth(screenUV);
            float sceneEyeZ = LinearEyeDepth(sceneRaw, _ZBufferParams);
            float waterEyeZ = mul(UNITY_MATRIX_V, float4(IN.positionWS, 1.0)).z;
            // 水面是透明材质，不能依靠 ZWrite 让船体自动盖住它。
            // 若深度纹理里的不透明物体在当前水面之前，当前像素属于船体/岸壁
            // 的水面上方部分，直接丢弃水面片元，避免水波穿过船舷。
            float waterEyeDepth = -waterEyeZ;
            if (sceneEyeZ > 0.0 && sceneEyeZ < waterEyeDepth - _DepthOcclusionBias)
                discard;
            float thickness = max(sceneEyeZ + waterEyeZ, 0.0);   // 两者均为负 z
            float3 sceneWS = ComputeWorldSpacePosition(screenUV, sceneRaw, UNITY_MATRIX_I_VP);
            float belowWater = max(IN.positionWS.y - sceneWS.y, 0.0);
            float shallow = 1.0 - saturate(belowWater / max(_DepthFadeDistance, 0.1));
            // 二次曲线：贴着交界最宽最实，向外快速收窄——陡岸/直壁船壳下
            // 也能保住一条可见软边（线性带在陡壁上是亚像素，直接不可见）。
            float foamEdge = 1.0 - saturate(belowWater / clamp(_ShoreFoamWidth, 0.05, 0.16));
            float shoreFoam = foamEdge * foamEdge * saturate(thickness / 0.025);

            // 折射:法线扰动屏幕UV采样不透明纹理;若扰动后命中水面前的物体则回退
            float2 refrUV = screenUV + detailXZ * _RefractionStrength * saturate(thickness);
            float refrScene = LinearEyeDepth(SampleSceneDepth(refrUV), _ZBufferParams);
            if (refrScene < -waterEyeZ) refrUV = screenUV;
            half3 sceneCol = SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture,
                sampler_CameraOpaqueTexture, UnityStereoTransformScreenSpaceTex(refrUV)).rgb;

            // 吸收:越厚越不透明、越偏向深水色
            float absorption = 1.0 - exp(-max(thickness, 0.0) * 1.1);

            // 基色随浪高过渡:谷底深、浪尖浅;近岸混入折射场景色
            float crest = saturate(IN.elevation * 2.2 + 0.5);
            half3 crestCol = lerp(_BaseColor.rgb, _ShallowColor.rgb, crest * 0.65).rgb;
            half3 waterBase = lerp(sceneCol * _ShallowColor.rgb * 1.35, crestCol,
                saturate(absorption * 1.3));
            waterBase = lerp(waterBase, _ShallowColor.rgb * 1.15, shallow * _DepthTint);

            // 反射:平面镜反射(岸/树/船)优先,无RT时回退天空盒
            float3 R = reflect(-V, N);
            R.y = abs(R.y);
            // Unresolved distant ripples spread reflected light instead of
            // turning into a perfect mirror as their normal detail fades out.
            // The first 5 metres retain the original level-zero reflection.
            half4 skySample = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0,
                samplerunity_SpecCube0, R, 2.0 + 1.5 * distantRoughness);
            half3 skyCol = DecodeHDREnvironment(skySample, unity_SpecCube0_HDR)
                * _SkyReflectTint.rgb;
            // The reflection belongs to this camera. Project world positions
            // into its texture, independently of the split-screen viewport.
            // Instanced/multiview uses the fragment's eye index. Multi-pass uses
            // the active eye captured at this camera's rendering callback.
            #if defined(USING_STEREO_MATRICES)
                bool rightEye = _WaterPlanarStereo > 0.5 && unity_StereoEyeIndex == 1;
            #else
                bool rightEye = _WaterPlanarStereo > 0.5 && _WaterPlanarActiveEye > 0.5;
            #endif
            float4 reflected = rightEye
                ? mul(_WaterPlanarWorldToUVRight, float4(IN.positionWS, 1.0))
                : mul(_WaterPlanarWorldToUV, float4(IN.positionWS, 1.0));
            float2 planarUV = reflected.xy / max(reflected.w, 1e-5) + detailXZ * _PlanarDistortion;
            planarUV += farSlope * (_PlanarDistortion * 1.5 * distantRoughness);
            half3 planarCol = rightEye
                ? SAMPLE_TEXTURE2D_LOD(_WaterPlanarReflectionRight,
                    sampler_WaterPlanarReflectionRight, planarUV, 4.5 * distantRoughness).rgb
                : SAMPLE_TEXTURE2D_LOD(_WaterPlanarReflection,
                    sampler_WaterPlanarReflection, planarUV, 4.5 * distantRoughness).rgb;
            half3 reflCol = lerp(skyCol, planarCol * _SkyReflectTint.rgb,
                saturate(_WaterPlanarOn * _PlanarStrength * (1.0 - 0.25 * distantRoughness)));

            float fres = 0.05 + 0.95 * pow(1.0 - saturate(dot(N, V)), _FresnelPower);
            half4 col;
            col.rgb = lerp(waterBase, reflCol, saturate(fres * _SkyReflectStrength));
            col.a = saturate(lerp(_BaseColor.a, 1.0, absorption) * 0.5 + absorption * 0.5);

            // 漫反射
            float ndl = saturate(dot(N, L));
            col.rgb *= 0.62 + 0.38 * ndl;

            // 太阳光路:真实水面的拉长闪烁光带。镜像反射向量和太阳方向做
            // 双瓣匹配——窄瓣出粼粼亮点,宽瓣铺出整条光路;涟漪法线天然把
            // 光带打散。比单一 Blinn 高光点真实得多(地平线5 的水就靠这条)。
            float3 Rn = normalize(R);
            float glintNarrow = pow(saturate(dot(Rn, L)), 240) * 1.6;
            float glintWide   = pow(saturate(dot(Rn, L)), 28) * 0.22;
            float3 H = normalize(L + V);
            float specPow = pow(saturate(dot(N, H)), 96);
            col.rgb += (specPow * _SpecStrength * 0.6
                      + (glintNarrow + glintWide) * _SpecStrength) * _MainLightColor.rgb;

            // 泡沫：浪尖破碎 + 岸线/船体交界 + 交互尾迹累积层
            // 交互层(Crest 泡沫模拟的桌面版):船壳/尾迹/桨叶画入跟随船的
            // 48m 窗口纹理并按秒级衰减——白色水痕会重叠扩散、慢慢消散。
            float2 trailUV = (IN.positionWS.xz - _FoamTrailAnchor.xz) / _FoamTrailAnchor.y + 0.5;
            float2 tcl = saturate(trailUV);
            float trailEdge = saturate(tcl.x * tcl.y * (1.0 - tcl.x) * (1.0 - tcl.y) * 16.0);
            float trailFoam = SAMPLE_TEXTURE2D(_FoamTrailTex, sampler_FoamTrailTex, tcl).r
                            * trailEdge * _FoamTrailAnchor.w;
            // Crest generates breaking foam from horizontal compression, not simply
            // from every elevated part of a gentle river wave.
            float crestFoam = saturate((0.85 - IN.compression) * 4.0);
            float foamAmount = min(0.82, max(crestFoam, max(trailFoam * 0.72, shoreFoam * 0.48)));
            half4 foam = CrestFoamSurface(IN.positionWS.xz, _FoamFlow, _SimTime, foamAmount, N, V, L, _FoamColor.rgb);
            half bubbles = CrestSubsurfaceBubbles(IN.positionWS.xz, _FoamFlow, _SimTime, foamAmount, N, V);
            col.rgb = lerp(col.rgb, _ShallowColor.rgb * 1.15, bubbles * 0.12);
            col.rgb = lerp(col.rgb, foam.rgb, foam.a);
            col.a = lerp(col.a, 1.0, max(foam.a * 0.8, fres * 0.4));

            col.rgb = MixFog(col.rgb, IN.fogCoord);
            return half4(col.rgb, saturate(col.a));
        }
            ENDHLSL
        }
    }
    FallBack Off
}
