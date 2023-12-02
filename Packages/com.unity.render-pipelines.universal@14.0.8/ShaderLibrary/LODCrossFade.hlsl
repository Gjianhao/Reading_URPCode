#ifndef UNIVERSAL_PIPELINE_LODCROSSFADE_INCLUDED
#define UNIVERSAL_PIPELINE_LODCROSSFADE_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

// 抖动纹理的倒数大小
float _DitheringTextureInvSize;

TEXTURE2D(_DitheringTexture);

half CopySign(half x, half s) {
    return (s >= 0) ? abs(x) : - abs(x);
}

// 这个方法的作用是为了实现基于屏幕空间抖动的LOD组之间的的交叉淡化，可以避免使用透明度造成的排序问题，也可以减少两个LOD之间的突现现象12。
void LODFadeCrossFade(float4 positionCS) {
    // 根据positionCS的xy分量和_DitheringTextureInvSize（抖动纹理的倒数大小）计算出一个uv坐标，用于采样抖动纹理。
    half2 uv = positionCS.xy * _DitheringTextureInvSize;

    // 使用sampler_PointRepeat（重复采样器）从_DitheringTexture（抖动纹理）中采样出一个alpha值，赋给d变量。
    half d = SAMPLE_TEXTURE2D(_DitheringTexture, sampler_PointRepeat, uv).a;

    // 使用unity_LODFade.x（当前LOD的淡化值）减去d的绝对值，得到一个新的d值。这个值表示当前像素是否在过渡区内，以及它的淡化权重。
    d = unity_LODFade.x - CopySign(d, unity_LODFade.x);

    // 使用clip函数对d进行裁剪，如果d小于0，就丢弃这个像素，否则保留这个像素。
    clip(d);
}

#endif
