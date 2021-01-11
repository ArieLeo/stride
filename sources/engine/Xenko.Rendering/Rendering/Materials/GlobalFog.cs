using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Xenko.Core;
using Xenko.Core.Mathematics;
using Xenko.Core.Storage;
using Xenko.Graphics;
using Xenko.Rendering.Compositing;
using Xenko.Rendering.Materials;
using Xenko.Rendering.Materials.ComputeColors;
using Xenko.Shaders;

namespace Xenko.Rendering.Rendering.Materials
{
    [DataContract("MaterialFogFeature")]
    [Display("GlobalFog")]
    public class GlobalFog : MaterialFeature, IMaterialFogFeature
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FogData
        {
            public Vector4 FogColor;
            public float FogStart;
        }

        private static Dictionary<RootEffectRenderFeature, ConstantBufferOffsetReference> effectReferences = new Dictionary<RootEffectRenderFeature, ConstantBufferOffsetReference>();

        private static FogData GlobalFogParameters;

        public static void SetGlobalFog(Color3? color = null, float? density = null, float? fogstart = null)
        {
            if (color.HasValue)
            {
                GlobalFogParameters.FogColor.X = color.Value.R;
                GlobalFogParameters.FogColor.Y = color.Value.G;
                GlobalFogParameters.FogColor.Z = color.Value.B;
            }

            if (density.HasValue)
            {
                GlobalFogParameters.FogColor.W = density.Value;
            }

            if (fogstart.HasValue)
            {
                GlobalFogParameters.FogStart = fogstart.Value;
            }
        }

        public static void GetGlobalFog(out Color3 color, out float density, out float fogstart)
        {
            color = ((Color4)GlobalFogParameters.FogColor).ToColor3();
            density = GlobalFogParameters.FogColor.W;
            fogstart = GlobalFogParameters.FogStart;
        }

        [DataMember]
        public Color3 FogColor
        {
            get => ((Color4)GlobalFogParameters.FogColor).ToColor3();
            set
            {
                GlobalFogParameters.FogColor.X = value.R;
                GlobalFogParameters.FogColor.Y = value.G;
                GlobalFogParameters.FogColor.Z = value.B;
            }
        }
        
        [DataMember]
        public float FogDensity
        {
            get => GlobalFogParameters.FogColor.W;
            set
            {
                GlobalFogParameters.FogColor.W = value;
            }
        }

        [DataMember]
        public float FogStart
        {
            get => GlobalFogParameters.FogStart;
            set
            {
                GlobalFogParameters.FogStart = value;
            }
        }

        public bool Equals(IMaterialShadingModelFeature other)
        {
            return other is GlobalFog;
        }

        internal static unsafe void PrepareFogConstantBuffer(RenderContext context)
        {
            // adjust for differences in DirectX and Vulkan
            Vector4 usecolor = GlobalFogParameters.FogColor;
            if (GraphicsDevice.Platform == GraphicsPlatform.Vulkan) usecolor.W = 1f / Math.Max(0.000001f, usecolor.W);
            usecolor.W = -usecolor.W; // flip this here so we don't need to do it in the shader

            for (int i=0; i<context.RenderSystem.RenderFeatures.Count; i++)
            {
                var renderFeature = context.RenderSystem.RenderFeatures[i];
                RootEffectRenderFeature rooteff = renderFeature as RootEffectRenderFeature;

                if (rooteff == null) continue;

                if (effectReferences.TryGetValue(rooteff, out var offsetref) == false)
                    effectReferences[rooteff] = offsetref = ((RootEffectRenderFeature)renderFeature).CreateFrameCBufferOffsetSlot(FogFeatureKeys.FogColor.Name);

                foreach (var frameLayout in ((RootEffectRenderFeature)renderFeature).FrameLayouts)
                {
                    var fogOffset = frameLayout.GetConstantBufferOffset(offsetref);
                    if (fogOffset == -1)
                        continue;

                    var resourceGroup = frameLayout.Entry.Resources;

                    var mappedCB = (FogData*)(resourceGroup.ConstantBuffer.Data + fogOffset);
                    mappedCB->FogColor = usecolor;
                    mappedCB->FogStart = GlobalFogParameters.FogStart;
                }
            }
        }

        public override void GenerateShader(MaterialGeneratorContext context)
        {
            var shaderBuilder = context.AddShading(this);
            shaderBuilder.ShaderSources.Add(new ShaderClassSource("FogFeature"));
        }
    }
}
