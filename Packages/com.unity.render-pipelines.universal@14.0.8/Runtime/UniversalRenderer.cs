using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Rendering modes for Universal renderer.通用管线渲染模式
    /// </summary>
    public enum RenderingMode
    {
        /// <summary>Render all objects and lighting in one pass, with a hard limit on the number of lights that can be applied on an object.</summary>
        /// 一个Pass渲染所有对象和灯光，并严格限制对象上可应用的灯光数量。
        Forward = 0,
        /// <summary>Render all objects and lighting in one pass using a clustered data structure to access lighting data.</summary>
        /// 使用聚类数据结构访问照明数据，一个Pass染所有对象和照明。
        [InspectorName("Forward+")]
        ForwardPlus = 2,
        /// <summary>Render all objects first in a g-buffer pass, then apply all lighting in a separate pass using deferred shading.</summary>
        /// 首先在一个 g 缓冲通道中渲染所有对象，然后在另一个通道中使用延迟着色技术应用所有照明。
        Deferred = 1
    };

    /// <summary>
    /// 就是Z-PrePass。When the Universal Renderer should use Depth Priming in Forward mode.
    /// </summary>
    public enum DepthPrimingMode
    {
        /// <summary>Depth Priming will never be used.</summary>
        Disabled,
        /// <summary>Depth Priming will only be used if there is a depth prepass needed by any of the render passes.</summary>
        Auto,
        /// <summary>A depth prepass will be explicitly requested so Depth Priming can be used.</summary>
        Forced,
    }

    /// <summary>
    /// Default renderer for Universal RP.
    /// This renderer is supported on all Universal RP supported platforms.
    /// It uses a classic forward rendering strategy with per-object light culling.
    /// 它采用了经典的前向渲染策略，并对每个对象进行光线剔除。
    /// </summary>
    public sealed partial class UniversalRenderer : ScriptableRenderer
    {
#if UNITY_SWITCH || UNITY_ANDROID
        const GraphicsFormat k_DepthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;
        const int k_DepthBufferBits = 24;
#else
        const GraphicsFormat k_DepthStencilFormat = GraphicsFormat.D32_SFloat_S8_UInt;
        const int k_DepthBufferBits = 32;
#endif

        const int k_FinalBlitPassQueueOffset = 1;
        const int k_AfterFinalBlitPassQueueOffset = k_FinalBlitPassQueueOffset + 1;

        static readonly List<ShaderTagId> k_DepthNormalsOnly = new List<ShaderTagId> { new ShaderTagId("DepthNormalsOnly") };

        // 创建一个 Profiling 的静态类，用于存放一些和性能分析相关的字段，方便在其他地方使用。
        private static class Profiling
        {
            // 一个字符串常量，表示类的名称，即 UniversalRenderer。
            private const string k_Name = nameof(UniversalRenderer);
            // 一个 ProfilingSampler 类型的静态只读字段，表示一个用于分析创建相机渲染目标的性能的采样器，它的名称是由 k_Name 和 CreateCameraRenderTarget 拼接而成的。
            public static readonly ProfilingSampler createCameraRenderTarget = new ProfilingSampler($"{k_Name}.{nameof(CreateCameraRenderTarget)}");
        }

        /// <inheritdoc/>
        /// 延迟渲染只能使用Base相机
        public override int SupportedCameraStackingTypes()
        {
            switch (m_RenderingMode)
            {
                case RenderingMode.Forward:
                case RenderingMode.ForwardPlus:
                    return 1 << (int)CameraRenderType.Base | 1 << (int)CameraRenderType.Overlay; // 左移0位或者1位，分别代表 1 或者 2
                case RenderingMode.Deferred:
                    return 1 << (int)CameraRenderType.Base; // 左移 0 位，代表 1
                default:
                    return 0;
            }
        }

        // Rendering mode setup from UI. The final rendering mode used can be different. See renderingModeActual.
        // 从用户界面设置渲染模式。最终使用的渲染模式可能不同。请参见 renderingModeActual。
        internal RenderingMode renderingModeRequested => m_RenderingMode;

        // Actual rendering mode, which may be different (ex: wireframe rendering, hardware not capable of deferred rendering).
        // 实际渲染模式，可能不同（例如：线框渲染，硬件无法进行延迟渲染）。
        /*
        首先，判断renderingModeRequested是否等于Deferred，如果是，说明请求的是延迟渲染模式，那么就继续判断是否满足以下条件之一：
            1. GL.wireframe：一个布尔值，表示是否开启了线框模式，如果是，说明不能使用延迟渲染模式，因为它需要使用多个渲染目标。
            2. DebugHandler != null && DebugHandler.IsActiveModeUnsupportedForDeferred：一个布尔值，表示是否存在一个DebugHandler对象，并且它的IsActiveModeUnsupportedForDeferred属性为真，如果是，说明当前的调试模式不支持延迟渲染模式，比如光线追踪。
            3. m_DeferredLights == null：一个布尔值，表示是否没有一个m_DeferredLights对象，如果是，说明没有初始化延迟光照的类，不能使用延迟渲染模式。
            4. !m_DeferredLights.IsRuntimeSupportedThisFrame()：一个布尔值，表示是否当前帧不支持运行时的延迟渲染模式，如果是，说明有一些限制条件，比如渲染目标的数量、格式、分辨率等，不能使用延迟渲染模式。
            5. m_DeferredLights.IsOverlay：一个布尔值，表示是否当前的相机类型是覆盖类型，如果是，说明不能使用延迟渲染模式，因为它只支持基础类型的相机。
        然后，如果满足以上任何一个条件，就返回Forward，表示实际使用的是正向渲染模式，否则返回renderingModeRequested，表示实际使用的是请求的渲染模式。
        */
        internal RenderingMode renderingModeActual => renderingModeRequested == RenderingMode.Deferred &&
                                                      (GL.wireframe ||
                                                       (DebugHandler != null && DebugHandler.IsActiveModeUnsupportedForDeferred) ||
                                                       m_DeferredLights == null ||
                                                       !m_DeferredLights.IsRuntimeSupportedThisFrame() ||
                                                       m_DeferredLights.IsOverlay)
        ? RenderingMode.Forward
        : this.renderingModeRequested;

        bool m_Clustering; // 渲染模式是否是Forward+

        internal bool accurateGbufferNormals => m_DeferredLights != null ? m_DeferredLights.AccurateGbufferNormals : false;

#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
        internal bool needTransparencyPass { get { return !UniversalRenderPipeline.asset.useAdaptivePerformance || !AdaptivePerformance.AdaptivePerformanceRenderSettings.SkipTransparentObjects;; } }
#endif
        /// <summary>Property to control the depth priming behavior of the forward rendering path. 在前向渲染中，用于控制z-prepass的行为的属性</summary>
        public DepthPrimingMode depthPrimingMode { get { return m_DepthPrimingMode; } set { m_DepthPrimingMode = value; } }
        DepthOnlyPass m_DepthPrepass; // 只提前渲染深度的Pass
        DepthNormalOnlyPass m_DepthNormalPrepass; // 提前渲染深度和法线
        CopyDepthPass m_PrimedDepthCopyPass;
        MotionVectorRenderPass m_MotionVectorPass;
        MainLightShadowCasterPass m_MainLightShadowCasterPass; // 主光源阴影投射Pass
        AdditionalLightsShadowCasterPass m_AdditionalLightsShadowCasterPass;
        GBufferPass m_GBufferPass;
        CopyDepthPass m_GBufferCopyDepthPass;
        DeferredPass m_DeferredPass;
        DrawObjectsPass m_RenderOpaqueForwardOnlyPass;
        DrawObjectsPass m_RenderOpaqueForwardPass;
        DrawObjectsWithRenderingLayersPass m_RenderOpaqueForwardWithRenderingLayersPass;
        DrawSkyboxPass m_DrawSkyboxPass;
        CopyDepthPass m_CopyDepthPass;
        CopyColorPass m_CopyColorPass;
        TransparentSettingsPass m_TransparentSettingsPass;
        DrawObjectsPass m_RenderTransparentForwardPass;
        InvokeOnRenderObjectCallbackPass m_OnRenderObjectCallbackPass;
        FinalBlitPass m_FinalBlitPass;
        CapturePass m_CapturePass;
#if ENABLE_VR && ENABLE_XR_MODULE
        XROcclusionMeshPass m_XROcclusionMeshPass;
        CopyDepthPass m_XRCopyDepthPass;
#endif
#if UNITY_EDITOR
        CopyDepthPass m_FinalDepthCopyPass;
#endif
        DrawScreenSpaceUIPass m_DrawOffscreenUIPass;
        DrawScreenSpaceUIPass m_DrawOverlayUIPass;

        internal RenderTargetBufferSystem m_ColorBufferSystem;

        internal RTHandle m_ActiveCameraColorAttachment;
        RTHandle m_ColorFrontBuffer;
        internal RTHandle m_ActiveCameraDepthAttachment;
        internal RTHandle m_CameraDepthAttachment;
        RTHandle m_XRTargetHandleAlias;
        internal RTHandle m_DepthTexture;
        RTHandle m_NormalsTexture;
        RTHandle m_DecalLayersTexture;
        RTHandle m_OpaqueColor;
        RTHandle m_MotionVectorColor;
        RTHandle m_MotionVectorDepth;

        ForwardLights m_ForwardLights; // 前向照明数据
        DeferredLights m_DeferredLights;
        RenderingMode m_RenderingMode;
        DepthPrimingMode m_DepthPrimingMode;
        CopyDepthMode m_CopyDepthMode;
        bool m_DepthPrimingRecommended;
        StencilState m_DefaultStencilState;
        LightCookieManager m_LightCookieManager;
        IntermediateTextureMode m_IntermediateTextureMode;

        // Materials used in URP Scriptable Render Passes
        Material m_BlitMaterial = null;
        Material m_BlitHDRMaterial = null;
        Material m_CopyDepthMaterial = null;
        Material m_SamplingMaterial = null;
        Material m_StencilDeferredMaterial = null;
        Material m_CameraMotionVecMaterial = null;
        Material m_ObjectMotionVecMaterial = null;

        PostProcessPasses m_PostProcessPasses;
        internal ColorGradingLutPass colorGradingLutPass { get => m_PostProcessPasses.colorGradingLutPass; }
        internal PostProcessPass postProcessPass { get => m_PostProcessPasses.postProcessPass; }
        internal PostProcessPass finalPostProcessPass { get => m_PostProcessPasses.finalPostProcessPass; }
        internal RTHandle colorGradingLut { get => m_PostProcessPasses.colorGradingLut; }
        internal DeferredLights deferredLights { get => m_DeferredLights; }

        /// <summary>
        /// Constructor for the Universal Renderer.
        /// </summary>
        /// <param name="data">The settings to create the renderer with.</param>
        public UniversalRenderer(UniversalRendererData data) : base(data)
        {
            // Query and cache runtime platform info first before setting up URP. 在设置 URP 之前，先查询和缓存运行时平台信息。
            PlatformAutoDetect.Initialize();

#if ENABLE_VR && ENABLE_XR_MODULE
            Experimental.Rendering.XRSystem.Initialize(XRPassUniversal.Create, data.xrSystemData.shaders.xrOcclusionMeshPS, data.xrSystemData.shaders.xrMirrorViewPS);
#endif

            // 创建材质
            m_BlitMaterial = CoreUtils.CreateEngineMaterial(data.shaders.coreBlitPS);
            m_BlitHDRMaterial = CoreUtils.CreateEngineMaterial(data.shaders.blitHDROverlay);
            m_CopyDepthMaterial = CoreUtils.CreateEngineMaterial(data.shaders.copyDepthPS);
            m_SamplingMaterial = CoreUtils.CreateEngineMaterial(data.shaders.samplingPS);
            m_StencilDeferredMaterial = CoreUtils.CreateEngineMaterial(data.shaders.stencilDeferredPS);
            m_CameraMotionVecMaterial = CoreUtils.CreateEngineMaterial(data.shaders.cameraMotionVector);
            m_ObjectMotionVecMaterial = CoreUtils.CreateEngineMaterial(data.shaders.objectMotionVector);

            // 设置模板测试状态
            StencilStateData stencilData = data.defaultStencilState;
            m_DefaultStencilState = StencilState.defaultValue;
            m_DefaultStencilState.enabled = stencilData.overrideStencilState;
            m_DefaultStencilState.SetCompareFunction(stencilData.stencilCompareFunction);
            m_DefaultStencilState.SetPassOperation(stencilData.passOperation);
            m_DefaultStencilState.SetFailOperation(stencilData.failOperation);
            m_DefaultStencilState.SetZFailOperation(stencilData.zFailOperation);

            // 中间纹理渲染模式
            m_IntermediateTextureMode = data.intermediateTextureMode;

            // 如果支持光照 Cookie，创建光照 Cookie 管理器
            if (UniversalRenderPipeline.asset?.supportsLightCookies ?? false)
            {
                var settings = LightCookieManager.Settings.Create();
                var asset = UniversalRenderPipeline.asset;
                if (asset)
                {
                    settings.atlas.format = asset.additionalLightsCookieFormat;
                    settings.atlas.resolution = asset.additionalLightsCookieResolution;
                }

                m_LightCookieManager = new LightCookieManager(ref settings);
            }

            this.stripShadowsOffVariants = true; // 去除阴影关闭的变体
            this.stripAdditionalLightOffVariants = true; // 去除额外光源关闭的变体
#if ENABLE_VR && ENABLE_VR_MODULE
#if PLATFORM_WINRT || PLATFORM_ANDROID
            // AdditionalLightOff variant is available on HL&Quest platform due to performance consideration.
            this.stripAdditionalLightOffVariants = !PlatformAutoDetect.isXRMobile;
#endif
#endif

            // 前向光源初始化
            ForwardLights.InitParams forwardInitParams;
            forwardInitParams.lightCookieManager = m_LightCookieManager;
            forwardInitParams.forwardPlus = data.renderingMode == RenderingMode.ForwardPlus;
            m_Clustering = data.renderingMode == RenderingMode.ForwardPlus;
            m_ForwardLights = new ForwardLights(forwardInitParams);
            //m_DeferredLights.LightCulling = data.lightCulling;
            this.m_RenderingMode = data.renderingMode;
            this.m_DepthPrimingMode = data.depthPrimingMode;
            this.m_CopyDepthMode = data.copyDepthMode;
            useRenderPassEnabled = data.useNativeRenderPass && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;

            // 移动平台不推荐z-prepass
#if UNITY_ANDROID || UNITY_IOS || UNITY_TVOS
            this.m_DepthPrimingRecommended = false;
#else
            this.m_DepthPrimingRecommended = true;
#endif

            // Note: Since all custom render passes inject first and we have stable sort,
            // we inject the builtin passes in the before events.
            // 注意:由于所有自定义渲染通道都是先注入的，而且我们有稳定的排序，所以我们在before事件中注入内置的通道。
            m_MainLightShadowCasterPass = new MainLightShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);
            m_AdditionalLightsShadowCasterPass = new AdditionalLightsShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);

