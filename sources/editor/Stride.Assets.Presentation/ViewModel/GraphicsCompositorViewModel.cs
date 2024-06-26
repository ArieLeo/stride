// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Assets.Rendering;
using Stride.Core.Assets.Editor.Annotations;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Quantum;
using Stride.Rendering.Images;

namespace Stride.Assets.Presentation.ViewModel
{
    /// <summary>
    /// View model for a <see cref="GraphicsCompositorAsset"/>.
    /// </summary>
    [AssetViewModel<GraphicsCompositorAsset>]
    public class GraphicsCompositorViewModel : AssetViewModel<GraphicsCompositorAsset>
    {
        public GraphicsCompositorViewModel(AssetViewModelConstructionParameters parameters) : base(parameters)
        {
        }

        [Obsolete]
        protected override void OnAssetPropertyChanged(string propertyName, IGraphNode node, NodeIndex index, object oldValue, object newValue)
        {
            base.OnAssetPropertyChanged(propertyName, node, index, oldValue, newValue);

            var memberNode = node as IMemberNode;
            if (memberNode != null && !PropertyGraph.UpdatingPropertyFromBase && !UndoRedoService.UndoRedoInProgress)
            {
                // If Dither changed, clamp Quality to the new valid range
                // Note: other part of the behavior is in GraphicsCompositorAssetNodeUpdater (hide Quality if Dither set to None)
                // TODO: This should be moved to FXAA-specific code to be plugin-ready (asset property changed works only at asset level)
                var ownerNode = memberNode.Parent;
                if (typeof(FXAAEffect).IsAssignableFrom(ownerNode.Type) && propertyName == nameof(FXAAEffect.Dither))
                {
                    var qualityNode = ownerNode[nameof(FXAAEffect.Quality)];
                    var dither = (FXAAEffect.DitherType)memberNode.Retrieve();

                    // Get new valid quality range
                    var (minQuality, maxQuality) = FXAAEffect.GetQualityRange(dither);

                    // Clamp and set it back (if different)
                    var quality = (int)qualityNode.Retrieve();
                    if (quality < minQuality)
                        qualityNode.Update(minQuality);
                    else if (quality > maxQuality)
                        qualityNode.Update(maxQuality);
                }
            }
        }
    }
}
