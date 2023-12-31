#ifndef UNIVERSAL_DEPTH_ONLY_PASS_INCLUDED
#define UNIVERSAL_DEPTH_ONLY_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct Attributes {
    float4 position : POSITION;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings {
    float2 uv : TEXCOORD0;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings DepthOnlyVertex(Attributes input) {
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);  // VR相关

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionCS = TransformObjectToHClip(input.position.xyz);
    return output;
}

// 作用是为了实现深度缓冲的渲染，可以用于阴影、遮挡剔除、后处理等效果。
half DepthOnlyFragment(Varyings input) : SV_TARGET {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // 从基础贴图中获取透明度，并根据基础颜色和 _Cutoff 阈值进行计算和剪裁。
    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);

    #ifdef LOD_FADE_CROSSFADE
        LODFadeCrossFade(input.positionCS);
    #endif

    // 返回当前像素的深度值
    return input.positionCS.z;  // 这里返回深度值z，于URP之前的源码有区别，之前直接返回0
}
#endif