#if ENABLE_VR && ENABLE_XR_MODULE
            m_XROcclusionMeshPass = new XROcclusionMeshPass(RenderPassEvent.BeforeRenderingOpaques);
            // Schedule XR copydepth right after m_FinalBlitPass
            m_XRCopyDepthPass = new CopyDepthPass(RenderPassEvent.AfterRendering + k_AfterFinalBlitPassQueueOffset, m_CopyDepthMaterial);
#endif
            m_DepthPrepass = new DepthOnlyPass(RenderPassEvent.BeforeRenderingPrePasses, RenderQueueRange.opaque, data.opaqueLayerMask);
            m_DepthNormalPrepass = new DepthNormalOnlyPass(RenderPassEvent.BeforeRenderingPrePasses, RenderQueueRange.opaque, data.opaqueLayerMask);
            m_MotionVectorPass = new MotionVectorRenderPass(m_CameraMotionVecMaterial, m_ObjectMotionVecMaterial);

            // 如果是前向渲染
            if (renderingModeRequested == RenderingMode.Forward || renderingModeRequested == RenderingMode.ForwardPlus)
            {
                m_PrimedDepthCopyPass = new CopyDepthPass(RenderPassEvent.AfterRenderingPrePasses, m_CopyDepthMaterial, true);
            }

            // 如果是延迟渲染
            if (this.renderingModeRequested == RenderingMode.Deferred)
            {
                var deferredInitParams = new DeferredLights.InitParams();
                deferredInitParams.stencilDeferredMaterial = m_StencilDeferredMaterial;
                deferredInitParams.lightCookieManager = m_LightCookieManager;
                m_DeferredLights = new DeferredLights(deferredInitParams, useRenderPassEnabled);
                m_DeferredLights.AccurateGbufferNormals = data.accurateGbufferNormals;

                m_GBufferPass = new GBufferPass(RenderPassEvent.BeforeRenderingGbuffer, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference, m_DeferredLights);
                // Forward-only pass only runs if deferred renderer is enabled.
                // It allows specific materials to be rendered in a forward-like pass.
                // We render both gbuffer pass and forward-only pass before the deferred lighting pass so we can minimize copies of depth buffer and
                // benefits from some depth rejection.
                // - If a material can be rendered either forward or deferred, then it should declare a UniversalForward and a UniversalGBuffer pass.
                // - If a material cannot be lit in deferred (unlit, bakedLit, special material such as hair, skin shader), then it should declare UniversalForwardOnly pass
                // - Legacy materials have unamed pass, which is implicitely renamed as SRPDefaultUnlit. In that case, they are considered forward-only too.
                // TO declare a material with unnamed pass and UniversalForward/UniversalForwardOnly pass is an ERROR, as the material will be rendered twice.
                StencilState forwardOnlyStencilState = DeferredLights.OverwriteStencil(m_DefaultStencilState, (int)StencilUsage.MaterialMask);
                ShaderTagId[] forwardOnlyShaderTagIds = new ShaderTagId[]
                {
                    new ShaderTagId("UniversalForwardOnly"),
                    new ShaderTagId("SRPDefaultUnlit"), // Legacy shaders (do not have a gbuffer pass) are considered forward-only for backward compatibility
                    new ShaderTagId("LightweightForward") // Legacy shaders (do not have a gbuffer pass) are considered forward-only for backward compatibility
                };
                int forwardOnlyStencilRef = stencilData.stencilReference | (int)StencilUsage.MaterialUnlit;
                m_GBufferCopyDepthPass = new CopyDepthPass(RenderPassEvent.BeforeRenderingGbuffer + 1, m_CopyDepthMaterial, true);
                m_DeferredPass = new DeferredPass(RenderPassEvent.BeforeRenderingDeferredLights, m_DeferredLights);
                m_RenderOpaqueForwardOnlyPass = new DrawObjectsPass("Render Opaques Forward Only", forwardOnlyShaderTagIds, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, forwardOnlyStencilState, forwardOnlyStencilRef);
            }

            // Always create this pass even in deferred because we use it for wireframe rendering in the Editor or offscreen depth texture rendering.
            m_RenderOpaqueForwardPass = new DrawObjectsPass(URPProfileId.DrawOpaqueObjects, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            m_RenderOpaqueForwardWithRenderingLayersPass = new DrawObjectsWithRenderingLayersPass(URPProfileId.DrawOpaqueObjects, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);

            bool copyDepthAfterTransparents = m_CopyDepthMode == CopyDepthMode.AfterTransparents; // 在透明物体之后拷贝深度

            m_CopyDepthPass = new CopyDepthPass(
                copyDepthAfterTransparents ? RenderPassEvent.AfterRenderingTransparents : RenderPassEvent.AfterRenderingSkybox,
                m_CopyDepthMaterial,
                shouldClear: true,  // 表示清除目标深度缓冲。
                copyResolvedDepth: RenderingUtils.MultisampleDepthResolveSupported() && copyDepthAfterTransparents); // 复制已经解析的深度缓冲。

            m_DrawSkyboxPass = new DrawSkyboxPass(RenderPassEvent.BeforeRenderingSkybox);
            m_CopyColorPass = new CopyColorPass(RenderPassEvent.AfterRenderingSkybox, m_SamplingMaterial, m_BlitMaterial);
#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
            if (needTransparencyPass)
# endif
            {
                m_TransparentSettingsPass = new TransparentSettingsPass(RenderPassEvent.BeforeRenderingTransparents, data.shadowTransparentReceive);
                m_RenderTransparentForwardPass = new DrawObjectsPass(URPProfileId.DrawTransparentObjects, false, RenderPassEvent.BeforeRenderingTransparents, RenderQueueRange.transparent, data.transparentLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            }
            m_OnRenderObjectCallbackPass = new InvokeOnRenderObjectCallbackPass(RenderPassEvent.BeforeRenderingPostProcessing);

            m_DrawOffscreenUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.BeforeRenderingPostProcessing, true);
            m_DrawOverlayUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.AfterRendering + k_AfterFinalBlitPassQueueOffset, false); // after m_FinalBlitPass

            {
                var postProcessParams = PostProcessParams.Create();
                postProcessParams.blitMaterial = m_BlitMaterial;
                postProcessParams.requestHDRFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                var asset = UniversalRenderPipeline.asset;
                if (asset)
                    postProcessParams.requestHDRFormat = UniversalRenderPipeline.MakeRenderTextureGraphicsFormat(asset.supportsHDR, asset.hdrColorBufferPrecision, false);

                m_PostProcessPasses = new PostProcessPasses(data.postProcessData, ref postProcessParams);
            }

            m_CapturePass = new CapturePass(RenderPassEvent.AfterRendering);
            m_FinalBlitPass = new FinalBlitPass(RenderPassEvent.AfterRendering + k_FinalBlitPassQueueOffset, m_BlitMaterial, m_BlitHDRMaterial);

#if UNITY_EDITOR
            m_FinalDepthCopyPass = new CopyDepthPass(RenderPassEvent.AfterRendering + 9, m_CopyDepthMaterial);
