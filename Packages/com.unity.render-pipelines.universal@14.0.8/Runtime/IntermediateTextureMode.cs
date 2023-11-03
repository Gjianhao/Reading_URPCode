namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Controls when URP renders via an intermediate texture.
    /// 控制URP何时通过中间纹理呈现。
    /// </summary>
    public enum IntermediateTextureMode
    {
        /// <summary>
        /// Uses information declared by active Renderer Features to automatically determine whether to render via an intermediate texture or not. <seealso cref="ScriptableRenderPass.ConfigureInput"/>.
        /// 使用由活动Renderer Features声明的信息来自动确定是否通过中间纹理进行渲染。
        /// </summary>
        Auto,
        /// <summary>
        /// Forces rendering via an intermediate texture if any Render Feature is active. Use this option for compatibility with Renderer Features that do not support rendering directly to backbuffer or RenderFeatures that do not declare their inputs with <see cref="ScriptableRenderPass.ConfigureInput"/>. Using this option might have a significant performance impact on some platforms such as Quest.
        /// 如果任何渲染特性处于激活状态，则通过中间纹理强制渲染。使用此选项以兼容不支持直接渲染到backbuffer的Renderer Features或不声明其输入的RenderFeatures。使用此选项可能会对某些平台(如Quest)的性能产生重大影响。
        /// </summary>
        Always
    }
}
