// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;

using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Rendering.Lights
{
    public enum LightShadowMapFilterTypePcfSize
    {
        Filter3x3,

        Filter5x5,

        Filter7x7,
    }

    /// <summary>
    /// Percentage-closer filtering (PCF) for shadow maps, with optional PCSS penumbra.
    /// </summary>
    [DataContract("LightShadowMapFilterTypePcf")]
    [Display("PCF")]
    public class LightShadowMapFilterTypePcf : ILightShadowMapFilterType
    {
        public LightShadowMapFilterTypePcf()
        {
            FilterSize = LightShadowMapFilterTypePcfSize.Filter3x3;
            PcssBlockerSearchRadius = 16.0f;
            PcssMinPenumbraTexels = 1.5f;
            PcssMaxPenumbraTexels = 5.0f;
            PcssPenumbraScale = 5.0f;
        }

        /// <summary>
        /// Gets or sets the size of the filter.
        /// </summary>
        /// <value>The size of the filter.</value>
        /// <userdoc>The size of the filter (size of the kernel).</userdoc>
        [DataMember(10)]
        [DefaultValue(LightShadowMapFilterTypePcfSize.Filter3x3)]
        public LightShadowMapFilterTypePcfSize FilterSize { get; set; }

        /// <summary>
        /// When enabled, uses percentage-closer soft shadows (PCSS) for variable penumbra; otherwise fixed-kernel PCF only.
        /// </summary>
        /// <userdoc>Use PCSS to widen the shadow filter based on estimated blocker distance (contact hard, distant soft).</userdoc>
        [DataMember(20)]
        [DefaultValue(false)]
        public bool UsePcss { get; set; }

        /// <summary>
        /// Blocker search radius in shadow map texels (PCSS search phase).
        /// </summary>
        /// <userdoc>Larger values find occluders farther from the sample point but cost more.</userdoc>
        [DataMember(30)]
        [DefaultValue(16.0f)]
        public float PcssBlockerSearchRadius { get; set; }

        /// <summary>
        /// Minimum penumbra radius in texels after PCSS estimation.
        /// </summary>
        [DataMember(40)]
        [DefaultValue(1.5f)]
        public float PcssMinPenumbraTexels { get; set; }

        /// <summary>
        /// Maximum penumbra radius in texels after PCSS estimation.
        /// </summary>
        [DataMember(50)]
        [DefaultValue(5.0f)]
        public float PcssMaxPenumbraTexels { get; set; }

        /// <summary>
        /// Scales penumbra width from the receiver/blocker depth ratio (artist tuning).
        /// </summary>
        /// <userdoc>Higher values produce wider, softer penumbra.</userdoc>
        [DataMember(60)]
        [DefaultValue(2.0f)]
        public float PcssPenumbraScale { get; set; }

        /// <summary>
        /// Packs PCSS parameters for the shadow shader (x: blocker search radius, y: min penumbra texels, z: max penumbra texels, w: scale).
        /// </summary>
        internal Vector4 GetPcssParametersGpu()
        {
            return new Vector4(PcssBlockerSearchRadius, PcssMinPenumbraTexels, PcssMaxPenumbraTexels, PcssPenumbraScale);
        }

        /// <summary>
        /// Resolves GPU PCSS parameters from a shadow map's filter, or zero when not a PCF filter.
        /// </summary>
        internal static Vector4 GetGpuPcssParameters(LightShadowMap shadowMap)
        {
            return shadowMap?.Filter is LightShadowMapFilterTypePcf pcf ? pcf.GetPcssParametersGpu() : Vector4.Zero;
        }

        public bool RequiresCustomBuffer()
        {
            return false;
        }
    }
}
