// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Tebjan Halm
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Core.Threading;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Engine.Processors
{
    public class InstancingProcessor : EntityProcessor<InstancingComponent, InstancingProcessor.InstancingData>, IEntityComponentRenderProcessor
    {
        private readonly Dictionary<RenderModel, RenderInstancing> modelInstancingMap = new Dictionary<RenderModel, RenderInstancing>();
        private ModelRenderProcessor modelRenderProcessor;
        public class InstancingData
        {
            public TransformComponent TransformComponent;
            public ModelComponent ModelComponent;
            public RenderModel RenderModel;
            public RenderInstancing RenderInstancing = new RenderInstancing();
        }

        public InstancingProcessor()
            : base(typeof(TransformComponent), typeof(ModelComponent)) // Requires TransformComponent and ModelComponent
        {
            // After TransformProcessor but before ModelRenderProcessor
            Order = -100;
        }

        public VisibilityGroup VisibilityGroup { get; set; }

        public override void Draw(RenderContext context)
        {
            // Process the components
            Dispatcher.ForEach(ComponentDatas, item =>
            {
                UpdateInstancing(item.Key, item.Value);
                TransferData(context, item.Key, item.Value.RenderInstancing);
            });
        }

        private static void TransferData(RenderContext context, InstancingComponent instancingComponent, RenderInstancing renderInstancing)
        {
            var instancing = instancingComponent.Type;
            // Get final instance count
            var instanceCount = instancingComponent.Enabled ? instancing.InstanceCount : 0;
            renderInstancing.InstanceCount = instanceCount;
            renderInstancing.ModelTransformUsage = (int)instancing.ModelTransformUsage;

            if (renderInstancing.InstanceCount > 0)
            {
                if (instancing is InstancingUserArray instancingUserArray)
                {
                    renderInstancing.BuffersManagedByUser = false;
                    renderInstancing.WorldMatrices = instancingUserArray.WorldMatrices;
                    renderInstancing.WorldInverseMatrices = instancingUserArray.WorldInverseMatrices;

                    if (renderInstancing.InstanceWorldBuffer == null || renderInstancing.InstanceWorldBuffer.ElementCount < instanceCount)
                    {
                        renderInstancing.InstanceWorldBuffer?.Dispose();
                        renderInstancing.InstanceWorldInverseBuffer?.Dispose();

                        renderInstancing.InstanceWorldBuffer = CreateMatrixBuffer(context.GraphicsDevice, instanceCount);
                        renderInstancing.InstanceWorldInverseBuffer = CreateMatrixBuffer(context.GraphicsDevice, instanceCount);
                    }

                }
                else if (instancing is InstancingUserBuffer instancingUserBuffer)
                {
                    renderInstancing.BuffersManagedByUser = true;
                    renderInstancing.InstanceWorldBuffer = instancingUserBuffer.InstanceWorldBuffer;
                    renderInstancing.InstanceWorldInverseBuffer = instancingUserBuffer.InstanceWorldInverseBuffer;
                }
            }
        }

        private static Buffer<Matrix> CreateMatrixBuffer(GraphicsDevice graphicsDevice, int elementCount)
        {
            return Buffer.New<Matrix>(graphicsDevice, elementCount, BufferFlags.ShaderResource | BufferFlags.StructuredBuffer, GraphicsResourceUsage.Dynamic);
        }

        private void UpdateInstancing(InstancingComponent instancingComponent, InstancingData instancingData)
        {
            if (instancingComponent.Enabled && instancingComponent.Type != null)
            {
                var instancing = instancingComponent.Type;

                // Calculate inverse world and bounding box
                instancing.Update();

                if (instancingData.ModelComponent != null && instancing.InstanceCount > 0)
                {
                    // Bounding box
                    var meshCount = instancingData.ModelComponent.MeshInfos.Count;
                    for (int i = 0; i < meshCount; i++)
                    {
                        var mesh = instancingData.ModelComponent.Model.Meshes[i];
                        var meshInfo = instancingData.ModelComponent.MeshInfos[i];

                        // This must reflect the transformations in the shaders
                        // This is currently not entirely correct, it ignores cases with extreme scalings
                        switch (instancing.ModelTransformUsage)
                        {
                            case ModelTransformUsage.Ignore:
                                BoundingBoxIgnoreWorld(instancingData, instancing, meshInfo, mesh);
                                break;
                            case ModelTransformUsage.PreMultiply:
                                BoundingBoxPreMultiplyWorld(instancingData, instancing, meshInfo, mesh);
                                break;
                            case ModelTransformUsage.PostMultiply:
                                BoundingBoxPostMultiplyWorld(instancingData, instancing, meshInfo, mesh);
                                break;
                            default:
                                break;
                        }
                    }
                }
            }
        }

        private static void BoundingBoxIgnoreWorld(InstancingData instancingData, IInstancing instancing, ModelComponent.MeshInfo meshInfo, Mesh mesh)
        {
            // Handle InstancingEntityTransform specially (more accurate bounding per instance)
            if (instancing is InstancingEntityTransform instancingEntity)
            {
                var bb = BoundingBox.Empty;
                for (int i = 0; i < instancing.InstanceCount; i++)
                {
                    var matrix = instancingEntity.WorldMatrices[i];
                    var mbb = MeshBoundsCache.Get(mesh);

                    BoundingBox.Transform(ref mbb, ref matrix, out var transformedBB);
                    BoundingBox.Merge(ref bb, ref transformedBB, out bb);
                }
                meshInfo.BoundingBox = bb;
            }
            // We need to remove the world transformation component
            else if (instancingData.ModelComponent.Skeleton != null)
            {
                var ibb = instancing.BoundingBox;
                var mbb = new BoundingBoxExt(meshInfo.BoundingBox);

                Matrix.Invert(ref instancingData.ModelComponent.Skeleton.NodeTransformations[0].LocalMatrix, out var invWorld);
                mbb.Transform(invWorld);

                var center = ibb.Center;
                var extend = ibb.Extent + mbb.Extent;
                meshInfo.BoundingBox = new BoundingBox(center - extend, center + extend);
            }
            else // Just take the original mesh one
            {
                var ibb = instancing.BoundingBox;

                var center = ibb.Center;
                var extend = ibb.Extent + mesh.BoundingBox.Extent;
                meshInfo.BoundingBox = new BoundingBox(center - extend, center + extend);
            }
        }

        private static void BoundingBoxPreMultiplyWorld(InstancingData instancingData, IInstancing instancing, ModelComponent.MeshInfo meshInfo, Mesh mesh)
        {
            var ibb = instancing.BoundingBox;

            var center = meshInfo.BoundingBox.Center;
            var extend = ibb.Extent + meshInfo.BoundingBox.Extent;
            meshInfo.BoundingBox = new BoundingBox(center - extend, center + extend);
        }

        private static void BoundingBoxPostMultiplyWorld(InstancingData instancingData, IInstancing instancing, ModelComponent.MeshInfo meshInfo, Mesh mesh)
        {
            var ibb = new BoundingBoxExt(instancing.BoundingBox);
            ibb.Transform(instancingData.TransformComponent.WorldMatrix);
            var center = meshInfo.BoundingBox.Center + ibb.Center - instancingData.TransformComponent.WorldMatrix.TranslationVector; // World translation was applied twice now
            var extend = meshInfo.BoundingBox.Extent + ibb.Extent;
            meshInfo.BoundingBox = new BoundingBox(center - extend, center + extend);
        }

        protected override void OnEntityComponentAdding(Entity entity, [NotNull] InstancingComponent component, [NotNull] InstancingData data)
        {
            data.TransformComponent = component.Entity.Transform;
            data.ModelComponent = component.Entity.Get<ModelComponent>();

            if (data.ModelComponent != null && modelRenderProcessor.RenderModels.TryGetValue(data.ModelComponent, out var renderModel))
            {
                modelInstancingMap[renderModel] = data.RenderInstancing;
                data.RenderModel = renderModel;
            }
        }

        protected override void OnEntityComponentRemoved(Entity entity, [NotNull] InstancingComponent component, [NotNull] InstancingData data)
        {
            if (data.RenderModel != null)
            {
                modelInstancingMap.Remove(data.RenderModel);
            }

            if (component.Type is InstancingUserArray)
            {
                data.RenderInstancing?.InstanceWorldBuffer?.Dispose();
                data.RenderInstancing?.InstanceWorldInverseBuffer?.Dispose();
            }
        }

        // Instancing data per InstancingComponent
        protected override InstancingData GenerateComponentData([NotNull] Entity entity, [NotNull] InstancingComponent component)
        {
            return new InstancingData();
        }

        protected override bool IsAssociatedDataValid([NotNull] Entity entity, [NotNull] InstancingComponent component, [NotNull] InstancingData associatedData)
        {
            return true;
        }

        protected internal override void OnSystemAdd()
        {
            base.OnSystemAdd();
            VisibilityGroup.Tags.Set(InstancingRenderFeature.ModelToInstancingMap, modelInstancingMap);

            modelRenderProcessor = EntityManager.GetProcessor<ModelRenderProcessor>();
            if (modelRenderProcessor == null)
            {
                modelRenderProcessor = new ModelRenderProcessor();
                EntityManager.Processors.Add(modelRenderProcessor);
            }
        }

        protected internal override void OnSystemRemove()
        {
            VisibilityGroup.Tags.Remove(InstancingRenderFeature.ModelToInstancingMap);
            base.OnSystemRemove();
        }
    }

    /// <summary>
    /// Caches mesh-space bounding boxes per <see cref="Mesh"/> instance.
    /// Automatically cleans up when meshes are garbage collected.
    /// </summary>
    internal static class MeshBoundsCache
    {
        private class BoundingBoxRef
        {
            public BoundingBox Value;
            public BoundingBoxRef(BoundingBox value)
            {
                Value = value;
            }
        }

        private static readonly ConditionalWeakTable<Mesh, BoundingBoxRef> Cache = new();

        /// <summary>
        /// Returns a cached copy of the mesh-space bounding box for the given mesh.
        /// </summary>
        public static BoundingBox Get(Mesh mesh)
        {
            return Cache.GetValue(mesh, m => new BoundingBoxRef(m.BoundingBox)).Value;
        }
    }
}
