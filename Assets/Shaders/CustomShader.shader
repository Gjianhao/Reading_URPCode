Shader "CustomShader"
{
    Properties
    {
        [KeywordEnum(Normal,R,G,B)] _CL("Color Select", Float) = 0
        [ToggleOff] _HH("HH", Int) = 0
    }
    SubShader
    {
        Tags { 
            "RenderType"="Opaque" 
            "Queue"="Geometry"
            "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma prefer_hlslcc gles  // 让OpenGL ES 2.0 也使用HLSLcc编译器，因为其他版本的图形库默认使用HLSLcc编译器
            #pragma exclude_renderers d3d11_9x  // 由于兼容性问题，排除掉d3d11_9x渲染器

            #pragma shader_feature_local __ _CL_R _CL_G _CL_B
            #pragma shader_feature _HH_OFF

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct appdata
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
            CBUFFER_END
            

            v2f vert (appdata v)
            {
                v2f o;
                VertexPositionInputs position_inputs = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = position_inputs.positionCS;
                o.positionWS = position_inputs.positionWS;

                VertexNormalInputs normal_inputs = GetVertexNormalInputs(v.normalOS.xyz, v.tangentOS);
                o.normalWS = normal_inputs.normalWS;
                o.uv = v.uv;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                Light light = GetMainLight();
                float3 L = light.direction;
                float3 V = GetWorldSpaceNormalizeViewDir(i.positionWS.xyz);
                float3 H = normalize(V + L);
                float3 N = normalize(i.normalWS);


                // 取消勾选时，为黑色
                #ifdef _HH_OFF
                    return half4(0,0,0,0);
                #else
                    return half4(1,1,1,0);
                #endif
                
                #ifdef _CL_R
                    return half4(1,0,0,0);
                #elif _CL_G
                    return half4(0,1,0,0);
                #elif _CL_B
                    return half4(0,0,1,0);
                #endif
                
                
                
                return pow(max(dot(N, H), 0), 32);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            // #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            // -------------------------------------
            // Material Keywords
            // #pragma shader_feature_local_fragment _ALPHATEST_ON
            // #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            // -------------------------------------
            // Unity defined keywords
            // #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            //--------------------------------------
            // GPU Instancing
            // #pragma multi_compile_instancing
            // #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }
    }
}
