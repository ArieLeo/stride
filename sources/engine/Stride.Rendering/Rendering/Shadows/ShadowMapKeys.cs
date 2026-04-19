// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering.Shadows
{
    /// <summary>
    /// Keys used for shadow mapping.
    /// </summary>
    public static partial class ShadowMapKeys
    {
        /// <summary>
        /// Final shadow map texture.
        /// </summary>
        public static readonly ObjectParameterKey<Texture> ShadowMapTexture = ParameterKeys.NewObject<Texture>();
        
        /// <summary>
        /// Final shadow map texture size
        /// </summary>
        public static readonly ValueParameterKey<Vector2> TextureSize = ParameterKeys.NewValue<Vector2>();

        /// <summary>
        /// Final shadow map texture texel size.
        /// </summary>
        public static readonly ValueParameterKey<Vector2> TextureTexelSize = ParameterKeys.NewValue<Vector2>();

        /// <summary>
        /// PCSS parameters for the PCF filter: x = blocker search radius (texels), y = min penumbra (texels),
        /// z = max penumbra (texels), w = penumbra scale. Only used when PCSS is enabled.
        /// </summary>
        public static readonly ValueParameterKey<Vector4> PcssParameters = ParameterKeys.NewValue<Vector4>();

        /// <summary>
        /// Monotonically increasing frame counter uploaded to the PCSS shader so the interleaved
        /// gradient noise rotation is shifted by the golden ratio each frame, giving maximal temporal
        /// spread for TAA accumulation.
        /// </summary>
        public static readonly ValueParameterKey<float> PcssFrameIndex = ParameterKeys.NewValue<float>();
    }
}