#endif

            // RenderTexture format depends on camera and pipeline (HDR, non HDR, etc)
            // Samples (MSAA) depend on camera and pipeline
            m_ColorBufferSystem = new RenderTargetBufferSystem("_CameraColorAttachment");

            supportedRenderingFeatures = new RenderingFeatures();

            if (this.renderingModeRequested == RenderingMode.Deferred)
            {
                // Deferred rendering does not support MSAA.
                this.supportedRenderingFeatures.msaa = false;

                // Avoid legacy platforms: use vulkan instead.
                unsupportedGraphicsDeviceTypes = new GraphicsDeviceType[]
                {
                    GraphicsDeviceType.OpenGLCore,
                    GraphicsDeviceType.OpenGLES2,
                    GraphicsDeviceType.OpenGLES3
                };
            }

            LensFlareCommonSRP.mergeNeeded = 0;
            LensFlareCommonSRP.maxLensFlareWithOcclusionTemporalSample = 1;
            LensFlareCommonSRP.Initialize();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            m_ForwardLights.Cleanup();
            m_GBufferPass?.Dispose();
            m_PostProcessPasses.Dispose();
            m_FinalBlitPass?.Dispose();
            m_DrawOffscreenUIPass?.Dispose();
            m_DrawOverlayUIPass?.Dispose();

            m_XRTargetHandleAlias?.Release();

            ReleaseRenderTargets();

            base.Dispose(disposing);
            CoreUtils.Destroy(m_BlitMaterial);
            CoreUtils.Destroy(m_BlitHDRMaterial);
            CoreUtils.Destroy(m_CopyDepthMaterial);
            CoreUtils.Destroy(m_SamplingMaterial);
            CoreUtils.Destroy(m_StencilDeferredMaterial);
            CoreUtils.Destroy(m_CameraMotionVecMaterial);
            CoreUtils.Destroy(m_ObjectMotionVecMaterial);

            CleanupRenderGraphResources();

            LensFlareCommonSRP.Dispose();
        }

        internal override void ReleaseRenderTargets()
        {
            m_ColorBufferSystem.Dispose();
            if (m_DeferredLights != null && !m_DeferredLights.UseRenderPass)
                m_GBufferPass?.Dispose();

            m_PostProcessPasses.ReleaseRenderTargets();
            m_MainLightShadowCasterPass?.Dispose();
            m_AdditionalLightsShadowCasterPass?.Dispose();

            m_CameraDepthAttachment?.Release();
            m_DepthTexture?.Release();
            m_NormalsTexture?.Release();
            m_DecalLayersTexture?.Release();
            m_OpaqueColor?.Release();
            m_MotionVectorColor?.Release();
            m_MotionVectorDepth?.Release();
            hasReleasedRTs = true;
        }

        private void SetupFinalPassDebug(ref CameraData cameraData)
        {
            if ((DebugHandler != null) && DebugHandler.IsActiveForCamera(ref cameraData))
            {
                if (DebugHandler.TryGetFullscreenDebugMode(out DebugFullScreenMode fullScreenDebugMode, out int textureHeightPercent) && (fullScreenDebugMode != DebugFullScreenMode.ReflectionProbeAtlas || m_Clustering))
                {
                    Camera camera = cameraData.camera;
                    float screenWidth = camera.pixelWidth;
                    float screenHeight = camera.pixelHeight;

                    var relativeSize = Mathf.Clamp01(textureHeightPercent / 100f);
                    var height = relativeSize * screenHeight;
                    var width = relativeSize * screenWidth;

                    if (fullScreenDebugMode == DebugFullScreenMode.ReflectionProbeAtlas)
                    {
                        // Ensure that atlas is not stretched, but doesn't take up more than the percentage in any dimension.
                        var texture = m_ForwardLights.reflectionProbeManager.atlasRT;
                        var targetWidth = height * texture.width / texture.height;
                        if (targetWidth > width)
                        {
                            height = width * texture.height / texture.width;
                        }
                        else
                        {
                            width = targetWidth;
                        }
                    }

                    float normalizedSizeX = width / screenWidth;
                    float normalizedSizeY = height / screenHeight;

                    Rect normalizedRect = new Rect(1 - normalizedSizeX, 1 - normalizedSizeY, normalizedSizeX, normalizedSizeY);

                    switch (fullScreenDebugMode)
                    {
                        case DebugFullScreenMode.Depth:
                            {
                                DebugHandler.SetDebugRenderTarget(m_DepthTexture.nameID, normalizedRect, true);
                                break;
                            }
                        case DebugFullScreenMode.AdditionalLightsShadowMap:
                            {
                                DebugHandler.SetDebugRenderTarget(m_AdditionalLightsShadowCasterPass.m_AdditionalLightsShadowmapHandle, normalizedRect, false);
                                break;
                            }
                        case DebugFullScreenMode.MainLightShadowMap:
                            {
                                DebugHandler.SetDebugRenderTarget(m_MainLightShadowCasterPass.m_MainLightShadowmapTexture, normalizedRect, false);
                                break;
                            }
                        case DebugFullScreenMode.ReflectionProbeAtlas:
                            {
                                DebugHandler.SetDebugRenderTarget(m_ForwardLights.reflectionProbeManager.atlasRT, normalizedRect, false);
                                break;
                            }
                        default:
                            {
                                break;
                            }
                    }
                }
                else
                {
                    DebugHandler.ResetDebugRenderTarget();
                }
            }
        }

        bool IsDepthPrimingEnabled(ref CameraData cameraData)
        {
            // depth priming requires an extra depth copy, disable it on platforms not supporting it (like GLES when MSAA is on)
            // depth priming需要额外的深度拷贝，在不支持深度打底的平台上请禁用它（如开启 MSAA 时的 GLES）。
            if (!CanCopyDepth(ref cameraData))
                return false;

            // 除了移动端，都推荐深度Priming
            bool depthPrimingRequested = (m_DepthPrimingRecommended && m_DepthPrimingMode == DepthPrimingMode.Auto) || m_DepthPrimingMode == DepthPrimingMode.Forced;
            bool isForwardRenderingMode = m_RenderingMode == RenderingMode.Forward || m_RenderingMode == RenderingMode.ForwardPlus;
            bool isFirstCameraToWriteDepth = cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth;
            // Enabled Depth priming when baking Reflection Probes causes artefacts (UUM-12397)
            bool isNotReflectionCamera = cameraData.cameraType != CameraType.Reflection;

            return depthPrimingRequested && isForwardRenderingMode && isFirstCameraToWriteDepth && isNotReflectionCamera;
        }

        bool IsGLESDevice()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
        }

        /// <summary>
        /// 是否是OpenGLES的设备
        /// </summary>
        /// <returns></returns>
        bool IsGLDevice()
        {
            return IsGLESDevice() || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;
        }

        /// <inheritdoc />
        /// 配置此渲染器将执行的渲染通道。此方法在每一帧中按摄像机调用。
        public override void Setup(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_ForwardLights.PreSetup(ref renderingData); // 如果是Forward+模式

            ref CameraData cameraData = ref renderingData.cameraData; // 保存多个与相机相关的渲染设置
            Camera camera = cameraData.camera;
            RenderTextureDescriptor cameraTargetDescriptor = cameraData.cameraTargetDescriptor;

            var cmd = renderingData.commandBuffer;
            if (DebugHandler != null)
            {
                DebugHandler.Setup(context, ref renderingData);

                if (DebugHandler.IsActiveForCamera(ref cameraData))
                {
                    if (DebugHandler.WriteToDebugScreenTexture(ref cameraData))
                    {
                        RenderTextureDescriptor colorDesc = cameraData.cameraTargetDescriptor;
                        DebugHandler.ConfigureColorDescriptorForDebugScreen(ref colorDesc, cameraData.pixelWidth, cameraData.pixelHeight);
                        RenderingUtils.ReAllocateIfNeeded(ref DebugHandler.DebugScreenColorHandle, colorDesc, name: "_DebugScreenColor");

                        RenderTextureDescriptor depthDesc = cameraData.cameraTargetDescriptor;
                        DebugHandler.ConfigureDepthDescriptorForDebugScreen(ref depthDesc, k_DepthBufferBits, cameraData.pixelWidth, cameraData.pixelHeight);
                        RenderingUtils.ReAllocateIfNeeded(ref DebugHandler.DebugScreenDepthHandle, depthDesc, name: "_DebugScreenDepth");
                    }

                    if (DebugHandler.HDRDebugViewIsActive(ref cameraData))
                    {
                        DebugHandler.hdrDebugViewPass.Setup(ref cameraData, DebugHandler.DebugDisplaySettings.lightingSettings.hdrDebugMode);
                        EnqueuePass(DebugHandler.hdrDebugViewPass);
                    }
                }
            }

            if (cameraData.cameraType != CameraType.Game)
                useRenderPassEnabled = false;  // 不是Game相机就这是为false

            // Special path for depth only offscreen cameras. Only write opaques + transparents. 仅用于深度离屏摄像机的特殊路径。只写入不透明+透明。
            bool isOffscreenDepthTexture = cameraData.targetTexture != null && cameraData.targetTexture.format == RenderTextureFormat.Depth;
            if (isOffscreenDepthTexture)
            {
                ConfigureCameraTarget(k_CameraTarget, k_CameraTarget);
                SetupRenderPasses(in renderingData);
                EnqueuePass(m_RenderOpaqueForwardPass);

                // TODO: Do we need to inject transparents and skybox when rendering depth only camera? They don't write to depth.
                EnqueuePass(m_DrawSkyboxPass);
#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
                if (!needTransparencyPass)
                    return;
#endif
                EnqueuePass(m_RenderTransparentForwardPass);
                return;
            }

            // Assign the camera color target early in case it is needed during AddRenderPasses.
            // 尽早指定相机的颜色目标，以防在AddRenderPasses过程中需要它。
            bool isPreviewCamera = cameraData.isPreviewCamera;
            var createColorTexture = ((rendererFeatures.Count != 0 && m_IntermediateTextureMode == IntermediateTextureMode.Always) && !isPreviewCamera) || (Application.isEditor && m_Clustering);

            // Gather render passe input requirements 收集渲染输入要求
            RenderPassInputSummary renderPassInputs = GetRenderPassInputs(ref renderingData);

            // Gather render pass require rendering layers event and mask size 收集渲染传递需要渲染图层事件和掩码大小
            bool requiresRenderingLayer = RenderingLayerUtils.RequireRenderingLayers(this, rendererFeatures, cameraTargetDescriptor.msaaSamples,
                out var renderingLayersEvent, out var renderingLayerMaskSize);

            // All passes that use write to rendering layers are excluded from gl
            // 所有使用写入渲染层的传递都不包括在 gl 中。
            // So we disable it to avoid setting multiple render targets
            // 因此，我们将其禁用，以避免设置多个渲染目标
            if (IsGLDevice())
                requiresRenderingLayer = false;

            bool renderingLayerProvidesByDepthNormalPass = false;
            bool renderingLayerProvidesRenderObjectPass = false;
            if (requiresRenderingLayer && renderingModeActual != RenderingMode.Deferred)
            {
                switch (renderingLayersEvent)
                {
                    case RenderingLayerUtils.Event.DepthNormalPrePass:
                        renderingLayerProvidesByDepthNormalPass = true;
                        break;
                    case RenderingLayerUtils.Event.Opaque:
                        renderingLayerProvidesRenderObjectPass = true;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            // Enable depth normal prepass
            if (renderingLayerProvidesByDepthNormalPass)
                renderPassInputs.requiresNormalsTexture = true;

            // TODO: investigate the order of call, had to change because of requiresRenderingLayer
            if (m_DeferredLights != null)
            {
                m_DeferredLights.RenderingLayerMaskSize = renderingLayerMaskSize;
                m_DeferredLights.UseDecalLayers = requiresRenderingLayer;

                // TODO: This needs to be setup early, otherwise gbuffer attachments will be allocated with wrong size
                m_DeferredLights.HasNormalPrepass = renderPassInputs.requiresNormalsTexture;

                m_DeferredLights.ResolveMixedLightingMode(ref renderingData);
                m_DeferredLights.IsOverlay = cameraData.renderType == CameraRenderType.Overlay;
            }

            // Should apply post-processing after rendering this camera?
            bool applyPostProcessing = cameraData.postProcessEnabled && m_PostProcessPasses.isCreated;

            // There's at least a camera in the camera stack that applies post-processing
            bool anyPostProcessing = renderingData.postProcessingEnabled && m_PostProcessPasses.isCreated;

            // If Camera's PostProcessing is enabled and if there any enabled PostProcessing requires depth texture as shader read resource (Motion Blur/DoF)
            // 如果启用了相机的后期处理，并且如果启用了后期处理，则需要深度纹理作为着色器读取资源（运动模糊/DoF）。
            bool cameraHasPostProcessingWithDepth = applyPostProcessing && cameraData.postProcessingRequiresDepthTexture;

            // TODO: We could cache and generate the LUT before rendering the stack
            // TODO: 我们可以在渲染堆栈之前缓存并生成 LUT
            bool generateColorGradingLUT = cameraData.postProcessEnabled && m_PostProcessPasses.isCreated;
            bool isSceneViewOrPreviewCamera = cameraData.isSceneViewCamera || cameraData.cameraType == CameraType.Preview;
            useDepthPriming = IsDepthPrimingEnabled(ref cameraData);
            // This indicates whether the renderer will output a depth texture.
            // 表示渲染器是否会输出深度纹理。下面三种情况表示需要深度纹理：1、勾选深度纹理；2、在renderPass中需要深度纹理或者需要运动向量的时候；3、选了强制DepthPriming
            bool requiresDepthTexture = cameraData.requiresDepthTexture || renderPassInputs.requiresDepthTexture || m_DepthPrimingMode == DepthPrimingMode.Forced;

#if UNITY_EDITOR
            bool isGizmosEnabled = UnityEditor.Handles.ShouldRenderGizmos();
#else
            bool isGizmosEnabled = false;
#endif

            bool mainLightShadows = m_MainLightShadowCasterPass.Setup(ref renderingData);
            bool additionalLightShadows = m_AdditionalLightsShadowCasterPass.Setup(ref renderingData);
            bool transparentsNeedSettingsPass = m_TransparentSettingsPass.Setup(ref renderingData);

            bool forcePrepass = (m_CopyDepthMode == CopyDepthMode.ForcePrepass); // 强制预处理

            // Depth prepass is generated in the following cases:
            // - If game or offscreen camera requires it we check if we can copy the depth from the rendering opaques pass and use that instead.
            // - 如果游戏或离屏的摄像机需要，我们会检查是否可以"从渲染不透明pass中Copy Depth"，然后使用它来代替。
            // - Scene or preview cameras always require a depth texture. We do a depth pre-pass to simplify it and it shouldn't matter much for editor.
            // - 场景或预览摄像机总是需要深度纹理。我们会进行深度预处理来简化它，这对编辑器来说应该没什么影响。
            // - Render passes require it
            // 下面几种情况需要深度预处理pre-z：1、需要深度纹理或者相机后处理需要深度；2、并且不能拷贝深度缓冲或者强制进行深度预处理
            bool requiresDepthPrepass = (requiresDepthTexture || cameraHasPostProcessingWithDepth) && (!CanCopyDepth(ref renderingData.cameraData) || forcePrepass);
            requiresDepthPrepass |= isSceneViewOrPreviewCamera;  // 如果是场景视图或者预览相机，那么需要深度预处理。
            requiresDepthPrepass |= isGizmosEnabled; // 如果启用了Gizmos，那么需要深度预处理。
            requiresDepthPrepass |= isPreviewCamera; // 如果是预览相机，那么需要深度预处理。
            requiresDepthPrepass |= renderPassInputs.requiresDepthPrepass; // 如果渲染流程的输入要求深度预处理，那么需要深度预处理。
            requiresDepthPrepass |= renderPassInputs.requiresNormalsTexture; // 如果渲染流程的输入要求法线纹理，那么需要深度预处理。

            // Current aim of depth prepass is to generate a copy of depth buffer, it is NOT to prime depth buffer and reduce overdraw on non-mobile platforms.
            // When deferred renderer is enabled, depth buffer is already accessible so depth prepass is not needed.
            // The only exception is for generating depth-normal textures: SSAO pass needs it and it must run before forward-only geometry.
            // DepthNormal prepass will render:
            // - forward-only geometry when deferred renderer is enabled
            // - all geometry when forward renderer is enabled
            if (requiresDepthPrepass && this.renderingModeActual == RenderingMode.Deferred && !renderPassInputs.requiresNormalsTexture)
                requiresDepthPrepass = false; // 如果需要深度预处理，且当前的渲染模式是延迟渲染，且不需要法线纹理，那么不需要深度预处理。

            requiresDepthPrepass |= useDepthPriming; // 如果使用深度优化，那么需要深度预处理。

            // If possible try to merge the opaque and skybox passes instead of splitting them when "Depth Texture" is required.
            // The copying of depth should normally happen after rendering opaques.
            // But if we only require it for post processing or the scene camera then we do it after rendering transparent objects 
            // Aim to have the most optimized render pass event for Depth Copy (The aim is to minimize the number of render passes) 
            // 如果可能，在需要使用 "深度纹理 "时，请尝试合并不透明和天空盒通道，而不是将它们分开。
            // 复制深度通常应在渲染不透明后进行。
            // 但是，如果我们只需要在后期处理或场景摄像机中使用，那么我们就可以在渲染透明对象后使用它
            // 目标是为深度复制提供最优化的渲染传递事件（目的是尽量减少渲染传递的次数）
            if (requiresDepthTexture)
            {
                bool copyDepthAfterTransparents = m_CopyDepthMode == CopyDepthMode.AfterTransparents; // 是否在渲染透明物体之后复制深度缓冲的结果。

                RenderPassEvent copyDepthPassEvent = copyDepthAfterTransparents ? RenderPassEvent.AfterRenderingTransparents : RenderPassEvent.AfterRenderingOpaques; // 拷贝深度缓冲的时机：透明物体之后还是不透明物体之后
                // RenderPassInputs's requiresDepthTexture is configured through ScriptableRenderPass's ConfigureInput function
                // 判断是否渲染流程的输入要求深度纹理
                if (renderPassInputs.requiresDepthTexture)
                {
                    // Do depth copy before the render pass that requires depth texture as shader read resource
                    copyDepthPassEvent = (RenderPassEvent)Mathf.Min((int)RenderPassEvent.AfterRenderingTransparents, ((int)renderPassInputs.requiresDepthTextureEarliestEvent) - 1); // 这样做的目的是为了尽可能早地复制深度缓冲，以满足渲染流程的输入要求。
                }
                m_CopyDepthPass.renderPassEvent = copyDepthPassEvent;
            }
            else if (cameraHasPostProcessingWithDepth || isSceneViewOrPreviewCamera || isGizmosEnabled) // 如果不需要深度纹理，但是相机有使用深度的后处理效果，或者是场景视图或者预览相机，或者启用了Gizmos
            {
                // If only post process requires depth texture, we can re-use depth buffer from main geometry pass instead of enqueuing a depth copy pass, but no proper API to do that for now, so resort to depth copy pass for now
                m_CopyDepthPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents; // 在渲染透明物体之后复制深度缓冲
            }


            createColorTexture |= RequiresIntermediateColorTexture(ref cameraData);
            createColorTexture |= renderPassInputs.requiresColorTexture;
            createColorTexture |= renderPassInputs.requiresColorTextureCreated;
            createColorTexture &= !isPreviewCamera;

            // If camera requires depth and there's no depth pre-pass we create a depth texture that can be read later by effect requiring it.
            // When deferred renderer is enabled, we must always create a depth texture and CANNOT use BuiltinRenderTextureType.CameraTarget. This is to get
            // around a bug where during gbuffer pass (MRT pass), the camera depth attachment is correctly bound, but during
            // deferred pass ("camera color" + "camera depth"), the implicit depth surface of "camera color" is used instead of "camera depth",
            // because BuiltinRenderTextureType.CameraTarget for depth means there is no explicit depth attachment...
            bool createDepthTexture = (requiresDepthTexture || cameraHasPostProcessingWithDepth) && !requiresDepthPrepass;
            createDepthTexture |= !cameraData.resolveFinalTarget;
            // Deferred renderer always need to access depth buffer.
            createDepthTexture |= (this.renderingModeActual == RenderingMode.Deferred && !useRenderPassEnabled);
            // Some render cases (e.g. Material previews) have shown we need to create a depth texture when we're forcing a prepass.
            createDepthTexture |= useDepthPriming;
            // Todo seems like with mrt depth is not taken from first target
            createDepthTexture |= (renderingLayerProvidesRenderObjectPass);

#if ENABLE_VR && ENABLE_XR_MODULE
            // URP can't handle msaa/size mismatch between depth RT and color RT(for now we create intermediate textures to ensure they match)
            if (cameraData.xr.enabled)
                createColorTexture |= createDepthTexture;
#endif
#if UNITY_ANDROID || UNITY_WEBGL
            // GLES can not use render texture's depth buffer with the color buffer of the backbuffer
            // in such case we create a color texture for it too.
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
                createColorTexture |= createDepthTexture;
#endif
            // If there is any scaling, the color and depth need to be the same resolution and the target texture
            // will not be the proper size in this case. Same happens with GameView.
            // This introduces the final blit pass.
            if (RTHandles.rtHandleProperties.rtHandleScale.x != 1.0f || RTHandles.rtHandleProperties.rtHandleScale.y != 1.0f)
                createColorTexture |= createDepthTexture;

            if (useRenderPassEnabled || useDepthPriming)
                createColorTexture |= createDepthTexture;

            //Set rt descriptors so preview camera's have access should it be needed
            var colorDescriptor = cameraTargetDescriptor;
            colorDescriptor.useMipMap = false;
            colorDescriptor.autoGenerateMips = false;
            colorDescriptor.depthBufferBits = (int)DepthBits.None;
            m_ColorBufferSystem.SetCameraSettings(colorDescriptor, FilterMode.Bilinear);

            // Configure all settings require to start a new camera stack (base camera only)
            // 配置启动新摄像机堆栈所需的所有设置（仅限基础摄像机）
            if (cameraData.renderType == CameraRenderType.Base)
            {
                // Scene filtering redraws the objects on top of the resulting frame. It has to draw directly to the sceneview buffer.
                // 场景过滤会重绘结果帧顶部的对象。它必须直接绘制到场景视图缓冲区。
                bool sceneViewFilterEnabled = camera.sceneViewFilterMode == Camera.SceneViewFilterMode.ShowFiltered;
                bool intermediateRenderTexture = (createColorTexture || createDepthTexture) && !sceneViewFilterEnabled;

                // RTHandles do not support combining color and depth in the same texture so we create them separately
                // RTHandles 不支持在同一纹理中结合颜色和深度，因此我们要分别创建它们。
                // Should be independent from filtered scene view
                // 应独立于过滤后的场景视图
                createDepthTexture |= createColorTexture;

                RenderTargetIdentifier targetId = BuiltinRenderTextureType.CameraTarget;
#if ENABLE_VR && ENABLE_XR_MODULE
                if (cameraData.xr.enabled)
                    targetId = cameraData.xr.renderTarget;
#endif
                if (m_XRTargetHandleAlias == null)
                {
                    m_XRTargetHandleAlias = RTHandles.Alloc(targetId);
                }
                else if (m_XRTargetHandleAlias.nameID != targetId)
                {
                    RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref m_XRTargetHandleAlias, targetId);
                }

                // Doesn't create texture for Overlay cameras as they are already overlaying on top of created textures.
                if (intermediateRenderTexture)
                    CreateCameraRenderTarget(context, ref cameraTargetDescriptor, useDepthPriming, cmd, ref cameraData);

                m_ActiveCameraColorAttachment = createColorTexture ? m_ColorBufferSystem.PeekBackBuffer() : m_XRTargetHandleAlias;
                m_ActiveCameraDepthAttachment = createDepthTexture ? m_CameraDepthAttachment : m_XRTargetHandleAlias;
            }
            else
            {
                cameraData.baseCamera.TryGetComponent<UniversalAdditionalCameraData>(out var baseCameraData);
                var baseRenderer = (UniversalRenderer)baseCameraData.scriptableRenderer;
                if (m_ColorBufferSystem != baseRenderer.m_ColorBufferSystem)
                {
                    m_ColorBufferSystem.Dispose();
                    m_ColorBufferSystem = baseRenderer.m_ColorBufferSystem;
                }
                m_ActiveCameraColorAttachment = m_ColorBufferSystem.PeekBackBuffer();
                m_ActiveCameraDepthAttachment = baseRenderer.m_ActiveCameraDepthAttachment;
                m_XRTargetHandleAlias = baseRenderer.m_XRTargetHandleAlias;
            }

            if (rendererFeatures.Count != 0 && !isPreviewCamera)
                ConfigureCameraColorTarget(m_ColorBufferSystem.PeekBackBuffer());

            bool copyColorPass = renderingData.cameraData.requiresOpaqueTexture || renderPassInputs.requiresColorTexture;
            // Check the createColorTexture logic above: intermediate color texture is not available for preview cameras.
            // Because intermediate color is not available and copyColor pass requires it, we disable CopyColor pass here.
            copyColorPass &= !isPreviewCamera;

            // Assign camera targets (color and depth)
            ConfigureCameraTarget(m_ActiveCameraColorAttachment, m_ActiveCameraDepthAttachment);

            bool hasPassesAfterPostProcessing = activeRenderPassQueue.Find(x => x.renderPassEvent == RenderPassEvent.AfterRenderingPostProcessing) != null;

            if (mainLightShadows)
                EnqueuePass(m_MainLightShadowCasterPass);

            if (additionalLightShadows)
                EnqueuePass(m_AdditionalLightsShadowCasterPass);

            bool requiresDepthCopyPass = !requiresDepthPrepass
                && (renderingData.cameraData.requiresDepthTexture || cameraHasPostProcessingWithDepth || renderPassInputs.requiresDepthTexture)
                && createDepthTexture;

            if ((DebugHandler != null) && DebugHandler.IsActiveForCamera(ref cameraData))
            {
                DebugHandler.TryGetFullscreenDebugMode(out var fullScreenMode);
                if (fullScreenMode == DebugFullScreenMode.Depth)
                {
                    requiresDepthPrepass = true;
                }

                if (!DebugHandler.IsLightingActive)
                {
                    mainLightShadows = false;
                    additionalLightShadows = false;

                    if (!isSceneViewOrPreviewCamera)
                    {
                        requiresDepthPrepass = false;
                        useDepthPriming = false;
                        generateColorGradingLUT = false;
                        copyColorPass = false;
                        requiresDepthCopyPass = false;
                    }
                }

                if (useRenderPassEnabled)
                    useRenderPassEnabled = DebugHandler.IsRenderPassSupported;
            }

            cameraData.renderer.useDepthPriming = useDepthPriming;

            if (this.renderingModeActual == RenderingMode.Deferred)
            {
                if (m_DeferredLights.UseRenderPass && (RenderPassEvent.AfterRenderingGbuffer == renderPassInputs.requiresDepthNormalAtEvent || !useRenderPassEnabled))
                    m_DeferredLights.DisableFramebufferFetchInput();
            }

            // Allocate m_DepthTexture if used
            if ((this.renderingModeActual == RenderingMode.Deferred && !this.useRenderPassEnabled) || requiresDepthPrepass || requiresDepthCopyPass)
            {
                var depthDescriptor = cameraTargetDescriptor;
                if ((requiresDepthPrepass && this.renderingModeActual != RenderingMode.Deferred) || !RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R32_SFloat, FormatUsage.Render))
                {
                    depthDescriptor.graphicsFormat = GraphicsFormat.None;
                    depthDescriptor.depthStencilFormat = k_DepthStencilFormat;
                    depthDescriptor.depthBufferBits = k_DepthBufferBits;
                }
                else
                {
                    depthDescriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
                    depthDescriptor.depthStencilFormat = GraphicsFormat.None;
                    depthDescriptor.depthBufferBits = 0;
                }

                depthDescriptor.msaaSamples = 1;// Depth-Only pass don't use MSAA
                RenderingUtils.ReAllocateIfNeeded(ref m_DepthTexture, depthDescriptor, FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_CameraDepthTexture");

                cmd.SetGlobalTexture(m_DepthTexture.name, m_DepthTexture.nameID);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
            }

            if (requiresRenderingLayer || (renderingModeActual == RenderingMode.Deferred && m_DeferredLights.UseRenderingLayers))
            {
                ref var renderingLayersTexture = ref m_DecalLayersTexture;
                string renderingLayersTextureName = "_CameraRenderingLayersTexture";

                if (this.renderingModeActual == RenderingMode.Deferred && m_DeferredLights.UseRenderingLayers)
                {
                    renderingLayersTexture = ref m_DeferredLights.GbufferAttachments[(int)m_DeferredLights.GBufferRenderingLayers];
                    renderingLayersTextureName = renderingLayersTexture.name;
                }

                var renderingLayersDescriptor = cameraTargetDescriptor;
                renderingLayersDescriptor.depthBufferBits = 0;
                // Never have MSAA on this depth texture. When doing MSAA depth priming this is the texture that is resolved to and used for post-processing.
                if (!renderingLayerProvidesRenderObjectPass)
                    renderingLayersDescriptor.msaaSamples = 1;// Depth-Only pass don't use MSAA
                // Find compatible render-target format for storing normals.
                // Shader code outputs normals in signed format to be compatible with deferred gbuffer layout.
                // Deferred gbuffer format is signed so that normals can be blended for terrain geometry.
                if (this.renderingModeActual == RenderingMode.Deferred && m_DeferredLights.UseRenderingLayers)
                    renderingLayersDescriptor.graphicsFormat = m_DeferredLights.GetGBufferFormat(m_DeferredLights.GBufferRenderingLayers); // the one used by the gbuffer.
                else
                    renderingLayersDescriptor.graphicsFormat = RenderingLayerUtils.GetFormat(renderingLayerMaskSize);

                if (renderingModeActual == RenderingMode.Deferred && m_DeferredLights.UseRenderingLayers)
                {
                    m_DeferredLights.ReAllocateGBufferIfNeeded(renderingLayersDescriptor, (int)m_DeferredLights.GBufferRenderingLayers);
                }
                else
                {
                    RenderingUtils.ReAllocateIfNeeded(ref renderingLayersTexture, renderingLayersDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: renderingLayersTextureName);
                }

                cmd.SetGlobalTexture(renderingLayersTexture.name, renderingLayersTexture.nameID);
                RenderingLayerUtils.SetupProperties(cmd, renderingLayerMaskSize);
                if (this.renderingModeActual == RenderingMode.Deferred) // As this is requested by render pass we still want to set it
                    cmd.SetGlobalTexture("_CameraRenderingLayersTexture", renderingLayersTexture.nameID);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
            }

            // Allocate normal texture if used
            if (requiresDepthPrepass && renderPassInputs.requiresNormalsTexture)
            {
                ref var normalsTexture = ref m_NormalsTexture;
                string normalsTextureName = "_CameraNormalsTexture";

                if (this.renderingModeActual == RenderingMode.Deferred)
                {
                    normalsTexture = ref m_DeferredLights.GbufferAttachments[(int)m_DeferredLights.GBufferNormalSmoothnessIndex];
                    normalsTextureName = normalsTexture.name;
                }

                var normalDescriptor = cameraTargetDescriptor;
                normalDescriptor.depthBufferBits = 0;
                // Never have MSAA on this depth texture. When doing MSAA depth priming this is the texture that is resolved to and used for post-processing.
                normalDescriptor.msaaSamples = useDepthPriming ? cameraTargetDescriptor.msaaSamples : 1;// Depth-Only passes don't use MSAA, unless depth priming is enabled
                // Find compatible render-target format for storing normals.
                // Shader code outputs normals in signed format to be compatible with deferred gbuffer layout.
                // Deferred gbuffer format is signed so that normals can be blended for terrain geometry.
                if (this.renderingModeActual == RenderingMode.Deferred)
                    normalDescriptor.graphicsFormat = m_DeferredLights.GetGBufferFormat(m_DeferredLights.GBufferNormalSmoothnessIndex); // the one used by the gbuffer.
                else
                    normalDescriptor.graphicsFormat = DepthNormalOnlyPass.GetGraphicsFormat();

                if (this.renderingModeActual == RenderingMode.Deferred)
                {
                    m_DeferredLights.ReAllocateGBufferIfNeeded(normalDescriptor, (int)m_DeferredLights.GBufferNormalSmoothnessIndex);
                }
                else
                {
                    RenderingUtils.ReAllocateIfNeeded(ref normalsTexture, normalDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: normalsTextureName);
                }

                cmd.SetGlobalTexture(normalsTexture.name, normalsTexture.nameID);
                if (this.renderingModeActual == RenderingMode.Deferred) // As this is requested by render pass we still want to set it
                    cmd.SetGlobalTexture("_CameraNormalsTexture", normalsTexture.nameID);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
            }

            // 需要提前深度纹理
            if (requiresDepthPrepass)
            {
                // 需要法线和深度纹理
                if (renderPassInputs.requiresNormalsTexture)
                {
                    // 是延迟渲染
                    if (this.renderingModeActual == RenderingMode.Deferred)
                    {
                        // In deferred mode, depth-normal prepass does really primes the depth and normal buffers, instead of creating a copy.
                        // 延迟模式下，depth-normal prepass是事先做缓冲，不是生成拷贝。
                        // It is necessary because we need to render depth&normal for forward-only geometry and it is the only way to get them before the SSAO pass.
                        // 这是必要的，因为我们需要为仅向前的几何体渲染深度和法线，这是在通过 SSAO 之前获取它们的唯一方法。
                        int gbufferNormalIndex = m_DeferredLights.GBufferNormalSmoothnessIndex;
                        if (m_DeferredLights.UseRenderingLayers)
                            m_DepthNormalPrepass.Setup(m_ActiveCameraDepthAttachment, m_DeferredLights.GbufferAttachments[gbufferNormalIndex], m_DeferredLights.GbufferAttachments[m_DeferredLights.GBufferRenderingLayers]);
                        else if (renderingLayerProvidesByDepthNormalPass)
                            m_DepthNormalPrepass.Setup(m_ActiveCameraDepthAttachment, m_DeferredLights.GbufferAttachments[gbufferNormalIndex], m_DecalLayersTexture);
                        else
                            m_DepthNormalPrepass.Setup(m_ActiveCameraDepthAttachment, m_DeferredLights.GbufferAttachments[gbufferNormalIndex]);

                        // Only render forward-only geometry, as standard geometry will be rendered as normal into the gbuffer.
                        // 只渲染只向前渲染的几何图形，标准几何图形将法线渲染到 gbuffer 中。
                        if (RenderPassEvent.AfterRenderingGbuffer <= renderPassInputs.requiresDepthNormalAtEvent &&
                            renderPassInputs.requiresDepthNormalAtEvent <= RenderPassEvent.BeforeRenderingOpaques)
                            m_DepthNormalPrepass.shaderTagIds = k_DepthNormalsOnly;
                    }
                    else
                    {
                        if (renderingLayerProvidesByDepthNormalPass)
                            m_DepthNormalPrepass.Setup(m_DepthTexture, m_NormalsTexture, m_DecalLayersTexture);
                        else
                            m_DepthNormalPrepass.Setup(m_DepthTexture, m_NormalsTexture);
                    }

                    EnqueuePass(m_DepthNormalPrepass);
                }
                else
                {
                    // Deferred renderer does not require a depth-prepass to generate samplable depth texture.
                    // 延迟渲染不需要z-prepass来生成可采样的深度纹理。
                    if (this.renderingModeActual != RenderingMode.Deferred)
                    {
                        m_DepthPrepass.Setup(cameraTargetDescriptor, m_DepthTexture);
                        EnqueuePass(m_DepthPrepass);
                    }
                }
            }

            // depth priming still needs to copy depth because the prepass doesn't target anymore CameraDepthTexture。
            // depth priming 仍然需要拷贝深度，因为prepass不再针对 CameraDepthTexture
            // TODO: this is unoptimal, investigate optimizations 这是不理想的，需要进行优化
            if (useDepthPriming)
            {
                m_PrimedDepthCopyPass.Setup(m_ActiveCameraDepthAttachment, m_DepthTexture);
                EnqueuePass(m_PrimedDepthCopyPass);
            }

            if (generateColorGradingLUT)
            {
                colorGradingLutPass.ConfigureDescriptor(in renderingData.postProcessingData, out var desc, out var filterMode);
                RenderingUtils.ReAllocateIfNeeded(ref m_PostProcessPasses.m_ColorGradingLut, desc, filterMode, TextureWrapMode.Clamp, anisoLevel: 0, name: "_InternalGradingLut");
                colorGradingLutPass.Setup(colorGradingLut);
                EnqueuePass(colorGradingLutPass);
            }

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.hasValidOcclusionMesh)
                EnqueuePass(m_XROcclusionMeshPass);
#endif

            bool lastCameraInTheStack = cameraData.resolveFinalTarget;

            if (this.renderingModeActual == RenderingMode.Deferred)
            {
                if (m_DeferredLights.UseRenderPass && (RenderPassEvent.AfterRenderingGbuffer == renderPassInputs.requiresDepthNormalAtEvent || !useRenderPassEnabled))
                    m_DeferredLights.DisableFramebufferFetchInput();

                EnqueueDeferred(ref renderingData, requiresDepthPrepass, renderPassInputs.requiresNormalsTexture, renderingLayerProvidesByDepthNormalPass, mainLightShadows, additionalLightShadows);
            }
            else
            {
                // Optimized store actions are very important on tile based GPUs and have a great impact on performance.
                // if MSAA is enabled and any of the following passes need a copy of the color or depth target, make sure the MSAA'd surface is stored
                // if following passes won't use it then just resolve (the Resolve action will still store the resolved surface, but discard the MSAA'd surface, which is very expensive to store).
                RenderBufferStoreAction opaquePassColorStoreAction = RenderBufferStoreAction.Store;
                if (cameraTargetDescriptor.msaaSamples > 1)
                    opaquePassColorStoreAction = copyColorPass ? RenderBufferStoreAction.StoreAndResolve : RenderBufferStoreAction.Store;


                // make sure we store the depth only if following passes need it.
                RenderBufferStoreAction opaquePassDepthStoreAction = (copyColorPass || requiresDepthCopyPass || !lastCameraInTheStack) ? RenderBufferStoreAction.Store : RenderBufferStoreAction.DontCare;
#if ENABLE_VR && ENABLE_XR_MODULE
                if (cameraData.xr.enabled && cameraData.xr.copyDepth)
                {
                    opaquePassDepthStoreAction = RenderBufferStoreAction.Store;
                }
#endif

                // handle multisample depth resolve by setting the appropriate store actions if supported
                if (requiresDepthCopyPass && cameraTargetDescriptor.msaaSamples > 1 && RenderingUtils.MultisampleDepthResolveSupported())
                {
                    bool isCopyDepthAfterTransparent = m_CopyDepthPass.renderPassEvent == RenderPassEvent.AfterRenderingTransparents;

                    // we could StoreAndResolve when the depth copy is after opaque, but performance wise doing StoreAndResolve of depth targets is more expensive than a simple Store + following depth copy pass on Apple GPUs,
                    // because of the extra resolve step. So, unless we are copying the depth after the transparent pass, just Store the depth target.
                    if (isCopyDepthAfterTransparent && !copyColorPass)
                    {
                        if (opaquePassDepthStoreAction == RenderBufferStoreAction.Store)
                            opaquePassDepthStoreAction = RenderBufferStoreAction.StoreAndResolve;
                        else if (opaquePassDepthStoreAction == RenderBufferStoreAction.DontCare)
                            opaquePassDepthStoreAction = RenderBufferStoreAction.Resolve;
                    }
                }

                DrawObjectsPass renderOpaqueForwardPass = null;
                if (renderingLayerProvidesRenderObjectPass)
                {
                    renderOpaqueForwardPass = m_RenderOpaqueForwardWithRenderingLayersPass;
                    m_RenderOpaqueForwardWithRenderingLayersPass.Setup(m_ActiveCameraColorAttachment, m_DecalLayersTexture, m_ActiveCameraDepthAttachment);
                }
                else
                    renderOpaqueForwardPass = m_RenderOpaqueForwardPass;

                renderOpaqueForwardPass.ConfigureColorStoreAction(opaquePassColorStoreAction);
                renderOpaqueForwardPass.ConfigureDepthStoreAction(opaquePassDepthStoreAction);

                // If there is any custom render pass renders to opaque pass' target before opaque pass,
                // we can't clear color as it contains the valid rendering output.
                bool hasPassesBeforeOpaque = activeRenderPassQueue.Find(x => (x.renderPassEvent <= RenderPassEvent.BeforeRenderingOpaques) && !x.overrideCameraTarget) != null;
                ClearFlag opaqueForwardPassClearFlag = (hasPassesBeforeOpaque || cameraData.renderType != CameraRenderType.Base)
                                                    ? ClearFlag.None
                                                    : ClearFlag.Color;
#if ENABLE_VR && ENABLE_XR_MODULE
                // workaround for DX11 and DX12 XR test failures.
                // XRTODO: investigate DX XR clear issues.
                if (SystemInfo.usesLoadStoreActions)
#endif
                    renderOpaqueForwardPass.ConfigureClear(opaqueForwardPassClearFlag, Color.black);

                EnqueuePass(renderOpaqueForwardPass);
            }

            if (camera.clearFlags == CameraClearFlags.Skybox && cameraData.renderType != CameraRenderType.Overlay)
            {
                if (RenderSettings.skybox != null || (camera.TryGetComponent(out Skybox cameraSkybox) && cameraSkybox.material != null))
                    EnqueuePass(m_DrawSkyboxPass);
            }

            // If a depth texture was created we necessarily need to copy it, otherwise we could have render it to a renderbuffer.
            // Also skip if Deferred+RenderPass as CameraDepthTexture is used and filled by the GBufferPass
            // however we might need the depth texture with Forward-only pass rendered to it, so enable the copy depth in that case
            if (requiresDepthCopyPass && !(this.renderingModeActual == RenderingMode.Deferred && useRenderPassEnabled && !renderPassInputs.requiresDepthTexture))
            {
                m_CopyDepthPass.Setup(m_ActiveCameraDepthAttachment, m_DepthTexture);
                EnqueuePass(m_CopyDepthPass);
            }

            // Set the depth texture to the far Z if we do not have a depth prepass or copy depth
            // 如果没有z-prepass或深度复制，则将深度纹理设置为远端 Z
            // Don't do this for Overlay cameras to not lose depth data in between cameras (as Base is guaranteed to be first)
            // 不对叠加摄像机执行此操作，以免在摄像机之间丢失深度数据（因为底层保证在先）
            if (cameraData.renderType == CameraRenderType.Base && !requiresDepthPrepass && !requiresDepthCopyPass)
                Shader.SetGlobalTexture("_CameraDepthTexture", SystemInfo.usesReversedZBuffer ? Texture2D.blackTexture : Texture2D.whiteTexture);

            if (copyColorPass)
            {
                // TODO: Downsampling method should be stored in the renderer instead of in the asset.
                // TODO: 降采样方法应存储在渲染器中，而不是资产中。
                // We need to migrate this data to renderer. For now, we query the method in the active asset.
                // 我们需要将这些数据迁移到渲染器中。现在，我们查询活动资产中的方法。
                Downsampling downsamplingMethod = UniversalRenderPipeline.asset.opaqueDownsampling;
                var descriptor = cameraTargetDescriptor;
                CopyColorPass.ConfigureDescriptor(downsamplingMethod, ref descriptor, out var filterMode);

                RenderingUtils.ReAllocateIfNeeded(ref m_OpaqueColor, descriptor, filterMode, TextureWrapMode.Clamp, name: "_CameraOpaqueTexture");
                m_CopyColorPass.Setup(m_ActiveCameraColorAttachment, m_OpaqueColor, downsamplingMethod);
                EnqueuePass(m_CopyColorPass);
            }

            // Motion vectors
            if (renderPassInputs.requiresMotionVectors)
            {
                var colorDesc = cameraTargetDescriptor;
                colorDesc.graphicsFormat = MotionVectorRenderPass.k_TargetFormat;
                colorDesc.depthBufferBits = (int)DepthBits.None;
                colorDesc.msaaSamples = 1;  // Disable MSAA, consider a pixel resolve for half left velocity and half right velocity --> no velocity, which is untrue.
                RenderingUtils.ReAllocateIfNeeded(ref m_MotionVectorColor, colorDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_MotionVectorTexture");

                var depthDescriptor = cameraTargetDescriptor;
                depthDescriptor.graphicsFormat = GraphicsFormat.None;
                depthDescriptor.msaaSamples = 1;
                RenderingUtils.ReAllocateIfNeeded(ref m_MotionVectorDepth, depthDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_MotionVectorDepthTexture");

                m_MotionVectorPass.Setup(m_MotionVectorColor, m_MotionVectorDepth);
                EnqueuePass(m_MotionVectorPass);
            }

#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
            if (needTransparencyPass)
#endif
            {
                if (transparentsNeedSettingsPass)
                {
                    EnqueuePass(m_TransparentSettingsPass);
                }

                // if this is not lastCameraInTheStack we still need to Store, since the MSAA buffer might be needed by the Overlay cameras
                RenderBufferStoreAction transparentPassColorStoreAction = cameraTargetDescriptor.msaaSamples > 1 && lastCameraInTheStack ? RenderBufferStoreAction.Resolve : RenderBufferStoreAction.Store;
                RenderBufferStoreAction transparentPassDepthStoreAction = lastCameraInTheStack ? RenderBufferStoreAction.DontCare : RenderBufferStoreAction.Store;

                // If CopyDepthPass pass event is scheduled on or after AfterRenderingTransparent, we will need to store the depth buffer or resolve (store for now until latest trunk has depth resolve support) it for MSAA case
                if (requiresDepthCopyPass && m_CopyDepthPass.renderPassEvent >= RenderPassEvent.AfterRenderingTransparents)
                {
                    transparentPassDepthStoreAction = RenderBufferStoreAction.Store;

                    // handle depth resolve on platforms supporting it
                    if (cameraTargetDescriptor.msaaSamples > 1 && RenderingUtils.MultisampleDepthResolveSupported())
                        transparentPassDepthStoreAction = RenderBufferStoreAction.Resolve;
                }

                m_RenderTransparentForwardPass.ConfigureColorStoreAction(transparentPassColorStoreAction);
                m_RenderTransparentForwardPass.ConfigureDepthStoreAction(transparentPassDepthStoreAction);
                EnqueuePass(m_RenderTransparentForwardPass);
            }
            EnqueuePass(m_OnRenderObjectCallbackPass);

            bool shouldRenderUI = cameraData.rendersOverlayUI;
            bool outputToHDR = cameraData.isHDROutputActive;
            if (shouldRenderUI && outputToHDR)
            {
                m_DrawOffscreenUIPass.Setup(ref cameraData, k_DepthBufferBits);
                EnqueuePass(m_DrawOffscreenUIPass);
            }

            bool hasCaptureActions = renderingData.cameraData.captureActions != null && lastCameraInTheStack;

            // When FXAA or scaling is active, we must perform an additional pass at the end of the frame for the following reasons:
            // 1. FXAA expects to be the last shader running on the image before it's presented to the screen. Since users are allowed
            //    to add additional render passes after post processing occurs, we can't run FXAA until all of those passes complete as well.
            //    The FinalPost pass is guaranteed to execute after user authored passes so FXAA is always run inside of it.
            // 2. UberPost can only handle upscaling with linear filtering. All other filtering methods require the FinalPost pass.
            // 3. TAA sharpening using standalone RCAS pass is required. (When upscaling is not enabled).
            bool applyFinalPostProcessing = anyPostProcessing && lastCameraInTheStack &&
                ((renderingData.cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing) ||
                 ((renderingData.cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (renderingData.cameraData.upscalingFilter != ImageUpscalingFilter.Linear)) ||
                 (renderingData.cameraData.IsTemporalAAEnabled() && renderingData.cameraData.taaSettings.contrastAdaptiveSharpening > 0.0f));

            // When post-processing is enabled we can use the stack to resolve rendering to camera target (screen or RT).
            // However when there are render passes executing after post we avoid resolving to screen so rendering continues (before sRGBConversion etc)
            bool resolvePostProcessingToCameraTarget = !hasCaptureActions && !hasPassesAfterPostProcessing && !applyFinalPostProcessing;
            bool needsColorEncoding = DebugHandler == null || !DebugHandler.HDRDebugViewIsActive(ref cameraData);

            if (applyPostProcessing)
            {
                var desc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, cameraTargetDescriptor.width, cameraTargetDescriptor.height, cameraTargetDescriptor.graphicsFormat, DepthBits.None);
                RenderingUtils.ReAllocateIfNeeded(ref m_PostProcessPasses.m_AfterPostProcessColor, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_AfterPostProcessTexture");
            }

            if (lastCameraInTheStack)
            {
                SetupFinalPassDebug(ref cameraData);

                // Post-processing will resolve to final target. No need for final blit pass.
                if (applyPostProcessing)
                {
                    // if resolving to screen we need to be able to perform sRGBConversion in post-processing if necessary
                    bool doSRGBEncoding = resolvePostProcessingToCameraTarget && needsColorEncoding;
                    postProcessPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment, resolvePostProcessingToCameraTarget, m_ActiveCameraDepthAttachment, colorGradingLut, m_MotionVectorColor, applyFinalPostProcessing, doSRGBEncoding);
                    EnqueuePass(postProcessPass);
                }

                var sourceForFinalPass = m_ActiveCameraColorAttachment;

                // Do FXAA or any other final post-processing effect that might need to run after AA.
                if (applyFinalPostProcessing)
                {
                    finalPostProcessPass.SetupFinalPass(sourceForFinalPass, true, needsColorEncoding);
                    EnqueuePass(finalPostProcessPass);
                }

                if (renderingData.cameraData.captureActions != null)
                {
                    EnqueuePass(m_CapturePass);
                }

                // if post-processing then we already resolved to camera target while doing post.
                // Also only do final blit if camera is not rendering to RT.
                bool cameraTargetResolved =
                    // final PP always blit to camera target
                    applyFinalPostProcessing ||
                    // no final PP but we have PP stack. In that case it blit unless there are render pass after PP
                    (applyPostProcessing && !hasPassesAfterPostProcessing && !hasCaptureActions) ||
                    // offscreen camera rendering to a texture, we don't need a blit pass to resolve to screen
                    m_ActiveCameraColorAttachment.nameID == m_XRTargetHandleAlias.nameID;

                // We need final blit to resolve to screen
                if (!cameraTargetResolved)
                {
                    m_FinalBlitPass.Setup(cameraTargetDescriptor, sourceForFinalPass);
                    EnqueuePass(m_FinalBlitPass);
                }

                if (shouldRenderUI && !outputToHDR)
                {
                    EnqueuePass(m_DrawOverlayUIPass);
                }

#if ENABLE_VR && ENABLE_XR_MODULE
                if (cameraData.xr.enabled)
                {
                    // active depth is depth target, we don't need a blit pass to resolve
                    bool depthTargetResolved = m_ActiveCameraDepthAttachment.nameID == cameraData.xr.renderTarget;

                    if (!depthTargetResolved && cameraData.xr.copyDepth)
                    {
                        m_XRCopyDepthPass.Setup(m_ActiveCameraDepthAttachment, m_XRTargetHandleAlias);
                        m_XRCopyDepthPass.CopyToDepth = true;
                        EnqueuePass(m_XRCopyDepthPass);
                    }
                }
#endif
            }
            // stay in RT so we resume rendering on stack after post-processing
            else if (applyPostProcessing)
            {
                postProcessPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment, false, m_ActiveCameraDepthAttachment, colorGradingLut, m_MotionVectorColor, false, false);
                EnqueuePass(postProcessPass);
            }

#if UNITY_EDITOR
            if (isSceneViewOrPreviewCamera || (isGizmosEnabled && lastCameraInTheStack))
            {
                // Scene view camera should always resolve target (not stacked)
                m_FinalDepthCopyPass.Setup(m_DepthTexture, k_CameraTarget);
                m_FinalDepthCopyPass.CopyToDepth = true;
                m_FinalDepthCopyPass.MssaSamples = 0;
                EnqueuePass(m_FinalDepthCopyPass);
            }
#endif
        }

        /// <inheritdoc />
        public override void SetupLights(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_ForwardLights.Setup(context, ref renderingData);

            if (this.renderingModeActual == RenderingMode.Deferred)
                m_DeferredLights.SetupLights(context, ref renderingData);
        }

        /// <inheritdoc />
        /// 这个函数根据渲染模式、阴影投射和光源数量等因素，设置裁剪参数的各个选项，以控制渲染管线在裁剪阶段的行为。这些裁剪参数将在后续的渲染过程中被使用。
        /// 准备阶段用于设置裁剪参数，它并不直接参与实际的渲染过程
        public override void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters,
            ref CameraData cameraData)
        {
            // TODO: PerObjectCulling also affect reflection probes. Enabling it for now.
            // if (asset.additionalLightsRenderingMode == LightRenderingMode.Disabled ||
            //     asset.maxAdditionalLightsCount == 0)
            if (renderingModeActual == RenderingMode.ForwardPlus)
            {
                cullingParameters.cullingOptions |= CullingOptions.DisablePerObjectCulling; // 禁用物体级别的剔除
            }

            // We disable shadow casters if both shadow casting modes are turned off
            // or the shadow distance has been turned down to zero
            // 根据阴影投射的设置情况，决定是否禁用阴影投射物体的裁剪。当主光源和其他附加光源的阴影投射都被禁用，或者阴影距离设置为0时，将裁剪选项的ShadowCasters位设置为false，即禁用阴影投射物体的裁剪。
            bool isShadowCastingDisabled = !UniversalRenderPipeline.asset.supportsMainLightShadows && !UniversalRenderPipeline.asset.supportsAdditionalLightShadows;
            bool isShadowDistanceZero = Mathf.Approximately(cameraData.maxShadowDistance, 0.0f);
            if (isShadowCastingDisabled || isShadowDistanceZero)
            {
                cullingParameters.cullingOptions &= ~CullingOptions.ShadowCasters;
            }

            // 根据渲染模式的不同，设置最大可见光源的数量。如果渲染模式是Deferred，将最大可见光源数量设置为0xFFFF（即无限大），
            // 否则将最大可见光源数量设置为UniversalRenderPipeline.maxVisibleAdditionalLights + 1（即附加光源数量加上主光源）。
            if (this.renderingModeActual == RenderingMode.Deferred)
                cullingParameters.maximumVisibleLights = 0xFFFF;
            else
            {
                // We set the number of maximum visible lights allowed and we add one for the mainlight...
                //
                // Note: However ScriptableRenderContext.Cull() does not differentiate between light types.
                //       If there is no active main light in the scene, ScriptableRenderContext.Cull() might return  ( cullingParameters.maximumVisibleLights )  visible additional lights.
                //       i.e ScriptableRenderContext.Cull() might return  ( UniversalRenderPipeline.maxVisibleAdditionalLights + 1 )  visible additional lights !
                cullingParameters.maximumVisibleLights = UniversalRenderPipeline.maxVisibleAdditionalLights + 1;
            }
            // 设置裁剪参数的阴影距离为CameraData中的最大阴影距离。
            cullingParameters.shadowDistance = cameraData.maxShadowDistance;

            cullingParameters.conservativeEnclosingSphere = UniversalRenderPipeline.asset.conservativeEnclosingSphere;

            cullingParameters.numIterationsEnclosingSphere = UniversalRenderPipeline.asset.numIterationsEnclosingSphere;
        }

        /// <inheritdoc />
        public override void FinishRendering(CommandBuffer cmd)
        {
            m_ColorBufferSystem.Clear();
            m_ActiveCameraColorAttachment = null;
            m_ActiveCameraDepthAttachment = null;
        }

        void EnqueueDeferred(ref RenderingData renderingData, bool hasDepthPrepass, bool hasNormalPrepass, bool hasRenderingLayerPrepass, bool applyMainShadow, bool applyAdditionalShadow)
        {
            m_DeferredLights.Setup(
                ref renderingData,
                applyAdditionalShadow ? m_AdditionalLightsShadowCasterPass : null,
                hasDepthPrepass,
                hasNormalPrepass,
                hasRenderingLayerPrepass,
                m_DepthTexture,
                m_ActiveCameraDepthAttachment,
                m_ActiveCameraColorAttachment
            );
            // Need to call Configure for both of these passes to setup input attachments as first frame otherwise will raise errors
            if (useRenderPassEnabled && m_DeferredLights.UseRenderPass)
            {
                m_GBufferPass.Configure(null, renderingData.cameraData.cameraTargetDescriptor);
                m_DeferredPass.Configure(null, renderingData.cameraData.cameraTargetDescriptor);
            }

            EnqueuePass(m_GBufferPass);

            //Must copy depth for deferred shading: TODO wait for API fix to bind depth texture as read-only resource.
            if (!useRenderPassEnabled || !m_DeferredLights.UseRenderPass)
            {
                m_GBufferCopyDepthPass.Setup(m_CameraDepthAttachment, m_DepthTexture);
                EnqueuePass(m_GBufferCopyDepthPass);
            }

            EnqueuePass(m_DeferredPass);

            EnqueuePass(m_RenderOpaqueForwardOnlyPass);
        }

        private struct RenderPassInputSummary
        {
            internal bool requiresDepthTexture; // 需要深度纹理
            internal bool requiresDepthPrepass; // 需要提前深度
            internal bool requiresNormalsTexture; // 需要法线纹理
            internal bool requiresColorTexture; // 需要颜色纹理
            internal bool requiresColorTextureCreated; // 需要颜色纹理生成
            internal bool requiresMotionVectors; // 需要运动向量
            internal RenderPassEvent requiresDepthNormalAtEvent; // 需要深度和法线事件
            internal RenderPassEvent requiresDepthTextureEarliestEvent; // 需要深度纹理最早事件
        }

        private RenderPassInputSummary GetRenderPassInputs(ref RenderingData renderingData)
        {
            // 如果是延迟渲染，渲染事件就是：GBuffer之前渲染，否则就是不透明物体之前渲染
            RenderPassEvent beforeMainRenderingEvent = m_RenderingMode == RenderingMode.Deferred ? RenderPassEvent.BeforeRenderingGbuffer : RenderPassEvent.BeforeRenderingOpaques;

            RenderPassInputSummary inputSummary = new RenderPassInputSummary();
            inputSummary.requiresDepthNormalAtEvent = RenderPassEvent.BeforeRenderingOpaques; // 需要深度和法线的事件，在不透明物体之前
            inputSummary.requiresDepthTextureEarliestEvent = RenderPassEvent.BeforeRenderingPostProcessing; // 后处理之前
            for (int i = 0; i < activeRenderPassQueue.Count; ++i)
            {
                ScriptableRenderPass pass = activeRenderPassQueue[i]; // 返回渲染器中渲染Pass列表
                // pass.input是在 ConfigureInput 方法里设置的，需要哪个就设为true
                bool needsDepth = (pass.input & ScriptableRenderPassInput.Depth) != ScriptableRenderPassInput.None;
                bool needsNormals = (pass.input & ScriptableRenderPassInput.Normal) != ScriptableRenderPassInput.None;
                bool needsColor = (pass.input & ScriptableRenderPassInput.Color) != ScriptableRenderPassInput.None;
                bool needsMotion = (pass.input & ScriptableRenderPassInput.Motion) != ScriptableRenderPassInput.None;
                bool eventBeforeMainRendering = pass.renderPassEvent <= beforeMainRenderingEvent; // 渲染顺序在这个主事件之前，为true

                // TODO: Need a better way to handle this, probably worth to recheck after render graph 需要一个更好的方法来处理这个问题，也许值得在渲染图形后重新检查
                // DBuffer requires color texture created as it does not handle y flip correctly DBuffer 需要创建彩色纹理，因为它无法正确处理 y 翻转
                if (pass is DBufferRenderPass dBufferRenderPass)
                {
                    inputSummary.requiresColorTextureCreated = true;
                }

                inputSummary.requiresDepthTexture |= needsDepth;
                inputSummary.requiresDepthPrepass |= needsNormals || needsDepth && eventBeforeMainRendering;
                inputSummary.requiresNormalsTexture |= needsNormals;
                inputSummary.requiresColorTexture |= needsColor;
                inputSummary.requiresMotionVectors |= needsMotion;
                if (needsDepth)
                    inputSummary.requiresDepthTextureEarliestEvent = (RenderPassEvent)Mathf.Min((int)pass.renderPassEvent, (int)inputSummary.requiresDepthTextureEarliestEvent); // 需要深度时
                if (needsNormals || needsDepth)
                    inputSummary.requiresDepthNormalAtEvent = (RenderPassEvent)Mathf.Min((int)pass.renderPassEvent, (int)inputSummary.requiresDepthNormalAtEvent);
            }

            // NOTE: TAA and motion vector dependencies added here to share between Execute and Render (Graph) paths.
            // TAA in postprocess requires motion to function.
            if (renderingData.cameraData.IsTemporalAAEnabled())
                inputSummary.requiresMotionVectors = true;

            // Motion vectors imply depth
            if (inputSummary.requiresMotionVectors)
                inputSummary.requiresDepthTexture = true;

            return inputSummary;
        }

        void CreateCameraRenderTarget(ScriptableRenderContext context, ref RenderTextureDescriptor descriptor, bool primedDepth, CommandBuffer cmd, ref CameraData cameraData)
        {
            using (new ProfilingScope(null, Profiling.createCameraRenderTarget))
            {
                if (m_ColorBufferSystem.PeekBackBuffer() == null || m_ColorBufferSystem.PeekBackBuffer().nameID != BuiltinRenderTextureType.CameraTarget)
                {
                    m_ActiveCameraColorAttachment = m_ColorBufferSystem.GetBackBuffer(cmd);
                    ConfigureCameraColorTarget(m_ActiveCameraColorAttachment);
                    cmd.SetGlobalTexture("_CameraColorTexture", m_ActiveCameraColorAttachment.nameID);
                    //Set _AfterPostProcessTexture, users might still rely on this although it is now always the cameratarget due to swapbuffer
                    cmd.SetGlobalTexture("_AfterPostProcessTexture", m_ActiveCameraColorAttachment.nameID);
                }

                if (m_CameraDepthAttachment == null || m_CameraDepthAttachment.nameID != BuiltinRenderTextureType.CameraTarget)
                {
                    var depthDescriptor = descriptor;
                    depthDescriptor.useMipMap = false;
                    depthDescriptor.autoGenerateMips = false;
                    depthDescriptor.bindMS = false;

                    bool hasMSAA = depthDescriptor.msaaSamples > 1 && (SystemInfo.supportsMultisampledTextures != 0);

                    // if MSAA is enabled and we are not resolving depth, which we only do if the CopyDepthPass is AfterTransparents,
                    // then we want to bind the multisampled surface.
                    if (hasMSAA)
                    {
                        // if depth priming is enabled the copy depth primed pass is meant to do the MSAA resolve, so we want to bind the MS surface
                        if (IsDepthPrimingEnabled(ref cameraData))
                            depthDescriptor.bindMS = true;
                        else
                            depthDescriptor.bindMS = !(RenderingUtils.MultisampleDepthResolveSupported() && m_CopyDepthMode == CopyDepthMode.AfterTransparents);
                    }

                    // binding MS surfaces is not supported by the GLES backend, and it won't be fixed after investigating
                    // the high performance impact of potential fixes, which would make it more expensive than depth prepass (fogbugz 1339401 for more info)
                    if (IsGLESDevice())
                        depthDescriptor.bindMS = false;

                    depthDescriptor.graphicsFormat = GraphicsFormat.None;
                    depthDescriptor.depthStencilFormat = k_DepthStencilFormat;
                    RenderingUtils.ReAllocateIfNeeded(ref m_CameraDepthAttachment, depthDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_CameraDepthAttachment");
                    cmd.SetGlobalTexture(m_CameraDepthAttachment.name, m_CameraDepthAttachment.nameID);
                }
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }

        bool PlatformRequiresExplicitMsaaResolve()
        {
#if UNITY_EDITOR
            // In the editor play-mode we use a Game View Render Texture, with
            // samples count forced to 1 so we always need to do an explicit MSAA resolve.
            return true;
#else
            // On Metal/iOS the MSAA resolve is done implicitly as part of the renderpass, so we do not need an extra intermediate pass for the explicit autoresolve.
            // Note: On Vulkan Standalone, despite SystemInfo.supportsMultisampleAutoResolve being true, the backbuffer has only 1 sample, so we still require
            // the explicit resolve on non-mobile platforms with supportsMultisampleAutoResolve.
            return !(SystemInfo.supportsMultisampleAutoResolve && Application.isMobilePlatform)
                && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal;
#endif
        }

        /// <summary>
        /// Checks if the pipeline needs to create a intermediate render texture.
        /// </summary>
        /// <param name="cameraData">CameraData contains all relevant render target information for the camera.</param>
        /// <seealso cref="CameraData"/>
        /// <returns>Return true if pipeline needs to render to a intermediate render texture.</returns>
        bool RequiresIntermediateColorTexture(ref CameraData cameraData)
        {
            // When rendering a camera stack we always create an intermediate render texture to composite camera results.
            // We create it upon rendering the Base camera.
            if (cameraData.renderType == CameraRenderType.Base && !cameraData.resolveFinalTarget)
                return true; // 当渲染一个相机堆栈时，我们总是需要创建一个中间渲染纹理来合成相机的结果。我们在渲染基础相机时创建它。

            // Always force rendering into intermediate color texture if deferred rendering mode is selected.
            // Reason: without intermediate color texture, the target camera texture is y-flipped.
            // However, the target camera texture is bound during gbuffer pass and deferred pass.
            // Gbuffer pass will not be y-flipped because it is MRT (see ScriptableRenderContext implementation),
            // while deferred pass will be y-flipped, which breaks rendering.
            // This incurs an extra blit into at the end of rendering.
            // 如果当前的渲染模式是延迟渲染，那么需要创建中间颜色纹理。
            // 这是因为如果没有中间颜色纹理，目标相机纹理会被沿y轴翻转。
            // 然而，目标相机纹理会在gbuffer pass和deferred pass中被绑定。
            // gbuffer pass不会被翻转，因为它是多渲染目标（参见ScriptableRenderContext的实现），
            // 而deferred pass会被翻转，这会破坏渲染。这会导致在渲染结束时多一个blit操作。
            if (this.renderingModeActual == RenderingMode.Deferred)
                return true;

            bool isSceneViewCamera = cameraData.isSceneViewCamera; // 如果当前的相机是场景视图相机，那么需要创建中间颜色纹理。
            var cameraTargetDescriptor = cameraData.cameraTargetDescriptor; // 这是一个RenderTextureDescriptor类型的变量，用于描述相机的目标纹理的属性，如宽度，高度，格式，维度，多重采样等。
            int msaaSamples = cameraTargetDescriptor.msaaSamples; // 用于存储目标纹理的多重采样数量。多重采样是一种抗锯齿技术，用于提高渲染的质量。
            // 用于存储是否需要对渲染结果进行缩放的结果。这个结果是根据cameraData的imageScalingMode属性来确定的，如果cameraData的imageScalingMode属性不等于ImageScalingMode.None，那么isScaledRender为true，否则为false。
            // 缩放渲染是一种渲染技术，用于将渲染结果的分辨率调整为目标纹理的分辨率，以便后续的渲染过程可以使用它。
            bool isScaledRender = cameraData.imageScalingMode != ImageScalingMode.None; 
            // 用于存储目标纹理的维度是否与后备缓冲的维度兼容的结果。这个结果是根据cameraTargetDescriptor的dimension属性来确定的，如果cameraTargetDescriptor的dimension属性等于TextureDimension.Tex2D，
            // 那么isCompatibleBackbufferTextureDimension为true，否则为false。后备缓冲是一种渲染目标，用于存储最终的渲染结果，以便显示到屏幕上。
            bool isCompatibleBackbufferTextureDimension = cameraTargetDescriptor.dimension == TextureDimension.Tex2D;
            // 用于存储是否需要显式地解析多重采样的结果。这个结果是根据msaaSamples的值和PlatformRequiresExplicitMsaaResolve()方法的返回值来确定的，
            // 如果msaaSamples大于1，且PlatformRequiresExplicitMsaaResolve()方法返回true，那么requiresExplicitMsaaResolve为true，否则为false。
            // 解析多重采样是一种渲染技术，用于将每个像素的多个颜色值合并为一个颜色值的过程。这样做的目的是为了将多重采样的纹理从一个渲染目标复制到另一个渲染目标，以便后续的渲染过程可以使用它。一些平台需要显式地进行这个过程，而一些平台可以隐式地进行这个过程。
            bool requiresExplicitMsaaResolve = msaaSamples > 1 && PlatformRequiresExplicitMsaaResolve();
            // 用于存储是否是离屏渲染的结果。这个结果是根据cameraData的targetTexture属性和isSceneViewCamera变量来确定的，如果cameraData的targetTexture属性不为null，且isSceneViewCamera为false，那么isOffscreenRender为true，否则为false。
            // 离屏渲染是一种渲染技术，用于将渲染结果输出到一个非屏幕的渲染目标，以便后续的渲染过程可以使用它。
            bool isOffscreenRender = cameraData.targetTexture != null && !isSceneViewCamera;
            // 用于存储是否正在捕获渲染结果的结果。这个结果是根据cameraData的captureActions属性来确定的，如果cameraData的captureActions属性不为null，那么isCapturing为true，否则为false。
            // 捕获渲染结果是一种渲染技术，用于将渲染结果保存到一个指定的位置，以便后续的使用或者分享。
            bool isCapturing = cameraData.captureActions != null;

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
            {
                isScaledRender = false;
                isCompatibleBackbufferTextureDimension = cameraData.xr.renderTargetDesc.dimension == cameraTargetDescriptor.dimension;
            }
#endif
            // 用于存储是否启用了后处理的结果。这个结果是根据cameraData的postProcessEnabled属性和m_PostProcessPasses的isCreated属性来确定的，
            // 如果cameraData的postProcessEnabled属性为true，且m_PostProcessPasses的isCreated属性为true，那么postProcessEnabled为true，否则为false。
            // 后处理是一种渲染技术，用于对渲染结果进行一系列的图像处理效果，如色彩校正，模糊，泛光，抗锯齿等，以提高渲染的质量和美感。
            bool postProcessEnabled = cameraData.postProcessEnabled && m_PostProcessPasses.isCreated;
            // 用于存储是否需要对离屏相机进行blit操作的结果。这个结果是根据postProcessEnabled变量，cameraData的requiresOpaqueTexture属性，requiresExplicitMsaaResolve变量，和cameraData的isDefaultViewport属性来确定的，
            // blit操作是一种渲染技术，用于将一个渲染目标的内容复制到另一个渲染目标中，可以选择使用一个材质来进行一些图像处理效果。
            bool requiresBlitForOffscreenCamera = postProcessEnabled || cameraData.requiresOpaqueTexture || requiresExplicitMsaaResolve || !cameraData.isDefaultViewport;
            if (isOffscreenRender)
                return requiresBlitForOffscreenCamera;

            return requiresBlitForOffscreenCamera || isSceneViewCamera || isScaledRender || cameraData.isHdrEnabled ||
                !isCompatibleBackbufferTextureDimension || isCapturing || cameraData.requireSrgbConversion;  // 不是离屏渲染，就是这几种情况需要生成中间纹理
        }

        /// <summary>
        /// 是否能拷贝深度：
        /// 1.如果没有开启MSAA，并且支持纹理的拷贝，就支持深度的拷贝
        /// 2.或者如果开启了msaa，并且支持多重采样纹理，但是不能在GLES移动设备上
        /// 首先，定义了几个布尔变量，分别表示相机是否开启了MSAA，系统是否支持复制纹理，系统是否支持深度渲染目标，以及系统是否支持复制深度缓冲区（需要同时满足不开启MSAA和支持深度渲染目标或复制纹理）。
        /// 然后，定义了一个布尔变量，表示是否可以通过解析多重采样纹理来获取深度缓冲区（需要同时满足开启MSAA和支持多重采样纹理）。
        /// 接着，判断是否是GLES设备，如果是的话，且可以通过解析多重采样纹理来获取深度缓冲区，那么返回false，因为GLES设备不支持高精度的多重采样纹理，会导致深度精度的损失。
        /// 最后，返回是否支持复制深度缓冲区或者解析多重采样纹理的结果，这就是相机是否能够复制深度缓冲区的判断条件。
        /// </summary>
        /// <param name="cameraData"></param>
        /// <returns></returns>
        bool CanCopyDepth(ref CameraData cameraData)
        {
            bool msaaEnabledForCamera = cameraData.cameraTargetDescriptor.msaaSamples > 1; // 是否开启了MSAA
            bool supportsTextureCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None; // 支持拷贝纹理
            bool supportsDepthTarget = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.Depth); // 是否支持深度纹理
            bool supportsDepthCopy = !msaaEnabledForCamera && (supportsDepthTarget || supportsTextureCopy); // 如果没有开启MSAA，并且支持纹理的拷贝，就支持深度的拷贝

            bool msaaDepthResolve = msaaEnabledForCamera && SystemInfo.supportsMultisampledTextures != 0; // 或者如果开启了msaa，并且支持多重采样纹理，但是不能在GLES移动设备上

            // copying MSAA depth on GLES3 is giving invalid results. This won't be fixed by the driver team because it would introduce performance issues (more info in the Fogbugz issue 1339401 comments)
            // 在 GLES3 上复制 MSAA 深度的结果无效。驱动程序团队不会修复这个问题，因为这会带来性能问题（更多信息请参见 Fogbugz 问题 1339401 评论）。
            if (IsGLESDevice() && msaaDepthResolve)
                return false;

            return supportsDepthCopy || msaaDepthResolve;
        }

        internal override void SwapColorBuffer(CommandBuffer cmd)
        {
            m_ColorBufferSystem.Swap();

            //Check if we are using the depth that is attached to color buffer
            if (m_ActiveCameraDepthAttachment.nameID != BuiltinRenderTextureType.CameraTarget)
                ConfigureCameraTarget(m_ColorBufferSystem.GetBackBuffer(cmd), m_ColorBufferSystem.GetBufferA());
            else
                ConfigureCameraColorTarget(m_ColorBufferSystem.GetBackBuffer(cmd));

            m_ActiveCameraColorAttachment = m_ColorBufferSystem.GetBackBuffer(cmd);
            cmd.SetGlobalTexture("_CameraColorTexture", m_ActiveCameraColorAttachment.nameID);
            //Set _AfterPostProcessTexture, users might still rely on this although it is now always the cameratarget due to swapbuffer
            cmd.SetGlobalTexture("_AfterPostProcessTexture", m_ActiveCameraColorAttachment.nameID);
        }

        internal override RTHandle GetCameraColorFrontBuffer(CommandBuffer cmd)
        {
            return m_ColorBufferSystem.GetFrontBuffer(cmd);
        }

        internal override RTHandle GetCameraColorBackBuffer(CommandBuffer cmd)
        {
            return m_ColorBufferSystem.GetBackBuffer(cmd);
        }

        internal override void EnableSwapBufferMSAA(bool enable)
        {
            m_ColorBufferSystem.EnableMSAA(enable);
        }
    }
}
