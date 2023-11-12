#ifndef UNIVERSAL_SURFACE_DATA_INCLUDED
#define UNIVERSAL_SURFACE_DATA_INCLUDED

// Must match Universal ShaderGraph master node
// 必须与 Universal ShaderGraph 主节点匹配
struct SurfaceData
{
    half3 albedo;  // 反照率  也就是基础颜色
    half3 specular; // 高光
    half  metallic; // 金属度
    half  smoothness; // 光滑度
    half3 normalTS; // 切线空间法线
    half3 emission; // 自发光
    half  occlusion; // 环境光遮蔽
    half  alpha; // 透明度
    half  clearCoatMask; // 清漆遮罩
    half  clearCoatSmoothness;  // 清漆光滑度
};

#endif
