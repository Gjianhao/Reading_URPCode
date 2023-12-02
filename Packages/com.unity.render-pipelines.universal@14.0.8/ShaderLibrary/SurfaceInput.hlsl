#ifndef UNIVERSAL_INPUT_SURFACE_INCLUDED
#define UNIVERSAL_INPUT_SURFACE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceData.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
float4 _BaseMap_TexelSize;
float4 _BaseMap_MipInfo;
TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);
TEXTURE2D(_EmissionMap);
SAMPLER(sampler_EmissionMap);

///////////////////////////////////////////////////////////////////////////////
//                      Material Property Helpers                            //
///////////////////////////////////////////////////////////////////////////////
//计算透明属性和透明裁切功能，需要传入纹理的alpha通道，基础色，裁切值
half Alpha(half albedoAlpha, half4 color, half cutoff) {
    #if !defined(_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A) && !defined(_GLOSSINESS_FROM_BASE_ALPHA) // 它们分别对应Lit和SimpleLit的Source选项，表示是否从基础贴图的alpha通道获取光滑度或者光泽度。
        half alpha = albedoAlpha * color.a; // 如果没有定义这两个宏，说明基础贴图的alpha通道没有被用于其他用途，那么就用它乘以颜色的alpha值，得到最终的透明度。
    #else
        half alpha = color.a; // 如果定义了这两个宏，说明基础贴图的alpha通道已经被用于光滑度或者光泽度，那么就直接用颜色的alpha值作为透明度。
    #endif

    alpha = AlphaDiscard(alpha, cutoff);

    return alpha;
}

// 采样Albedo纹理
half4 SampleAlbedoAlpha(float2 uv, TEXTURE2D_PARAM(albedoAlphaMap, sampler_albedoAlphaMap)) {
    return half4(SAMPLE_TEXTURE2D(albedoAlphaMap, sampler_albedoAlphaMap, uv));
}

// 法线贴图的采样
half3 SampleNormal(float2 uv, TEXTURE2D_PARAM(bumpMap, sampler_bumpMap), half scale = half(1.0)) {
    #ifdef _NORMALMAP
        half4 n = SAMPLE_TEXTURE2D(bumpMap, sampler_bumpMap, uv);  // 对法线贴图采样
        #if BUMP_SCALE_NOT_SUPPORTED // 不支持调节法线强度 在SimpleLit、BakeLit
            return UnpackNormal(n);
        #else
            return UnpackNormalScale(n, scale);
        #endif
    #else
        return half3(0.0h, 0.0h, 1.0h);  // 没有使用法线纹理，就返回默认值
    #endif
}

// 采样自发光
half3 SampleEmission(float2 uv, half3 emissionColor, TEXTURE2D_PARAM(emissionMap, sampler_emissionMap)) {
    #ifndef _EMISSION
        return 0;
    #else
        return SAMPLE_TEXTURE2D(emissionMap, sampler_emissionMap, uv).rgb * emissionColor;
    #endif
}

#endif
