using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Render all objects that have a 'DepthOnly' pass into the given depth buffer.  渲染所有具有` DepthOnly `属性的对象到给定的深度缓冲区。
    /// You can use this pass to prime a depth buffer for subsequent rendering.   你可以使用此传递为深度缓冲区预热，以便后续渲染。
    /// Use it as a z-prepass, or use it to generate a depth buffer.   使用它作为z-prepass，或者生成深度缓冲
    /// </summary>
    public class DepthOnlyPass : ScriptableRenderPass
    {
        private static readonly ShaderTagId k_ShaderTagId = new ShaderTagId("DepthOnly");

        private RTHandle destination { get; set; }
        private GraphicsFormat depthStencilFormat;
        internal ShaderTagId shaderTagId { get; set; } = k_ShaderTagId;

        private PassData m_PassData;
        FilteringSettings m_FilteringSettings;

        /// <summary>
        /// Creates a new <c>DepthOnlyPass</c> instance. 创建一个新的 DepthOnlyPass 实例
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <param name="renderQueueRange">The <c>RenderQueueRange</c> to use for creating filtering settings that control what objects get rendered.</param>
        /// <param name="layerMask">The layer mask to use for creating filtering settings that control what objects get rendered.</param>
        /// <seealso cref="RenderPassEvent"/>
        /// <seealso cref="RenderQueueRange"/>
        /// <seealso cref="LayerMask"/>
        public DepthOnlyPass(RenderPassEvent evt, RenderQueueRange renderQueueRange, LayerMask layerMask)
        {
            base.profilingSampler = new ProfilingSampler(nameof(DepthOnlyPass));
            m_PassData = new PassData();
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask); // 不透明层
            renderPassEvent = evt; // prepass之前
            useNativeRenderPass = false; // 不使用nativeRenderPass
            this.shaderTagId = k_ShaderTagId;
        }

        /// <summary>
        /// Configures the pass. 配置Pass
        /// </summary>
        /// <param name="baseDescriptor">The <c>RenderTextureDescriptor</c> used for the depthStencilFormat.</param>
        /// <param name="depthAttachmentHandle">The <c>RTHandle</c> used to render to.</param>
        /// <seealso cref="RenderTextureDescriptor"/>
        /// <seealso cref="RTHandle"/>
        /// <seealso cref="GraphicsFormat"/>
        public void Setup(RenderTextureDescriptor baseDescriptor, RTHandle depthAttachmentHandle)
        {
            this.destination = depthAttachmentHandle;
            this.depthStencilFormat = baseDescriptor.depthStencilFormat;
        }

        /// <inheritdoc />
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor; // 渲染纹理设置，用于为渲染创建中间相机纹理。

            // When depth priming is in use the camera target should not be overridden so the Camera's MSAA depth attachment is used.
            // 使用 depth priming 时，不应覆盖摄像机目标，因此应使用摄像机的 MSAA 深度附件。
            if (renderingData.cameraData.renderer.useDepthPriming && (renderingData.cameraData.renderType == CameraRenderType.Base || renderingData.cameraData.clearDepth))
            {
                ConfigureTarget(renderingData.cameraData.renderer.cameraDepthTargetHandle);
                // Only clear depth here so we don't clear any bound color target. It might be unused by this pass but that doesn't mean we can just clear it. (e.g. in case of overlay cameras + depth priming)
                // 这里只清除深度，因此我们不会清除任何绑定的色彩目标。在此过程中，它可能未被使用，但这并不意味着我们可以直接清除它。(例如，在叠加摄像机 + 深度处理的情况下）
                ConfigureClear(ClearFlag.Depth, Color.black);
            }
            // When not using depth priming the camera target should be set to our non MSAA depth target.
            // 不使用深度引导时，摄像机目标应设置为非 MSAA 深度目标。
            else
            {
                useNativeRenderPass = true;
                ConfigureTarget(destination);
                ConfigureClear(ClearFlag.All, Color.black);
            }
        }

        private static void ExecutePass(ScriptableRenderContext context, PassData passData, ref RenderingData renderingData)
        {
            var cmd = renderingData.commandBuffer;
            var shaderTagId = passData.shaderTagId;
            var filteringSettings = passData.filteringSettings;

            using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.DepthPrepass)))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var sortFlags = renderingData.cameraData.defaultOpaqueSortFlags;
                var drawSettings = RenderingUtils.CreateDrawingSettings(shaderTagId, ref renderingData, sortFlags);
                drawSettings.perObjectData = PerObjectData.None;

                // 绘制渲染
                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);
            }
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_PassData.shaderTagId = this.shaderTagId;
            m_PassData.filteringSettings = m_FilteringSettings;
            ExecutePass(context, m_PassData, ref renderingData);
        }

        private class PassData
        {
            internal TextureHandle cameraDepthTexture; // 相机深度纹理
            internal RenderingData renderingData; // 渲染数据
            internal ShaderTagId shaderTagId; // 标签id
            internal FilteringSettings filteringSettings; // 过滤器设置
        }

        internal void Render(RenderGraph renderGraph, out TextureHandle cameraDepthTexture, ref RenderingData renderingData)
        {
            const GraphicsFormat k_DepthStencilFormat = GraphicsFormat.D32_SFloat_S8_UInt;
            const int k_DepthBufferBits = 32;

            using (var builder = renderGraph.AddRenderPass<PassData>("DepthOnly Prepass", out var passData, base.profilingSampler))
            {
                var depthDescriptor = renderingData.cameraData.cameraTargetDescriptor;
                depthDescriptor.graphicsFormat = GraphicsFormat.None;
                depthDescriptor.depthStencilFormat = k_DepthStencilFormat;
                depthDescriptor.depthBufferBits = k_DepthBufferBits;
                depthDescriptor.msaaSamples = 1;// Depth-Only pass don't use MSAA
                cameraDepthTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDescriptor, "_CameraDepthTexture", true); // 生成一张深度图

                passData.cameraDepthTexture = builder.UseDepthBuffer(cameraDepthTexture, DepthAccess.Write);
                passData.renderingData = renderingData;
                passData.shaderTagId = this.shaderTagId;
                passData.filteringSettings = m_FilteringSettings;

                //  TODO RENDERGRAPH: culling? force culling off for testing
                //  TODO RENDERGRAPH：剔除？ 强制关闭剔除以进行测试
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RenderGraphContext context) =>
                {
                    ExecutePass(context.renderContext, data, ref data.renderingData);
                });

                return;
            }
        }
    }
}
